// Standalone harness for Machine.Core.MachineCrypto.VerifyRelease.
// Compiles together with src/Machine.Core/Config.cs + JsonValue.cs so the C# signature
// verification used inside the game can be exercised outside Unity.
// usage: CryptoVerify.exe <Machine.Core.dll> <Machine.Core.dll.sig>
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Machine.Core
{
    // stub: Config.cs logs through MachineLog, which lives in Log.cs (Unity dependent)
    public static class MachineLog
    {
        public static void Warn(string m) { Console.WriteLine("  [warn] " + m); }
        public static void Info(string m) { }
        public static void Error(string m) { Console.WriteLine("  [error] " + m); }
    }
}

public class CryptoVerify
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: CryptoVerify <Machine.Core.dll> <Machine.Core.dll.sig>");
            return 1;
        }
        string dllPath = args[0], sigPath = args[1];
        byte[] data = File.ReadAllBytes(dllPath);
        var sb = new StringBuilder();
        foreach (char c in File.ReadAllText(sigPath)) if (!char.IsWhiteSpace(c)) sb.Append(c);
        byte[] sig;
        try { sig = Convert.FromBase64String(sb.ToString()); }
        catch (Exception e) { Console.WriteLine("BAD SIG FILE: " + e.Message); return 1; }

        Console.WriteLine("key available = " + Machine.Core.ReleaseKey.Available);
        Console.WriteLine("key fingerprint = " + Machine.Core.ReleaseKey.FingerprintSha256);
        Console.WriteLine("payload = " + data.Length + " bytes, sig = " + sig.Length + " bytes");
        Console.WriteLine("sha256 = " + Machine.Core.MachineCrypto.Sha256Hex(data));

        string reason;
        bool platformOk = Machine.Core.MachineCrypto.VerifyRelease(data, sig, out reason);
        Console.WriteLine("platform RSA verify = " + platformOk + (reason.Length > 0 ? " (" + reason + ")" : ""));

        // managed fallback path (used when Mono's RSACryptoServiceProvider is unavailable)
        var mi = typeof(Machine.Core.MachineCrypto).GetMethod("VerifyReleaseManaged",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (mi == null) { Console.WriteLine("FAIL: VerifyReleaseManaged not found"); return 1; }
        byte[] modBytes = Machine.Core.MachineCrypto.FromHex(Machine.Core.ReleaseKey.ModulusHex);
        byte[] expBytes = Machine.Core.MachineCrypto.FromHex(Machine.Core.ReleaseKey.ExponentHex);
        bool managedOk = false;
        try { managedOk = (bool)mi.Invoke(null, new object[] { modBytes, expBytes, data, sig }); }
        catch (Exception ex) { Console.WriteLine("managed verify threw: " + ex.InnerException); }
        Console.WriteLine("managed RSA verify  = " + managedOk);

        // negative controls
        byte[] tampered = (byte[])data.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        string r2;
        bool tamperedOk = Machine.Core.MachineCrypto.VerifyRelease(tampered, sig, out r2);
        Console.WriteLine("tampered verify     = " + tamperedOk + " (expect False)");

        byte[] sigBad = (byte[])sig.Clone();
        sigBad[10] ^= 0xFF;
        string r3;
        bool sigOk = Machine.Core.MachineCrypto.VerifyRelease(data, sigBad, out r3);
        Console.WriteLine("bad-signature verify= " + sigOk + " (expect False)");

        bool good = platformOk && managedOk && !tamperedOk && !sigOk;
        Console.WriteLine(good ? "CRYPTO CHECK OK" : "CRYPTO CHECK FAILED");

        // ------------------------------------------------------------------
        // JsonValue regression tests
        // 现场真实案例：仓库里的 version.json 是 PowerShell ConvertTo-Json 生成的，
        // 带 UTF-8 BOM，旧解析器把 BOM 当数字 -> 报 "is not a JSON object"，
        // 导致游戏内更新检查直接失败。以下用例锁定这个行为。
        // ------------------------------------------------------------------
        int jsonFail = 0;
        Console.WriteLine();
        Console.WriteLine("--- JsonValue ---");

        jsonFail += Check("plain object", "{\"version\":\"2.4.0\"}", true);
        jsonFail += Check("BOM prefixed (PS ConvertTo-Json)",
                          "\uFEFF{\"version\":\"2.2.1\",\"core\":\"https://x/y.dll\"}", true);
        jsonFail += Check("BOM + leading whitespace", "\uFEFF \r\n\t{\"a\":1}", true);
        jsonFail += Check("double BOM", "\uFEFF\uFEFF{\"a\":1}", true);
        jsonFail += Check("BOM-only string", "\uFEFF", false);
        jsonFail += Check("empty string", "", false);
        jsonFail += Check("HTML error page (404/502)", "<html><body>502</body></html>", false);
        jsonFail += Check("bare array", "[1,2,3]", false);

        // 真实字段抽取（BOM 包 -> 应能正常取到 version / sig）
        var jv = Machine.Core.JsonValue.Parse("\uFEFF{\"version\":\"2.2.1\",\"sig\":\"abc\"}");
        bool fieldsOk = jv != null && jv.GetString("version", "") == "2.2.1"
                        && jv.GetString("sig", "") == "abc";
        Console.WriteLine("  BOM json field access = " + fieldsOk + " (expect True)");
        if (!fieldsOk) jsonFail++;

        bool allOk = good && jsonFail == 0;
        Console.WriteLine();
        Console.WriteLine(allOk ? "ALL CHECKS OK" : ("FAILURES: " + jsonFail));
        return allOk ? 0 : 1;
    }

    // root Kind must be object -> expectObject true; otherwise expect anything but object
    static int Check(string name, string text, bool expectObject)
    {
        var v = Machine.Core.JsonValue.Parse(text);
        bool isObj = v != null && v.Kind == "object";
        bool ok = (isObj == expectObject);
        Console.WriteLine("  " + (ok ? "PASS" : "FAIL") + "  " + name
                          + " -> kind=" + (v == null ? "<null>" : v.Kind));
        return ok ? 0 : 1;
    }
}
