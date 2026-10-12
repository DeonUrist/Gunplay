using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Gunplay.Ammo
{
    internal static class AmmoSettings
    {
        internal const string LegacyGuid = "com.denis.apocalypter.apocaunloader";
        internal static ConfigEntry<bool> Enabled, DropBackpackOverflow;
        internal static ConfigEntry<float> HoldSeconds, PhaseTimeout;
        internal static ConfigEntry<Key> FallbackKey;
        internal static ConfigEntry<bool> Verbose { get { return Plugin.VerboseLog; } }
        internal static ManualLogSource Log { get { return Plugin.Log; } }
        internal static int TapFrame = -100;
        internal static bool Active;
        internal static bool LegacySuppressed;
        internal static void V(string message) { Plugin.Verbose(message); }

        internal static void Bind(ConfigFile config)
        {
            var local = ReadSection(config.ConfigFilePath, "Ammo");
            var legacy = ReadSection(Path.Combine(Paths.ConfigPath, LegacyGuid + ".cfg"), "General");
            Enabled = Bind(config, local, legacy, "EnableUnloading", "Enabled", true,
                new ConfigDescription("Hold Reload to unload the current gun. Short presses reload on release. Per-gun magazines remain tracked when this is off."));
            HoldSeconds = Bind(config, local, legacy, "HoldSeconds", "HoldSeconds", 0.35f,
                new ConfigDescription("Seconds to hold Reload before unloading; a shorter press reloads on release.", new AcceptableValueRange<float>(0.1f, 2f)));
            PhaseTimeout = Bind(config, local, legacy, "AnimationTimeout", "AnimationTimeout", 4f,
                new ConfigDescription("Seconds to wait for an unload animation phase before completing it.", new AcceptableValueRange<float>(1f, 10f)));
            FallbackKey = Bind(config, local, legacy, "FallbackKey", "FallbackKey", Key.R,
                new ConfigDescription("Key polled if the game's Reload input axis cannot be read."));
            DropBackpackOverflow = Bind(config, local, legacy, "DropBackpackOverflow", "DropBackpackOverflow", true,
                new ConfigDescription("Drop excess ammo when switching to a smaller backpack or removing it. Failed drops retain the ammo and retry."));
            // The standalone Enabled switch also disabled overflow protection. Preserve that on migration.
            string oldEnabled;
            bool enabled;
            if (!local.ContainsKey("DropBackpackOverflow") && legacy.TryGetValue("Enabled", out oldEnabled)
                && bool.TryParse(oldEnabled, out enabled) && !enabled) DropBackpackOverflow.Value = false;
        }

        private static ConfigEntry<T> Bind<T>(ConfigFile config, Dictionary<string, string> local, Dictionary<string, string> legacy,
            string key, string oldKey, T value, ConfigDescription description)
        {
            var entry = config.Bind("Ammo", key, value, description);
            string previous;
            if (!local.ContainsKey(key) && legacy.TryGetValue(oldKey, out previous))
                try { entry.SetSerializedValue(previous); }
                catch (Exception e) { Log.LogWarning("Could not import Unloader " + oldKey + ": " + e.Message); }
            return entry;
        }

        private static Dictionary<string, string> ReadSection(string path, string wanted)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return values;
            try
            {
                string section = null;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    int equals = line.IndexOf('=');
                    if (section == wanted && equals > 0) values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
                }
            }
            catch (Exception e) { Log.LogWarning("Read ammo settings: " + e.Message); }
            return values;
        }

        internal static bool TakeOwnership()
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var runner = assembly.GetType("Apocaunloader.Runner", false);
                    if (runner == null) continue;
                    var update = runner.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (update == null) throw new MissingMethodException(runner.FullName, "Update");
                    new Harmony(Plugin.GUID + ".legacy-ammo").Patch(update, prefix: new HarmonyMethod(typeof(AmmoSettings), nameof(SuppressLegacyUpdate)));
                    new Harmony(LegacyGuid).UnpatchSelf();
                    LegacySuppressed = true;
                    Log.LogInfo("Gunplay owns ammo: standalone Apocaunloader hooks and runner suppressed.");
                }
                Active = true;
            }
            catch (Exception e) { Active = false; Log.LogError("Ammo takeover unavailable; retaining standalone behavior: " + e); }
            return Active;
        }

        private static bool SuppressLegacyUpdate() { return false; }

        internal static bool ReloadHeld()
        {
            try { return Input.GetButton("Reload"); } catch { }
            try { var kb = Keyboard.current; if (kb != null) return kb[FallbackKey.Value].isPressed; } catch { }
            try { KeyCode kc; if (Enum.TryParse(FallbackKey.Value.ToString(), out kc)) return Input.GetKey(kc); } catch { }
            return false;
        }
        internal static bool TapPending() { int frame = Time.frameCount; return frame == TapFrame || frame == TapFrame + 1; }
    }
}
