using System;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;

namespace Gunplay.Ammo
{
    internal static class ArmorOverflow
    {
        private static readonly Dictionary<FloatClamp, ArmorOverflowTransfer> _capacities = new Dictionary<FloatClamp, ArmorOverflowTransfer>();
        internal static void Reset() { _capacities.Clear(); }

        internal static bool BeforeClamp(FloatClamp action)
        {
            var fsm = action.Fsm;
            if (fsm == null || fsm.Name != "ArmorBackpack" || fsm.GameObject == null || fsm.GameObject.name != "__GameManager__") return true;
            var armor = action.floatVariable;
            var max = action.maxValue;
            if (armor == null || max == null || armor.Name != "armor" || max.Name != "armor_capacity") return true;
            if (!AmmoSettings.DropArmorOverflow.Value) { _capacities.Remove(action); return true; }
            if (float.IsNaN(armor.Value) || float.IsInfinity(armor.Value) || float.IsNaN(max.Value) || float.IsInfinity(max.Value)) return true;
            float capacity = Math.Max(0f, max.Value);
            ArmorOverflowTransfer transfer;
            if (!_capacities.TryGetValue(action, out transfer))
            {
                _capacities[action] = new ArmorOverflowTransfer(capacity);
                return true; // Initial observation/load establishes the native baseline.
            }
            float retained;
            bool allowClamp = transfer.BeforeClamp(armor.Value, capacity, UnityEngine.Time.unscaledTime,
                plates => AmmoBox.Drop("armorplate", plates, fsm.GameObject.transform), out retained);
            if (retained != armor.Value)
            {
                float returned = armor.Value - retained;
                armor.Value = retained;
                AmmoSettings.Log.LogInfo("Armor capacity reduced: returned " + returned + " armor as " + (returned / ArmorOverflowTransfer.ArmorPerPlate) + " plate(s)");
            }
            return allowClamp;
        }
    }

    [HarmonyPatch(typeof(FloatClamp), "DoClamp")]
    internal static class ArmorCapacityPatch
    {
        static bool Prefix(FloatClamp __instance) { return ArmorOverflow.BeforeClamp(__instance); }
    }
}
