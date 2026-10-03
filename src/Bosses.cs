using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gunplay
{
    // [Gunplay] AdjustHumanBossHP: the human bosses' health in % of the game's own (Duke Ironjaw 1500, Buzzgut 600; asset read 2026-10-02).
    // Bosses carry the FSM "BossUI" (it shows the Health number) and a "Health" FSM. Bosses are registered by NPC detection (Registered) and when the game spawns them
    // (Senses' CreateObject postfix -> Spawned) and by a scene scan shortly after each scene load (bosses restored from a save); only
    // that short list is checked every 3 s. A slow full scan every 60 s stays as a safety net. Health is capped at base x % - a cap,
    // not a multiplication, so it is safe with the game saving and reloading their Health and with any number of checks.
    // Above 100 % only an unhurt boss (Health = base) is raised, once per instance.
    internal static class Bosses
    {
        private sealed class Boss { public GameObject Go; public string Name; public float Base; public FsmFloatRef Hp; }
        private sealed class FsmFloatRef { public PlayMakerFSM Fsm; public HutongGames.PlayMaker.FsmFloat Var; }

        private static readonly Dictionary<string, float> Base = new Dictionary<string, float> { { "Duke_Ironjaw", 1500f }, { "Buzzgut", 600f } };
        private static readonly Dictionary<int, Boss> _bosses = new Dictionary<int, Boss>();
        private static readonly HashSet<int> _raised = new HashSet<int>();
        private static readonly List<int> _dead = new List<int>();
        private static float _next, _nextScan;
        private static int _scanBurst;

        public static void OnSceneLoaded() { _raised.Clear(); _bosses.Clear(); _next = 0f; _nextScan = Time.unscaledTime + 2f; _scanBurst = 4; }

        private static string Clean(string name) { int cut = name.IndexOf('('); return cut > 0 ? name.Substring(0, cut) : name; }

        // NPC detection registered an NPC (every boss has a Detection FSM): no scene scans needed while detection is on
        internal static void Registered(GameObject owner)
        {
            if (owner == null) return;
            float b; string name = Clean(owner.name);
            if (Base.TryGetValue(name, out b)) Add(owner, name, b);
        }

        // from the CreateObject postfix: prefab name already known (cached), the instance is what was made
        internal static void Spawned(GameObject made, string prefabName)
        {
            float b;
            if (made == null || prefabName == null || !Base.TryGetValue(prefabName, out b)) return;
            Add(made, prefabName, b);
        }

        private static void Add(GameObject go, string name, float b)
        {
            int id = go.GetInstanceID();
            if (!_bosses.ContainsKey(id)) _bosses[id] = new Boss { Go = go, Name = name, Base = b };
        }

        private static void Scan()
        {
            foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "BossUI") continue;
                var go = f.gameObject;
                string name = Clean(go.name);
                float b;
                if (Base.TryGetValue(name, out b)) Add(go, name, b);
            }
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                // Restored bosses are discovered independently of NPCAI.
                if (GameObject.Find("Player") != null) { Scan(); if (_scanBurst > 0) _scanBurst--; }
                _nextScan = now + (_scanBurst > 0 ? 5f : 60f);
            }
            if (now < _next || _bosses.Count == 0) return;
            _next = now + 3f;
            float pct = Mathf.Max(1f, Plugin.BossHpPercent.Value) / 100f;
            _dead.Clear();
            foreach (var kv in _bosses)
            {
                var bo = kv.Value;
                if (bo.Go == null) { _dead.Add(kv.Key); continue; }
                if (bo.Hp == null || bo.Hp.Fsm == null)
                {
                    bo.Hp = null;
                    foreach (var h in bo.Go.GetComponents<PlayMakerFSM>())
                        if (h != null && h.FsmName == "Health" && h.Fsm != null && h.Fsm.Initialized)
                        {
                            var hv = h.FsmVariables.FindFsmFloat("Health");
                            if (hv != null) bo.Hp = new FsmFloatRef { Fsm = h, Var = hv };
                            break;
                        }
                    if (bo.Hp == null) continue;        // not initialised yet: next tick
                }
                var v = bo.Hp.Var;
                if (v.Value < 1f) continue;
                float b = bo.Base, target = b * pct;
                int id = kv.Key;
                if (v.Value > target + 0.5f)
                {
                    if (Plugin.VerboseLog.Value) Plugin.Verbose("Bosses: " + bo.Go.name + " health " + v.Value.ToString("0") + " -> " + target.ToString("0") + " (" + (pct * 100f).ToString("0") + " % of " + b.ToString("0") + ")");
                    v.Value = target;
                }
                else if (pct > 1f && !_raised.Contains(id) && Mathf.Abs(v.Value - b) <= 0.5f)
                {
                    v.Value = target;
                    if (Plugin.VerboseLog.Value) Plugin.Verbose("Bosses: " + bo.Go.name + " health raised to " + target.ToString("0"));
                }
                _raised.Add(id);
            }
            foreach (var k in _dead) { _bosses.Remove(k); _raised.Remove(k); }
        }
    }
}
