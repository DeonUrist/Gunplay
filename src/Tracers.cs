using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Micosmo.SensorToolkit.PlayMaker;
using UnityEngine;

namespace Gunplay
{
    // Visible bullets for every gun-wielding human (raiders, Coyotes, traders' guards ...).
    //
    // Vanilla (asset dump 2026-10-01, FSM "Damage Ranged", same on every shooter): state trigger = sound + muzzle flash, aim the
    // child AttackRaycast_Ranged (RaySensor, Length 80, DetectsOn layers 6+10, ObstructedBy 0/8/9/11/14) at detectedObj + a random
    // {offset}, then SensorGetDetectionRayHit: target reached -> state attack (CreateObject RangedHit_Effect at {hitPoint},
    // SetFsmFloat <target>/Bodypart.Damage = damage, SendEvent Damage to the target, Wait rpm) else -> shoot (Wait rpm, nothing).
    //
    // Here: a Harmony prefix on SensorGetDetectionRayHit.OnEnter, only inside "Damage Ranged" of an owner holding a gun or a crossbow,
    // fires a projectile from the gun's fire_effect point toward the same aimed point and sends the FSM down the miss branch, so the
    // burst rhythm stays vanilla. The projectile reproduces the attack state when it reaches the target. Monsters with a
    // "Damage Ranged" FSM (no gun model) keep the vanilla path.
    //
    // Cost: projectiles are structs in one list; each one costs one short RaycastNonAlloc per frame (the distance it moved); all lines
    // are one camera-facing mesh = one draw call with an unlit material (Sprites/Default: no lighting, same look day and night).
    // Above [Tracers] MaxTracers live projectiles a shot is resolved at once (hitscan) instead, so no damage is ever lost.
    internal static partial class Tracers
    {
        internal enum Kind { Pistol, Smg, Rifle, Sniper, Shotgun, Crossbow }

        private struct Shot
        {
            public Vector3 Pos, Dir, Start;
            public float Speed, Range, Damage, Travelled;
            public GameObject Target, ShooterRoot;
            public GameObject Impact;
            public string EventName, EventFsm;
            public bool Bolt, Shotgun, Alive;
            public int Layers, DetectLayers;
            public PlayerGun Gun;            // non-null: the player's shot (first collider it meets is hit, like the vanilla camera ray)
            public GameObject Player;
            public GameObject Head;          // the target's head object (own Bodypart FSM), for head hits
            public CapsuleCollider Cap;      // the player's body capsule (virtual hitbox) - only for shots at the Player
            public Kind Kind;                // (1.5.0) the gun type: how well it penetrates car parts
            public float DmgMult;            // damage left after the car parts it went through (1 = none)
            public int Passed, Passed0, Passed1;   // car parts gone through (at most 2) and their instance ids
            public bool Orb;                 // (1.1.0) a monster's projectile (Orbs.cs): penetrates like a pistol bullet
        }

        // A first-person gun under PlayerCamera/WeaponsArm/Parent/<gun>: its Attack FSM, the Raycast it fires and the states that
        // handle a hit (getLayer / actorHitSound / hit), replayed when the bullet lands.
        private sealed class PlayerGun
        {
            public Fsm Fsm;
            public Kind Kind;
            public Transform Muzzle;
            public int Pellets = 1;
            public float PelletSpread;
            public FsmFloat Damage;          // the hit state's Bodypart.Damage value (per bullet / pellet)
            public int Layers;
            public FsmStateAction[] GetLayer, ActorHit, Hit;
        }

        private sealed class ShooterInfo
        {
            public Transform Muzzle, Weapon;
            public GameObject Owner;
            public Kind Kind;
            public bool IsGun;
            public int ObstructLayers = 19201, DetectLayers = 1088;
            public GameObject Impact;
            public FsmFloat DamageVar;      // the attack state's SetFsmFloat.setValue (usually {Damage Value})
            public float DamageLiteral;
            public float LeadSkill = 1f;    // this NPC's share of [NpcAim] LeadAccuracy (0.5..1, rolled once)
            public string EventName = "Damage";
            public string EventFsm;         // the vanilla SendEvent's named FSM (null = every FSM on the object, as PlayMaker broadcasts)
            public string BodypartFsm = "Bodypart", BodypartVar = "Damage";
            public float Retry; public int Tries;   // no weapon found: look again after Retry (the gun model may be inactive for a frame), 10 tries at most
        }

        private static readonly List<Shot> _shots = new List<Shot>(256);
        private static readonly Dictionary<int, ShooterInfo> _shooters = new Dictionary<int, ShooterInfo>();
        private static readonly RaycastHit[] _hits = new RaycastHit[64];
        private static readonly List<KeyValuePair<Rigidbody, Vector3>> _pushes = new List<KeyValuePair<Rigidbody, Vector3>>();
        private static readonly List<KeyValuePair<GameObject, Vector3>> _popped = new List<KeyValuePair<GameObject, Vector3>>();
        private static int _poppedFrame;
        private static readonly HashSet<GameObject> _forceFree = new HashSet<GameObject>();   // popped wheels: freed by hand if CheckTag doesn't

        // ---------- the hook ----------
        // Harmony prefix on Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit.OnEnter. false = vanilla skipped.
        public static bool BeforeRayHit(SensorGetDetectionRayHit __instance)
        {
            try
            {
                if (!Plugin.TracersEnabled.Value) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Damage Ranged") return true;
                var owner = fsm.GameObject;
                if (owner == null) return true;
                var target = __instance.targetObject != null ? __instance.targetObject.Value : null;
                if (target == null) return true;
                var info = Info(fsm, owner);
                if (!info.IsGun) return true;

                // The vanilla target is whatever collider-object the sensor found nearest - for the player that is the "head" child
                // (its own Bodypart FSM), 3 cm nearer to an NPC's eye-level sensor than the body pivot. The bullet needs the whole
                // body: resolve to the root that carries the capsule, remember the head for head hits, and aim at the body centre
                // ([Tracers] NpcAimAtBody) instead of the head so the vanilla jitter lands on the body, not above it.
                GameObject head = null;
                var root = BodyRootOf(target);
                if (root != null && root != target) { head = target; target = root; }
                else if (root != null) head = HeadOf(root);
                var offsetVar = fsm.Variables.GetFsmVector3("offset");
                Vector3 jitter = offsetVar != null ? offsetVar.Value : Vector3.zero;
                // Optional NPCAI spread calculation; standalone uses the preserved default.
                    jitter *= Integration.SpreadFactor(Vector3.Distance(owner.transform.position, target.transform.position));
                Vector3 aim;
                var cap0 = root != null ? PlayerCapsule(root) : null;
                if (cap0 != null && Plugin.NpcAimAtBody.Value) aim = cap0.transform.TransformPoint(cap0.center);
                else aim = (head != null ? head : target).transform.position;
                Transform muzzle = MuzzleOf(info, owner);
                Vector3 from = muzzle != null ? muzzle.position : fsm.GetOwnerDefaultTarget(__instance.gameObject).transform.position;
                float speed0 = info.Kind == Kind.Crossbow ? Plugin.BoltSpeed.Value : Plugin.BulletSpeed.Value;
                // lead a moving target ([NpcAim] LeadTargets): where it will be when the bullet arrives, as well as this NPC can guess
                if (Plugin.LeadTargets.Value)
                {
                    Vector3 v = VelocityOf(target, root);
                    v.y *= 0.5f;                                   // a jump is not a direction
                    if (v.sqrMagnitude > 0.25f && speed0 > 1f)
                    {
                        float t = Vector3.Distance(from, aim) / speed0;
                        t = Vector3.Distance(from, aim + v * t) / speed0;   // one refinement: the far point takes longer
                        t = Mathf.Min(t, Mathf.Max(0f, Plugin.MaxLeadTime.Value));
                        float skill = Mathf.Clamp01(Plugin.LeadAccuracy.Value) * info.LeadSkill;
                        float err = 1f + UnityEngine.Random.Range(-1f, 1f) * Mathf.Clamp(Plugin.LeadError.Value, 0f, 100f) / 100f;
                        Vector3 lead = v * (t * skill * err);
                        aim += lead;
                        if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Lead: " + owner.name + " leads " + lead.magnitude.ToString("0.00") + " m (target " + v.magnitude.ToString("0.0") + " m/s, flight " + t.ToString("0.00") + " s, skill " + skill.ToString("0.00") + ")");
                    }
                }
                aim += jitter;
                Vector3 dir = aim - from;
                if (dir.sqrMagnitude < 1e-4f) dir = owner.transform.forward;
                dir.Normalize();

                float damage = info.DamageVar != null ? info.DamageVar.Value : info.DamageLiteral;
                int pellets = info.Kind == Kind.Shotgun ? Mathf.Clamp(Plugin.ShotgunPellets.Value, 1, 8) : 1;
                float spread = info.Kind == Kind.Shotgun ? Plugin.ShotgunPelletSpread.Value : 0f;
                for (int i = 0; i < pellets; i++)
                {
                    Vector3 d = dir;
                    if (spread > 0f && pellets > 1)
                        d = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, spread), Vector3.Cross(dir, UnityEngine.Random.onUnitSphere).normalized) * dir;
                    var s = new Shot
                    {
                        Pos = from, Start = from, Dir = d,
                        Speed = speed0,
                        Range = RangeOf(info.Kind),
                        Damage = damage / pellets * (info.Kind == Kind.Shotgun ? Mathf.Max(0f, Plugin.NpcShotgunDamage.Value) : 1f),
                        Target = target,
                        Head = head,
                        Cap = cap0,
                        ShooterRoot = owner.transform.root.gameObject,
                        Impact = info.Impact,
                        EventName = info.EventName, EventFsm = info.EventFsm,
                        Bolt = info.Kind == Kind.Crossbow,
                        Shotgun = info.Kind == Kind.Shotgun,
                        Kind = info.Kind, DmgMult = 1f,
                        Alive = true,
                        Layers = info.ObstructLayers | info.DetectLayers,
                        DetectLayers = info.DetectLayers,
                    };
                    if (_shots.Count >= Plugin.MaxTracers.Value) { Snapshot(); s.Speed = s.Range * 2f; Step(ref s, 1f); }   // over the cap: hitscan
                    else _shots.Add(s);
                }

