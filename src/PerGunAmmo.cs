using System;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Gunplay.Ammo
{
    /// Bridges item identity to the reusable arm model before its activation actions run.
    internal static class PerGunAmmo
    {
        internal const string SaveKey = "Apocaunloader.Magazines";
        private static MagazineLedger _ledger = new MagazineLedger();
        private static PlayMakerFSM _reload, _handler, _saveFsm;
        private static GameObject _item;
        private static string _identity, _type, _snapshot, _saveFile;
        private static bool _loading = true, _saving, _faulted;
        private static float _scanAt;

        internal static bool Suspended { get { return _loading || _saving || _faulted; } }

        internal static PlayMakerFSM Find(GameObject go, string name)
        {
            if (go == null) return null;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f.FsmName == name) return f;
            return null;
        }

        internal static bool ArmModel(GameObject go)
        {
            for (var p = go == null ? null : go.transform.parent; p != null; p = p.parent)
                if (p.name == "WeaponsArm") return true;
            return false;
        }

        internal static void SceneChanged()
        {
            _handler = null; _saveFsm = null; _scanAt = 0;
            // LoadAll may precede sceneLoaded. Keep the ledger from that exact load.
            Release();
        }

        private static Magazine Read(PlayMakerFSM reload)
        {
            var rounds = reload.FsmVariables.GetFsmInt("ammo_in_gun");
            var attack = Find(reload.gameObject, "Attack");
            var hammer = attack == null ? null : attack.FsmVariables.GetFsmBool("hammer");
            return new Magazine(rounds == null ? 0 : rounds.Value, hammer != null && hammer.Value);
        }

        private static void Apply(PlayMakerFSM reload, Magazine magazine)
        {
            reload.FsmVariables.GetFsmInt("ammo_in_gun").Value = magazine.Rounds;
            var attack = Find(reload.gameObject, "Attack");
            var hammer = attack == null ? null : attack.FsmVariables.GetFsmBool("hammer");
            if (hammer != null) hammer.Value = magazine.Hammer;
        }

        internal static void Capture()
        {
            if (_reload == null || _identity == null || _faulted || _loading) return;
            _ledger.Capture(_identity, _type, Read(_reload));
        }

        private static void Release()
        { _reload = null; _item = null; _identity = null; _type = null; }

        internal static void Holster(Fsm fsm)
        {
            if (fsm.Name != "WeaponInHand" || Suspended) return;
            AmmoRunner.Unloader.CompleteForSwitch();
            Capture(); Release();
        }

        internal static void Draw(Fsm handler, GameObject model)
        {
            if (Suspended || handler == null || handler.Name != "WeaponInHand" || !ArmModel(model)) return;
            var reload = Find(model, "Reload");
            if (reload == null || reload.FsmVariables.GetFsmInt("ammo_in_gun") == null) return; // melee
            var itemVar = handler.Variables.GetFsmGameObject("Item");
            var item = itemVar == null ? null : itemVar.Value;
            var itemTypeFsm = Find(item, "weaponType");
            var itemType = itemTypeFsm == null ? null : itemTypeFsm.FsmVariables.GetFsmString("weaponType");
            // Only the physical item selected by vanilla owns this model. Save-time activation of all models is ignored.
            if (item == null || itemType == null || itemType.Value != model.name || string.IsNullOrEmpty(item.name)) return;
            if (_item == item && _reload == reload) return;
            AmmoRunner.Unloader.CompleteForSwitch();
            Capture();
            var magazine = _ledger.Draw(item.name, itemType.Value, Read(reload));
            _item = item; _identity = item.name; _type = itemType.Value; _reload = reload; _handler = handler.FsmComponent;
            Apply(reload, magazine);
            AmmoSettings.V("Magazine " + _identity + ": " + magazine.Rounds + " rounds");
        }

        internal static void BeforeState(Fsm fsm, string state)
        {
            if (fsm == null) return;
            if (fsm.Name == "WeaponInHand" && state == "Deactivate") Holster(fsm);
            if (fsm.Name != "SaveLoadGame") return;
            _saveFsm = fsm.FsmComponent;
            if (state == "Start" || state == "setSeed" || state == "LoadGame")
            {
                AmmoRunner.Unloader.CancelForLoad();
                _ledger = new MagazineLedger(); Release();
                _loading = true; _saving = false; _faulted = false; _snapshot = null; _saveFile = null;
            }
            else if (state == "wait" || state == "SaveGame") BeginSave();
        }

        internal static void AfterState(Fsm fsm, string state)
        {
            if (fsm == null || fsm.Name != "SaveLoadGame" || state != "isPlay") return;
            _saveFsm = fsm.FsmComponent;
            _loading = false; _saving = false;
            // Native load restored only the per-type counts. Replace the selected one from our item ledger.
            Release();
            AdoptDrawn();
        }

        private static void AdoptDrawn()
        {
            if (Suspended) return;
            if (_handler == null)
                foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                    if (f.FsmName == "WeaponInHand" && f.gameObject.name == "Weapons") { _handler = f; break; }
            if (_handler == null || _handler.Fsm == null || !_handler.Fsm.Initialized) return;
            // A holstered handler retains its Item variable; require a currently activated gun model.
            string state = _handler.ActiveStateName;
            if (state == "off" || state == "Deactivate" || state == "hands" || state == "Activate" || state.StartsWith("Slot ", StringComparison.Ordinal)) return;
            var type = _handler.FsmVariables.GetFsmString("weaponType");
            if (type == null) return;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                if (f.FsmName == "Reload" && f.gameObject.name == type.Value && f.gameObject.activeInHierarchy && ArmModel(f.gameObject))
                { Draw(_handler.Fsm, f.gameObject); break; }
        }

        internal static void Tick()
        {
            if (_saveFsm == null && Time.unscaledTime >= _scanAt)
            {
                _scanAt = Time.unscaledTime + 1f;
                foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                    if (f.FsmName == "SaveLoadGame") { _saveFsm = f; break; }
                if (_saveFsm != null && _saveFsm.Fsm.Initialized && _saveFsm.ActiveStateName == "isPlay") _loading = false;
            }
            if (Suspended) return;
            if (_reload != null && (_item == null || !_reload.gameObject.activeInHierarchy)) { Capture(); Release(); }
            if (_reload == null && Time.unscaledTime >= _scanAt) { _scanAt = Time.unscaledTime + 1f; AdoptDrawn(); }
        }

        internal static void BeginSave()
        {
            if (_loading || _faulted || _saving) return;
            AmmoRunner.Unloader.CompleteForSwitch(); Capture();
            _snapshot = _ledger.Encode(); _saving = true;
        }

        internal static void SaveGlobal(ES3PlayMaker.SaveAll action)
        {
            if (action.Fsm == null || action.Fsm.Name != "SaveLoadGame" || action.key.Value != "global" || _faulted || _loading) return;
            BeginSave();
            var settings = action.GetSettings();
            _saveFile = settings.path;
            CacheSnapshot(settings);
        }

        private static void CacheSnapshot(ES3Settings settings)
        {
            if (_snapshot == null || string.IsNullOrEmpty(_saveFile)) return;
            var cache = new ES3Settings(settings.path, settings) { location = ES3.Location.Cache };
            ES3.Save<string>(SaveKey, _snapshot, cache);
        }

        internal static void BeforeStore(ES3Settings settings)
        {
            // Also inject into Apocasaver's final rebuilt cache, immediately before its transactional flush.
            if (settings != null && settings.path == _saveFile && !_faulted && !_loading) CacheSnapshot(settings);
        }

        internal static void LoadGlobal(ES3PlayMaker.LoadAll action)
        {
            if (action.Fsm == null || action.Fsm.Name != "SaveLoadGame" || action.key.Value != "global") return;
            AmmoRunner.Unloader.CancelForLoad(); Release();
            _loading = true; _saving = false; _snapshot = null; _saveFile = null; _faulted = false;
            var settings = action.GetSettings();
            _ledger = ES3.KeyExists(SaveKey, settings) ? MagazineLedger.Decode(ES3.Load<string>(SaveKey, settings)) : new MagazineLedger();
        }

        internal static void Fail(Exception error)
        {
            AmmoRunner.Unloader.CancelForLoad();
            _faulted = true;
            AmmoSettings.Log.LogError("Per-gun ammo suspended to preserve save data: " + error);
        }
    }

    [HarmonyPatch(typeof(ActivateGameObject), "OnEnter")]
    internal static class MagazineDrawPatch
    {
        static void Prefix(ActivateGameObject __instance)
        {
            if (__instance.Fsm == null || __instance.Fsm.Name != "WeaponInHand" || !__instance.activate.Value) return;
            try { PerGunAmmo.Draw(__instance.Fsm, __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject)); }
            catch (Exception e) { PerGunAmmo.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(Fsm), "EnterState")]
    internal static class MagazineStatePatch
    {
        static void Prefix(Fsm __instance, FsmState __0)
        { if (__0 != null) try { PerGunAmmo.BeforeState(__instance, __0.Name); } catch (Exception e) { PerGunAmmo.Fail(e); } }
        static void Postfix(Fsm __instance, FsmState __0)
        { if (__0 != null) try { PerGunAmmo.AfterState(__instance, __0.Name); } catch (Exception e) { PerGunAmmo.Fail(e); } }
    }

    [HarmonyPatch(typeof(ES3PlayMaker.SaveAll), "Enter")]
    internal static class MagazineSavePatch
    {
        static void Postfix(ES3PlayMaker.SaveAll __instance)
        { try { PerGunAmmo.SaveGlobal(__instance); } catch (Exception e) { PerGunAmmo.Fail(e); } }
    }

    [HarmonyPatch(typeof(ES3PlayMaker.LoadAll), "Enter")]
    internal static class MagazineLoadPatch
    {
        static void Prefix(ES3PlayMaker.LoadAll __instance)
        { try { PerGunAmmo.LoadGlobal(__instance); } catch (Exception e) { PerGunAmmo.Fail(e); } }
    }

    [HarmonyPatch(typeof(ES3), "StoreCachedFile", new[] { typeof(ES3Settings) })]
    internal static class MagazineStorePatch
    {
        static void Prefix(ES3Settings settings)
        { try { PerGunAmmo.BeforeStore(settings); } catch (Exception e) { PerGunAmmo.Fail(e); } }
    }
}
