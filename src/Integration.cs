using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Gunplay
{
    // Uses only CLR and game types in delegates; absent sibling DLLs never enter assembly references.
    internal static class Integration
    {
        private static Action<GameObject, Vector3, int, bool> _shot;
        private static Action<GameObject, GameObject> _hurt;
        private static Action<GameObject, Vector3, float, bool> _playerHit;
        private static Action<GameObject, Vector3, float> _partHit;
        private static Func<float, float> _spread;
        private static float _next;
        private static bool _patrol;
        internal static void Ensure()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var npc = assembly.GetType("NPCAI.Api", false);
                    if (npc != null)
                    {
                        Bind(ref _shot, npc, "Shot", typeof(GameObject), typeof(Vector3), typeof(int), typeof(bool));
                        Bind(ref _hurt, npc, "Hurt", typeof(GameObject), typeof(GameObject));
                        Bind(ref _spread, npc, "SpreadFactor", typeof(float));
                    }
                    var hud = assembly.GetType("GunplayHUD.Api", false);
                    if (hud != null)
                    {
                        Bind(ref _playerHit, hud, "PlayerHit", typeof(GameObject), typeof(Vector3), typeof(float), typeof(bool));
                        Bind(ref _partHit, hud, "PartHit", typeof(GameObject), typeof(Vector3), typeof(float));
                    }
                    // Legacy patrol checks assembly name Apocaraider. Redirect its ownership probe dynamically.
                    var patrol = assembly.GetType("Apocapatrol.MeleeWheels", false);
                    var probe = patrol?.GetMethod("RaiderHandles", BindingFlags.Static | BindingFlags.NonPublic);
                    if (!_patrol && probe != null)
                    {
                        new Harmony(Plugin.GUID + ".patrol").Patch(probe, prefix: new HarmonyMethod(typeof(Integration), nameof(PatrolHandles)));
                        _patrol = true;
                        Plugin.Log.LogInfo("Apocapatrol fitted-wheel melee ownership redirected to Gunplay.");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Optional integration: " + e.Message); }
            }
        }
        private static void Bind<T>(ref T callback, Type owner, string method, params Type[] parameters) where T : class
        {
            if (callback != null) return;
            var target = owner.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (target != null) callback = Delegate.CreateDelegate(typeof(T), target) as T;
        }
        private static bool PatrolHandles(ref bool __result) { __result = true; return false; }
        internal static float SpreadFactor(float distance)
        {
            Ensure();
            try { if (_spread != null) return _spread(distance); }
            catch (Exception e) { Plugin.Log.LogWarning("NPCAI spread: " + e.Message); }
            float beyond = distance - 5f;
            return 1f + (beyond <= 0f ? 0 : (int)(beyond / 5f)) * 0.1f;
        }
        internal static void Shot(GameObject shooter, Vector3 position, int kind, bool player)
        { Ensure(); try { _shot?.Invoke(shooter, position, kind, player); } catch (Exception e) { Plugin.Log.LogWarning("NPCAI shot: " + e.Message); } }
        internal static void Hurt(GameObject victim, GameObject attacker)
        { Ensure(); try { _hurt?.Invoke(victim, attacker); } catch (Exception e) { Plugin.Log.LogWarning("NPCAI hurt: " + e.Message); } }
        internal static void PlayerHit(GameObject target, Vector3 position, float damage, bool head)
        { Ensure(); try { _playerHit?.Invoke(target, position, damage, head); } catch (Exception e) { Plugin.Log.LogWarning("HUD hit: " + e.Message); } }
        internal static void PartHit(GameObject target, Vector3 position, float damage)
        { Ensure(); try { _partHit?.Invoke(target, position, damage); } catch (Exception e) { Plugin.Log.LogWarning("HUD part hit: " + e.Message); } }
        internal static void Dispose() { if (_patrol) new Harmony(Plugin.GUID + ".patrol").UnpatchSelf(); }
    }
}
