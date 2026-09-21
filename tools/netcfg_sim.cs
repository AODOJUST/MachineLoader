// Offline regression for ConfigGuard.ReadNet (Machine\..\net.json).
//
// Why this exists: net.json is *external input*, and the loader's rule is that every
// field must be schema-validated with a safe fallback. ReadNet is pure managed code
// (no UnityEngine), so it can be driven directly against the REAL compiled
// Machine.Core.dll -- the same code the game runs -- with nothing but a temp dir.
//
// It covers the field added on 2026-09-14 ("remoteModel": "clone" | "box"), which
// NetSync.UseRealRemoteModel is derived from in Net.MachineNet.Start:
//     UseRealRemoteModel = !string.Equals(net.RemoteModel, "box", OrdinalIgnoreCase)
// so the contract this sim pins down is: RemoteModel is ALWAYS exactly "clone" or "box".
//
// Exit code 0 = every assertion green. ASCII only.

using System;
using System.IO;
using System.Text;

internal static class NetCfgSim
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool ok, string label)
    {
        if (ok) { _pass++; Console.WriteLine("PASS  " + label); }
        else { _fail++; Console.WriteLine("FAIL  " + label); }
    }

    private static void CheckEq(string got, string want, string label)
    {
        Check(got == want, label + "  (got [" + got + "] want [" + want + "])");
    }

    private static string _dir;

    /// <summary>Write net.json (or nothing, when json == null) into the scratch dir and read it back.</summary>
    private static Machine.Core.NetConfig Read(string json)
    {
        string path = Path.Combine(_dir, "net.json");
        if (File.Exists(path)) File.Delete(path);
        if (json != null) File.WriteAllText(path, json, new UTF8Encoding(false));
        return Machine.Core.ConfigGuard.ReadNet(_dir);
    }

    /// <summary>The invariant Net.MachineNet.Start relies on when deriving the bool toggle.</summary>
    private static void CheckModeDomain(Machine.Core.NetConfig c, string label)
    {
        Check(c.RemoteModel == "clone" || c.RemoteModel == "box",
              label + " -> remoteModel in {clone,box}");
    }

    private static int Main()
    {
        Console.WriteLine("== Machine.Core net.json (ConfigGuard.ReadNet) sim ==");
        Console.WriteLine("core assembly = " + typeof(Machine.Core.ConfigGuard).Assembly.Location);

        _dir = Path.Combine(Path.GetTempPath(), "machinecfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        try
        {
            // ---- 1. no file at all -> pure defaults ----
            Machine.Core.NetConfig c = Read(null);
            CheckEq(c.Server, "", "missing net.json: server default");
            CheckEq(c.Port.ToString(), "26460", "missing net.json: port default");
            CheckEq(c.PlayerName, "Pilot", "missing net.json: playerName default");
            CheckEq(c.RemoteModel, "clone", "missing net.json: remoteModel defaults to clone");
            CheckEq(c.Warnings, "", "missing net.json: no warnings");

            // ---- 2. remoteModel happy paths ----
            CheckEq(Read("{\"remoteModel\":\"box\"}").RemoteModel, "box", "remoteModel=box honoured");
            CheckEq(Read("{\"remoteModel\":\"clone\"}").RemoteModel, "clone", "remoteModel=clone honoured");
            CheckEq(Read("{\"remoteModel\":\"BOX\"}").RemoteModel, "box", "remoteModel is case-insensitive");
            CheckEq(Read("{\"remoteModel\":\"  box  \"}").RemoteModel, "box", "remoteModel is trimmed");

            // ---- 3. remoteModel rejects junk, keeps the domain closed ----
            Machine.Core.NetConfig bad = Read("{\"remoteModel\":\"banana\"}");
            CheckEq(bad.RemoteModel, "clone", "unknown remoteModel falls back to clone");
            Check(bad.Warnings.IndexOf("remoteModel", StringComparison.OrdinalIgnoreCase) >= 0,
                  "unknown remoteModel records a warning");

            Machine.Core.NetConfig num = Read("{\"remoteModel\":123}");
            CheckEq(num.RemoteModel, "clone", "non-string remoteModel falls back to clone");
            CheckModeDomain(num, "non-string remoteModel");

            Machine.Core.NetConfig nul = Read("{\"remoteModel\":null}");
            CheckEq(nul.RemoteModel, "clone", "null remoteModel falls back to clone");

            // ---- 4. the pre-existing fields still behave (regression guard) ----
            Machine.Core.NetConfig full = Read("{\"server\":\"192.168.1.9\",\"port\":27015,\"playerName\":\"AODo\",\"remoteModel\":\"box\"}");
            CheckEq(full.Server, "192.168.1.9", "server parsed");
            CheckEq(full.Port.ToString(), "27015", "port parsed");
            CheckEq(full.PlayerName, "AODo", "playerName parsed");
            CheckEq(full.RemoteModel, "box", "remoteModel parsed alongside the others");

            Machine.Core.NetConfig badPort = Read("{\"port\":70000}");
            CheckEq(badPort.Port.ToString(), "26460", "out-of-range port falls back to 26460");
            Check(badPort.Warnings.Length > 0, "out-of-range port records a warning");

            Machine.Core.NetConfig badHost = Read("{\"server\":\"not a host!!\"}");
            CheckEq(badHost.Server, "", "illegal host rejected");
            Check(badHost.Warnings.Length > 0, "illegal host records a warning");

            // ---- 5. structurally broken input must not throw ----
            // NOTE: JsonValue.Parse is deliberately lenient -- text that opens with '{' is
            // parsed into an object (unmatched keys are simply ignored) instead of throwing.
            // So this case does NOT warn; the defaults just win. Pinned here so nobody later
            // mistakes that silence for a regression in ReadNet.
            Machine.Core.NetConfig broken = Read("{ this is not json ");
            CheckEq(broken.RemoteModel, "clone", "malformed json: remoteModel default survives");
            CheckEq(broken.Warnings, "", "malformed json: lenient parser, so no warning");

            Machine.Core.NetConfig arr = Read("[1,2,3]");
            CheckEq(arr.Port.ToString(), "26460", "non-object json: port default survives");
            Check(arr.Warnings.IndexOf("not a JSON object", StringComparison.OrdinalIgnoreCase) >= 0,
                  "non-object json says so");

            // An empty file parses to null, which ReadNet reports as "not a JSON object".
            Machine.Core.NetConfig empty = Read("");
            CheckEq(empty.RemoteModel, "clone", "empty file: remoteModel default survives");
            Check(empty.Warnings.IndexOf("not a JSON object", StringComparison.OrdinalIgnoreCase) >= 0,
                  "empty file reports 'not a JSON object'");

            // ---- 6. the domain stays closed across every case above ----
            CheckModeDomain(c, "missing file");
            CheckModeDomain(bad, "banana");
            CheckModeDomain(full, "full config");
            CheckModeDomain(broken, "malformed json");
            CheckModeDomain(empty, "empty file");

            // ---- 7. the config file the game actually ships with ----
            // Aviassembly_DEV\Machine\net.json is {"server":"","port":26460,"playerName":"Pilot"},
            // i.e. no remoteModel key -> the new feature is ON by default with no migration.
            Machine.Core.NetConfig shipped = Read("{\"server\":\"\",\"port\":26460,\"playerName\":\"Pilot\"}");
            CheckEq(shipped.RemoteModel, "clone", "shipped net.json (no key) -> clone by default");
            CheckEq(shipped.Warnings, "", "shipped net.json -> no warnings");
        }
        finally
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        Console.WriteLine("---- " + _pass + " passed, " + _fail + " failed ----");
        return _fail == 0 ? 0 : 1;
    }
}
