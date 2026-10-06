using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gunplay
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.gunplay";
        public const string NAME = "Gunplay";
        public const string VERSION = "1.1.0";
        internal static ManualLogSource Log;
        internal static string Dir;
        internal static ConfigEntry<Color> BoltColor;
        internal static ConfigEntry<float> BoltLength;
        internal static ConfigEntry<float> BoltSpeed;
        internal static ConfigEntry<float> BoltWidth;
        internal static ConfigEntry<float> BossHpPercent;
        internal static ConfigEntry<float> BulletSpeed;
        internal static ConfigEntry<float> CrossbowRange;
        internal static ConfigEntry<float> FullDamageUntil;
        internal static ConfigEntry<float> HeadshotMultiplier;
        internal static ConfigEntry<bool> HitLog;
        internal static ConfigEntry<bool> ImpactEffects;
        internal static ConfigEntry<float> LeadAccuracy;
        internal static ConfigEntry<float> LeadError;
        internal static ConfigEntry<bool> LeadTargets;
        internal static ConfigEntry<float> MaxLeadTime;
        internal static ConfigEntry<int> MaxTracers;
        internal static ConfigEntry<bool> MetalSparks;
        internal static ConfigEntry<float> MetalSparksScale;
        internal static ConfigEntry<bool> NpcAimAtBody;
        internal static ConfigEntry<bool> OrbsHitWalls;
        internal static ConfigEntry<float> NpcHitRadius;
        internal static ConfigEntry<float> NpcShotgunDamage;
        internal static ConfigEntry<float> PistolRange;
        internal static ConfigEntry<float> PlayerBodyRadius;
        internal static ConfigEntry<bool> PlayerGunTracers;
        internal static ConfigEntry<float> PlayerHeadRadius;
        internal static ConfigEntry<bool> PlayerTracers;
        internal static ConfigEntry<float> RifleRange;
        internal static ConfigEntry<float> ShotgunPelletSpread;
        internal static ConfigEntry<int> ShotgunPellets;
        internal static ConfigEntry<float> ShotgunRange;
        internal static ConfigEntry<float> SmgRange;
        internal static ConfigEntry<float> SniperRange;
        internal static ConfigEntry<string> StabSound;
        internal static ConfigEntry<string> StabWeapons;
        internal static ConfigEntry<Color> TracerColor;
        internal static ConfigEntry<float> TracerGlow;
        internal static ConfigEntry<float> TracerLength;
        internal static ConfigEntry<float> TracerWidth;
        internal static ConfigEntry<bool> TracersDraw;
        internal static ConfigEntry<bool> TracersEnabled;
        internal static ConfigEntry<bool> VehicleDamage;
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<float> WheelDamageMultiplier;
        internal static ConfigEntry<bool> WheelPopOff;
        private static ConfigFile _hidden;
        private Harmony _harmony;
        private static GameObject _runner;
        internal static ConfigEntry<float> VehicleDamagePer1;
        private static ConfigEntry<T> H<T>(string section, string key, T value, ConfigDescription description) { return _hidden.Bind(section, key, value, description); }
        private static ConfigEntry<T> H<T>(string section, string key, T value, string description) { return _hidden.Bind(section, key, value, description); }

        private void Awake()
        {
            Log = Logger;
            bool existingConfig = File.Exists(Config.ConfigFilePath);
            Dir = Path.GetDirectoryName(Info.Location);
            _hidden = new ConfigFile(Path.Combine(Paths.ConfigPath, "Gunplay.hidden-settings.not-saved"), false) { SaveOnConfigSet = false };
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            TracersEnabled = Config.Bind("General", "EnableGunplay", true,
                "Real bullets for NPC and player guns: flight time, falloff with distance, headshots and vehicle part damage. Off = the game's own instant hits; NPCAI controls NPC decisions separately.");
            BoltColor = H("Tracers", "BoltColor", new Color(0.85f, 0.72f, 0.5f, 1f), "Crossbow bolt colour (RGBA hex).");
            BoltLength = H("Tracers", "BoltLength", 0.8f, new ConfigDescription("Bolt line length, m.", new AcceptableValueRange<float>(0.1f, 5f)));
            BoltWidth = H("Tracers", "BoltWidth", 0.04f, new ConfigDescription("Bolt line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            BossHpPercent = Config.Bind("Gunplay", "AdjustHumanBossHP", 40f, new ConfigDescription(
                "Health of the human bosses (Duke Ironjaw 1500, Buzzgut 600) in % of the game's own.", new AcceptableValueRange<float>(5f, 300f)));
            BoltSpeed = Config.Bind("Gunplay", "BoltSpeed", 60f, new ConfigDescription("Crossbow bolt speed, m/s.", new AcceptableValueRange<float>(10f, 500f)));
            BulletSpeed = Config.Bind("Gunplay", "BulletSpeed", 250f, new ConfigDescription("Bullet speed, m/s.", new AcceptableValueRange<float>(20f, 2000f)));
            CrossbowRange = Config.Bind("Tracers", "CrossbowRange", 90f, new ConfigDescription("Crossbows, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            FullDamageUntil = H("Tracers", "FullDamageUntil", 50f, new ConfigDescription(
                "Every gun (NPC and yours) does full damage until this % of its range, then the damage falls off to 0 at the range. 0 = falloff from the muzzle (half damage at 50 %).",
                new AcceptableValueRange<float>(0f, 99f)));
            HeadshotMultiplier = Config.Bind("Gunplay", "HeadshotMultiplier", 1.5f, new ConfigDescription(
                "Damage of a bullet in the head - yours on NPCs, NPCs' on you and on each other (replaces the game's own x2 on NPC heads).", new AcceptableValueRange<float>(0.5f, 5f)));
            ImpactEffects = H("Tracers", "ImpactEffects", true,
                "NPC bullets that hit the world show the same impact (sparks, sound) your own hits do; the game showed nothing for their misses.");
            LeadAccuracy = H("NpcAim", "LeadAccuracy", 0.75f, new ConfigDescription(
                "How much of the ideal lead NPCs apply: 1 = perfect prediction, 0 = none. Each NPC rolls a personal skill between half of this and all of it.",
                new AcceptableValueRange<float>(0f, 1f)));
            LeadError = H("NpcAim", "LeadError", 30f, new ConfigDescription("Random error on the NPC's estimate of your speed, +/- %, per shot.", new AcceptableValueRange<float>(0f, 100f)));
            LeadTargets = H("NpcAim", "LeadTargets", true,
                "NPCs aim where a moving target will be when the bullet arrives (the game's hitscan never had to). Changing direction still beats them.");
            MaxLeadTime = H("NpcAim", "MaxLeadTime", 1.5f, new ConfigDescription("Longest flight time NPCs lead for, s (bolts at long range).", new AcceptableValueRange<float>(0f, 5f)));
            MaxTracers = H("Tracers", "MaxTracers", 300, new ConfigDescription("Most bullets in flight at once; shots above this hit instantly (vanilla style) instead.", new AcceptableValueRange<int>(16, 2000)));
            MetalSparks = H("Tracers", "MetalSparks", true,
                "Extra sparks, smoke and a bullet mark on anything that is part of a car - attached parts, the body and frame, loose parts - except wheels (any bullet), using the game's MetalImpact effect.");
            MetalSparksScale = H("Tracers", "MetalSparksScale", 0.25f, new ConfigDescription("Size of the metal sparks effect (1 = the prefab's own, demo-scene size).", new AcceptableValueRange<float>(0.05f, 4f)));
            NpcAimAtBody = H("Tracers", "NpcAimAtBody", true,
                "NPCs aim at the centre of your body. Off = the game's own aim point, your head, which with the game's aim jitter sends many shots over your head.");
            NpcHitRadius = H("Tracers", "NpcHitRadius", 0.05f, new ConfigDescription(
                "Thickness of NPC bullets, m, added to the target's hitbox (your body/head shapes, other targets' colliders). 0 = hairline. Your own bullets are always a hairline.",
                new AcceptableValueRange<float>(0f, 0.5f)));
            NpcShotgunDamage = H("Tracers", "NpcShotgunDamage", 1.7f, new ConfigDescription(
                "Damage multiplier for NPC shotgun pellets (the game's shotgunners do 5-8 per ray x 4 rays per blast, a third of a rifle burst).",
                new AcceptableValueRange<float>(0f, 5f)));
            OrbsHitWalls = Config.Bind("Gunplay", "MonsterProjectilesHitWalls", true,
                "Projectiles of monsters without guns (Arachnid, Teacher and the like) are stopped by walls, terrain and vehicles; car glass, grids, doors and plates let them through as often as a pistol bullet. Off = the game's own projectiles, which fly through everything.");
            PistolRange = Config.Bind("Tracers", "PistolRange", 60f, new ConfigDescription("Pistols/revolvers: damage falls off linearly with distance - half at 50 % of this range, the bullet is gone at 100 %. Metres.", new AcceptableValueRange<float>(5f, 1000f)));
            PlayerBodyRadius = H("Tracers", "PlayerBodyRadius", 0.22f, new ConfigDescription(
                "Your body as NPC bullets see it: a capsule from your feet to your neck with this radius, m (the game's own collider is only 0.17-0.20).",
                new AcceptableValueRange<float>(0.05f, 0.6f)));
            PlayerGunTracers = Config.Bind("Gunplay", "PlayerGunTracers", true, "Draw tracers for your own shots too.");
            PlayerHeadRadius = H("Tracers", "PlayerHeadRadius", 0.14f, new ConfigDescription(
                "Your head as NPC bullets see it: a sphere of this radius at the top of your body, m.",
                new AcceptableValueRange<float>(0.05f, 0.4f)));
            PlayerTracers = H("Tracers", "PlayerGuns", true,
                "Your own guns follow the same rules: visible bullets with travel time, the same range falloff and vehicle-part hits.");
            RifleRange = Config.Bind("Tracers", "RifleRange", 120f, new ConfigDescription("Automatic rifles and machine guns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunPelletSpread = H("Tracers", "ShotgunPelletSpread", 1f, new ConfigDescription("Extra spread of each pellet, degrees.", new AcceptableValueRange<float>(0f, 15f)));
            ShotgunPellets = H("Tracers", "ShotgunPellets", 3, new ConfigDescription("Pellets per vanilla shotgun ray (a vanilla blast is 4 rays; 3 = 12 pellets). The blast's damage is split between them.", new AcceptableValueRange<int>(1, 8)));
            ShotgunRange = Config.Bind("Tracers", "ShotgunRange", 35f, new ConfigDescription("Shotguns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SmgRange = Config.Bind("Tracers", "SmgRange", 70f, new ConfigDescription("SMGs, falloff range in metres (half damage at half range).", new AcceptableValueRange<float>(5f, 1000f)));
            SniperRange = Config.Bind("Tracers", "SniperRange", 250f, new ConfigDescription("Sniper/scoped rifles, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            StabSound = H("Tracers", "StabSound", "Sounds/knifestab.wav", "(1.6.1) The stab sound for blades on corpses (WAV, relative to the plugin folder). Missing file: the game's knife hit.");
            StabWeapons = H("Tracers", "StabWeapons", "knife,shiv,machete", "(1.6.1) Melee weapons (name contains one of these) that stab a corpse with StabSound instead of the game's knife hit. Every melee hit on a corpse shows blood.");
            TracerColor = H("Tracers", "TracerColor", new Color(1f, 0.78f, 0.35f, 1f), "Bullet tracer colour (RGBA hex). Unlit: same brightness day and night.");
            TracerGlow = H("Tracers", "Glow", 1.5f, new ConfigDescription("Brightness multiplier of tracers and bolts.", new AcceptableValueRange<float>(0f, 8f)));
            TracerLength = H("Tracers", "TracerLength", 4f, new ConfigDescription("Tracer line length, m.", new AcceptableValueRange<float>(0.1f, 30f)));
            TracerWidth = H("Tracers", "TracerWidth", 0.1f, new ConfigDescription("Tracer line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            TracersDraw = Config.Bind("Gunplay", "Tracers", true, "Draw the glowing trail of every bullet in flight (crossbow bolts are always drawn).");
            VehicleDamage = Config.Bind("Gunplay", "VehicleDamage", true, "Bullets damage the vehicle parts they hit (20 bullet damage = 1 % condition; wheels much more). Off = the game's own rule.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Detailed logs for every part of the mod (hits, detection, movement, maps).");
            WheelDamageMultiplier = H("Tracers", "WheelDamageMultiplier", 5f, new ConfigDescription(
                "Wheels (and tyres) lose this many times more condition per bullet - and per blow of your melee weapons - than other vehicle parts, so shooting or slashing the wheels is the way to stop a car.", new AcceptableValueRange<float>(0f, 100f)));
            WheelPopOff = Config.Bind("Gunplay", "WheelPopOff", true, "A wheel shot or slashed to 0 condition jumps off its car.");
            VehicleDamagePer1 = H("Tracers", "VehicleDamagePer1", 20f, new ConfigDescription("Bullet damage that takes 1 % off a vehicle part condition.", new AcceptableValueRange<float>(1f, 1000f)));
            HitLog = VerboseLog;
            LegacyConfig.Import(Config, Log, existingConfig);
            _harmony = new Harmony(GUID);
            Patch(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit), "OnEnter", typeof(Tracers), nameof(Tracers.BeforeRayHit), true);
            Patch(typeof(Raycast), "OnEnter", typeof(Tracers), nameof(Tracers.BeforeRaycast), true);
            Patch(typeof(SetFsmFloat), "OnEnter", typeof(Tracers), nameof(Tracers.AfterSetFsmFloat), false);
            Patch(typeof(GetLayer), "OnEnter", typeof(Tracers), nameof(Tracers.AfterGetLayer), false);
            Patch(typeof(SetAudioClip), "OnEnter", typeof(Tracers), nameof(Tracers.AfterSetAudioClip), false);
            Patch(typeof(CreateObject), "OnEnter", typeof(Plugin), nameof(AfterCreateObject), false);
            Patch(typeof(TranslatePosition), "OnFixedUpdate", typeof(Tracers), nameof(Tracers.BeforeTranslatePosition), true);
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(default(Scene), default(LoadSceneMode));
            Log.LogInfo(NAME + " loaded; sibling integrations are optional.");
        }

        private void Patch(Type action, string method, Type owner, string handler, bool prefix)
        {
            try {
                var patch = new HarmonyMethod(owner, handler);
                _harmony.Patch(AccessTools.Method(action, method), prefix: prefix ? patch : null, postfix: prefix ? null : patch);
            } catch (Exception e) { Log.LogError("Patch " + action.Name + "." + method + " failed: " + e); }
        }
        private static void AfterCreateObject(CreateObject __instance)
        {
            if (__instance.storeObject == null || __instance.gameObject == null) return;
            var prefab = __instance.gameObject.Value;
            if (prefab != null) Bosses.Spawned(__instance.storeObject.Value, prefab.name);
            Tracers.OrbSpawned(__instance, __instance.storeObject.Value);
        }
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { EnsureRunner(); Tracers.OnSceneLoaded(); Bosses.OnSceneLoaded(); }
        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("Gunplay.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner);
            _runner.AddComponent<Runner>();
        }
        internal static void Verbose(string message) { if (VerboseLog != null && VerboseLog.Value) Log.LogInfo(message); }
    }
    internal sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            Integration.Ensure();
            try { Tracers.Tick(); } catch (Exception e) { Plugin.Log.LogError("Tracers: " + e); }
            try { Bosses.Tick(); } catch (Exception e) { Plugin.Log.LogError("Bosses: " + e); }
        }
    }
}
