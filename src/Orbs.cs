using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Gunplay
{
    // (1.1.0) Monster projectiles stop at walls.
    //
    // Vanilla (sharedassets1 read 2026-10-06): monsters without a gun (Arachnid, Teacher, ...) shoot from their "Damage Ranged" FSM with
    // CreateObject <projectile> at their spawnPos child (Arachnid and Teacher: sanity_ranged_attack_small; the Teacher fires three in a row).
    // The projectile is a kinematic Rigidbody + trigger SphereCollider (r 0.15) on layer 2 (Ignore Raycast). Its "Move" FSM runs
    // TranslatePosition in FixedUpdate (Rigidbody.MovePosition by a fixed vector per second). Its own FSM deals the damage on a trigger
    // with a Player/Scrapyard/Coyotes/Herbivore/Carnivore tag, and WaitDelete removes it after a while. Nothing ever stops it at a wall.
    //
    // Here: CreateObject's postfix registers each projectile created by a gunless shooter's Damage Ranged. A prefix on
    // TranslatePosition.OnFixedUpdate takes over that projectile's step: it computes the same movement, then sweeps the projectile's
    // sphere along it before moving.
    // - A wall, the ground, an item, a door or a vehicle comes first (the bullets' obstruction layers 0/8/9/11/14): the car-part
    //   penetration roll a pistol bullet gets. Through glass/grid/sheet/armour: the part takes condition damage and the projectile flies
    //   on with the bullet's damage cut. Otherwise the impact effect plays, a car part takes condition damage, and the projectile is gone.
    // - A body comes first (Player / Actor layer): the projectile is moved into it, so the game's own trigger deals the hit.
    // The shooter's own colliders are ignored. Everything else (damage, delete timer, sanity) stays vanilla.
    internal static partial class Tracers
    {
        private sealed class Orb
        {
            public Rigidbody Body;
            public GameObject Go;
            public Transform Shooter;
            public string ShooterName;
            public float Radius;
            public Shot S;                    // penetration state, shared with the bullets' rules (Kind = Pistol)
            public FsmFloat DamageVar;        // the projectile's own SetFsmFloat Bodypart.Damage value (looked up on first need)
            public bool DamageLooked;
        }

        private static readonly Dictionary<int, Orb> _orbs = new Dictionary<int, Orb>();
        private const int OrbBlockers = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14);    // = the RaySensor ObstructedByLayers 19201
        private const int OrbBodies = (1 << 6) | (1 << 10);                                         // Player, Actor
        private static readonly RaycastHit[] _orbHits = new RaycastHit[32];

        // CreateObject.OnEnter postfix (via Plugin.AfterCreateObject)
        internal static void OrbSpawned(CreateObject action, GameObject obj)
        {
            try
            {
                if (obj == null || !Plugin.TracersEnabled.Value || !Plugin.OrbsHitWalls.Value) return;
                var fsm = action.Fsm;
                if (fsm == null || fsm.Name != "Damage Ranged") return;
                var owner = fsm.GameObject;
                if (owner == null) return;
                if (Info(fsm, owner).IsGun) return;               // guns and crossbows fire Gunplay bullets
                var rb = obj.GetComponent<Rigidbody>();
                if (rb == null) return;                           // not something TranslatePosition can move
                float r = 0f;
                var sc = obj.GetComponent<SphereCollider>();
                if (sc != null)
                {
                    Vector3 ls = obj.transform.lossyScale;
                    r = sc.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                }
                var o = new Orb
                {
                    Body = rb, Go = obj,
                    Shooter = owner.transform.root,
                    ShooterName = owner.name,
                    Radius = Mathf.Clamp(r, 0f, 1f),
                    S = new Shot { Kind = Kind.Pistol, DmgMult = 1f, Alive = true, Orb = true },
                };
                _orbs[obj.GetInstanceID()] = o;
                if (Plugin.VerboseLog.Value) Plugin.Verbose("Orbs: " + owner.name + " fires " + obj.name + " (r " + o.Radius.ToString("0.00") + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("Orbs (spawn): " + e); }
        }

        // TranslatePosition.OnFixedUpdate prefix: false = we moved it (or stopped it)
        public static bool BeforeTranslatePosition(TranslatePosition __instance)
        {
            if (_orbs.Count == 0) return true;
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null) return true;
                var go = fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go == null) return true;
                Orb o;
                int id = go.GetInstanceID();
                if (!_orbs.TryGetValue(id, out o)) return true;
                if (o.Body == null || !Plugin.OrbsHitWalls.Value) { _orbs.Remove(id); return true; }

                // the vanilla movement (TranslatePosition.DoMovePosition)
                Vector3 v = !__instance.vector.IsNone ? __instance.vector.Value
                    : new Vector3(__instance.x.Value, __instance.y.Value, __instance.z.Value);
                if (!__instance.x.IsNone) v.x = __instance.x.Value;
                if (!__instance.y.IsNone) v.y = __instance.y.Value;
                if (!__instance.z.IsNone) v.z = __instance.z.Value;
                if (__instance.perSecond) v *= Time.deltaTime;
                if (__instance.space != Space.World) v = go.transform.TransformVector(v);

                bool alive = OrbStep(o, ref v);
                if (alive) o.Body.MovePosition(o.Body.position + v);
                else _orbs.Remove(id);
                if (!__instance.everyFrame) __instance.Finish();
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Orbs: " + e);
                return true;
            }
        }

        // true = keep flying (v may have been shortened), false = stopped and removed
        private static bool OrbStep(Orb o, ref Vector3 v)
        {
            float len = v.magnitude;
            if (len < 1e-5f) return true;
            Vector3 dir = v / len;
            Vector3 p = o.Body.position;
            int mask = OrbBlockers | OrbBodies;
            int n = o.Radius > 0f
                ? Physics.SphereCastNonAlloc(p, o.Radius, dir, _orbHits, len, mask, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(p, dir, _orbHits, len, mask, QueryTriggerInteraction.Ignore);
            if (n >= _orbHits.Length)
            {
                RaycastHit one;
                n = Physics.Raycast(p, dir, out one, len, mask, QueryTriggerInteraction.Ignore) ? 1 : 0;
                if (n == 1) _orbHits[0] = one;
            }
            else if (n > 1) Array.Sort(_orbHits, 0, n, HitDistance.Instance);
            for (int k = 0; k < n; k++)
            {
                var h = _orbHits[k];
                var col = h.collider;
                if (col == null || col.isTrigger) continue;
                if (h.distance <= 0f && h.point == Vector3.zero) continue;            // already overlapping at the start: no direction to judge
                var tr = col.transform;
                if (o.Shooter != null && tr.IsChildOf(o.Shooter)) continue;          // its own body
                if ((OrbBodies & (1 << col.gameObject.layer)) != 0 || Creature(tr) != null)
                {
                    // a body is first: into it, so the game's own trigger deals the hit (or, for a creature it doesn't hurt, it flies on)
                    v = dir * Mathf.Min(len, h.distance + o.Radius + 0.05f);
                    return true;
                }
                if (o.S.Passed > 0 && WentThrough(ref o.S, tr)) continue;           // a part it already went through
                o.S.Dir = dir;
                float dmg = OrbDamage(o) * o.S.DmgMult;
                float before = o.S.DmgMult;
                if (Penetrates(ref o.S, col, h, dmg))
                {
                    if (o.DamageVar != null && before > 0f) o.DamageVar.Value *= o.S.DmgMult / before;   // what is left for whoever is behind
                    continue;
                }
                // stopped
                WorldImpact(col, h.point, h.normal, dir);
                HitWorld(ref o.S, col, h.point, dmg);
                if (Plugin.HitLog.Value) Plugin.Log.LogInfo("Hit: " + o.ShooterName + "'s " + o.Go.name + " stopped by " + col.name + " at " + h.distance.ToString("0.00") + " m");
                Kill(o, p + dir * h.distance);
                return false;
            }
            return true;
        }

        // the projectile's own damage (absolute), for car-part condition and the penetration cut
        private static float OrbDamage(Orb o)
        {
            if (!o.DamageLooked)
            {
                o.DamageLooked = true;
                foreach (var f in o.Go.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.Fsm == null || f.FsmName == "Move") continue;
                    foreach (var st in f.FsmStates)
                    {
                        if (st == null || st.Actions == null) continue;
                        foreach (var a in st.Actions)
                        {
                            var sf = a as SetFsmFloat;
                            if (sf == null || sf.setValue == null || sf.variableName == null || sf.variableName.Value != "Damage") continue;
                            o.DamageVar = sf.setValue;
                            break;
                        }
                        if (o.DamageVar != null) break;
                    }
                    if (o.DamageVar != null) break;
                }
            }
            return o.DamageVar != null ? Mathf.Abs(o.DamageVar.Value) : 10f;
        }

        private static void Kill(Orb o, Vector3 at)
        {
            var go = o.Go;
            if (go == null) return;
            go.transform.position = at;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;   // no trigger in this physics step
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }

        private static void SweepOrbs()
        {
            if (_orbs.Count == 0) return;
            _dead.Clear();
            foreach (var kv in _orbs) if (kv.Value.Go == null || kv.Value.Body == null) _dead.Add(kv.Key);
            foreach (var k in _dead) _orbs.Remove(k);
            _dead.Clear();
        }
    }
}