                Integration.Shot(owner.transform.root.gameObject, from, (int)info.Kind, false);
                fsm.Event(__instance.isNotIntersectedEvent);    // vanilla goes down its miss branch: Wait rpm, next shot
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Tracers: " + e);
                return true;
            }
        }

        public static void OnSceneLoaded()
        {
            _shots.Clear();
            _shooters.Clear();
            _counts.Clear();
            _rangedHit = null; _metalImpact = null; _effectsLooked = false;
            _carRoots.Clear();
            _isPart.Clear(); _headMul.Clear(); _health.Clear(); _creature.Clear();
            _reported.Clear();
            _guns.Clear();
            _notGun.Clear();
            _heads.Clear();
            _player = null;
            _playerTracked = false;
            _orbs.Clear();
            _pushes.Clear();
            _popped.Clear(); _forceFree.Clear(); _meleeWheels.Clear(); _isMelee.Clear(); _corpseHitAt.Clear();
        }

        // ---------- the player's guns ----------
        private static readonly Dictionary<Fsm, PlayerGun> _guns = new Dictionary<Fsm, PlayerGun>();
        private static readonly Dictionary<Fsm, bool> _notGun = new Dictionary<Fsm, bool>();
        private static GameObject _player;

        // ---------- target velocity (for the lead) ----------
        // The player: sampled every frame by Tick and smoothed over ~0.1 s (works however the game moves them, on foot or in a car).
        // Other targets (NPC vs NPC): their Rigidbody, which the Movement FSM drives with SetVelocity.
        private static Vector3 _playerVel, _playerLast;
        private static bool _playerTracked;
        private static float _nextFind;

        private static void TrackPlayer(float dt)
        {
            if (_player == null)
            {
                if (Time.unscaledTime < _nextFind) return;         // no player (menu): look once a second, not every frame
                _nextFind = Time.unscaledTime + 1f;
                _player = GameObject.Find("Player");
                if (_player == null) { _playerTracked = false; return; }
            }
            Vector3 p = _player.transform.position;
            if (!_playerTracked || dt <= 0f) { _playerLast = p; _playerVel = Vector3.zero; _playerTracked = true; return; }
            Vector3 v = (p - _playerLast) / dt;
            _playerLast = p;
            if (v.sqrMagnitude > 50f * 50f) v = Vector3.zero;              // a teleport / load, not movement
            float k = Mathf.Clamp01(dt / 0.1f);
            _playerVel = Vector3.Lerp(_playerVel, v, k);
        }

        private static Vector3 VelocityOf(GameObject target, GameObject root)
        {
            if (_player != null && (target == _player || (root != null && root == _player))) return _playerTracked ? _playerVel : Vector3.zero;
            var rb = target.GetComponentInParent<Rigidbody>();
            return rb != null ? rb.velocity : Vector3.zero;
        }

        // Harmony prefix on HutongGames.PlayMaker.Actions.Raycast.OnEnter. Only the "fire" Raycast of a first-person gun's Attack FSM
        // (an FSM named Attack whose GameObject also has a Reload FSM; melee weapons have none). false = vanilla skipped.
        public static bool BeforeRaycast(Raycast __instance)
        {
            try
            {
                if (!Plugin.TracersEnabled.Value || !Plugin.PlayerTracers.Value) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.repeatInterval == null || __instance.repeatInterval.Value != 0) return true;
                bool no;
                if (_notGun.TryGetValue(fsm, out no)) return true;
                PlayerGun gun;
                if (!_guns.TryGetValue(fsm, out gun))
                {
                    gun = MakeGun(fsm, __instance);
                    if (gun == null) { _notGun[fsm] = true; return true; }
                    _guns[fsm] = gun;
                }

                var from = fsm.GetOwnerDefaultTarget(__instance.fromGameObject);
                if (from == null) return true;
                Transform ft = from.transform;
                Vector3 origin = ft.position + (__instance.fromPosition != null && !__instance.fromPosition.IsNone ? __instance.fromPosition.Value : Vector3.zero);
                Vector3 local = __instance.direction != null && !__instance.direction.IsNone ? __instance.direction.Value : Vector3.forward;
                float range = RangeOf(gun.Kind);
                if (_player == null) _player = GameObject.Find("Player");

                Vector3 muzzle = gun.Muzzle != null && gun.Muzzle.gameObject.activeInHierarchy ? gun.Muzzle.position
                               : origin + ft.forward * 0.4f - ft.up * 0.15f;
                for (int p = 0; p < gun.Pellets; p++)
                {
                    Vector3 l = local;
                    if (p > 0) l = new Vector3(UnityEngine.Random.Range(-gun.PelletSpread, gun.PelletSpread), UnityEngine.Random.Range(-gun.PelletSpread, gun.PelletSpread), 1f);
                    Vector3 dir = __instance.space == Space.Self ? ft.TransformDirection(l) : l;
                    dir.Normalize();
                    // aim where the camera ray points, fly from the muzzle
                    RaycastHit aimHit;
                    Vector3 aim = FirstHit(origin, dir, range, gun.Layers, ft.root, out aimHit) ? aimHit.point : origin + dir * range;
                    Vector3 d = aim - muzzle;
                    if (d.sqrMagnitude < 0.01f) d = dir;
                    var s = new Shot
                    {
                        Pos = muzzle, Start = muzzle, Dir = d.normalized,
                        Speed = gun.Kind == Kind.Crossbow ? Plugin.BoltSpeed.Value : Plugin.BulletSpeed.Value,
                        Range = range, Bolt = gun.Kind == Kind.Crossbow, Shotgun = gun.Kind == Kind.Shotgun, Alive = true, Kind = gun.Kind, DmgMult = 1f,
                        Layers = gun.Layers, Gun = gun, Player = _player,
                        ShooterRoot = ft.root.gameObject,
                    };
                    if (_shots.Count >= Plugin.MaxTracers.Value) { Snapshot(); s.Speed = s.Range * 2f; Step(ref s, 1f); }
                    else _shots.Add(s);
                }
                Integration.Shot(_player, muzzle, (int)gun.Kind, true);
                __instance.Finish();         // vanilla: no hit -> FINISHED -> wait -> next shot, same rhythm
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Tracers (player): " + e);
                return true;
            }
        }

        private static PlayerGun MakeGun(Fsm fsm, Raycast ray)
        {
            var go = fsm.GameObject;
            if (go == null) return null;
            bool reload = false;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Reload") { reload = true; break; }
            if (!reload) return null;
            var hit = fsm.GetState("hit");
            if (hit == null) return null;
            var gun = new PlayerGun { Fsm = fsm, Hit = hit.Actions };
            foreach (var a in gun.Hit) { var sf = a as SetFsmFloat; if (sf != null && sf.fsmName != null && sf.fsmName.Value == "Bodypart") gun.Damage = sf.setValue; }
            var gl = fsm.GetState("getLayer"); if (gl != null) gun.GetLayer = gl.Actions;
            var ah = fsm.GetState("actorHitSound"); if (ah != null) gun.ActorHit = ah.Actions;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("muzzle_flesh_effect", StringComparison.Ordinal) || t.name.StartsWith("muzzle_flash", StringComparison.Ordinal)) { gun.Muzzle = t; break; }
            // layers: the Raycast's own mask
            int mask = 0;
            if (ray.layerMask != null) foreach (var l in ray.layerMask) if (l != null) mask |= 1 << l.Value;
            if (mask == 0) mask = ~0;
            if (ray.invertMask != null && ray.invertMask.Value) mask = ~mask;
            gun.Layers = mask;
            // shotguns: a "pellets" counter; the hit state loops until pellets > N
            // (FsmVariables.GetFsmInt returns a placeholder for a missing variable, so the variable's presence proves nothing:
            // only the loop itself - IntCompare on {pellets} in the hit state - makes a shotgun. 0.4.3 and earlier fired 9 bullets per shot from every gun.)
            bool shotgun = false;
            foreach (var a in gun.Hit)
            {
                var ic = a as IntCompare;
                if (ic != null && ic.integer1 != null && ic.integer1.Name == "pellets" && ic.integer2 != null)
                { shotgun = true; gun.Pellets = Mathf.Clamp(ic.integer2.Value + 1, 1, 32); }
            }
            if (shotgun)
            {
                var fire = fsm.GetState("fire");
                gun.PelletSpread = 0.02f;
                if (fire != null) foreach (var a in fire.Actions) { var rf = a as RandomFloat; if (rf != null && rf.max != null) { gun.PelletSpread = Mathf.Abs(rf.max.Value); break; } }
            }
            gun.Kind = shotgun ? Kind.Shotgun : Classify(go.name);
            if (gun.Kind == Kind.Shotgun && !shotgun) gun.Kind = Kind.Rifle;      // a "rochester" without pellets is not a shotgun
            Plugin.Verbose("Tracers: player gun " + go.name + " = " + gun.Kind + (shotgun ? ", " + gun.Pellets + " pellets" : "") + (gun.Muzzle != null ? "" : ", no muzzle point"));
            return gun;
        }

        // the nearest thing the camera ray meets, as the vanilla Raycast sees it (QueriesHitTriggers is on in this game: head
        // triggers with a Bodypart FSM count, other triggers don't), skipping the player and their car
        private static bool FirstHit(Vector3 origin, Vector3 dir, float range, int mask, Transform shooterRoot, out RaycastHit hit)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, _hits, range, mask, QueryTriggerInteraction.Collide);
            if (n >= _hits.Length) { return Physics.Raycast(origin, dir, out hit, range, mask, QueryTriggerInteraction.Ignore); }
            if (n > 1) Array.Sort(_hits, 0, n, HitDistance.Instance);
            for (int k = 0; k < n; k++)
            {
                var c = _hits[k].collider;
                if (c == null || Ignored(c.transform, shooterRoot, _player) || !Counts(c)) continue;
                hit = _hits[k];
                return true;
            }
            hit = default(RaycastHit);
            return false;
        }

        // Does a collider stop a bullet? Solid colliders always; trigger colliders only when their object has a Bodypart FSM
        // (the NPC head capsules, the player's head sphere) - vanilla raycasts hit those (QueriesHitTriggers), FireDamageCollider
        // and other triggers are not bodies. Cached per collider.
        private static readonly Dictionary<int, bool> _counts = new Dictionary<int, bool>();
        private static bool Counts(Collider c)
        {
            if (!c.isTrigger) return true;
            bool ok;
            int id = c.GetInstanceID();
            if (_counts.TryGetValue(id, out ok)) return ok;
            ok = HasBodypart(c.gameObject);
            _counts[id] = ok;
            if (ok) ReportHitbox(c);
            return ok;
        }

        // (1.5.2) A dead NPC is a separate "<Name>_Dead" ragdoll (layer Item, limbs on Default with Bodypart FSMs that add to a Health
        // the corpse doesn't have): a Bodypart on a body with no Health FSM. The game picks the hit effect by layer (Actor = blood), so
        // its limbs got the ground's dust; and our creature test showed damage numbers on it.
        private static bool IsCorpse(GameObject go)
        {
            if (go == null || IsPlayerObj(go)) return false;
            if (!HasBodypart(go) && !HasBodypart(RootOf(go.transform).gameObject)) return false;
            return float.IsNaN(HealthOf(go));
        }

        private static bool HasBodypart(GameObject go)
        {
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Bodypart") return true;
            return false;
        }

        // Verbose, once per creature type: where its head trigger and body capsule reach, against the visible model
        private static readonly HashSet<string> _reported = new HashSet<string>();
        private static void ReportHitbox(Collider head)
        {
            if (!Plugin.VerboseLog.Value) return;
            var root = RootOf(head.transform);
            string name = root.name; int cut = name.IndexOf('('); if (cut > 0) name = name.Substring(0, cut);
            if (!_reported.Add(name)) return;
            try
            {
                var body = PlayerCapsule(root.gameObject);
                float feet = body != null ? body.bounds.min.y : root.position.y;
                float bodyTop = body != null ? body.bounds.max.y - feet : 0f;
                float headTop = head.bounds.max.y - feet, headBottom = head.bounds.min.y - feet;
                float meshTop = 0f;
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>()) meshTop = Mathf.Max(meshTop, smr.bounds.max.y - feet);
                Plugin.Log.LogInfo("Hitbox " + name + ": body capsule 0.00-" + bodyTop.ToString("0.00") + " m above the feet, head " + headBottom.ToString("0.00")
                    + "-" + headTop.ToString("0.00") + " (r " + head.bounds.extents.x.ToString("0.00") + "), model top " + meshTop.ToString("0.00"));
            }
            catch (Exception e) { Plugin.Verbose("Hitbox report failed: " + e.Message); }
        }

        private static bool Ignored(Transform t, Transform shooterRoot, GameObject player)
        {
            if (shooterRoot != null && t.IsChildOf(shooterRoot)) return true;
            if (player != null && t.IsChildOf(player.transform.root)) return true;     // the player and, when driving, their car
            return false;
        }

        // the gun's vanilla hit path, for the collider the bullet met: set its hitObj/hitPoint/hitNormal, then getLayer or
        // actorHitSound (which impact sound), then the hit state's CreateObject / SetFsmFloat (damage x falloff) / SendEvent actions
        private static void PlayerHit(ref Shot s, RaycastHit h, float falloff)
        {
            var gun = s.Gun;
            var fsm = gun.Fsm;
            if (fsm == null) return;
            var go = h.collider.gameObject;
            var vo = fsm.Variables.GetFsmGameObject("hitObj"); if (vo != null) vo.Value = go;
            var vp = fsm.Variables.GetFsmVector3("hitPoint"); if (vp != null) vp.Value = h.point;
            var vn = fsm.Variables.GetFsmVector3("hitNormal"); if (vn != null) vn.Value = h.normal;
            // vehicle parts carry a Bodypart FSM too (their condition): not a creature - no hurt ghost, no white/red number (the part rule shows blue)
            bool isPart = OwningPart(go.transform) != null || go.CompareTag("vehPartRemoved") || IsVehiclePart(go);   // the same test HitWorld uses (a vehPart-tagged ancestor)
            bool corpse = !isPart && IsCorpse(go);                // (1.5.2) a dead NPC's ragdoll: blood, but no number / marker / hurt
            bool creature = !isPart && !corpse && (HasBodypart(go) || HasBodypart(RootOf(go.transform).gameObject));
            if (creature) Integration.Hurt(go, s.Player);
            bool feedback = creature;
            float before = Plugin.HitLog.Value || feedback ? HealthOf(go) : 0f;
            Replay(fsm, (go.layer == 10 || corpse) && gun.ActorHit != null ? gun.ActorHit : gun.GetLayer, falloff);   // a corpse bleeds like the living NPC did
            float headF = creature && col_isHead(go) && !IsPlayerObj(go) ? HeadFactor(go) : 1f;     // [Gunplay] HeadshotMultiplier instead of the head's own x2
            Replay(fsm, gun.Hit, falloff * headF, Plugin.VehicleDamage.Value && isPart, (creature || corpse) && (go.layer == 10 || corpse) && gun.ActorHit != null);
            float after = Plugin.HitLog.Value || feedback ? HealthOf(go) : 0f;
            if (feedback)
            {
                float nominal = gun.Damage != null ? gun.Damage.Value * falloff : 0f;
                float dealt = !float.IsNaN(before) && !float.IsNaN(after) && after < before ? after - before : nominal;
                bool head = col_isHead(go);
                Integration.PlayerHit(RootOf(go.transform).gameObject, h.point, dealt, head);
            }
            if (Plugin.HitLog.Value)
            {
                Plugin.Log.LogInfo("Hit: " + fsm.GameObject.name + " -> " + RootOf(go.transform).name + "/" + go.name + " at " + (s.Travelled + h.distance).ToString("0.0") + " m, damage "
                    + (gun.Damage != null ? gun.Damage.Value * falloff : 0f).ToString("0.0") + " (x" + falloff.ToString("0.00") + ")"
                    + (float.IsNaN(before) ? ", no Health FSM" : ", Health " + before.ToString("0.0") + " -> " + after.ToString("0.0")));
            }
        }

        // An NPC's head carries its own Bodypart FSM whose damage state multiplies the damage (FloatMultiply, x2 on every NPC) before taking it
        // off Health. A hit there is pre-scaled so the result is [Gunplay] HeadshotMultiplier x the damage. Cached per head object.
        private static readonly Dictionary<int, float> _headMul = new Dictionary<int, float>();
        private static float HeadFactor(GameObject head)
        {
            float m;
            int id = head.GetInstanceID();
            if (!_headMul.TryGetValue(id, out m))
            {
                m = 1f;
                foreach (var f in head.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.FsmName != "Bodypart" || f.Fsm == null || f.Fsm.States == null) continue;
                    foreach (var st in f.Fsm.States)
                    {
                        if (st.Actions == null) continue;
                        foreach (var a in st.Actions) { var fm = a as FloatMultiply; if (fm != null && fm.multiplyBy != null && fm.multiplyBy.Value > 0f) m = fm.multiplyBy.Value; }
                    }
                }
                _headMul[id] = m;
            }
            return Mathf.Max(0f, Plugin.HeadshotMultiplier.Value) / m;
        }

        private static bool IsPlayerObj(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent) if (t.CompareTag("Player")) return true;
            return false;
        }

        // the hit object is a creature's head when it is a child with its own Bodypart FSM (the player's "head", NPC mixamorig:Head)
        private static bool col_isHead(GameObject go)
        {
            return go.transform.parent != null && HasBodypart(go);
        }

        // Vehicle parts (45 prefabs: engines, wheels, doors ...) carry a vanilla Bodypart FSM whose Damage event subtracts the bullet's damage 1:1 from
        // their Condition (an akms round = -23 %). With [Tracers] VehicleDamage on, the mod's VehicleDamagePer1 rule replaces that: the vanilla
        // Damage event / Bodypart.Damage write are not replayed for a part (the other hit actions - effect, HitEffect, DamageFlammable, CheckFriendly - are).
        private static readonly Dictionary<int, bool> _isPart = new Dictionary<int, bool>();
        private static bool IsVehiclePart(GameObject go)
        {
            bool part;
            int id = go.GetInstanceID();
            if (_isPart.TryGetValue(id, out part)) return part;
            part = false;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && (f.FsmName == "Condition" || f.FsmName == "Repair")) { part = true; break; }
            _isPart[id] = part;
            return part;
        }

        private static void Replay(Fsm fsm, FsmStateAction[] actions, float falloff, bool skipBodypartDamage = false, bool skipDust = false)
        {
            if (actions == null) return;
            foreach (var a in actions)
            {
                if (skipBodypartDamage)
                {
                    var sfx = a as SetFsmFloat;
                    if (sfx != null && sfx.fsmName != null && sfx.fsmName.Value == "Bodypart") continue;
                    var sex = a as SendEvent;
                    if (sex != null && sex.sendEvent != null && sex.sendEvent.Name == "Damage") continue;
                }
                try
                {
                    var co = a as CreateObject;
                    if (co != null)
                    {
                        var prefab = co.gameObject != null ? co.gameObject.Value : null;
                        if (prefab == null) continue;
                        if (skipDust && prefab.name == "RangedHit_Effect") continue;     // (1.5.5) a creature / corpse: its blood only, no ground dust
                        var sp = co.spawnPoint != null ? co.spawnPoint.Value : null;
                        Vector3 pos; Quaternion rot;
                        bool hasPos = co.position != null && !co.position.IsNone, hasRot = co.rotation != null && !co.rotation.IsNone;
                        if (sp != null)
                        {
                            pos = sp.transform.position + (hasPos ? co.position.Value : Vector3.zero);
                            rot = hasRot ? Quaternion.Euler(co.rotation.Value) : sp.transform.rotation;
                        }
                        else
                        {
                            pos = hasPos ? co.position.Value : Vector3.zero;
                            rot = hasRot ? Quaternion.Euler(co.rotation.Value) : prefab.transform.rotation;
                        }
                        var obj = UnityEngine.Object.Instantiate(prefab, pos, rot);
                        var par = co.parent != null ? co.parent.Value : null;
                        if (par != null) obj.transform.parent = par.transform;
                        continue;
                    }
                    var sf = a as SetFsmFloat;
                    if (sf != null)
                    {
                        var tgt = fsm.GetOwnerDefaultTarget(sf.gameObject);
                        if (tgt == null || sf.setValue == null) continue;
                        string fn = sf.fsmName != null ? sf.fsmName.Value : "", vn = sf.variableName != null ? sf.variableName.Value : "";
                        foreach (var f in tgt.GetComponents<PlayMakerFSM>())
                            if (f.FsmName == fn) { var v = f.FsmVariables.GetFsmFloat(vn); if (v != null) v.Value = sf.setValue.Value * falloff; break; }
                        continue;
                    }
                    var sg = a as SetFsmGameObject;
                    if (sg != null)
                    {
                        var tgt = fsm.GetOwnerDefaultTarget(sg.gameObject);
                        if (tgt == null || sg.setValue == null) continue;
                        string fn = sg.fsmName != null ? sg.fsmName.Value : "", vn = sg.variableName != null ? sg.variableName.Value : "";
                        foreach (var f in tgt.GetComponents<PlayMakerFSM>())
                            if (f.FsmName == fn) { var v = f.FsmVariables.GetFsmGameObject(vn); if (v != null) v.Value = sg.setValue.Value; break; }
                        continue;
                    }
                    var se = a as SendEvent;
                    if (se != null && se.sendEvent != null) { fsm.Event(se.eventTarget, se.sendEvent); continue; }
                }
                catch (Exception e) { Plugin.Verbose("Tracers: replay " + a.GetType().Name + " failed: " + e.Message); }
            }
        }

        // ---------- shooter info (once per FSM owner) ----------
        private static ShooterInfo Info(Fsm fsm, GameObject owner)
        {
            ShooterInfo info;
            int id = owner.GetInstanceID();
            if (_shooters.TryGetValue(id, out info) && (info.Weapon != null ? info.Weapon.gameObject.activeInHierarchy : Time.time < info.Retry)) return info;
            int tries = info != null && info.Weapon == null ? info.Tries + 1 : 0;
            info = new ShooterInfo { Owner = owner, LeadSkill = UnityEngine.Random.Range(0.5f, 1f), Tries = tries, Retry = tries < 10 ? Time.time + 1f : float.MaxValue };
            _shooters[id] = info;

            // the weapon in the hand: a model with a fire_effect child (guns) or an active "crossbow"
            foreach (var t in owner.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "fire_effect" && t.parent != null && t.parent.name != "fire_effect" && t.parent.gameObject.activeInHierarchy)
                { info.Weapon = t.parent; info.Muzzle = t; break; }
            }
            if (info.Weapon == null)
                foreach (var t in owner.GetComponentsInChildren<Transform>(false))
                    if (t.name.StartsWith("crossbow", StringComparison.OrdinalIgnoreCase) && t.GetComponent<Renderer>() != null)
                    { info.Weapon = t; break; }
            if (info.Weapon == null) return info;    // a monster: vanilla
            info.IsGun = true;
            info.Kind = Classify(info.Weapon.name);

            // the vanilla attack state: impact prefab, damage, event
            var st = fsm.GetState("attack");
            if (st != null && st.Actions != null)
                foreach (var a in st.Actions)
                {
                    var co = a as CreateObject;
                    if (co != null && co.gameObject != null && co.gameObject.Value != null) info.Impact = co.gameObject.Value;
                    var sf = a as SetFsmFloat;
                    if (sf != null && sf.setValue != null)
                    {
                        info.DamageVar = sf.setValue;
                        if (sf.fsmName != null && !string.IsNullOrEmpty(sf.fsmName.Value)) info.BodypartFsm = sf.fsmName.Value;
                        if (sf.variableName != null && !string.IsNullOrEmpty(sf.variableName.Value)) info.BodypartVar = sf.variableName.Value;
                    }
                    var se = a as SendEvent;
                    if (se != null && se.sendEvent != null && !string.IsNullOrEmpty(se.sendEvent.Name))
                    {
                        info.EventName = se.sendEvent.Name;
                        var et = se.eventTarget;
                        info.EventFsm = et != null && et.target == FsmEventTarget.EventTarget.GameObjectFSM && et.fsmName != null && !string.IsNullOrEmpty(et.fsmName.Value) ? et.fsmName.Value : null;
                    }
                }
            if (info.DamageVar == null) info.DamageLiteral = -7f;

            // the vanilla ray's layers
            var ray = owner.transform.Find("AttackRaycast_Ranged");
            if (ray != null)
            {
                var rs = ray.GetComponent<Micosmo.SensorToolkit.RaySensor>();
                if (rs != null) { info.ObstructLayers = rs.ObstructedByLayers.value; info.DetectLayers = rs.DetectsOnLayers.value; }
            }
            if (Plugin.VerboseLog.Value) Plugin.Verbose("Tracers: " + owner.name + " fires " + info.Kind + " (" + info.Weapon.name + "), damage " +
                           (info.DamageVar != null ? info.DamageVar.Value : info.DamageLiteral) + ", impact " + (info.Impact != null ? info.Impact.name : "none"));
            return info;
        }

        private static Transform MuzzleOf(ShooterInfo info, GameObject owner)
        {
            if (info.Muzzle != null) return info.Muzzle;
            return info.Weapon;
        }

        internal static Kind Classify(string weapon)
        {
            string n = weapon.ToLowerInvariant();
            if (n.Contains("crossbow")) return Kind.Crossbow;
            if (n.Contains("shotgun") || n.Contains("slamfire") || n.Contains("slamberg") || n.Contains("rochester")) return Kind.Shotgun;
            if (n.Contains("scoped") || n.Contains("sniper") || n.Contains("redmark")) return Kind.Sniper;
            if (n.Contains("smg") || n.Contains("borz")) return Kind.Smg;
            if (n.Contains("pistol") || n.Contains("revolver") || n.Contains("folk_17")) return Kind.Pistol;
            return Kind.Rifle;
        }

        // The gun an NPC holds, for the Aim pacing (cached like Info; finds the Damage Ranged FSM itself).
        internal static bool GunKindOf(GameObject owner, out Kind kind)
        {
            kind = Kind.Rifle;
            ShooterInfo info;
            if (!_shooters.TryGetValue(owner.GetInstanceID(), out info) || (info.Weapon != null ? !info.Weapon.gameObject.activeInHierarchy : Time.time >= info.Retry))
            {
                Fsm dr = null;
                foreach (var f in owner.GetComponents<PlayMakerFSM>())
                    if (f != null && f.FsmName == "Damage Ranged") { dr = f.Fsm; break; }
                if (dr == null) return false;
                info = Info(dr, owner);
            }
            if (!info.IsGun) return false;
            kind = info.Kind;
            return true;
        }

        // Damage multiplier by distance flown: full damage until [Tracers] FullDamageUntil % of the range, then linear to 0 at the range.
        private static float Falloff(ref Shot s, float dist)
        {
            float x = dist / s.Range;
            float k = _fullUntil;
            if (x <= k) return 1f;
            return Mathf.Clamp01((1f - x) / (1f - k));
        }

        // [Debug] HitLog: the Health of the thing hit (its root's Health FSM), before and after
        // the creature's Health variable, found once per root (a full hierarchy scan) and cached by root object
        private sealed class HealthRef { public GameObject Root; public FsmFloat Var; }
        private static readonly Dictionary<int, HealthRef> _health = new Dictionary<int, HealthRef>();
        private static float HealthOf(GameObject go)
        {
            if (go == null) return float.NaN;
            var root = RootOf(go.transform).gameObject;
            int id = root.GetInstanceID();
            HealthRef hr;
            if (_health.TryGetValue(id, out hr) && hr.Root == root) return hr.Var != null ? hr.Var.Value : float.NaN;
            hr = new HealthRef { Root = root };
            foreach (var f in root.GetComponentsInChildren<PlayMakerFSM>())
                if (f.FsmName == "Health") { var v = f.FsmVariables.FindFsmFloat("Health"); if (v != null) { hr.Var = v; break; } }
            _health[id] = hr;
            return hr.Var != null ? hr.Var.Value : float.NaN;
        }

        internal static float RangeOf(Kind k)
        {
            switch (k)
            {
                case Kind.Pistol: return Mathf.Max(1f, Plugin.PistolRange.Value);
                case Kind.Smg: return Mathf.Max(1f, Plugin.SmgRange.Value);
                case Kind.Sniper: return Mathf.Max(1f, Plugin.SniperRange.Value);
                case Kind.Shotgun: return Mathf.Max(1f, Plugin.ShotgunRange.Value);
                case Kind.Crossbow: return Mathf.Max(1f, Plugin.CrossbowRange.Value);
                default: return Mathf.Max(1f, Plugin.RifleRange.Value);
            }
        }

        // ---------- simulation ----------
        // settings read once per frame (not per bullet)
        private static float _npcRadius, _fullUntil, _headMult, _bodyR, _headR;
        private static float _nextSweep;

        private static void Snapshot()
        {
            _npcRadius = Mathf.Max(0f, Plugin.NpcHitRadius.Value);
            _fullUntil = Mathf.Clamp(Plugin.FullDamageUntil.Value, 0f, 99f) / 100f;
            _headMult = Mathf.Max(0f, Plugin.HeadshotMultiplier.Value);
            _bodyR = Mathf.Max(0.03f, Plugin.PlayerBodyRadius.Value);
            _headR = Mathf.Max(0.03f, Plugin.PlayerHeadRadius.Value);
        }

        // ---------- melee on corpses (1.6.1) ----------
        // The player's melee weapons (hands, kick, old_knife, shiv, machete, pipe_wrench: an Attack FSM without Reload under WeaponsArm) pick
        // the hit effect in their own "getLayer" state: GetLayer(hitObj) == 10 (Actor) -> "actorHitSound" (knife_hit + BloodHit_Effect),
        // anything else -> Crash-02 + MeleeHit_Effect (brown dust). A corpse's limbs are on Default, so a corpse got dust like the guns did
        // before 1.5.2. Postfix on GetLayer.OnEnter (DoGetLayer could be inlined): in that state, on a corpse, the stored layer becomes 10 -
        // the FSM's own blood path runs. A blade (name in hidden [Tracers] StabWeapons) then stabs with Sounds/knifestab.wav instead of
        // knife_hit: postfix on SetAudioClip.OnEnter in "actorHitSound" of the same FSM within the same moment.
        private static readonly Dictionary<int, float> _corpseHitAt = new Dictionary<int, float>();   // Attack FSM id -> when it hit a corpse
        private static AudioClip _stab; private static bool _stabTried;

        internal static bool IsPlayerMelee(GameObject weapon)
        {
            if (weapon == null) return false;
            bool melee;
            if (_isMelee.TryGetValue(weapon.GetInstanceID(), out melee)) return melee;
            // the player's weapons live under PlayerCameraHolder/PlayerCamera/WeaponsArm (Camera.main may be another camera)
            melee = false;
            for (var t = weapon.transform.parent; t != null; t = t.parent) if (t.name == "WeaponsArm") { melee = true; break; }
            if (melee) foreach (var f in weapon.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Reload") { melee = false; break; }
            _isMelee[weapon.GetInstanceID()] = melee;
            return melee;
        }

        public static void AfterGetLayer(GetLayer __instance)
        {
            try
            {
                if (!Plugin.TracersEnabled.Value) return;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.State == null || __instance.State.Name != "getLayer") return;
                if (__instance.storeResult == null || __instance.storeResult.IsNone || __instance.storeResult.Value == 10) return;
                if (!IsPlayerMelee(fsm.GameObject)) return;
                var hit = __instance.gameObject != null ? __instance.gameObject.Value : null;
                if (hit == null || !IsCorpse(hit)) return;
                __instance.storeResult.Value = 10;                       // the FSM goes on to actorHitSound: knife_hit + BloodHit_Effect
                _corpseHitAt[fsm.GameObject.GetInstanceID()] = Time.time;
                if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Hit: " + fsm.GameObject.name + " (melee) -> corpse " + hit.transform.root.name + "/" + hit.name + ": blood" + (IsBlade(fsm.GameObject.name) ? ", stab sound" : ""));
            }
            catch (Exception e) { Plugin.Log.LogError("Tracers melee corpse: " + e); }
        }

        public static void AfterSetAudioClip(SetAudioClip __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.State == null || __instance.State.Name != "actorHitSound" || fsm.GameObject == null) return;
                float at;
                int id = fsm.GameObject.GetInstanceID();
                if (!_corpseHitAt.TryGetValue(id, out at)) return;
                _corpseHitAt.Remove(id);
                if (Time.time - at > 0.25f || !IsBlade(fsm.GameObject.name)) return;
                var clip = StabClip();
                if (clip == null) return;
                var go = fsm.GetOwnerDefaultTarget(__instance.gameObject);
                var src = go != null ? go.GetComponent<AudioSource>() : null;
                if (src != null) src.clip = clip;                        // the hit state's AudioPlay plays it (pitch 0.5-1.5, volume 0.5 as vanilla)
            }
            catch (Exception e) { Plugin.Log.LogError("Tracers stab sound: " + e); }
        }

        private static bool IsBlade(string weapon)
        {
            string list = Plugin.StabWeapons.Value ?? "";
            foreach (var part in list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string k = part.Trim();
                if (k.Length > 0 && weapon.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Sounds/knifestab.wav next to the DLL (hidden [Tracers] StabSound), loaded once
        private static AudioClip StabClip()
        {
            if (_stab != null || _stabTried) return _stab;
            _stabTried = true;
            string rel = Plugin.StabSound.Value ?? "";
            if (rel.Length == 0) return null;
            string path = System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(Plugin.Dir, rel);
            try
            {
                if (!System.IO.File.Exists(path)) { Plugin.Log.LogWarning("Tracers: no stab sound at " + path + " - corpses get the game's knife sound"); return null; }
                int ch, rate;
                float[] smp = Wav.Read(System.IO.File.ReadAllBytes(path), out ch, out rate);
                _stab = AudioClip.Create("knifestab", Math.Max(1, smp.Length / ch), ch, rate, false);
                _stab.SetData(smp, 0);
                _stab.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Plugin.Verbose("Tracers: stab sound loaded (" + System.IO.Path.GetFileName(path) + ", " + (smp.Length / ch / (float)rate).ToString("0.00") + " s)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Tracers: stab sound " + path + " not loaded: " + e.Message); _stab = null; }
            return _stab;
        }

        // ---------- melee on wheels ----------
        // The player's melee weapons (an "Attack" FSM without a Reload FSM, under the camera) hit with SetFsmFloat(hitObj / Bodypart.Damage) +
        // SendEvent Damage; a vehicle part's Bodypart FSM takes that straight off its Condition. Postfix on SetFsmFloat.OnEnter (DoSetFsmFloat could be inlined): when the
        // target is a wheel, its Bodypart.Damage is multiplied by [Tracers] WheelDamageMultiplier (and shown in blue); a wheel brought to 0
        // jumps off next frame (WheelPopOff).
        private struct MeleeWheel { public Transform Part; public float Before; public Vector3 Dir; public int Frame; }
        private static readonly List<MeleeWheel> _meleeWheels = new List<MeleeWheel>();
        private static readonly Dictionary<int, bool> _isMelee = new Dictionary<int, bool>();
        public static void AfterSetFsmFloat(SetFsmFloat __instance)
        {
            try
            {
                if (__instance.fsmName == null || __instance.fsmName.Value != "Bodypart" || __instance.variableName == null || __instance.variableName.Value != "Damage") return;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || fsm.GameObject == null) return;
                var weapon = fsm.GameObject;
                if (!IsPlayerMelee(weapon)) return;
                var target = fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (target == null) return;
                var part = OwningPart(target.transform);
                if (part == null || !IsWheel(target.transform, part)) return;
                float mult = Mathf.Max(0f, Plugin.WheelDamageMultiplier.Value);
                float before = PartCondition(part);
                foreach (var f in target.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.FsmName != "Bodypart") continue;
                    var v = f.FsmVariables.FindFsmFloat("Damage");
                    if (v == null) continue;
                    v.Value *= mult;
                    Integration.PartHit(part.gameObject, target.transform.position, Mathf.Abs(v.Value));
                    if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Hit: " + weapon.name + " (melee) -> " + part.name + ", condition " + before.ToString("0.0") + " " + v.Value.ToString("0.0") + " (x" + mult.ToString("0.#") + ")");
                    break;
                }
                Vector3 dir = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
                if (Plugin.WheelPopOff.Value && before > 0f) _meleeWheels.Add(new MeleeWheel { Part = part, Before = before, Dir = dir, Frame = Time.frameCount });
            }
            catch (Exception e) { Plugin.Log.LogError("Tracers melee: " + e); }
        }

        // ---------- melee on FITTED wheels (1.5.1) ----------
        // A fitted wheel's collider is a trigger (its CheckTag FSM, state vehPart: ColliderSetIsTrigger true) and the melee weapons' own
        // SphereCast2 (PlayerCamera, r 0.02, 1.8 m) ignores triggers: knives cut loose wheels but passed through fitted ones, so the hook
        // above never ran for them. While a melee Attack FSM is in "fire" (the swing) the cast is repeated with triggers; a fitted wheel
        // met before anything solid gets the weapon's hit value x WheelDamageMultiplier on its Bodypart (+ the Damage event), the blue hit
        // number, and the WheelPopOff check - once per swing. (Apocapatrol has a plain version that steps aside when it sees this type.)
        private static Transform _wParent, _wCam;
        private static float _wNextFind;
        private static PlayMakerFSM _wSwing;
        private static bool _wDone;
        private static readonly Dictionary<int, string> _wPrev = new Dictionary<int, string>();   // Attack FSM id -> last seen state
        private static readonly Dictionary<int, float> _wDamage = new Dictionary<int, float>();   // Attack FSM id -> hit value; NaN = not melee
        private static readonly RaycastHit[] _wHits = new RaycastHit[24];
        private const int WheelSwingMask = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 13) | (1 << 14) | (1 << 16);

        private static void SwingAtFittedWheels()
        {
            if (_wParent == null)
            {
                if (Time.unscaledTime < _wNextFind) return;
                _wNextFind = Time.unscaledTime + 2f;
                var holder = GameObject.Find("PlayerCameraHolder");
                if (holder == null) return;
                _wCam = holder.transform.Find("PlayerCamera");
                _wParent = _wCam != null ? _wCam.Find("WeaponsArm/Parent") : null;
                if (_wParent == null) return;
            }
            // a swing: the Attack FSM in "fire", or (1.5.4) one that just left "on" for "hit" / "wait" - when the game's own cast meets
            // something at once (the hub around a fitted tyre), on -> fire -> hit -> wait runs within ONE frame and "fire" is never seen here
            PlayMakerFSM active = null;
            bool fresh = false;
            for (int i = 0; i < _wParent.childCount; i++)
            {
                var w = _wParent.GetChild(i);
                if (!w.gameObject.activeInHierarchy) continue;
                foreach (var f in w.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.FsmName != "Attack" || f.Fsm == null || !f.Fsm.Initialized) continue;
                    string cur = f.ActiveStateName, prev;
                    int fid = f.GetInstanceID();
                    _wPrev.TryGetValue(fid, out prev);
                    _wPrev[fid] = cur;
                    if (active != null) break;
                    if (cur == "fire") active = f;
                    else if (prev == "on" && (cur == "hit" || cur == "wait")) { active = f; fresh = true; }
                    break;
                }
            }
            if (active == null) { _wSwing = null; _wDone = false; return; }
            if (active != _wSwing || fresh) { _wSwing = active; _wDone = false; }
            if (_wDone) return;
            float dmg = SwingDamage(active);
            if (float.IsNaN(dmg)) return;

            int n = Physics.SphereCastNonAlloc(_wCam.position, 0.02f, _wCam.forward, _wHits, 1.8f, WheelSwingMask, QueryTriggerInteraction.Collide);
            if (n <= 0) return;
            Array.Sort(_wHits, 0, n, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
            Transform wheel = null; Vector3 at = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var col = _wHits[i].collider;
                if (col == null) continue;
                var root = col.transform.root;
                if (root == _wCam.root || root.name == "Player") continue;   // the player's own body / arms
                var w = FittedWheelAt(col.transform);
                if (w != null) { wheel = w; at = _wHits[i].point; break; }
                if (!col.isTrigger) return;          // something solid first (a body panel, a door, the ground): the game's own cast handles that
            }
            if (wheel == null) return;
            _wDone = true;
            float mult = Mathf.Max(0f, Plugin.WheelDamageMultiplier.Value);
            float before = PartCondition(wheel);
            foreach (var f in wheel.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "Bodypart") continue;
                var v = f.FsmVariables.FindFsmFloat("Damage");
                if (v == null) break;
                v.Value = dmg * mult;
                f.SendEvent("Damage");
                Integration.PartHit(wheel.gameObject, at == Vector3.zero ? wheel.position : at, Mathf.Abs(v.Value));
                if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Hit: " + active.gameObject.name + " (melee, fitted wheel) -> " + wheel.name + ", condition " + before.ToString("0.0") + " " + v.Value.ToString("0.0") + " (x" + mult.ToString("0.#") + ")");
                break;
            }
            if (Plugin.WheelPopOff.Value && before > 0f) _meleeWheels.Add(new MeleeWheel { Part = wheel, Before = before, Dir = _wCam.forward, Frame = Time.frameCount });
        }

        // (1.5.3) The wheel a collider belongs to: the wheel item itself (tag vehPart on a hinge_wheel*), or anything else under a wheel
        // hinge - the solid sphere collider of hinge_wheel*/wheel_hub (the game's AddSphereCollider, about the tyre's size, part of the
        // FRAME) wraps the fitted wheel, so a swing meets it before the tyre's own trigger and 1.5.1 stopped there as "solid first".
        // That hub now counts as the wheel on its hinge. Another part on the way up (a fender, a plate) = not a wheel.
        private static Transform FittedWheelAt(Transform t)
        {
            for (var a = t; a != null; a = a.parent)
            {
                if (a.CompareTag("vehPart"))
                    return a.parent != null && a.parent.name.StartsWith("hinge_wheel", StringComparison.Ordinal) ? a : null;
                if (a.name.StartsWith("hinge_wheel", StringComparison.Ordinal))
                {
                    for (int i = 0; i < a.childCount; i++) if (a.GetChild(i).CompareTag("vehPart")) return a.GetChild(i);
                    return null;                     // a bare hub: no wheel fitted
                }
            }
            return null;
        }

        // the weapon's "hit" state value (SetFsmFloat Bodypart.Damage, old_knife -12); a weapon with a Reload FSM is a gun
        private static float SwingDamage(PlayMakerFSM attack)
        {
            float d;
            if (_wDamage.TryGetValue(attack.GetInstanceID(), out d)) return d;
            d = float.NaN;
            bool gun = false;
            foreach (var x in attack.GetComponents<PlayMakerFSM>()) if (x != null && x.FsmName == "Reload") { gun = true; break; }
            if (!gun)
                foreach (var st in attack.Fsm.States)
                {
                    if (st.Name != "hit") continue;
                    var acts = st.Actions;
                    if (acts == null || acts.Length == 0) { st.LoadActions(); acts = st.Actions; }
                    if (acts != null)
                        foreach (var a in acts)
                        {
                            var sf = a as SetFsmFloat;
                            if (sf != null && sf.fsmName != null && sf.fsmName.Value == "Bodypart" && sf.variableName != null && sf.variableName.Value == "Damage" && sf.setValue != null) { d = sf.setValue.Value; break; }
                        }
                    break;
                }
            _wDamage[attack.GetInstanceID()] = d;
            return d;
        }

        private static float PartCondition(Transform part)
        {
            foreach (var f in part.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null || (f.FsmName != "Condition" && f.FsmName != "Repair") || f.Fsm == null || !f.Fsm.Initialized) continue;
                if (OwningPart(f.transform) != part) continue;
                var v = f.FsmVariables.FindFsmFloat("Condition");
                if (v != null) return v.Value;
            }
            return -1f;
        }

        private static void CheckMeleeWheels()
        {
            for (int i = _meleeWheels.Count - 1; i >= 0; i--)
            {
                var m = _meleeWheels[i];
                if (Time.frameCount <= m.Frame) continue;       // the Damage event lands the same frame; look the frame after
                _meleeWheels.RemoveAt(i);
                if (m.Part == null || !m.Part.CompareTag("vehPart")) continue;
                float c = PartCondition(m.Part);
                if (c >= 0f && c <= 0f) PopOff(m.Part, m.Dir);
            }
        }

        public static void Tick()
        {
            float dt = Time.deltaTime;
            Snapshot();
            try { SwingAtFittedWheels(); } catch (Exception e) { Plugin.Log.LogError("Tracers melee (fitted wheels): " + e); _wParent = null; }
            if (_meleeWheels.Count > 0) { try { CheckMeleeWheels(); } catch (Exception e) { Plugin.Log.LogError("Tracers melee: " + e); _meleeWheels.Clear(); } }
            if (dt <= 0f) { if (!_drawnPaused) { Draw(); _drawnPaused = true; } return; }   // paused: the mesh is drawn once and left as it is
            _drawnPaused = false;
            if (Plugin.LeadTargets.Value) TrackPlayer(dt);
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                try { Step(ref s, dt); }
                catch (Exception e) { s.Alive = false; Plugin.Log.LogError("Tracers: bullet dropped: " + e); }
                if (s.Alive) _shots[i] = s; else _shots.RemoveAt(i);
            }
            if (Time.unscaledTime >= _nextSweep) { _nextSweep = Time.unscaledTime + 30f; Sweep(); }
            if (_pushes.Count > 0)
            {
                foreach (var p in _pushes) if (p.Key != null) p.Key.AddForce(p.Value, ForceMode.VelocityChange);
                _pushes.Clear();
            }
            if (_popped.Count > 0 && Time.frameCount >= _poppedFrame)
            {
                // CheckTag adds the Rigidbody a frame or two after de_Attach
                for (int i = _popped.Count - 1; i >= 0; i--)
                {
                    var go = _popped[i].Key;
                    var rb = go != null ? go.GetComponent<Rigidbody>() : null;
                    if (rb != null && go.transform.parent == null) { rb.AddForce(_popped[i].Value, ForceMode.VelocityChange); _popped.RemoveAt(i); _forceFree.Remove(go); }
                    else if (go == null) { _popped.RemoveAt(i); _forceFree.RemoveWhere(g => g == null); }
                    else if (Time.frameCount > _poppedFrame + 30 && !_forceFree.Contains(go)) _popped.RemoveAt(i);
                    else if (Time.frameCount > _poppedFrame + 30)
                    {
                        _forceFree.Remove(go);
                        // the part's CheckTag didn't free it: do it the way it would
                        go.transform.SetParent(null, true);
                        if (rb == null) rb = go.AddComponent<Rigidbody>();
                        rb.isKinematic = false;
                        rb.AddForce(_popped[i].Value, ForceMode.VelocityChange);
                        _popped.RemoveAt(i);
                    }
                }
            }
            Draw();
        }

        // caches keyed by dead NPCs would otherwise hold their objects until the next scene load
        private static readonly List<int> _dead = new List<int>();
        private static void Sweep()
        {
            _dead.Clear();
            foreach (var kv in _shooters) if (kv.Value.Owner == null) _dead.Add(kv.Key);
            foreach (var k in _dead) _shooters.Remove(k);
            _dead.Clear();
            foreach (var kv in _heads) if (kv.Value == null) _dead.Add(kv.Key);
            foreach (var k in _dead) _heads.Remove(k);
            _dead.Clear();
            foreach (var kv in _health) if (kv.Value.Root == null) _dead.Add(kv.Key);
            foreach (var k in _dead) _health.Remove(k);
            // instance-id keyed yes/no caches: cheap to rebuild, so they are simply emptied every sweep instead of growing all session
            _counts.Clear(); _isPart.Clear(); _headMul.Clear(); _carRoots.Clear(); _isMelee.Clear(); _creature.Clear();
            _forceFree.RemoveWhere(g => g == null);
            SweepOrbs();
        }

        private static void Step(ref Shot s, float dt)
        {
            float move = Mathf.Min(s.Speed * dt, s.Range - s.Travelled);
            if (move <= 0f) { s.Alive = false; return; }
            // NPC bullets have a thickness: the player's body is two capsules only 0.34-0.40 m wide (head at the camera), so a
            // hairline that visibly passes through your face misses the collider by centimetres. Player bullets stay a hairline.
            float radius = s.Gun == null ? _npcRadius : 0f;
            // NPC bullet at the player: the virtual hitbox decides the hit on the player, physics only the obstructions in front of it
            CapsuleCollider vcap = s.Gun == null && s.Target != null ? s.Cap : null;
            if (vcap != null && !vcap.enabled) { vcap = PlayerCapsule(s.Target); if (vcap != null) s.Cap = vcap; }     // the game swapped capsules (crouch): re-find, once
            float vt = float.MaxValue; bool vhead = false;
            if (vcap != null && !PlayerHit(s.Pos, s.Dir, move, radius, s.Target, vcap, out vt, out vhead)) vt = float.MaxValue;
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(s.Pos, radius, s.Dir, _hits, move, s.Layers, QueryTriggerInteraction.Collide)
                : Physics.RaycastNonAlloc(s.Pos, s.Dir, _hits, move, s.Layers, QueryTriggerInteraction.Collide);
            if (n >= _hits.Length)
            {
                // buffer full: NonAlloc returns the first N found, not the nearest N - take the guaranteed-nearest hit alone
                RaycastHit one;
                n = Physics.Raycast(s.Pos, s.Dir, out one, move, s.Layers, QueryTriggerInteraction.Ignore) ? 1 : 0;
                if (n == 1) _hits[0] = one;
            }
            else if (n > 1) Array.Sort(_hits, 0, n, HitDistance.Instance);
            for (int k = 0; k < n; k++)
            {
                var h = _hits[k];
                var col = h.collider;
                if (col == null || !Counts(col)) continue;
                var tr = col.transform;
                if (vcap != null && tr.IsChildOf(s.Target.transform)) continue;      // the real capsules: the virtual hitbox handles the player
                if (vcap != null && h.distance > vt) break;                          // the player is hit before this obstruction
                if (s.Passed > 0 && WentThrough(ref s, tr)) continue;                  // a part it already went through (its other colliders, backfaces)
                if (s.Gun != null)
                {
                    if (Ignored(tr, s.ShooterRoot != null ? s.ShooterRoot.transform : null, s.Player)) continue;
                    float pd = s.Travelled + h.distance;
                    float pf = Falloff(ref s, pd) * s.DmgMult;
                    if (Penetrates(ref s, col, h, (s.Gun.Damage != null ? s.Gun.Damage.Value : 0f) * pf)) continue;
                    PlayerHit(ref s, h, pf);
                    MetalSparks(col, h.point, h.normal);
                    HitWorld(ref s, col, h.point, (s.Gun.Damage != null ? s.Gun.Damage.Value : 0f) * pf);   // vehicle part rules
                    s.Pos = h.point; s.Travelled = pd; s.Alive = false;
                    return;
                }
                if (s.ShooterRoot != null && tr.IsChildOf(s.ShooterRoot.transform)) continue;        // own body / own car
                bool onTarget = s.Target != null && tr.IsChildOf(s.Target.transform);
                bool detectable = (s.DetectLayers & (1 << col.gameObject.layer)) != 0;
                if (!onTarget && detectable) continue;      // other creatures don't stop a vanilla shot either
                float dist = s.Travelled + h.distance;
                float falloff = Falloff(ref s, dist) * s.DmgMult;
                if (!onTarget && Penetrates(ref s, col, h, s.Damage * falloff)) continue;
                if (onTarget) HitTarget(ref s, h.point, s.Damage * falloff, col.isTrigger ? col.gameObject : s.Target, dist);   // a head trigger: its own Bodypart (x2)
                else if (s.Impact != null && IsCorpse(col.gameObject))     // (1.5.2) an NPC's bullet in a corpse: the shooter's own blood impact, not dust
                {
                    UnityEngine.Object.Instantiate(s.Impact, h.point, Quaternion.identity);   // as HitTarget does
                }
                else { WorldImpact(col, h.point, h.normal, s.Dir); HitWorld(ref s, col, h.point, s.Damage * falloff); }
                s.Pos = h.point;
                s.Travelled = dist;
                s.Alive = false;
                return;
            }
            if (vcap != null && vt < float.MaxValue)
            {
                float dist = s.Travelled + vt;
                float falloff = Falloff(ref s, dist) * s.DmgMult;
                float mult = vhead ? _headMult : 1f;
                if (vhead && Plugin.VerboseLog.Value) Plugin.Verbose("Tracers: headshot on " + s.Target.name + " at " + dist.ToString("0.0") + " m");
                HitTarget(ref s, s.Pos + s.Dir * vt, s.Damage * falloff * mult, vhead && s.Head != null ? s.Head : s.Target, dist);
                s.Pos += s.Dir * vt;
                s.Travelled = dist;
                s.Alive = false;
                return;
            }
            s.Pos += s.Dir * move;
            s.Travelled += move;
            if (s.Travelled >= s.Range - 1e-3f) s.Alive = false;
        }

        // ---------- the player's hitbox for NPC bullets ----------
        // The game's player collider is two slim physics capsules (r 0.17 / 0.20) with the camera on their axis and a trigger sphere
        // for the head. Tracers aimed at the player use their own shapes instead, built each frame from the tallest real capsule so a
        // crouch is followed: a body capsule (feet to neck, [Tracers] PlayerBodyRadius) and a head sphere ([Tracers] PlayerHeadRadius)
        // at the top. A bullet hits whichever it reaches first, once.
        // the object that carries the body capsule: the target itself or its nearest ancestor (the player's "head" child -> Player)
        private static GameObject BodyRootOf(GameObject target)
        {
            for (var tr = target.transform; tr != null; tr = tr.parent)
                if (tr.CompareTag("Player") && PlayerCapsule(tr.gameObject) != null) return tr.gameObject;
            return null;    // NPCs, animals: their real colliders
        }

        private static readonly Dictionary<int, GameObject> _heads = new Dictionary<int, GameObject>();
        private static GameObject HeadOf(GameObject root)
        {
            GameObject h;
            int id = root.GetInstanceID();
            if (_heads.TryGetValue(id, out h) && h != null) return h;
            h = null;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                if (tr != root.transform && tr.name == "head")
                    foreach (var f in tr.GetComponents<PlayMakerFSM>())
                        if (f.FsmName == "Bodypart") { h = tr.gameObject; break; }
            _heads[id] = h;
            return h;
        }

        private static CapsuleCollider PlayerCapsule(GameObject target)
        {
            CapsuleCollider best = null;
            foreach (var c in target.GetComponents<CapsuleCollider>())
                if (c.enabled && !c.isTrigger && (best == null || c.height > best.height)) best = c;
            return best;
        }

        // segment p0 + d*t (0..len) vs the virtual shapes; returns the nearest hit distance along the bullet
        private static bool PlayerHit(Vector3 p0, Vector3 d, float len, float thick, GameObject target, CapsuleCollider cap, out float t, out bool head)
        {
            t = 0f; head = false;
            var tr = cap.transform;
            Vector3 up = cap.direction == 1 ? tr.up : cap.direction == 0 ? tr.right : tr.forward;
            float sc = Mathf.Abs(cap.direction == 1 ? tr.lossyScale.y : cap.direction == 0 ? tr.lossyScale.x : tr.lossyScale.z);
            Vector3 center = tr.TransformPoint(cap.center);
            float half = Mathf.Max(0f, cap.height * sc * 0.5f);
            Vector3 feet = center - up * half, top = center + up * half;
            float headR = _headR, bodyR = _bodyR;
            Vector3 headC = top - up * headR;
            Vector3 neck = headC - up * headR;
            // body capsule: its rounded top ends exactly at the neck, so the head zone belongs to the head alone
            Vector3 bodyLo = feet + up * bodyR, bodyHi = neck - up * bodyR;
            if (Vector3.Dot(bodyHi - bodyLo, up) < 0f) bodyHi = bodyLo;         // crouched very low: a ball
            float th, tb;
            bool hh = RaySphere(p0, d, len, headC, headR + thick, out th);
            bool hb = SegCapsule(p0, d, len, bodyLo, bodyHi, bodyR + thick, out tb);
            if (!hh && !hb) return false;
            if (hh && (!hb || th <= tb)) { t = th; head = true; } else t = tb;
            return true;
        }

        private static bool RaySphere(Vector3 p0, Vector3 d, float len, Vector3 c, float r, out float t)
        {
            t = 0f;
            Vector3 m = p0 - c;
            float b = Vector3.Dot(m, d), cc = Vector3.Dot(m, m) - r * r;
            if (cc > 0f && b > 0f) return false;
            float disc = b * b - cc;
            if (disc < 0f) return false;
            t = -b - Mathf.Sqrt(disc);
            if (t < 0f) t = 0f;            // starts inside
            return t <= len;
        }

        // closest approach between the bullet segment and the capsule axis a..b (Ericson, Real-Time Collision Detection 5.1.9)
        private static bool SegCapsule(Vector3 p0, Vector3 d, float len, Vector3 a, Vector3 b, float r, out float t)
        {
            Vector3 p1 = p0 + d * len;
            Vector3 d1 = p1 - p0, d2 = b - a, rr = p0 - a;
            float A = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, rr);
            float s, u;
            if (A <= 1e-8f && e <= 1e-8f) { s = u = 0f; }
            else if (A <= 1e-8f) { s = 0f; u = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, rr);
                if (e <= 1e-8f) { u = 0f; s = Mathf.Clamp01(-c / A); }
                else
                {
                    float bb = Vector3.Dot(d1, d2), den = A * e - bb * bb;
                    s = den != 0f ? Mathf.Clamp01((bb * f - c * e) / den) : 0f;
                    u = (bb * s + f) / e;
                    if (u < 0f) { u = 0f; s = Mathf.Clamp01(-c / A); }
                    else if (u > 1f) { u = 1f; s = Mathf.Clamp01((bb - c) / A); }
                }
            }
            Vector3 c1 = p0 + d1 * s, c2 = a + d2 * u;
            if ((c1 - c2).sqrMagnitude > r * r) { t = 0f; return false; }
            // back up from the closest approach to the surface along the bullet (entry point), never before the segment start
            float along = s * len;
            float gap = Mathf.Sqrt(Mathf.Max(0f, r * r - (c1 - c2).sqrMagnitude));
            t = Mathf.Max(0f, along - gap);
            return true;
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance Instance = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }

        // exactly the vanilla attack state: impact effect at the point, <target>/Bodypart.Damage = damage, SendEvent Damage to the target
        private static void HitTarget(ref Shot s, Vector3 point, float damage, GameObject target, float dist)
        {
            if (s.Impact != null) UnityEngine.Object.Instantiate(s.Impact, point, Quaternion.identity);
            if (target == null || Mathf.Abs(damage) < 0.01f) return;
            if (col_isHead(target) && !IsPlayerObj(target)) damage *= HeadFactor(target);
            var fsms = target.GetComponents<PlayMakerFSM>();
            foreach (var f in fsms)
                if (f.FsmName == "Bodypart")
                {
                    var v = f.FsmVariables.GetFsmFloat("Damage");
                    if (v != null) v.Value = damage;
                }
            float before = Plugin.HitLog.Value ? HealthOf(target) : 0f;
            foreach (var f in fsms) if (s.EventFsm == null || f.FsmName == s.EventFsm) f.SendEvent(s.EventName);
            Integration.Hurt(target, s.ShooterRoot);
            if (Plugin.HitLog.Value)
                Plugin.Log.LogInfo("Hit: " + (s.ShooterRoot != null ? s.ShooterRoot.name : "?") + " -> " + RootOf(target.transform).name + "/" + target.name + " at "
                    + dist.ToString("0.0") + " m, damage " + damage.ToString("0.0")
                    + (float.IsNaN(before) ? "" : ", Health " + before.ToString("0.0") + " -> " + HealthOf(target).ToString("0.0")));
        }

        // obstruction: a vehicle part loses condition, a bolted metal plate may come off
        // ---------- impact effects ----------
        // The player's own world hit (vanilla): RangedHit_Effect (sparks + its own sound) created twice - by the Attack hit state and again
        // by the HitEffect FSM - at hitPoint with rotation Euler(hitNormal), and 400 N along the camera forward on the thing hit.
        // NPC bullets that miss did nothing at all in vanilla; here they get exactly the same look. MetalImpact (an unused asset-store
        // prefab in the build: sparks, smoke, a bullet decal) is added on vehicle parts and metal plates when the game has it loaded.
        private static GameObject _rangedHit, _metalImpact;
        private static bool _effectsLooked;

        private static void FindEffects()
        {
            if (_effectsLooked) return;
            _effectsLooked = true;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.scene.IsValid()) continue;          // prefabs only, not scene instances
                if (_rangedHit == null && go.name == "RangedHit_Effect") _rangedHit = go;
                else if (_metalImpact == null && go.name == "MetalImpact" && go.transform.parent == null) _metalImpact = go;
                if (_rangedHit != null && _metalImpact != null) break;
            }
            Plugin.Verbose("Tracers: impact effects " + (_rangedHit != null ? "RangedHit_Effect" : "none") + (_metalImpact != null ? " + MetalImpact" : ""));
        }

        private static void WorldImpact(Collider col, Vector3 point, Vector3 normal, Vector3 dir)
        {
            if (!Plugin.ImpactEffects.Value) return;
            FindEffects();
            if (_rangedHit != null)
            {
                var rot = Quaternion.Euler(normal);                       // what the vanilla CreateObject does with {hitNormal}
                UnityEngine.Object.Instantiate(_rangedHit, point, rot);
                UnityEngine.Object.Instantiate(_rangedHit, point, rot);   // vanilla spawns it twice (hit state + HitEffect FSM)
            }
            var rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic && !OnCar(col, rb)) rb.AddForceAtPosition(dir * 400f, point, ForceMode.Force);   // loose things only: doors / hoods don't swing
            MetalSparks(col, point, normal);
        }

        private static void MetalSparks(Collider col, Vector3 point, Vector3 normal)
        {
            if (!Plugin.MetalSparks.Value) return;
            FindEffects();
            if (_metalImpact == null) return;
            if (!IsCarMetal(col.transform)) return;
            var fx = UnityEngine.Object.Instantiate(_metalImpact, point + normal * 0.01f, Quaternion.LookRotation(normal));
            float k = Mathf.Clamp(Plugin.MetalSparksScale.Value, 0.05f, 4f);
            if (Mathf.Abs(k - 1f) > 0.001f)
            {
                // the asset-store prefab is sized for a demo scene: scale the whole effect (sizes, speeds, the decal) through the hierarchy
                foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                fx.transform.localScale *= k;
            }
            UnityEngine.Object.Destroy(fx, 4f);                            // the prefab has no auto-destroy of its own
        }

        // Any part of a car (attached parts, the frame/body itself, loose parts) except wheels. A car = a root carrying NWH's VehicleController
        // (looked up by name, no reference to the NWH assembly); cached per root.
        private static readonly Dictionary<int, bool> _carRoots = new Dictionary<int, bool>();
        private static bool IsCarMetal(Transform t)
        {
            if (Creature(t) != null) return false;          // (1.0.1) a creature riding in a car (Apocapatrol crews): flesh, not metal
            bool onCar = false;
            for (var p = t; p != null; p = p.parent)
            {
                string n = p.name;
                if (n.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;                                   // a wheel item, its hinge, the hub collider: rubber, no sparks
                if (!onCar && (p.CompareTag("vehPart") || p.CompareTag("vehPartRemoved"))) onCar = true;
            }
            if (onCar) return true;
            var root = t.root;
            int id = root.GetInstanceID();
            bool car;
            if (!_carRoots.TryGetValue(id, out car)) { car = root.GetComponent("VehicleController") != null; _carRoots[id] = car; }
            return car;
        }

        private static void HitWorld(ref Shot s, Collider col, Vector3 point, float damage)
        {
            Transform part = null;
            for (var t = col.transform; t != null; t = t.parent)
                if (t.CompareTag("vehPart")) { part = t; break; }
            if (part == null) return;


            if (!Plugin.VehicleDamage.Value) return;
            float pct = Mathf.Abs(damage) / Mathf.Max(1f, Plugin.VehicleDamagePer1.Value);   // [Tracers] VehicleDamagePer1 bullet damage = 1 % condition
            bool wheel = IsWheel(col.transform, part);
            if (wheel) pct *= Mathf.Max(0f, Plugin.WheelDamageMultiplier.Value);
            if (pct <= 0f) return;
            if (s.Gun != null) Integration.PartHit(part.gameObject, point, pct);
            bool broke = false;
            foreach (var f in part.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "Condition" && f.FsmName != "Repair") continue;
                if (f.Fsm == null || !f.Fsm.Initialized) continue;
                if (OwningPart(f.transform) != part) continue;     // not a nested part's FSM
                var v = f.FsmVariables.GetFsmFloat("Condition");
                if (v == null) continue;
                float before = v.Value;
                v.Value = Mathf.Max(0f, v.Value - pct);
                if (before > 0f && v.Value <= 0f) broke = true;
            }
            if (wheel && broke && Plugin.WheelPopOff.Value) PopOff(part, s.Dir);
        }

        // A wheel shot to 0 jumps off the car: the wrench's de_Attach recipe (layer Item + tag vehPartRemoved -> the part's CheckTag FSM
        // unparents it and adds a Rigidbody a frame or two later), then a kick up and along the bullet. Works with Apocapatrol's crews too
        // (they switch the de_Attach FSMs off, but this doesn't go through them). If CheckTag never frees it, it is freed by hand.
        // ---------- (1.5.0) penetration of car parts ----------
        // A bullet hitting a car part rolls, by gun type, whether it goes through: glass windshields, grid windshields / the wire plate (the
        // gaps), doors / hoods / trunks (sheet metal) and bolted-on metal plates (armour). Through: sparks, the part takes its condition
        // damage, the bullet flies on with less damage (glass/grid x0.8, sheet x0.6, armour x0.4) and can hit whoever is behind; at most 2
        // parts. Engines, radiators, bumpers, wheels, frames stop every bullet. Replaces knocking plates off (wheels still pop off at 0).
        private enum Mat { None, Glass, Grid, Sheet, Armour }
        //                                   Pistol Smg Rifle Sniper Shotgun Crossbow  (% to pass)
        private static readonly float[] PassGlass  = { 80f, 85f, 95f, 100f, 60f, 70f };
        private static readonly float[] PassGrid   = { 50f, 50f, 55f, 60f, 40f, 30f };
        private static readonly float[] PassSheet  = { 30f, 35f, 70f, 90f, 10f, 20f };
        private static readonly float[] PassArmour = { 0f, 5f, 20f, 50f, 0f, 0f };
        private static readonly Dictionary<string, Mat> _partMat = new Dictionary<string, Mat>();
        private static Mat MatOf(string partName)
        {
            Mat m;
            if (_partMat.TryGetValue(partName, out m)) return m;
            string n = partName.ToLowerInvariant();
            int k = n.IndexOf(" ("); if (k > 0) n = n.Substring(0, k);
            if (n.StartsWith("windshield_1_glass")) m = Mat.Glass;
            else if (n.StartsWith("windshield_2_grid") || n.StartsWith("wire_plate")) m = Mat.Grid;
            else if (n.StartsWith("metal_plate")) m = Mat.Armour;
            else if (n.Contains("door") || n.Contains("hood") || n.Contains("trunk")) m = Mat.Sheet;   // door_car_*, junker_door_FL, poloska_door_engine, *_hood*, *_trunk
            else m = Mat.None;
            _partMat[partName] = m;
            return m;
        }

        private static bool WentThrough(ref Shot s, Transform t)
        {
            var part = OwningPart(t);
            if (part == null) return false;
            int id = part.GetInstanceID();
            return id == s.Passed0 || id == s.Passed1;
        }

        private static bool Penetrates(ref Shot s, Collider col, RaycastHit h, float damage)
        {
            if (s.Passed >= 2) return false;
            var part = OwningPart(col.transform);
            if (part == null) return false;
            Mat m = MatOf(part.name);
            if (m == Mat.None) return false;
            float[] t = m == Mat.Glass ? PassGlass : m == Mat.Grid ? PassGrid : m == Mat.Sheet ? PassSheet : PassArmour;
            int ki = (int)s.Kind; if (ki < 0 || ki >= t.Length) ki = 0;
            if (UnityEngine.Random.Range(0f, 100f) >= t[ki]) return false;
            MetalSparks(col, h.point, h.normal);
            HitWorld(ref s, col, h.point, damage);           // the part still takes its condition damage
            s.DmgMult *= m == Mat.Armour ? 0.4f : m == Mat.Sheet ? 0.6f : 0.8f;
            int id = part.GetInstanceID();
            if (s.Passed == 0) s.Passed0 = id; else s.Passed1 = id;
            s.Passed++;
            if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Hit: " + (s.Orb ? "monster orb" : s.Kind + " bullet") + " goes through " + part.name + " (" + m + "), damage x" + s.DmgMult.ToString("0.00"));
            return true;
        }

        // a rigidbody that belongs to a car (an attached part: door, hood, trunk on its hinge; the car body itself) - bullets don't push it
        private static bool OnCar(Collider col, Rigidbody rb)
        {
            if (OwningPart(col.transform) != null) return true;
            if (rb.GetComponent<Joint>() != null && rb.transform.parent != null) return true;
            var root = col.transform.root;
            int id = root.GetInstanceID();
            bool car;
            if (!_carRoots.TryGetValue(id, out car)) { car = root.GetComponent("VehicleController") != null; _carRoots[id] = car; }
            return car;
        }

        private static void PopOff(Transform part, Vector3 dir) { PopOff(part, dir, "shot off its car (condition 0)"); }
        private static void PopOff(Transform part, Vector3 dir, string why)
        {
            part.gameObject.layer = 9;
            try { part.tag = "vehPartRemoved"; } catch (Exception e) { Plugin.Verbose("Tracers: wheel tag: " + e.Message); }
            Vector3 d = dir; d.y = 0f; d = d.sqrMagnitude > 0.001f ? d.normalized : Vector3.zero;
            _popped.Add(new KeyValuePair<GameObject, Vector3>(part.gameObject, d * 2.5f + Vector3.up * 4f));
            _forceFree.Add(part.gameObject);
            _poppedFrame = Time.frameCount + 2;
            if (Plugin.VerboseLog.Value) Plugin.Verbose("Tracers: " + part.name + " " + why);
        }

        // a wheel / tyre part: the hit collider or any object up to (and including) its part is named like one
        private static bool IsWheel(Transform t, Transform part)
        {
            for (; t != null; t = t.parent)
            {
                string n = t.name;
                if (n.IndexOf("steering", StringComparison.OrdinalIgnoreCase) >= 0) return false;     // a steering wheel is not a wheel
                if (n.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (t == part) break;
            }
            return false;
        }

        // (1.0.1) The creature a collider belongs to. Apocapatrol seats its crews under the car (sitPos / Apocapatrol.PassengerPos), so
        // transform.root of a driver's collider is the car, not the NPC: the car's VehicleController made the driver "car metal" (sparks),
        // and HealthOf / the HUD target / HitLog read the car instead of the person. Walks up from the collider, never past a vehicle part
        // (tag vehPart / vehPartRemoved) or a car root (VehicleController): the first object with a Health FSM is the creature (NPC root);
        // without one (a ragdoll corpse, the player's parts) the highest object with a Bodypart FSM or on the NPC layer (10). null = not a creature.
        private sealed class CreatureRef { public Transform From; public Transform Root; public bool Found; }
        private static readonly Dictionary<int, CreatureRef> _creature = new Dictionary<int, CreatureRef>();
        private static Transform Creature(Transform t)
        {
            if (t == null) return null;
            int id = t.GetInstanceID();
            CreatureRef cr;
            if (_creature.TryGetValue(id, out cr) && cr.From == t && (cr.Root == null ? !cr.Found : true)) return cr.Root;
            Transform health = null, body = null;
            for (var p = t; p != null; p = p.parent)
            {
                if (p.CompareTag("vehPart") || p.CompareTag("vehPartRemoved") || p.GetComponent("VehicleController") != null) break;
                bool bp = false, hp = false;
                foreach (var f in p.GetComponents<PlayMakerFSM>())
                {
                    if (f == null) continue;
                    if (f.FsmName == "Health") hp = true;
                    else if (f.FsmName == "Bodypart") bp = true;
                }
                if (hp) { health = p; break; }
                if (bp || p.gameObject.layer == 10) body = p;
            }
            var root = health != null ? health : body;
            _creature[id] = new CreatureRef { From = t, Root = root, Found = root != null };
            return root;
        }
        // the creature's root when the collider is part of one, else the hierarchy root as before
        private static Transform RootOf(Transform t)
        {
            var c = Creature(t);
            return c != null ? c : t.root;
        }

        private static Transform OwningPart(Transform t)
        {
            if (Creature(t) != null) return null;           // (1.0.1) a creature seated under a car part is never the part
            for (; t != null; t = t.parent) if (t.CompareTag("vehPart")) return t;
            return null;
        }

        // ---------- drawing: one mesh, camera-facing quads ----------
        private static GameObject _drawGo;
        private static Mesh _mesh;
        private static Material _mat;
        private static readonly List<Vector3> _v = new List<Vector3>();
        private static readonly List<Color> _c = new List<Color>();
        private static readonly List<int> _t = new List<int>();

        private static bool _drawnEmpty, _drawnPaused;
        private static void Draw()
        {
            if (_shots.Count == 0 && _drawnEmpty) return;      // nothing to draw and the mesh is already empty
            _drawnEmpty = _shots.Count == 0;
            if (_drawGo == null)
            {
                _drawGo = new GameObject("Gunplay.Tracers") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_drawGo);
                _mesh = new Mesh { name = "Tracers" };
                _mesh.MarkDynamic();
                _drawGo.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = _drawGo.AddComponent<MeshRenderer>();
                var sh = Shader.Find("Sprites/Default");
                _mat = new Material(sh) { name = "TracerLine", renderQueue = 3100 };
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            float glow = Mathf.Max(0f, Plugin.TracerGlow.Value);
            _mat.color = new Color(glow, glow, glow, 1f);

            _v.Clear(); _c.Clear(); _t.Clear();
            var cam = Camera.main;
            if (cam != null && _shots.Count > 0)
            {
                Vector3 eye = cam.transform.position;
                Color tc = Plugin.TracerColor.Value, bc = Plugin.BoltColor.Value;
                float tw = Plugin.TracerWidth.Value, tl = Plugin.TracerLength.Value;
                float bw = Plugin.BoltWidth.Value, bl = Plugin.BoltLength.Value;
                bool tracers = Plugin.TracersDraw.Value, mine = Plugin.PlayerGunTracers.Value;
                foreach (var s in _shots)
                {
                    if (!tracers && !mine && !s.Bolt) continue;
                    if (!s.Bolt && (!tracers || (s.Gun != null && !mine))) continue;      // [Gunplay] Tracers / PlayerGunTracers (bolts always show)
                    float len = Mathf.Min(s.Bolt ? bl : tl, s.Travelled);
                    if (len <= 0.01f) continue;
                    Vector3 head = s.Pos, tail = s.Pos - s.Dir * len;
                    Vector3 side = Vector3.Cross(s.Dir, (head - eye).normalized);
                    if (side.sqrMagnitude < 1e-6f) continue;
                    side = side.normalized * ((s.Bolt ? bw : tw) * 0.5f);
                    Color head_c = s.Bolt ? bc : tc, tail_c = head_c;
                    tail_c.a = s.Bolt ? head_c.a : 0f;           // tracers fade toward the tail, bolts are solid
                    int b = _v.Count;
                    _v.Add(tail - side); _v.Add(tail + side); _v.Add(head + side); _v.Add(head - side);
                    _c.Add(tail_c); _c.Add(tail_c); _c.Add(head_c); _c.Add(head_c);
                    _t.Add(b); _t.Add(b + 1); _t.Add(b + 2); _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
                }
            }
            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetTriangles(_t, 0);
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            }
        }
    }
}
