using System;

namespace Gunplay.Ammo
{
    /// Whole plates only: each returned plate represents 50 points lost to a capacity downgrade.
    internal sealed class ArmorOverflowTransfer
    {
        internal const int ArmorPerPlate = 50;
        private float _capacity, _retryAt;
        private bool _pending;
        internal ArmorOverflowTransfer(float capacity) { _capacity = Math.Max(0f, capacity); }

        internal bool BeforeClamp(float armor, float capacity, float now, Func<int, bool> drop, out float retained)
        {
            retained = armor;
            capacity = Math.Max(0f, capacity);
            if (capacity < _capacity) { _pending = true; _retryAt = 0f; }
            _capacity = capacity;
            if (!_pending) return true;
            double excess = Math.Max(0d, (double)armor - capacity);
            int plates = (int)Math.Min(int.MaxValue, Math.Floor(excess / ArmorPerPlate));
            if (plates == 0) { _pending = false; return true; }
            if (now < _retryAt) return false;
            if (drop(plates))
            {
                retained = armor - plates * (float)ArmorPerPlate;
                _pending = false;
                return true;
            }
            _retryAt = now + 1f;
            return false;
        }
    }
}
