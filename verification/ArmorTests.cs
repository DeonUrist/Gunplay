using System;
using Gunplay.Ammo;

internal static class ArmorTests
{
    internal static void Run(Action<bool, string> check)
    {
        float retained;
        int dropped = 0, attempts = 0;
        Func<int, bool> success = n => { dropped += n; attempts++; return true; };
        Func<int, bool> fail = n => { attempts++; return false; };
        var transfer = new ArmorOverflowTransfer(300);
        check(transfer.BeforeClamp(250, 100, 0, success, out retained) && retained == 100 && dropped == 3, "250 armor to 100 capacity returns three 50-point plates");
        check(transfer.BeforeClamp(100, 100, .1f, success, out retained) && attempts == 1, "Repeated armor clamp cannot duplicate plates");
        transfer = new ArmorOverflowTransfer(300); dropped = 0;
        check(transfer.BeforeClamp(149.999f, 100, 0, success, out retained) && retained == 149.999f && dropped == 0, "Excess strictly below 50 creates no armor plate");
        transfer = new ArmorOverflowTransfer(300);
        check(transfer.BeforeClamp(150, 100, 0, success, out retained) && retained == 100 && dropped == 1, "Exactly 50 excess returns one armor plate");
        transfer = new ArmorOverflowTransfer(300); dropped = 0;
        check(transfer.BeforeClamp(230, 100, 0, success, out retained) && retained == 130 && dropped == 2, "130 excess returns only two whole plates; 30 stays subject to native clamp");
        transfer = new ArmorOverflowTransfer(300); dropped = 0;
        check(transfer.BeforeClamp(80, 100, 0, success, out retained) && retained == 80 && dropped == 0, "Smaller armor that still fits current protection creates no plate");
        transfer = new ArmorOverflowTransfer(100); dropped = 0;
        check(transfer.BeforeClamp(250, 100, 0, success, out retained) && dropped == 0, "Initial observation/unchanged capacity never converts save-loaded armor");
        transfer = new ArmorOverflowTransfer(300); attempts = 0;
        check(!transfer.BeforeClamp(250, 100, 0, fail, out retained) && retained == 250, "Spawn failure retains all armor and suppresses its destructive clamp");
        check(!transfer.BeforeClamp(250, 100, .5f, fail, out retained) && attempts == 1, "Armor drop retry is throttled for one second");
        dropped = 0;
        check(transfer.BeforeClamp(220, 100, 1, success, out retained) && retained == 120 && dropped == 2, "Retry converts current armor after damage without duplicating consumed protection");
        transfer = new ArmorOverflowTransfer(300); attempts = 0;
        transfer.BeforeClamp(250, 100, 0, fail, out retained);
        check(transfer.BeforeClamp(250, 300, .1f, success, out retained) && retained == 250 && attempts == 1, "Larger armor absorbs retained overflow without a drop");
        transfer = new ArmorOverflowTransfer(300); dropped = 0;
        transfer.BeforeClamp(250, 100, 0, fail, out retained);
        check(transfer.BeforeClamp(250, 50, .1f, success, out retained) && retained == 50 && dropped == 4, "Further armor downgrade retries immediately at the new capacity");
        transfer = new ArmorOverflowTransfer(300); dropped = 0;
        check(transfer.BeforeClamp(235.5f, 85.5f, 0, success, out retained) && retained == 85.5f && dropped == 3, "Fractional armor and capacities preserve full-plate accounting");
        for (int capacity = 0; capacity <= 300; capacity += 3)
            for (int armor = 0; armor <= 500; armor++)
            {
                transfer = new ArmorOverflowTransfer(1000);
                int plates = 0;
                float count = armor + .25f, cap = capacity + .5f;
                bool allow = transfer.BeforeClamp(count, cap, 0, n => { plates += n; return true; }, out retained);
                if (!allow || retained + plates * 50 != count || plates < 0 || retained < 0 || Math.Max(0f, retained - cap) >= 50)
                    throw new Exception("Armor conversion/conservation failed for " + count + "/" + cap);
            }
        check(true, "50,601 fractional count/capacity cases return whole plates without creating armor");
    }
}
