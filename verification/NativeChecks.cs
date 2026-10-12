using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gunplay;
using Gunplay.Ammo;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

[BepInPlugin("com.denis.gunplay.ammo.verifier", "Gunplay ammo verifier", "1.0")]
[BepInDependency(Plugin.GUID)]
public class MagazineProbe : BaseUnityPlugin
{
    internal static string Root;
    void Awake()
    {
        string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-magazine-test-root="));
        if (arg == null) return;
        Root = Path.GetFullPath(arg.Substring("-magazine-test-root=".Length)); Directory.CreateDirectory(Root);
        new Harmony("gunplay.nativechecks").PatchAll(typeof(MagazineProbe).Assembly);
        try { NativeMagazineChecks.Run(); }
        catch (Exception e) { File.AppendAllText(Path.Combine(Root, "report.txt"), "FAIL: " + e + "\n"); }
        Application.Quit();
    }
    [HarmonyPatch(typeof(ES3Settings), "get_FullPath")]
    class IsolateSaves
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ES3Settings __instance, ref string __result)
        { if (__instance.location != ES3.Location.File) return true; __result = Path.Combine(Root, Path.GetFileName(__instance.path)); return false; }
    }
    [HarmonyPatch(typeof(PlayMakerFSM), "OnEnable")]
    class BlockWorld
    {
        static bool Prefix(PlayMakerFSM __instance)
        { return __instance.Fsm != null && __instance.Fsm.Initialized && PerGunAmmo.ArmModel(__instance.gameObject); }
    }
}

