using System;
using Gunplay.Ammo;

internal static class LogicTests
{
    private static int checks;
    private static void Check(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
        checks++;
        Console.WriteLine("PASS: " + text);
    }
    private static void Main()
    {
        int attempts = 0, dropped = 0, retained;
        Func<int, bool> success = n => { attempts++; dropped += n; return true; };
        var transfer = new OverflowTransfer(100);
        Check(transfer.BeforeClamp(90, 30, 0, success, out retained) && retained == 30 && dropped == 60, "downgrade conserves all 90 rounds: 30 stored + 60 dropped");
        Check(transfer.BeforeClamp(retained, 30, .1f, success, out retained) && attempts == 1, "repeated frame does not duplicate a drop");
        Check(transfer.BeforeClamp(retained, 150, .2f, success, out retained) && retained == 30 && attempts == 1, "upgrade preserves count without dropping");
        transfer = new OverflowTransfer(100);
        Check(transfer.BeforeClamp(20, 30, 0, success, out retained) && retained == 20 && attempts == 1, "downgrade below current count threshold leaves ammo untouched");
        transfer = new OverflowTransfer(30);
        Check(transfer.BeforeClamp(90, 30, 0, success, out retained) && retained == 90 && attempts == 1, "initial observation does not spill save-loaded ammo");

        attempts = 0; dropped = 0;
        Func<int, bool> fail = n => { attempts++; return false; };
        transfer = new OverflowTransfer(100);
        Check(!transfer.BeforeClamp(90, 30, 10, fail, out retained) && retained == 90, "spawn failure suppresses destructive clamp and preserves all ammo");
        Check(!transfer.BeforeClamp(90, 30, 10.5f, fail, out retained) && attempts == 1, "failed drop is throttled for one second");
        Check(transfer.BeforeClamp(85, 30, 11, success, out retained) && retained == 30 && dropped == 55, "retry uses current ammo count after five rounds were consumed");
        Check(transfer.BeforeClamp(30, 30, 12, success, out retained) && attempts == 2, "successful retry clears pending transfer");
        transfer = new OverflowTransfer(100);
        Check(!transfer.BeforeClamp(90, 30, 10, fail, out retained), "second failed transfer stays pending");
        Check(transfer.BeforeClamp(90, 100, 10.1f, success, out retained) && retained == 90, "larger backpack absorbs retained overflow without an unnecessary drop");
        transfer = new OverflowTransfer(100);
        Check(!transfer.BeforeClamp(90, 30, 10, fail, out retained), "failed transfer remains available for a further downgrade");
        int before = attempts;
        Check(transfer.BeforeClamp(90, 10, 10.1f, success, out retained) && retained == 10 && attempts == before + 1, "further downgrade retries immediately with the new capacity");
        transfer = new OverflowTransfer(100);
        before = dropped;
        Check(transfer.BeforeClamp(90, 0, 0, success, out retained) && retained == 0 && dropped - before == 90, "zero capacity drops the entire reserve");

        // Exercise thousands of different capacities/counts, including boundary values.
        for (int capacity = 0; capacity <= 100; capacity++)
            for (int count = 0; count <= 150; count++)
            {
                transfer = new OverflowTransfer(200);
                int spilled = 0;
                if (!transfer.BeforeClamp(count, capacity, 0, n => { spilled += n; return true; }, out retained)
                    || retained + spilled != count || retained > capacity || retained < 0 || spilled < 0)
                    throw new Exception("Ammo conservation failed for " + count + "/" + capacity);
            }
        Check(true, "15,251 count/capacity combinations conserve ammo and respect capacity");
        MagazineTests.Run(Check);
        Console.WriteLine(checks + " checks passed");
    }
}
