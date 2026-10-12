using System;

namespace Gunplay.Ammo
{
    /// Tracks a single caliber across capacity changes. A failed transfer must suppress vanilla clamping.
    internal sealed class OverflowTransfer
    {
        private int _capacity;
        private bool _pending;
        private float _retryAt;

        internal OverflowTransfer(int capacity) { _capacity = Math.Max(0, capacity); }

        internal bool BeforeClamp(int count, int capacity, float now, Func<int, bool> drop, out int retained)
        {
            retained = count;
            capacity = Math.Max(0, capacity);
            if (capacity < _capacity) { _pending = true; _retryAt = 0f; }
            _capacity = capacity;
            if (!_pending) return true;
            if (count <= capacity) { _pending = false; return true; }
            if (now < _retryAt) return false;
            int excess = count - capacity;
            if (drop(excess))
            {
                retained = count - excess;
                _pending = false;
                return true;
            }
            _retryAt = now + 1f;
            return false;
        }
    }
}
