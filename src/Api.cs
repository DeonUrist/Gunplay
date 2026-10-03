using UnityEngine;

namespace Gunplay
{
    /// <summary>Optional reflection contract. Kind codes: pistol=0, SMG=1, rifle=2, sniper=3, shotgun=4, crossbow=5.</summary>
    public static class Api
    {
        public const int ContractVersion = 1;
        public static bool ProjectilesEnabled => Plugin.TracersEnabled != null && Plugin.TracersEnabled.Value;
        public static float EffectiveRange(int kind) => Tracers.RangeOf((Tracers.Kind)kind);
        public static bool TryGetWeaponKind(GameObject owner, out int kind)
        {
            Tracers.Kind found = Tracers.Kind.Rifle;
            bool result = owner != null && Tracers.GunKindOf(owner, out found);
            kind = result ? (int)found : 2;
            return result;
        }
    }
}
