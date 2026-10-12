using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Gunplay.Ammo
{
    internal class AmmoRunner : MonoBehaviour
    {
        private bool _held;
        private float _pressStart;
        private bool _consumed;
        internal static Unloader Unloader = new Unloader();

        private void Update()
        {
            try { PerGunAmmo.Tick(); } catch (Exception e) { PerGunAmmo.Fail(e); }
            BackpackOverflow.Tick();
            if (!AmmoSettings.Enabled.Value) { Unloader.Tick(); return; }

            bool down = AmmoSettings.ReloadHeld();
            float now = Time.unscaledTime;

            if (down && !_held)
            {
                _held = true; _pressStart = now; _consumed = false;
            }
            else if (down && _held && !_consumed && now - _pressStart >= AmmoSettings.HoldSeconds.Value)
            {
                _consumed = true;
                if (Time.timeScale > 0f && !Unloader.Busy) Unloader.TryStart();
            }
            else if (!down && _held)
            {
                _held = false;
                if (!_consumed && !Unloader.Busy) { AmmoSettings.TapFrame = Time.frameCount; AmmoSettings.V("Reload tap"); }
            }

            Unloader.Tick();
        }
    }

    /// Preserve ammo before the vanilla per-frame clamp discards it after a capacity reduction.
    internal static class BackpackOverflow
    {
        private static readonly Dictionary<IntClamp, OverflowTransfer> _capacities = new Dictionary<IntClamp, OverflowTransfer>();
        private static Fsm _emptySlot;
        private static int _removalFrame;
        internal static bool Enabled { get { return AmmoSettings.DropBackpackOverflow.Value; } }

        internal static void Reset() { _capacities.Clear(); _emptySlot = null; }

        internal static bool BeforeClamp(IntClamp action)
        {
            var fsm = action.Fsm;
            if (fsm == null || fsm.Name != "Ammo" || fsm.GameObject == null || fsm.GameObject.name != "__GameManager__") return true;
            var count = action.intVariable;
            var max = action.maxValue;
            if (count == null || max == null || !count.Name.StartsWith("ammo_", StringComparison.Ordinal) || max.Name != count.Name + "_capacity") return true;
            if (!Enabled) { _capacities.Remove(action); return true; }

            int capacity = Math.Max(0, max.Value);
            OverflowTransfer state;
            if (!_capacities.TryGetValue(action, out state))
            {
                // Establish a baseline without spilling ammo during scene initialization/save loading.
                _capacities[action] = new OverflowTransfer(capacity);
                return true;
            }
            int retained;
            bool allowClamp = state.BeforeClamp(count.Value, capacity, Time.unscaledTime,
                excess => AmmoBox.Drop(count.Name, excess, fsm.GameObject.transform), out retained);
            if (retained != count.Value)
            {
                int excess = count.Value - retained;
                count.Value = retained;
                AmmoSettings.Log.LogInfo("Backpack capacity reduced: dropped " + excess + " x " + count.Name + "; retained " + count.Value + "/" + capacity);
            }
            // Letting vanilla run here would delete the ammo we could not put into a world item.
            return allowClamp;
        }

        internal static void OnSlotEnter(FsmState state)
        {
            var fsm = state.Fsm;
            if (!Enabled || fsm == null || fsm.Name != "SlotEmptyFull" || fsm.GameObject == null || fsm.GameObject.name != "Backpack_Item") return;
            if (state.Name == "full") { _emptySlot = null; return; }
            var item = fsm.Variables.GetFsmGameObject("item");
            if (state.Name != "empty" || item == null || item.Value == null) return;
            _emptySlot = fsm;
            _removalFrame = Time.frameCount;
        }

        internal static void Tick()
        {
            if (!Enabled) { Reset(); return; }
            if (_emptySlot == null || Time.frameCount <= _removalFrame) return;
            var slot = _emptySlot.GameObject;
            if (slot == null || _emptySlot.ActiveStateName != "empty" || slot.transform.childCount != 0) { _emptySlot = null; return; }
            var take = PlayMakerFSM.FindFsmOnGameObject(slot, "TakeItem");
            // Swapping packs temporarily empties the slot while dropCurrent waits one frame.
            if (take != null && (take.ActiveStateName == "dropCurrent" || take.ActiveStateName == "takeWeapon")) return;

            var gm = GameObject.Find("__GameManager__");
            var backpack = gm != null ? PlayMakerFSM.FindFsmOnGameObject(gm, "Backpack") : null;
            var ammo = gm != null ? PlayMakerFSM.FindFsmOnGameObject(gm, "Ammo") : null;
            var armor = gm != null ? PlayMakerFSM.FindFsmOnGameObject(gm, "ArmorBackpack") : null;
            if (backpack == null || ammo == null || armor == null) return;
            var set = backpack.FsmStates.FirstOrDefault(s => s.Name == "set");
            if (set == null) return;
            // Read vanilla's base capacities instead of duplicating its caliber table.
            foreach (var action in set.Actions.OfType<SetIntValue>())
            {
                if (!action.Enabled || action.intVariable == null || action.intValue == null) continue;
                string name = action.intVariable.Name;
                if (!name.StartsWith("ammo_", StringComparison.Ordinal) || !name.EndsWith("_capacity", StringComparison.Ordinal)) continue;
                var capacity = ammo.FsmVariables.GetFsmInt(name);
                if (capacity != null) capacity.Value = action.intValue.Value;
            }
            var packCapacity = armor.FsmVariables.GetFsmFloat("backpack_capacity");
            var multiply = backpack.FsmVariables.GetFsmFloat("multiply");
            if (packCapacity != null) packCapacity.Value = 0f;
            if (multiply != null) multiply.Value = 1f;
            _emptySlot = null;
            AmmoSettings.V("Backpack removed: restored base ammo capacities");
        }
    }

    /// Drives the unload sequence by replaying the weapon's own Reload FSM events.
    internal class Unloader
    {
        private enum Phase { Idle, WaitAnimFinished, WaitAnimStart, WaitAnimEnd }
        private Phase _phase = Phase.Idle;
        private PlayMakerFSM _reload;
        private GameObject _weapon;
        private PlayMakerFSM _ammoFsm;
        private string _caliber;
        private float _deadline;
        private bool _moved;

        public bool Busy { get { return _phase != Phase.Idle; } }

        public void TryStart()
        {
            if (PerGunAmmo.Suspended) return;
            var reload = FindActiveReloadFsm();
            if (reload == null) { AmmoSettings.V("No active weapon Reload FSM in idle"); return; }
            var inGun = reload.FsmVariables.GetFsmInt("ammo_in_gun");
            if (inGun == null) { AmmoSettings.V("Reload FSM has no ammo_in_gun"); return; }
            if (inGun.Value <= 0) { AmmoSettings.V("Gun already empty"); return; }

            // Caliber variable name from the checkAmmo state's GetFsmInt action.
            string caliber = null;
            var check = FindState(reload, "checkAmmo");
            if (check != null)
                foreach (var a in check.Actions)
                {
                    var g = a as GetFsmInt;
                    if (g != null && g.fsmName != null && g.fsmName.Value == "Ammo") { caliber = g.variableName.Value; break; }
                }
            if (string.IsNullOrEmpty(caliber)) { AmmoSettings.Log.LogWarning("Could not determine caliber for " + reload.gameObject.name); return; }

            var gm = GameObject.Find("__GameManager__");
            var ammoFsm = gm != null ? PlayMakerFSM.FindFsmOnGameObject(gm, "Ammo") : null;
            if (ammoFsm == null || ammoFsm.FsmVariables.GetFsmInt(caliber) == null) { AmmoSettings.Log.LogWarning("Ammo FSM / variable " + caliber + " not found"); return; }

            // The state the game enters after checkAmmo passes: it sends Deactivate/Reloading/animation events.
            FsmState start = null;
            if (check != null)
                foreach (var t in check.Transitions) if (t.EventName == "next") { start = FindState(reload, t.ToState); break; }
            if (start == null) { AmmoSettings.Log.LogWarning("No start state found for " + reload.gameObject.name); return; }

            _reload = reload; _weapon = reload.gameObject; _ammoFsm = ammoFsm; _caliber = caliber; _moved = false;
            bool threePhase = start.Transitions.Any(t => t.EventName == "AnimStart");
            AmmoSettings.Log.LogInfo("Unloading " + _weapon.name + ": " + inGun.Value + " x " + caliber + (threePhase ? " (3-phase)" : ""));

            ReplaySendEvents(start);
            _phase = threePhase ? Phase.WaitAnimStart : Phase.WaitAnimFinished;
            _deadline = Time.unscaledTime + AmmoSettings.PhaseTimeout.Value;
        }

        /// Called from the Harmony hook on Fsm.Event.
        public void OnFsmEvent(Fsm fsm, string evName)
        {
            if (_phase == Phase.Idle || fsm == null || fsm.GameObject != _weapon) return;
            switch (_phase)
            {
                case Phase.WaitAnimFinished: if (evName == "AnimFinished") { AmmoSettings.V("AnimFinished"); Finish(); } break;
                case Phase.WaitAnimStart: if (evName == "AnimStart") { AmmoSettings.V("AnimStart"); AfterAnimStart(); } break;
                case Phase.WaitAnimEnd: if (evName == "AnimEnd") { AmmoSettings.V("AnimEnd"); Finish(); } break;
            }
        }

        public void Tick()
        {
            if (_phase == Phase.Idle) return;
            if (_weapon == null || !_weapon.activeInHierarchy) { AmmoSettings.Log.LogWarning("Weapon vanished during unload; aborting"); Finish(); return; }
            if (Time.unscaledTime < _deadline) return;
            AmmoSettings.Log.LogWarning("Unload phase " + _phase + " timed out; continuing");
            if (_phase == Phase.WaitAnimStart) AfterAnimStart(); else Finish();
        }

        private void AfterAnimStart()
        {
            MoveAmmo();
            // Replay the "empty shell" sound the game plays between the open and close animations.
            foreach (var st in _reload.FsmStates)
            {
                if (st.Name.IndexOf("shell", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var a in st.Actions)
                {
                    var ps = a as PlaySound;
                    if (ps == null || ps.clip == null) continue;
                    var clip = ps.clip.Value as AudioClip;
                    if (clip != null) AudioSource.PlayClipAtPoint(clip, _weapon.transform.position + ps.position.Value, ps.volume.Value);
                }
            }
            // State that waits for AnimEnd sends ReloadEnd (close animation).
            var endState = _reload.FsmStates.FirstOrDefault(s => s.Transitions.Any(t => t.EventName == "AnimEnd"));
            if (endState != null) ReplaySendEvents(endState);
            _phase = Phase.WaitAnimEnd;
            _deadline = Time.unscaledTime + AmmoSettings.PhaseTimeout.Value;
        }

        private void MoveAmmo()
        {
            if (_moved) return;
            _moved = true;
            var inGun = _reload.FsmVariables.GetFsmInt("ammo_in_gun");
            var store = _ammoFsm.FsmVariables.GetFsmInt(_caliber);
            if (inGun == null || store == null) return;
            int n = inGun.Value;
            var capVar = _ammoFsm.FsmVariables.GetFsmInt(_caliber + "_capacity");
            int cap = capVar != null ? capVar.Value : int.MaxValue;
            int space = Math.Max(0, cap - store.Value);
            int toStore = Math.Min(n, space);
            int leftover = n - toStore;

            store.Value += toStore;
            inGun.Value = leftover;
            if (leftover > 0)
            {
                if (AmmoBox.Drop(_caliber, leftover, _weapon.transform)) inGun.Value = 0;
                else AmmoSettings.Log.LogWarning("Could not spawn an ammo box; " + leftover + " rounds left in the gun");
            }
            var mirror = _reload.FsmVariables.GetFsmInt("ammo");
            if (mirror != null) mirror.Value = store.Value;
            if (inGun.Value == 0)
            {
                var attack = PerGunAmmo.Find(_weapon, "Attack");
                var hammer = attack == null ? null : attack.FsmVariables.GetFsmBool("hammer");
                if (hammer != null) hammer.Value = false;
            }
            AmmoSettings.Log.LogInfo("Returned " + toStore + " x " + _caliber + " to backpack (now " + store.Value + "/" + cap + ")" + (leftover > 0 ? ", " + leftover + " dropped as a box" : ""));
        }

        private void Finish()
        {
            MoveAmmo();
            _phase = Phase.Idle; // Event replay can synchronously re-enter this hook.
            var idle = FindState(_reload, "idle");
            if (idle != null && _weapon != null && _weapon.activeInHierarchy) ReplaySendEvents(idle); // MenuOut / Activate: re-enable firing etc.
            _reload = null; _weapon = null; _ammoFsm = null;
        }

        internal void CompleteForSwitch() { if (Busy) Finish(); }
        internal void CancelForLoad() { _phase = Phase.Idle; _reload = null; _weapon = null; _ammoFsm = null; }

        private void ReplaySendEvents(FsmState st)
        {
            foreach (var a in st.Actions)
            {
                var se = a as SendEvent;
                if (se == null || !se.Enabled || se.sendEvent == null) continue;
                AmmoSettings.V("  replay " + st.Name + ": " + se.sendEvent.Name);
                try { _reload.Fsm.Event(se.eventTarget, se.sendEvent); }
                catch (Exception e) { AmmoSettings.Log.LogWarning("SendEvent replay failed: " + e.Message); }
            }
        }

        private static FsmState FindState(PlayMakerFSM f, string name)
        {
            foreach (var s in f.FsmStates) if (s.Name == name) return s;
            return null;
        }

        private static PlayMakerFSM FindActiveReloadFsm()
        {
            foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (f.FsmName != "Reload" || !f.gameObject.activeInHierarchy || !f.enabled) continue;
                if (f.Fsm == null || !f.Fsm.Initialized) continue;
                if (f.FsmVariables.GetFsmInt("ammo_in_gun") == null) continue;
                var p = f.transform.parent;
                bool underArm = false;
                while (p != null) { if (p.name == "WeaponsArm") { underArm = true; break; } p = p.parent; }
                if (!underArm) continue;
                if (f.ActiveStateName != "idle") { AmmoSettings.V(f.gameObject.name + " Reload busy (" + f.ActiveStateName + ")"); continue; }
                return f;
            }
            return null;
        }
    }

    /// Spawns an ammo box item (same save-compatible recipe as Apocaspawner) holding the rounds that
    /// did not fit in the backpack.
    internal static class AmmoBox
    {
        private static readonly Dictionary<string, string> DefaultBoxes = new Dictionary<string, string>
        {
            { "ammo_762", "ammo_box_762mm" }, { "ammo_556", "ammo_box_556mm" }, { "ammo_9mm", "ammo_box_9mm" },
            { "ammo_12gauge", "ammo_box_12gauge" }, { "ammo_20gauge", "ammo_box_20gauge" }, { "ammo_22caliber", "ammo_box_22" },
            { "ammo_3006", "ammo_box_3006" }, { "ammo_arrow", "ammo_arrow" },
            { "ammo_battery", "flashlight_batteries" }, { "ammo_grenade", "grenade" },
        };
        private static readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();

        private static string BoxName(string caliber)
        {
            string d;
            return DefaultBoxes.TryGetValue(caliber, out d) ? d : null;
        }

        private static GameObject FindPrefab(string caliber)
        {
            GameObject cached;
            if (_prefabs.TryGetValue(caliber, out cached) && cached != null) return cached;
            string name = BoxName(caliber);
            if (name == null) return null;
            GameObject asset = null, instance = null;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.transform.parent != null) continue;
                if (go.name != name && !go.name.StartsWith(name + "(Clone)", StringComparison.Ordinal)) continue;
                string fsmName = caliber == "ammo_battery" ? "Batteries" : "Ammo";
                if (!go.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == fsmName)) continue;
                if (!go.scene.IsValid()) { if (go.name == name && asset == null) asset = go; }
                else if (instance == null && go.name.StartsWith(name + "(Clone)", StringComparison.Ordinal)) instance = go;
            }
            var found = asset ?? instance;
            if (found != null) { _prefabs[caliber] = found; AmmoSettings.V("Ammo box template for " + caliber + ": " + found.name + (asset != null ? " (prefab)" : " (scene copy)")); }
            else AmmoSettings.Log.LogWarning("No ammo box prefab named '" + name + "' is loaded");
            return found;
        }

        internal static bool Drop(string caliber, int rounds, Transform at)
        {
            if (rounds <= 0 || at == null) return false;
            var prefab = FindPrefab(caliber);
            if (prefab == null) return false;
            var counterGo = GameObject.Find("itemNameID");
            var cf = counterGo != null ? PlayMakerFSM.FindFsmOnGameObject(counterGo, "itemNameID") : null;
            var counter = cf != null ? cf.FsmVariables.GetFsmInt("intName") : null;
            var reg = GameObject.Find("NewGO_ArrayList");
            var list = reg != null ? reg.GetComponents<PlayMakerArrayListProxy>().FirstOrDefault(p => (p.referenceName ?? "").ToLowerInvariant().Contains("item")) : null;
            if (counter == null || list == null)
            {
                AmmoSettings.Log.LogWarning("Cannot drop " + caliber + ": save registry/counter unavailable; retaining ammo");
                return false;
            }
            var cam = Camera.main != null ? Camera.main.transform : at;
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = at.forward;
            var pos = cam.position + fwd * 0.7f - Vector3.up * 0.3f;
            var created = new List<GameObject>();
            var staging = new GameObject("Gunplay.DropStaging");
            staging.SetActive(false);
            try
            {
                // Grenades are individual items; boxes/arrows/batteries store an ammo count.
                int itemCount = caliber == "ammo_grenade" ? rounds : 1;
                for (int i = 0; i < itemCount; i++)
                {
                    // Configure before activation so Randomize cannot overwrite the returned rounds.
                    var go = UnityEngine.Object.Instantiate(prefab, pos, UnityEngine.Random.rotation, staging.transform);
                    created.Add(go);
                    go.SetActive(false);
                    var fsms = go.GetComponents<PlayMakerFSM>();
                    var rnd = fsms.FirstOrDefault(f => f.FsmName == "Randomize");
                    if (rnd != null) rnd.enabled = false;
                    var ammo = fsms.FirstOrDefault(f => f.FsmName == (caliber == "ammo_battery" ? "Batteries" : "Ammo"));
                    var v = ammo != null ? ammo.FsmVariables.GetFsmInt(caliber == "ammo_battery" ? "batteries" : "ammo") : null;
                    if (v == null) throw new InvalidOperationException(go.name + " has no ammo count variable");
                    v.Value = caliber == "ammo_grenade" ? 1 : rounds;
                    counter.Value += 1;
                    go.name = BoxName(caliber) + "(Clone)" + counter.Value;
                    list.arrayList.Add(go);
                }
                foreach (var go in created)
                {
                    go.transform.SetParent(null, true);
                    go.SetActive(true);
                    var rb = go.GetComponent<Rigidbody>();
                    if (rb != null) { rb.isKinematic = false; rb.velocity = fwd * 1.5f; }
                }
                AmmoSettings.Log.LogInfo("Dropped " + rounds + " x " + caliber + " in " + created.Count + " save-registered item(s)");
                return true;
            }
            catch (Exception ex)
            {
                foreach (var go in created)
                {
                    list.arrayList.Remove(go);
                    go.SetActive(false);
                    UnityEngine.Object.Destroy(go);
                }
                AmmoSettings.Log.LogWarning("Could not drop " + caliber + "; retaining ammo: " + ex.Message);
                return false;
            }
            finally { UnityEngine.Object.Destroy(staging); }
        }
    }

    // ---------------------------------------------------------------------------------
    // Harmony patches
    // ---------------------------------------------------------------------------------

    [HarmonyPatch(typeof(IntClamp), "DoClamp")]
    internal static class AmmoCapacity_Patch
    {
        static bool Prefix(IntClamp __instance) { return BackpackOverflow.BeforeClamp(__instance); }
    }

    [HarmonyPatch(typeof(FsmState), "OnEnter")]
    internal static class BackpackSlot_Patch
    {
        static void Postfix(FsmState __instance) { BackpackOverflow.OnSlotEnter(__instance); }
    }

    /// The game's Reload FSMs poll GetButtonDown("Reload"). We take over that button: it no longer fires on press;
    /// instead we fire the action's event on a short tap (released before HoldSeconds).
    [HarmonyPatch(typeof(GetButtonDown), "OnUpdate")]
    internal static class GetButtonDown_Patch
    {
        private static readonly Dictionary<GetButtonDown, int> _lastTap = new Dictionary<GetButtonDown, int>();

        static bool Prefix(GetButtonDown __instance)
        {
            if (!AmmoSettings.Enabled.Value) return true;
            if (__instance.buttonName == null || __instance.buttonName.Value != "Reload") return true;
            if (AmmoSettings.TapPending())
            {
                int last;
                if (!_lastTap.TryGetValue(__instance, out last) || last != AmmoSettings.TapFrame)
                {
                    _lastTap[__instance] = AmmoSettings.TapFrame;
                    if (__instance.storeResult != null) __instance.storeResult.Value = true;
                    if (__instance.Fsm != null && __instance.sendEvent != null) __instance.Fsm.Event(__instance.sendEvent);
                    return false;
                }
            }
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class Fsm_Event_Patch
    {
        static void Prefix(Fsm __instance, FsmEvent fsmEvent)
        {
            if (fsmEvent == null || !AmmoRunner.Unloader.Busy) return;
            AmmoRunner.Unloader.OnFsmEvent(__instance, fsmEvent.Name);
        }
    }
}