internal static class NativeMagazineChecks
{
    static PlayMakerFSM handler, main, akms, redmark;
    static GameObject a, b, c, d;
    static readonly List<string> Report = new List<string>();
    static void Check(bool pass, string message)
    { if (!pass) throw new Exception(message); Report.Add("PASS: " + message); File.WriteAllLines(Path.Combine(MagazineProbe.Root, "report.txt"), Report); }
    static FsmInt Int(string name, int value) { var v = new FsmInt { Value = value }; v.SetName(name); return v; }
    static FsmFloat Float(string name, float value) { var v = new FsmFloat { Value = value }; v.SetName(name); return v; }
    static FsmBool Bool(string name, bool value) { var v = new FsmBool { Value = value }; v.SetName(name); return v; }
    static FsmString Str(string name, string value) { var v = new FsmString { Value = value }; v.SetName(name); return v; }
    static FsmGameObject Obj(string name, GameObject value) { var v = new FsmGameObject { Value = value }; v.SetName(name); return v; }
    static PlayMakerFSM Fsm(GameObject go, string name, string first, params string[] others)
    {
        var f = go.AddComponent<PlayMakerFSM>(); f.Fsm.Name = name;
        f.Fsm.States = new[] { first }.Concat(others).Select(n => new FsmState(f.Fsm) { Name = n, Actions = new FsmStateAction[0], Transitions = new FsmTransition[0] }).ToArray();
        f.Fsm.GlobalTransitions = new FsmTransition[0]; f.Fsm.StartState = first;
        f.Fsm.Init(f); f.Fsm.SetState(first); PlayMakerFSM.FsmList.Add(f); return f;
    }
    static void Action(PlayMakerFSM f, string state, FsmStateAction action)
    { var s = f.Fsm.States.First(x => x.Name == state); s.Actions = new[] { action }; var es = action as ES3PlayMaker.ActionBase; if (es != null) es.errorMessage = ""; action.Init(s); }
    static GameObject Item(string name, string type)
    { var go = new GameObject(name); var f = Fsm(go, "weaponType", "off"); f.FsmVariables.StringVariables = new[] { Str("weaponType", type) }; f.FsmVariables.Reinitialize(); return go; }
    static PlayMakerFSM Model(Transform arm, string type, int rounds, bool hammer)
    {
        var go = new GameObject(type); go.transform.SetParent(arm);
        var reload = Fsm(go, "Reload", "idle", "checkAmmo", "anim");
        reload.FsmVariables.IntVariables = new[] { Int("ammo_in_gun", rounds), Int("ammo", 0) }; reload.FsmVariables.Reinitialize();
        Action(reload, "checkAmmo", new GetFsmInt { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = GameObject.Find("__GameManager__") }, fsmName = "Ammo", variableName = "ammo_762", storeValue = reload.FsmVariables.GetFsmInt("ammo") });
        reload.Fsm.States.First(s => s.Name == "checkAmmo").Transitions = new[] { new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("next"), ToState = "anim" } };
        var attack = Fsm(go, "Attack", "on"); attack.FsmVariables.BoolVariables = new[] { Bool("hammer", hammer) }; attack.FsmVariables.Reinitialize();
        go.SetActive(false); return reload;
    }
    static int Rounds(PlayMakerFSM f) { return f.FsmVariables.GetFsmInt("ammo_in_gun").Value; }
    static bool Hammer(PlayMakerFSM f) { return PerGunAmmo.Find(f.gameObject, "Attack").FsmVariables.GetFsmBool("hammer").Value; }
    static void Select(GameObject item, PlayMakerFSM model)
    {
        handler.Fsm.SetState("Deactivate");
        akms.gameObject.SetActive(false); redmark.gameObject.SetActive(false);
        handler.FsmVariables.GetFsmGameObject("Item").Value = item;
        handler.FsmVariables.GetFsmString("weaponType").Value = model.gameObject.name;
        handler.Fsm.SetState(model.gameObject.name);
    }
    static ES3Settings Disk(string file) { return new ES3Settings(file) { location = ES3.Location.File }; }
    static ES3Settings Cache(string file) { return new ES3Settings(file) { location = ES3.Location.Cache }; }
    static void Save(string file)
    {
        ES3Settings.defaultSettings.path = file; ES3Settings.defaultSettings.location = ES3.Location.Cache;
        main.Fsm.SetState("wait"); main.Fsm.SetState("SaveGame");
        ES3.StoreCachedFile(Disk(file)); main.Fsm.SetState("isPlay");
    }
    static void Load(string file)
    {
        main.Fsm.SetState("LoadGame"); handler.Fsm.SetState("off");
        ES3Settings.defaultSettings.path = file; ES3Settings.defaultSettings.location = ES3.Location.Cache;
        ES3.CacheFile(Disk(file));
        // LoadGame entry marks the session; LoadAll is normally a later action in that state.
        var action = new ES3PlayMaker.LoadAll { key = "global", loadFsmVariables = true, loadGlobalVariables = false, errorMessage = "" };
        action.Init(main.Fsm.States.First(s => s.Name == "LoadGame")); action.Enter();
        main.Fsm.SetState("isPlay");
    }
    internal static void Run()
    {
        Check(Math.Abs(AmmoSettings.HoldSeconds.Value - 0.45f) < 0.001f, "Existing Gunplay ammo preference wins over legacy Unloader config");
        Check(Math.Abs(AmmoSettings.PhaseTimeout.Value - 5.5f) < 0.001f && AmmoSettings.FallbackKey.Value == UnityEngine.InputSystem.Key.X,
            "Unset Gunplay ammo preferences import legacy timeout and fallback key");
        bool legacyLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(s => s.GetType("Apocaunloader.Runner", false) != null);
        Check(AmmoSettings.Active && AmmoSettings.LegacySuppressed == legacyLoaded, "Gunplay owns ammo with or without standalone Unloader installed");
        Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Owners.Contains(AmmoSettings.LegacyGuid)), "Standalone ammo action hooks cannot process the same ammo twice");
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(UnityEngine.SceneManagement.SceneManager.CreateScene("Gunplay verification"));
        var gm = new GameObject("__GameManager__"); var ammo = Fsm(gm, "Ammo", "idle");
        ammo.FsmVariables.IntVariables = new[] { Int("ammo_762", 100), Int("ammo_762_capacity", 500) }; ammo.FsmVariables.Reinitialize();
        main = Fsm(new GameObject("SaveLoadGame"), "SaveLoadGame", "isPlay", "wait", "SaveGame", "LoadGame");
        Action(main, "SaveGame", new ES3PlayMaker.SaveAll { key = "global", saveFsmVariables = true, saveGlobalVariables = false });
        ArmorChecks(gm);
        var arm = new GameObject("WeaponsArm").transform;
        akms = Model(arm, "akms", 30, false); redmark = Model(arm, "redmark_m11", 4, true);
        handler = Fsm(new GameObject("Weapons"), "WeaponInHand", "off", "Deactivate", "akms", "redmark_m11");
        handler.FsmVariables.GameObjectVariables = new[] { Obj("Item", null) };
        handler.FsmVariables.StringVariables = new[] { Str("weaponType", "") }; handler.FsmVariables.Reinitialize();
        foreach (var model in new[] { akms, redmark })
            Action(handler, model.gameObject.name, new ActivateGameObject { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = model.gameObject }, activate = true, recursive = false, everyFrame = false, resetOnExit = false });
        a = Item("akms(Clone)1", "akms"); b = Item("akms(Clone)2", "akms"); c = Item("redmark_m11(Clone)3", "redmark_m11"); d = Item("redmark_m11(Clone)4", "redmark_m11");
        Select(a, akms); Check(Rounds(akms) == 30, "Native activation migrates the first AKMS magazine");
        akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 17;
        Select(b, akms); Check(Rounds(akms) == 0, "Same model's second physical gun starts empty");
        akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 9;
        Select(a, akms); Check(Rounds(akms) == 17, "Native switching restores the first gun after firing the second");
        for (int i = 0; i < 50; i++) { Select(b, akms); if (Rounds(akms) != 9) throw new Exception("Second magazine changed"); Select(a, akms); if (Rounds(akms) != 17) throw new Exception("First magazine changed"); }
        Check(true, "100 actual FSM switches retain 17/9 rounds without duplication");
        // Logical slot borrowing reparents physical items; the selected item identity remains authoritative.
        b.transform.SetParent(new GameObject("Slot 1").transform);
        Select(b, akms); b.transform.SetParent(new GameObject("Slot 5").transform);
        Select(a, akms); Select(b, akms); Check(Rounds(akms) == 9, "Apocapocket-style host/logical slot movement preserves magazine ownership");
        Select(c, redmark); Check(Rounds(redmark) == 4 && Hammer(redmark), "First chambered weapon inherits ammo and hammer");
        Select(d, redmark); Check(Rounds(redmark) == 0 && !Hammer(redmark), "Duplicate chambered weapon starts empty and uncocked");
        redmark.FsmVariables.GetFsmInt("ammo_in_gun").Value = 2;
        Select(c, redmark); Check(Rounds(redmark) == 4 && Hammer(redmark), "Independent hammer state is restored before native model activation");
        Select(a, akms); AmmoRunner.Unloader.TryStart(); Check(AmmoRunner.Unloader.Busy, "Native unload begins on the tracked gun");
        Select(b, akms); Check(!AmmoRunner.Unloader.Busy && Rounds(akms) == 9 && ammo.FsmVariables.GetFsmInt("ammo_762").Value == 117, "Switch during unload transfers only outgoing gun's 17 rounds");
        AmmoRunner.Unloader.Tick(); Select(a, akms); Check(Rounds(akms) == 0, "Unloaded first gun remains empty after returning");
        Select(b, akms); Save("SaveGame1.es3");
        string saved = ES3.Load<string>(PerGunAmmo.SaveKey, Disk("SaveGame1.es3"));
        Check(saved.Contains("M|akms(Clone)2|akms|9|0") || MagazineLedger.Decode(saved).Draw(b.name, "akms", new Magazine()).Rounds == 9, "Real Easy Save file contains each item's magazine");
        ES3.DeleteKey(PerGunAmmo.SaveKey, Cache("SaveGame1.es3")); // simulate a rebuilt cache in Apocasaver
        ES3.StoreCachedFile(Disk("SaveGame1.es3"));
        Check(ES3.Load<string>(PerGunAmmo.SaveKey, Disk("SaveGame1.es3")) == saved, "Final cache flush restores magazine snapshot after cache rebuilding");
        akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 1; Select(c, redmark);
        Load("SaveGame1.es3"); Select(b, akms); Check(Rounds(akms) == 9, "LoadAll restores saved magazine instead of latest shared model value");
        Select(c, redmark); Check(Rounds(redmark) == 4 && Hammer(redmark), "Real Easy Save load restores chamber state");
        Select(b, akms); akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 6; Save("SaveGame2.es3");
        Check(ES3.Load<string>(PerGunAmmo.SaveKey, Disk("SaveGame1.es3")) == saved, "Save-as leaves source slot's magazine snapshot unchanged");
        Load("SaveGame1.es3"); Select(b, akms); Check(Rounds(akms) == 9, "Switching save slots restores their independent gun counts");
        Load("SaveGame2.es3"); Select(b, akms); Check(Rounds(akms) == 6, "Destination slot retains its own six-round magazine");
        Select(a, akms); akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 5; AmmoRunner.Unloader.TryStart();
        int reserve = ammo.FsmVariables.GetFsmInt("ammo_762").Value;
        Load("SaveGame1.es3"); Select(b, akms); AmmoRunner.Unloader.Tick();
        Check(Rounds(akms) == 9 && !AmmoRunner.Unloader.Busy && reserve == ammo.FsmVariables.GetFsmInt("ammo_762").Value, "Loading during unload cancels the old transaction without altering incoming ammo");
        Select(c, redmark); UnityEngine.Object.DestroyImmediate(b); b = Item("akms(Clone)2", "akms");
        Select(b, akms); Check(Rounds(akms) == 9, "Recreated/dropped item recovers its magazine using the save-stable name");
        AmmoSettings.Enabled.Value = false;
        Select(a, akms); Select(b, akms);
        Check(Rounds(akms) == 9 && BackpackOverflow.Enabled, "Disabling unloading retains per-gun magazines and backpack overflow protection");
        AmmoSettings.Enabled.Value = true;
        Plugin.TracersEnabled.Value = false;
        Select(a, akms); Select(b, akms);
        Check(Rounds(akms) == 9 && BackpackOverflow.Enabled, "Disabling projectile simulation does not disable ammo preservation");
        Plugin.TracersEnabled.Value = true;
        main.Fsm.SetState("wait");
        akms.gameObject.SetActive(true); redmark.gameObject.SetActive(true);
        PerGunAmmo.Tick(); main.Fsm.SetState("SaveGame"); ES3.StoreCachedFile(Disk("SaveGame1.es3"));
        main.Fsm.SetState("isPlay"); Select(c, redmark); Select(b, akms);
        Check(Rounds(akms) == 9 && MagazineLedger.Decode(ES3.Load<string>(PerGunAmmo.SaveKey, Disk("SaveGame1.es3"))).Draw(c.name, "redmark_m11", new Magazine()).Rounds == 4,
            "Save-time activation of every arm model cannot acquire another item's magazine");
        // A vanilla save has the native global wrapper and type pools, with no plugin metadata.
        ES3.Save<ES3PlayMaker.PMDataWrapper>("global", new ES3PlayMaker.PMDataWrapper(main.Fsm, true, false), Cache("SaveGame4.es3"));
        ES3.StoreCachedFile(Disk("SaveGame4.es3")); Load("SaveGame4.es3");
        akms.FsmVariables.GetFsmInt("ammo_in_gun").Value = 12;
        Select(a, akms); Check(Rounds(akms) == 12, "Legacy save without metadata migrates the native pool exactly once");
        Select(b, akms); Check(Rounds(akms) == 0, "A new/legacy character cannot inherit another session's saved magazine");
        ES3.Save<string>(PerGunAmmo.SaveKey, "corrupt", Cache("SaveGame3.es3"));
        // Seed global with a valid native wrapper, without passing through the magazine save hook.
        ES3.Save<ES3PlayMaker.PMDataWrapper>("global", new ES3PlayMaker.PMDataWrapper(main.Fsm, true, false), Cache("SaveGame3.es3"));
        ES3.StoreCachedFile(Disk("SaveGame3.es3")); Load("SaveGame3.es3");
        Check(PerGunAmmo.Suspended && ES3.Load<string>(PerGunAmmo.SaveKey, Disk("SaveGame3.es3")) == "corrupt", "Corrupt metadata suspends overrides and preserves original save data");
        Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains(Plugin.GUID)) == 17, "All seventeen production Harmony patches install on shipped game assemblies");
    }

    static void ArmorChecks(GameObject gm)
    {
        var config = BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.GUID].Instance.Config;
        var keys = config.Keys.ToArray();
        int ammoSection = Array.FindIndex(keys, k => k.Section == "Ammo"), debugSection = Array.FindIndex(keys, k => k.Section == "Debug");
        int ammoOverflow = Array.FindIndex(keys, k => k.Section == "Ammo" && k.Key == "DropBackpackOverflow");
        int armorOverflow = Array.FindIndex(keys, k => k.Section == "Ammo" && k.Key == "DropArmorOverflow");
        Check(ammoSection >= 0 && ammoSection < debugSection && armorOverflow == ammoOverflow + 1 && AmmoSettings.DropArmorOverflow.Value,
            "Ammo is registered above Debug with enabled armor overflow directly after ammo overflow");
        var armor = Fsm(gm, "ArmorBackpack", "armor_backpack");
        armor.FsmVariables.FloatVariables = new[] { Float("armor", 250), Float("armor_capacity", 300) }; armor.FsmVariables.Reinitialize();
        var count = armor.FsmVariables.GetFsmFloat("armor"); var cap = armor.FsmVariables.GetFsmFloat("armor_capacity");
        var clamp = new FloatClamp { floatVariable = count, minValue = 0, maxValue = cap, everyFrame = true };
        Action(armor, "armor_backpack", clamp); clamp.OnUpdate();
        Check(count.Value == 250, "Initial armor observation establishes baseline without spawning plates");
        var slot = Fsm(new GameObject("Armor_Item"), "SlotEmptyFull", "empty", "full");
        Action(slot, "full", new SetFsmFloat { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = gm },
            fsmName = "ArmorBackpack", variableName = "armor_capacity", setValue = 100, everyFrame = false });
        slot.Fsm.SetState("full"); clamp.OnUpdate();
        Check(cap.Value == 100 && count.Value == 250, "Native smaller-armor equip retains overflow when plate spawning is unavailable");
        var template = new GameObject("armorplate(Clone)900");
        var id = Fsm(template, "ID", "off"); id.FsmVariables.StringVariables = new[] { Str("ID", "armorplate") }; id.FsmVariables.Reinitialize();
        var quantity = Fsm(template, "Quantity", "off"); quantity.FsmVariables.IntVariables = new[] { Int("Quantity", 99) }; quantity.FsmVariables.Reinitialize();
        template.AddComponent<BoxCollider>().isTrigger = true;
        var body = template.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
        template.SetActive(false);
        var counter = Fsm(new GameObject("itemNameID"), "itemNameID", "off"); counter.FsmVariables.IntVariables = new[] { Int("intName", 1000) }; counter.FsmVariables.Reinitialize();
        var list = new GameObject("NewGO_ArrayList").AddComponent<PlayMakerArrayListProxy>(); list.referenceName = "ArrayList_Items";
        cap.Value = 99; clamp.OnUpdate();
        Check(count.Value == 99 && list.arrayList.Count == 3, "Native armor clamp drops three save-registered plates after a further downgrade");
        var plates = list.arrayList.Cast<GameObject>().ToArray();
        Check(plates.Select(p => p.name).Distinct().Count() == 3 && plates.All(p => p.name.StartsWith("armorplate(Clone)") && p.activeInHierarchy
            && p.transform.parent == null && p.layer == 9 && !p.GetComponent<Rigidbody>().isKinematic && p.GetComponent<Rigidbody>().useGravity
            && p.GetComponent<Collider>().enabled && !p.GetComponent<Collider>().isTrigger
            && PerGunAmmo.Find(p, "Quantity").FsmVariables.GetFsmInt("Quantity").Value == 1),
            "Returned armor plates are distinct, collectible single items with restored world physics");
        clamp.OnUpdate(); Check(list.arrayList.Count == 3, "Repeated native clamp cannot duplicate returned armor plates");
        cap.Value = 200; count.Value = 149.999f; clamp.OnUpdate(); cap.Value = 100; clamp.OnUpdate();
        Check(count.Value == 100 && list.arrayList.Count == 3, "49.999 excess armor creates no plate");
        cap.Value = 200; count.Value = 150; clamp.OnUpdate(); cap.Value = 100; clamp.OnUpdate();
        Check(count.Value == 100 && list.arrayList.Count == 4, "Exactly 50 excess armor creates one plate");
        cap.Value = 200; count.Value = 80; clamp.OnUpdate(); cap.Value = 100; clamp.OnUpdate();
        Check(count.Value == 80 && list.arrayList.Count == 4, "Armor downgrade within current protection creates no plate");
        AmmoSettings.DropArmorOverflow.Value = false; count.Value = 250; cap.Value = 50; clamp.OnUpdate();
        Check(count.Value == 50 && list.arrayList.Count == 4, "Disabling armor overflow restores native clamping without affecting ammo settings");
        AmmoSettings.DropArmorOverflow.Value = true; ArmorOverflow.Reset();
        cap.Value = 300; count.Value = 250; clamp.OnUpdate();
        main.Fsm.SetState("LoadGame"); cap.Value = 100; clamp.OnUpdate(); main.Fsm.SetState("isPlay");
        Check(list.arrayList.Count == 4, "Character loading resets armor-capacity baseline and never spawns migration plates");
    }
}
