using System;
using Gunplay.Ammo;

internal static class MagazineTests
{
    internal static void Run(Action<bool, string> check)
    {
        var ledger = new MagazineLedger();
        check(ledger.Draw("akms(Clone)1", "akms", new Magazine(30, false)).Rounds == 30, "First AKMS inherits the vanilla pool");
        check(ledger.Draw("akms(Clone)2", "akms", new Magazine(30, false)).Rounds == 0, "Second AKMS cannot inherit the same 30 rounds");
        ledger.Capture("akms(Clone)1", "akms", new Magazine(17, false));
        ledger.Capture("akms(Clone)2", "akms", new Magazine(9, false));
        for (int i = 0; i < 100; i++)
            if (ledger.Draw("akms(Clone)1", "akms", new Magazine(9, false)).Rounds != 17
                || ledger.Draw("akms(Clone)2", "akms", new Magazine(17, false)).Rounds != 9) throw new Exception("Repeated switching mixed magazines");
        check(true, "100 same-type switches preserve independently fired/reloaded magazines");
        ledger.Capture("akms(Clone)1", "akms", new Magazine(0, false));
        check(ledger.Draw("akms(Clone)2", "akms", new Magazine(0, false)).Rounds == 9, "Unloading gun one does not empty gun two");
        check(ledger.Draw("redmark(Clone)3", "redmark_m11", new Magazine(4, true)).Hammer, "A separate type inherits its own chamber state");
        check(!ledger.Draw("redmark(Clone)4", "redmark_m11", new Magazine(4, true)).Hammer, "A duplicate does not inherit another gun's chamber state");
        string encoded = ledger.Encode();
        var loaded = MagazineLedger.Decode(encoded);
        check(loaded.Encode() == encoded && loaded.Draw("akms(Clone)2", "akms", new Magazine(0, false)).Rounds == 9, "Save roundtrip retains per-item rounds");
        check(loaded.Draw("redmark(Clone)3", "redmark_m11", new Magazine(0, false)).Hammer, "Save roundtrip retains chamber state");
        check(loaded.Draw("akms(Clone)5", "akms", new Magazine(9, false)).Rounds == 0, "New gun after loading cannot duplicate native per-type mirror");
        loaded = MagazineLedger.Decode("Apocaunloader.Magazines|1\nC|akms");
        check(loaded.Draw("new", "akms", new Magazine(30, false)).Rounds == 0, "Claim survives even when the original gun no longer exists");
        ledger.Draw("gun|name\n%", "type|%", new Magazine(8, true));
        loaded = MagazineLedger.Decode(ledger.Encode());
        check(loaded.Draw("gun|name\n%", "type|%", new Magazine(0, false)).Rounds == 8, "Unusual item names serialize without splitting identities");
        foreach (string corrupt in new[] { "", "Apocaunloader.Magazines|2", "Apocaunloader.Magazines|1\nM|a|akms|30|0",
            "Apocaunloader.Magazines|1\nC|akms\nM|a|akms|-1|0", "Apocaunloader.Magazines|1\nC|akms\nM|a|akms|0|1",
            "Apocaunloader.Magazines|1\nC|akms\nM|a|akms|5|0\nM|a|akms|7|0" })
        {
            bool rejected = false;
            try { MagazineLedger.Decode(corrupt); } catch (FormatException) { rejected = true; }
            check(rejected, "Invalid magazine data rejected atomically");
        }
        var fresh = new MagazineLedger();
        check(fresh.Draw("akms(Clone)2", "akms", new Magazine(6, false)).Rounds == 6, "New character starts an independent migration pool");
        check(new Magazine(0, true).Hammer == false, "An empty gun cannot retain a loaded chamber");
    }
}
