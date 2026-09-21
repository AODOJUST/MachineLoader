// Offline regression for the DropTank fuel bookkeeping (Machine.DropTank.FuelLink).
// References the REAL compiled bin\mods\DropTank.dll so it exercises production code.
// Pure managed (no engine calls) -> runs outside Unity.
// Usage: run-droptank-sim.ps1  ->  tools/droptank_sim_out.txt
using System;
using Machine.DropTank;

public static class DropTankSim
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + "  " + detail);
    }

    private static string S(FuelLink s)
    {
        return "cap=" + s.Cap.ToString("F2") + " fuel=" + s.Fuel.ToString("F2")
             + " ref=" + s.RefCap.ToString("F2") + " applied=" + s.Applied.ToString("F2")
             + " paid=" + s.PaidTanks;
    }

    private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.001f; }

    public static int Main()
    {
        const float PER = 10f;

        // A) no tanks on a full plane -> nothing changes
        FuelLink s = FuelLink.New(100f, 100f, 100f);
        bool changed = FuelLink.Apply(ref s, 0, PER, true);
        Check("A no-tank idle", !changed && Near(s.Cap, 100f) && Near(s.Fuel, 100f) && Near(s.RefCap, 100f), S(s));

        // B) one tank -> +10 everywhere
        changed = FuelLink.Apply(ref s, 1, PER, true);
        Check("B +1 tank", changed && Near(s.Cap, 110f) && Near(s.Fuel, 110f) && Near(s.RefCap, 110f), S(s));

        // C) three tanks
        changed = FuelLink.Apply(ref s, 3, PER, true);
        Check("C +3 tanks", changed && Near(s.Cap, 130f) && Near(s.Fuel, 130f) && Near(s.RefCap, 130f), S(s));

        // D) fuel burnt down by the engine (host writes only fuel) -> no work for us
        s.Fuel = 20f;
        changed = FuelLink.Apply(ref s, 3, PER, true);
        Check("D burnt fuel idle", !changed && Near(s.Cap, 130f) && Near(s.Fuel, 20f), S(s));

        // E) drop two tanks -> capacity down, fuel clipped, no refund
        changed = FuelLink.Apply(ref s, 1, PER, true);
        Check("E -2 tanks", changed && Near(s.Cap, 110f) && Near(s.RefCap, 110f) && Near(s.Fuel, 20f), S(s));

        // F) host ActivateFlyMode resets capacity -> bonus must come back, but WITHOUT free fuel
        s.Cap = 100f;
        s.RefCap = 100f;
        changed = FuelLink.Apply(ref s, 1, PER, true);
        Check("F host reset re-applies", changed && Near(s.Cap, 110f) && Near(s.RefCap, 110f) && Near(s.Fuel, 20f), S(s));

        // G) unload the tank entirely
        changed = FuelLink.Apply(ref s, 0, PER, true);
        Check("G all tanks off", changed && Near(s.Cap, 100f) && Near(s.RefCap, 100f) && Near(s.Fuel, 20f), S(s));

        // H) empty plane picks up a full tank -> the tank brings its fuel
        FuelLink z = FuelLink.New(100f, 0f, 100f);
        changed = FuelLink.Apply(ref z, 1, PER, true);
        Check("H empty +1 tank", changed && Near(z.Cap, 110f) && Near(z.Fuel, 10f) && Near(z.RefCap, 110f), S(z));

        // I) grant disabled -> capacity only
        FuelLink g = FuelLink.New(100f, 50f, 100f);
        changed = FuelLink.Apply(ref g, 2, PER, false);
        Check("I grant=false", changed && Near(g.Cap, 120f) && Near(g.Fuel, 50f) && Near(g.RefCap, 120f), S(g));

        // J) picking up 2 more tanks tops the gauge up by exactly 2 tanks
        FuelLink j = FuelLink.New(100f, 100f, 100f);
        FuelLink.Apply(ref j, 2, PER, true);
        FuelLink.Apply(ref j, 4, PER, true);
        Check("J 2->4 tanks", Near(j.Cap, 140f) && Near(j.Fuel, 140f) && Near(j.RefCap, 140f), S(j));

        // K) jettison while the gauge is above the new capacity -> clipped, never negative
        FuelLink k = FuelLink.New(100f, 100f, 100f);
        FuelLink.Apply(ref k, 5, PER, true);
        k.Cap = 100f; k.RefCap = 100f;      // host reset, fuel stays at 150
        FuelLink.Apply(ref k, 0, PER, true);
        Check("K clip after jettison", Near(k.Cap, 100f) && Near(k.Fuel, 100f) && Near(k.RefCap, 100f), S(k));

        // L) idempotent: two ticks in a row with the same state do nothing the second time
        FuelLink l = FuelLink.New(100f, 100f, 100f);
        FuelLink.Apply(ref l, 2, PER, true);
        bool second = FuelLink.Apply(ref l, 2, PER, true);
        Check("L idempotent", !second && Near(l.Cap, 120f) && Near(l.Fuel, 120f), S(l));

        // M) fuel per tank = 0 -> cargo with no fuel bonus must not touch anything
        FuelLink m = FuelLink.New(100f, 100f, 100f);
        bool mChanged = FuelLink.Apply(ref m, 3, 0f, true);
        Check("M per=0", !mChanged && Near(m.Cap, 100f) && Near(m.Fuel, 100f), S(m));

        Console.WriteLine();
        Console.WriteLine("verdict=" + (_fail == 0 ? "PASS" : "FAIL") + " pass=" + _pass + " fail=" + _fail);
        return _fail == 0 ? 0 : 1;
    }
}
