// Machine 安装器 - 用户态命令行（零依赖：不需要系统里装 Python）
//
// 为什么要这一层：
//   * 旧发布包把"安装/卸载"交给了 install_machine.py / uninstall_machine.py，
//     而它们要靠系统 PATH 上的 python.exe。Windows 里 `python` 常常是
//     Microsoft Store 的 App Execution Alias 占位程序（%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe），
//     where python 能找到它、运行却直接返回 9009 并打印
//     "Python was not found; run without arguments to install from the Microsoft Store"。
//     结果就是"黑窗一闪而过 / 卸载报 exit code 9009"。
//   * 这里把安装、卸载、应用更新三条流程全部收进 exe：
//     自动定位游戏目录 → 校验发布包哈希与 RSA 签名 → 注入 + 部署 → 校验 → 写安装记录，
//     并且由本进程渲染进度条与步骤文字（不再依赖任何外部脚本）。
//
// 用法（双击 exe 或跑同名 .bat 都走这一层）：
//   MachineInstaller.exe                           安装（自动定位游戏目录）
//   MachineInstaller.exe --install [游戏目录]      安装（可显式指定目录）
//   MachineInstaller.exe --uninstall [游戏目录]    卸载（--backup 会先备份玩家数据）
//   MachineInstaller.exe --apply-update [游戏目录] 应用 Machine/update/ 里已下载的更新
//   MachineInstaller.exe --auto ...                等价于双击（自动定位 + 结束后按键暂停）
//   通用开关：--yes 全默认、--quiet 少啰嗦、--no-pause 不等待按键、--allow-unsigned 放行未签名包
//
// 兼容旧调用（保持原语义，供 machine_update.py / uninstall_machine.py / 自测脚本使用）：
//   MachineInstaller.exe <游戏目录>                    仅注入 + 复制核心
//   MachineInstaller.exe <游戏目录> --uninstall        仅还原 + 清理
//   MachineInstaller.exe <游戏目录> --update --source <dll> --expect-sha256 <hex>
//   这些"给了目录又没给 --auto/--install/--apply-update"的调用会原样走旧路径（Installer.LegacyMain），
//   因此第三方脚本与 tools/selftest_release.py 的行为不变。
//
// 退出码沿用 Python 侧的约定：0 成功 · 1 参数/环境 · 2 哈希不符 · 3 签名被拒
//                             4 安装器失败 · 5 已回滚 · 6 用户取消 · 7 游戏在运行 · 8 权限不足

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// 注入/还原/替换的底层实现都在 Installer.cs 的 MachineInstaller 类里。
// 这里起个别名，读起来短一点，也避免与下面 Cli 自己的 Install() 方法混淆。
using Installer = MachineInstaller;

internal static class Cli
{
    // ------------------------------------------------------------------
    // 常量
    // ------------------------------------------------------------------

    const string LoaderName = "Machine";
    const string GameExe = "Aviassembly.exe";
    const string CoreDll = "Machine.Core.dll";
    const string AsmDll = "Assembly-CSharp.dll";
    const string InstallerExe = "MachineInstaller.exe";
    const string CecilDll = "Mono.Cecil.dll";
    const string ManagedRel = @"Aviassembly_Data\Managed";
    const string VersionJson = "version.json";
    const string RecordFileName = "installed.json";
    const string LoaderVersion = "2.4.1";

    // 发布公钥（RSA-3072 / PKCS#1 v1.5 / SHA-256）。
    // 与 machine_common.py 里的 BEGIN MACHINE_PUBKEY 标记块、src/Machine.Core/Config.cs 必须一致，
    // 三者由 tools/sign_release.py 一起回写。
    // BEGIN MACHINE_PUBKEY
    const string PubModulusHex =
        "91d8e329a3e546f01aacbb356e46dd7cd99e3aa8b90e91307d9da678485c91cdfe2f1989a529b3e328b43711eb32acdd" +
        "32ec2180d04ae9b95e32476fff28ace3a53a1f0a1eb560f018e0bd68e16aa4b531e44abda36aa1851a7ac50f45d4fafb0cc" +
        "7a1ef8b2b477421303af8cba1cd511661ced32ffe1a52c442d6897e718068a2672c9613b5a1d968bbf3052f3354f2df2618" +
        "7be701e68f0c1c69a376bd72c375a3e3306037eebe278a897afa221c8470a5aeb61c12f5f100553020422e6d18ac5e6592b" +
        "804f4368fdc9055ab6843d8d1f659c9e11c08ccf50a7c3eff1b3132097d364c9e2bf7d21f5d7228cb040e615d71df8ccc25" +
        "a9b4f26e5dfe00c363e49a8d30f2d530b70d21348af8e2c709af99d0f1b30cac5826dd53b4aece1436f979c52c45094d32b" +
        "f44cab3e54041a87266dcc2a1b8f0e31bde7760ffffff2254bbd1dbda45fe6fe571748a8fbd1f815fe301277d428368aa34" +
        "1dbbd3c17c392d9095e8806d2297532322f20958d5fb020aa51f6f42f962d300ed348fefbfbe99";
    const string PubExponentHex = "010001";
    // END MACHINE_PUBKEY

    // 结束时必须留在 <游戏目录>/Machine/ 的文件（卸载/更新依赖）
    static readonly string[] MachineRuntimeFiles = new string[]
    {
        InstallerExe, CecilDll, "machine_common.py", "machine_update.py", "machine_update.bat",
        "uninstall_machine.py", "uninstall_machine.bat",
    };

    static readonly string[] PlayerDataFiles = new string[]
    {
        "profile.json", "net.json", "update.json", "installed.json", "lang",
    };

    // ------------------------------------------------------------------
    // 控制台 UI（进度条 + 步骤文字）
    //
    // 这一块是"可视化工具"的实际实现。上一版的进度条在 Windows 上大概率看不见，
    // 原因有三个，这里逐个处理：
    //   1) 进度条用 \r 原地重画，但同一条线上还会被日志/普通 print 插话 → 整行被冲花。
    //      解决：画条时置 BarActive，任何日志行先换行收尾，再由下一步重画。
    //   2) 行宽超过控制台宽度会自动折行，\r 就再也回不到行首 → 视觉效果彻底乱掉。
    //      解决：每行按控制台宽度截断（留 1 列余量）。
    //   3) █ ░ ✓ 这些字符在 conhost 的点阵字体下不显示（或显示成方块）。
    //      解决：进度条一律用 ASCII 的 # 与 -，步骤标记用 [ OK ] / [FAIL] / [ !! ]。
    // ------------------------------------------------------------------

    static bool _barActive;

    internal static int ConsoleWidth()
    {
        try
        {
            int w = Console.BufferWidth;
            if (w < 40) w = 100;
            return w;
        }
        catch (Exception) { return 100; }
    }

    static string Clip(string s)
    {
        int w = ConsoleWidth() - 1;
        if (s.Length <= w) return s;
        return s.Substring(0, w);
    }

    static void BarEnd()
    {
        if (!_barActive) return;
        Console.WriteLine();
        _barActive = false;
    }

    // 供 Installer.LogLine 调用：日志行不能跟进度条挤在同一行
    internal static void BeforeLogLine()
    {
        BarEnd();
    }

    static void Color(ConsoleColor c)
    {
        try { Console.ForegroundColor = c; } catch (Exception) { }
    }

    static void ResetColor()
    {
        try { Console.ResetColor(); } catch (Exception) { }
    }

    static void Line(string text, ConsoleColor c)
    {
        BarEnd();
        Color(c);
        Console.WriteLine(text);
        ResetColor();
    }

    internal static void Banner(string title)
    {
        BarEnd();
        Color(ConsoleColor.Cyan);
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  " + title);
        Console.WriteLine(new string('=', 60));
        ResetColor();
        Console.WriteLine();
    }

    /// <summary>画一条进度条： [####----------]  33%  正在做某事</summary>
    internal static void Step(int index, int total, string message)
    {
        if (total <= 0) total = 1;
        if (index < 1) index = 1;
        if (index > total) index = total;
        int pct = index * 100 / total;
        const int barLen = 24;
        int filled = index * barLen / total;
        var sb = new StringBuilder();
        sb.Append('[');
        sb.Append(new string('#', filled));
        sb.Append(new string('-', barLen - filled));
        sb.Append("] ");
        sb.Append(pct.ToString().PadLeft(3));
        sb.Append("%  ");
        sb.Append(message);
        string line = Clip(sb.ToString());
        Color(ConsoleColor.White);
        // 先 \r 回行首，把整行连同补位空格写出去（冲掉上一次更长的残留），
        // 再 \r 回车等待 BarEnd() 收尾换行。整体写入宽度控制在 ConsoleWidth()-1
        // 以内，避免触发自动折行把 \r 打乱。
        Console.Write("\r" + line);
        int pad = ConsoleWidth() - 1 - line.Length;
        if (pad > 0) Console.Write(new string(' ', pad));
        Console.Write("\r");
        ResetColor();
        _barActive = true;
    }

    internal static void StepOk(string message)
    {
        BarEnd();
        Color(ConsoleColor.Green);
        Console.WriteLine("  [ OK ] " + message);
        ResetColor();
    }

    internal static void StepSkip(string message)
    {
        BarEnd();
        Color(ConsoleColor.DarkGray);
        Console.WriteLine("  [ -- ] " + message);
        ResetColor();
    }

    internal static void Warn(string message)
    {
        Line("[ !! ] " + message, ConsoleColor.Yellow);
    }

    internal static void Err(string message)
    {
        Line("[FAIL] " + message, ConsoleColor.Red);
    }

    internal static void Info(string message)
    {
        Line("       " + message, ConsoleColor.Gray);
    }

    internal static void Pause(bool noPause)
    {
        if (noPause) return;
        try { if (Console.IsInputRedirected) return; } catch (Exception) { return; }
        BarEnd();
        Console.WriteLine();
        Console.Write("按回车键退出...");
        try { Console.ReadLine(); } catch (Exception) { }
    }

    static bool AskYesNo(string question, bool def, bool autoYes)
    {
        if (autoYes) return true;
        try { if (Console.IsInputRedirected) return def; } catch (Exception) { return def; }
        BarEnd();
        Console.Write(question);
        string a;
        try { a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant(); }
        catch (Exception) { return def; }
        if (a.Length == 0) return def;
        return a == "y" || a == "yes" || a == "是";
    }

    static string AskLine(string question, string def, bool autoYes)
    {
        if (autoYes) return def;
        try { if (Console.IsInputRedirected) return def; } catch (Exception) { return def; }
        BarEnd();
        Console.Write(question);
        try { return (Console.ReadLine() ?? "").Trim().Trim('"'); }
        catch (Exception) { return def; }
    }

    // ------------------------------------------------------------------
    // 命令行解析
    // ------------------------------------------------------------------

    static readonly string[] ValueOptions = new string[] { "--source", "--expect-sha256", "--sig", "--game" };

    internal static bool Has(string[] args, params string[] names)
    {
        foreach (string a in args)
            foreach (string n in names)
                if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string FirstValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>取第一个"不是开关、也不是开关取值"的参数，作为显式游戏目录。</summary>
    static string ExplicitDir(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == null || a.Length == 0) continue;
            if (a[0] == '-')
            {
                if (Array.IndexOf(ValueOptions, a.ToLowerInvariant()) >= 0) i++;  // 跳过它的取值
                continue;
            }
            return a;
        }
        return null;
    }

    static void Help()
    {
        Banner("Machine 加载器 v" + LoaderVersion + " - 安装程序");
        Console.WriteLine("用法:");
        Console.WriteLine("  MachineInstaller.exe                      安装（自动定位 Aviassembly）");
        Console.WriteLine("  MachineInstaller.exe --install [目录]     安装到指定目录");
        Console.WriteLine("  MachineInstaller.exe --uninstall [目录]   卸载并还原成纯净版");
        Console.WriteLine("  MachineInstaller.exe --apply-update       应用 Machine\\update 里已下载的更新");
        Console.WriteLine();
        Console.WriteLine("开关:");
        Console.WriteLine("  --auto             自动定位游戏目录（双击运行时默认开启）");
        Console.WriteLine("  --backup           卸载前把 Machine\\ 里的玩家数据备份一份");
        Console.WriteLine("  --purge-mods       卸载时连 mods\\ 一起删（默认保留）");
        Console.WriteLine("  --yes              全部使用默认答案（自动化，不询问）");
        Console.WriteLine("  --quiet            少说话");
        Console.WriteLine("  --no-pause         结束时不等待按键");
        Console.WriteLine("  --allow-unsigned   放行未签名的发布包（仅开发自测）");
        Console.WriteLine("  --force            版本不比当前新也照样应用更新");
        Console.WriteLine();
        Console.WriteLine("退出码: 0 成功 1 参数/环境 2 哈希 3 签名 4 安装器 5 已回滚 6 取消 7 游戏在运行 8 权限");
        Console.WriteLine();
        Console.WriteLine("注：本程序不需要你安装 Python。");
    }

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    /// <summary>本次调用的原始参数（卸载等流程里要重复判断开关）。</summary>
    internal static string[] Args = new string[0];

    internal static int Run(string[] args)
    {
        Args = args;

        // 旧路径的判定要严格：只有"第一个参数就是游戏目录、且没有任何新式开关"时才走旧路径。
        // 之前用 "--uninstall 是否带目录" 来区分，导致
        //   MachineInstaller.exe --uninstall --yes --no-pause "D:\game"
        // 被当成旧调用，于是把 "--uninstall" 本身当成了目录名 ——
        // InitLog 会先把这个不存在的目录建出来，后续检查自然"通过"，
        // 最后什么都没卸载、还留下一个垃圾目录。
        bool legacy = args.Length >= 1
                      && args[0].Length > 0
                      && args[0][0] != '-'
                      && !Has(args, "--auto", "--interactive", "--install", "--apply-update", "--help", "-h");
        if (legacy) return LegacyMain(args);

        string dir = ExplicitDir(args);

        if (Has(args, "--help", "-h"))
        {
            Help();
            Pause(Has(args, "--no-pause"));
            return Installer.RcOk;
        }

        bool yes = Has(args, "--yes", "-y");
        bool noPause = Has(args, "--no-pause");
        if (Has(args, "--quiet")) Installer.Verbose = false;
        bool allowUnsigned = Has(args, "--allow-unsigned");

        try
        {
            if (Has(args, "--apply-update")) return ApplyUpdate(dir, yes, noPause, allowUnsigned);
            if (Has(args, "--uninstall")) return Uninstall(dir, yes, noPause);
            return Install(dir, yes, noPause, allowUnsigned);
        }
        catch (Exception e)
        {
            Err("未预期的错误: " + e.Message);
            Installer.LogLine("[Machine] cli exception: " + e);
            return Installer.RcException;
        }
    }

    /// <summary>旧调用路径（第三方脚本/自测仍在使用），语义与历史版本完全一致。</summary>
    static int LegacyMain(string[] args)
    {
        try
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法: MachineInstaller.exe <游戏目录> [--uninstall | --update [--source <dll>] [--expect-sha256 <hex>]]");
                return Installer.RcUsage;
            }
            string gameDir = Path.GetFullPath(args[0]);
            // 注意顺序：InitLog 会顺手把 <gameDir>\Machine\logs 建出来，
            // 先建目录再检查目录是否存在就永远"存在"了（曾经因此静默跑完一趟空卸载）。
            if (!Directory.Exists(gameDir))
            {
                Console.WriteLine("错误: 目录不存在 - " + gameDir);
                return Installer.RcUsage;
            }
            Installer.InitLog(gameDir);
            bool uninstall = Has(args, "--uninstall");
            string dataDir = Path.Combine(gameDir, "Aviassembly_Data");
            string managedDir = Path.Combine(dataDir, "Managed");
            string asmPath = Path.Combine(managedDir, AsmDll);
            string corePath = Path.Combine(managedDir, CoreDll);
            string backupPath = asmPath + ".machinebak";

            if (uninstall) return Installer.DoUninstall(gameDir, asmPath, corePath, backupPath);
            if (Has(args, "--update"))
                return Installer.DoApplyUpdate(gameDir, corePath, FirstValue(args, "--source"), FirstValue(args, "--expect-sha256"));
            return Installer.DoInstall(gameDir, asmPath, corePath, backupPath);
        }
        catch (Exception e)
        {
            Installer.LogLine("安装器异常: " + e);
            return Installer.RcException;
        }
    }

    // ------------------------------------------------------------------
    // 发布包校验：SHA-256 + RSA PKCS#1 v1.5 (SHA-256)
    // ------------------------------------------------------------------

    static string DistRoot()
    {
        try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        catch (Exception) { return Directory.GetCurrentDirectory(); }
    }

    static string CoreSha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            byte[] h = sha.ComputeHash(fs);
            var sb = new StringBuilder(h.Length * 2);
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }

    internal static int ByteLen(BigInteger v)
    {
        byte[] b = v.ToByteArray();
        int n = b.Length;
        while (n > 1 && b[n - 1] == 0) n--;
        return n;
    }

    static byte[] BigEndian(BigInteger v, int k)
    {
        byte[] le = v.ToByteArray();
        int n = le.Length;
        while (n > 1 && le[n - 1] == 0) n--;
        if (n > k) return null;
        byte[] be = new byte[k];
        for (int i = 0; i < n; i++) be[k - 1 - i] = le[i];
        return be;
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>
    /// RSA PKCS#1 v1.5 + SHA-256 验签。用 BigInteger 做 s^e mod n，
    /// 与 machine_common.rsa_verify_sha256 是同一套算法（不依赖本机 CryptoAPI 是否支持 SHA-256）。
    /// </summary>
    internal static bool VerifyRsaSha256(byte[] message, byte[] signature)
    {
        if (message == null || signature == null || signature.Length == 0) return false;
        BigInteger n, e;
        try
        {
            n = BigInteger.Parse("0" + PubModulusHex, System.Globalization.NumberStyles.HexNumber);
            e = BigInteger.Parse("0" + PubExponentHex, System.Globalization.NumberStyles.HexNumber);
        }
        catch (Exception) { return false; }
        if (n.Sign <= 0 || e <= 1) return false;

        int k = ByteLen(n);
        if (signature.Length != k) return false;

        // BigInteger(byte[]) 是按**小端补码**解释的：签名最高字节 >= 0x80 时整数值会变负，
        // 于是 s.Sign < 0 直接判失败 —— 大约一半的签名会踩中这个坑（必须补 0x00 强制为正）。
        byte[] le = Reverse(signature);
        byte[] lePos = new byte[le.Length + 1];
        Buffer.BlockCopy(le, 0, lePos, 0, le.Length);
        BigInteger s = new BigInteger(lePos);
        if (s.Sign <= 0) return false;
        byte[] em = BigEndian(BigInteger.ModPow(s, e, n), k);
        if (em == null) return false;

        byte[] digest;
        using (var sha = SHA256.Create()) digest = sha.ComputeHash(message);

        // DigestInfo 前缀：SHA-256 (RFC 8017 / EMSA-PKCS1-v1_5)
        byte[] prefix = Hex("3031300d060960864801650304020105000420");
        byte[] t = new byte[prefix.Length + digest.Length];
        Buffer.BlockCopy(prefix, 0, t, 0, prefix.Length);
        Buffer.BlockCopy(digest, 0, t, prefix.Length, digest.Length);
        if (k < t.Length + 11) return false;

        byte[] expect = new byte[k];
        expect[0] = 0x00;
        expect[1] = 0x01;
        for (int i = 2; i < k - t.Length - 1; i++) expect[i] = 0xFF;
        expect[k - t.Length - 1] = 0x00;
        Buffer.BlockCopy(t, 0, expect, k - t.Length, t.Length);
        return BytesEqual(em, expect);
    }

    static byte[] Reverse(byte[] b)
    {
        byte[] r = new byte[b.Length];
        for (int i = 0; i < b.Length; i++) r[i] = b[b.Length - 1 - i];
        return r;
    }

    static byte[] Hex(string s)
    {
        byte[] b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>签名文件既可能是二进制 .sig，也可能是 base64 文本（本发布流程写的是 base64）。</summary>
    static byte[] LoadSignature(params string[] candidates)
    {
        foreach (string c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (!File.Exists(c)) continue;
            byte[] data;
            try { data = File.ReadAllBytes(c); }
            catch (Exception) { continue; }
            if (data.Length == 0) continue;
            bool looksBinary = (data[0] == 0x00 || data[0] == 0x01) && Array.IndexOf(data, (byte)'\n') > 64;
            if (!looksBinary)
            {
                try
                {
                    string text = Encoding.ASCII.GetString(data);
                    var clean = new StringBuilder();
                    foreach (char ch in text)
                        if (!char.IsWhiteSpace(ch)) clean.Append(ch);
                    if (clean.Length > 0)
                    {
                        byte[] dec = Convert.FromBase64String(clean.ToString());
                        if (dec.Length > 0) return dec;
                    }
                }
                catch (Exception) { }
            }
            return data;
        }
        return null;
    }

    /// <summary>不引入 JSON 库的最小取字符串字段实现（发布清单都是扁平结构）。</summary>
    internal static string JsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (at < 0) return "";
        at = json.IndexOf(':', at + key.Length + 2);
        if (at < 0) return "";
        at++;
        while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
        if (at >= json.Length || json[at] != '"') return "";
        at++;
        var sb = new StringBuilder();
        while (at < json.Length && json[at] != '"')
        {
            if (json[at] == '\\' && at + 1 < json.Length) { at++; }
            sb.Append(json[at]);
            at++;
        }
        return sb.ToString();
    }

    static string ReadTextNoBom(string path)
    {
        string s = File.ReadAllText(path, new UTF8Encoding(false));
        if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
        return s;
    }

    internal static string PublicKeyFingerprint()
    {
        byte[] raw = new byte[PubModulusHex.Length / 2 + PubExponentHex.Length / 2];
        byte[] m = Hex(PubModulusHex);
        byte[] e = Hex(PubExponentHex);
        Buffer.BlockCopy(m, 0, raw, 0, m.Length);
        Buffer.BlockCopy(e, 0, raw, m.Length, e.Length);
        using (var sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(raw);
            var sb = new StringBuilder(h.Length * 2);
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 校验"要装进去的那份 Machine.Core.dll"：先比 version.json 记录的 SHA-256，再验 RSA 签名。
    /// 返回核心哈希；校验不过直接以对应退出码退出（拒绝安装，而不是"失败当成功"）。
    /// </summary>
    static string VerifyDist(string dist, bool yes, bool allowUnsigned)
    {
        string core = Path.Combine(dist, CoreDll);
        if (!File.Exists(core))
        {
            Err("找不到 " + CoreDll + "（应与安装器同目录）");
            throw new ExitException(Installer.RcUsage);
        }

        string sha = CoreSha256(core);
        string manifestPath = Path.Combine(dist, VersionJson);
        string declared = "", sigInline = "", version = "", fp = "";
        if (File.Exists(manifestPath))
        {
            try
            {
                string doc = ReadTextNoBom(manifestPath);
                declared = JsonString(doc, "sha256").ToLowerInvariant();
                sigInline = JsonString(doc, "sig");
                version = JsonString(doc, "version");
                fp = JsonString(doc, "signerFingerprint");
                if (declared.Length == 0) declared = JsonString(doc, "core_sha256").ToLowerInvariant();
            }
            catch (Exception ex) { Warn("version.json 读取失败: " + ex.Message); }
        }
        else
        {
            Warn("未找到 version.json，无法核对发布包版本与哈希");
        }

        Info("发布包版本: " + (version.Length > 0 ? version : "?"));
        Info(CoreDll + " SHA-256: " + sha);

        if (declared.Length > 0 && !string.Equals(declared, sha, StringComparison.OrdinalIgnoreCase))
        {
            Err("发布包自检失败：核心哈希与 version.json 记录不一致（包可能损坏或被篡改）");
            Info("期望: " + declared);
            Info("实际: " + sha);
            throw new ExitException(Installer.RcHash);
        }

        byte[] sig = LoadSignature(core + ".sig", sigInline);
        if (sig == null)
        {
            Warn("未找到签名文件 " + CoreDll + ".sig（本次发布包未签名）");
            if (!allowUnsigned && !AskYesNo("安装未签名的加载器有被篡改的风险。仍要继续? (y/N): ", false, yes))
                throw new ExitException(Installer.RcCancelled);
            return sha;
        }

        byte[] data = File.ReadAllBytes(core);
        if (!VerifyRsaSha256(data, sig))
        {
            Err("签名校验失败，拒绝安装（文件可能被替换）。请从官方发布页重新下载。");
            throw new ExitException(Installer.RcSignature);
        }
        StepOk("签名校验通过");
        string mine = PublicKeyFingerprint();
        Info("发布公钥指纹: " + mine);
        if (fp.Length > 0 && !string.Equals(fp, mine, StringComparison.OrdinalIgnoreCase))
            Warn("version.json 里的公钥指纹与本程序内置的不一致（可能来自另一条发布链）");
        return sha;
    }

    // ------------------------------------------------------------------
    // 游戏目录定位
    // ------------------------------------------------------------------

    static bool LooksLikeGame(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, GameExe))
               && File.Exists(Path.Combine(Path.Combine(dir, ManagedRel), AsmDll));
    }

    static void AddCandidate(List<string> list, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        if (!LooksLikeGame(dir)) return;
        foreach (string s in list)
            if (string.Equals(s, dir, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(dir);
    }

    static string ReadRegistryString(RegistryKey root, string sub, string name)
    {
        try
        {
            using (RegistryKey k = root.OpenSubKey(sub))
            {
                if (k == null) return null;
                object v = k.GetValue(name);
                return v == null ? null : v.ToString();
            }
        }
        catch (Exception) { return null; }
    }

    static List<string> FindCandidates()
    {
        var found = new List<string>();

        // 0) 发布包被解压进游戏目录、或就在游戏目录旁边的情况
        string self = DistRoot();
        AddCandidate(found, self);
        try { AddCandidate(found, Path.GetDirectoryName(self)); } catch (Exception) { }
        AddCandidate(found, Path.Combine(self, "Aviassembly"));

        // 1) Steam 注册表 + libraryfolders.vdf
        var steamRoots = new List<string>();
        string[] subs = new string[] { @"Software\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" };
        foreach (string sub in subs)
        {
            string p = ReadRegistryString(Registry.CurrentUser, sub, "SteamPath")
                       ?? ReadRegistryString(Registry.CurrentUser, sub, "InstallPath")
                       ?? ReadRegistryString(Registry.LocalMachine, sub, "InstallPath")
                       ?? ReadRegistryString(Registry.LocalMachine, sub, "SteamPath");
            if (!string.IsNullOrEmpty(p))
            {
                p = p.Replace('/', '\\');
                if (!steamRoots.Contains(p)) steamRoots.Add(p);
            }
        }
        foreach (string s in steamRoots)
        {
            AddCandidate(found, Path.Combine(Path.Combine(Path.Combine(s, "steamapps"), "common"), "Aviassembly"));
            string vdf = Path.Combine(Path.Combine(s, "steamapps"), "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string txt;
            try { txt = File.ReadAllText(vdf); }
            catch (Exception) { continue; }
            foreach (Match m in Regex.Matches(txt, "\"path\"\\s+\"([^\"]+)\""))
            {
                string lib = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                AddCandidate(found, Path.Combine(Path.Combine(Path.Combine(lib, "steamapps"), "common"), "Aviassembly"));
            }
        }

        // 2) 常见安装位置
        foreach (string pf in new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                             Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            AddCandidate(found, Path.Combine(pf, "Steam", "steamapps", "common", "Aviassembly"));
            AddCandidate(found, Path.Combine(pf, "Aviassembly"));
        }
        AddCandidate(found, @"C:\SteamLibrary\steamapps\common\Aviassembly");
        AddCandidate(found, @"D:\SteamLibrary\steamapps\common\Aviassembly");
        AddCandidate(found, @"D:\Steam\steamapps\common\Aviassembly");

        if (found.Count > 0) return found;

        // 3) 兜底：限深全盘扫描
        Info("未从 Steam 记录里找到，开始扫描磁盘（可能要几十秒）...");
        for (char d = 'C'; d <= 'Z'; d++)
        {
            string drive = d + ":\\";
            try { if (!Directory.Exists(drive)) continue; }
            catch (Exception) { continue; }
            ScanDrive(drive, found, 0);
            if (found.Count > 0) break;
        }
        return found;
    }

    static readonly string[] SkipDirs = new string[]
    {
        "windows", "$recycle.bin", "system volume information", "programdata", "appdata",
        "node_modules", ".git", "perflogs", "recovery", "msocache",
    };

    static void ScanDrive(string dir, List<string> found, int depth)
    {
        if (depth > 4 || found.Count > 0) return;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch (Exception) { return; }
        foreach (string sub in subs)
        {
            if (found.Count > 0) return;
            string name;
            try { name = Path.GetFileName(sub).ToLowerInvariant(); }
            catch (Exception) { continue; }
            if (Array.IndexOf(SkipDirs, name) >= 0) continue;
            if (name.StartsWith("aviassembly"))
            {
                if (LooksLikeGame(sub))
                {
                    found.Add(sub);
                    return;
                }
            }
            ScanDrive(sub, found, depth + 1);
        }
    }

    static string PickGameDir(string explicitDir, bool yes, bool allowUnsigned)
    {
        if (!string.IsNullOrEmpty(explicitDir))
        {
            string full = Path.GetFullPath(explicitDir.Trim('"').Trim());
            if (!LooksLikeGame(full)) return full;   // 交给调用方报"目录无效"，信息更准确
            return full;
        }
        List<string> cands = FindCandidates();
        if (cands.Count == 0) return null;
        if (cands.Count == 1)
        {
            Info("游戏目录: " + cands[0]);
            return cands[0];
        }
        Line("找到多个候选游戏目录:", ConsoleColor.White);
        for (int i = 0; i < cands.Count; i++) Console.WriteLine("  [" + (i + 1) + "] " + cands[i]);
        string sel = AskLine("直接回车使用第一个，或输入编号 / 手动路径: ", "", yes);
        if (sel.Length == 0) return cands[0];
        int n;
        if (int.TryParse(sel, out n) && n >= 1 && n <= cands.Count) return cands[n - 1];
        if (Directory.Exists(sel)) return Path.GetFullPath(sel);
        Warn("无法识别的输入，使用第一个候选。");
        return cands[0];
    }

    // ------------------------------------------------------------------
    // 环境探测
    // ------------------------------------------------------------------

    static List<string> GameProcesses()
    {
        var pids = new List<string>();
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(GameExe);
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(baseName))
            {
                try { pids.Add(p.Id.ToString()); }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
        }
        catch (Exception) { }
        return pids;
    }

    static bool IsFileLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch (Exception) { return true; }
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".machine_write_probe.tmp");
            File.WriteAllBytes(probe, new byte[] { 0x78 });
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    static void CopyTree(string src, string dst, bool overwrite, string[] skipDirs)
    {
        if (!Directory.Exists(src)) return;
        try { Directory.CreateDirectory(dst); }
        catch (Exception e) { Warn("无法创建目录 " + dst + ": " + e.Message); return; }
        foreach (string item in Directory.GetDirectories(src))
        {
            string name = Path.GetFileName(item);
            if (skipDirs != null && Array.IndexOf(skipDirs, name) >= 0) continue;
            CopyTree(item, Path.Combine(dst, name), overwrite, skipDirs);
        }
        foreach (string file in Directory.GetFiles(src))
        {
            string to = Path.Combine(dst, Path.GetFileName(file));
            try
            {
                if (File.Exists(to) && !overwrite) continue;
                File.Copy(file, to, true);
            }
            catch (Exception e) { Warn("跳过 " + Path.GetFileName(file) + " (" + e.Message + ")"); }
        }
    }

    static void WriteJson(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    static string JsonEscape(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static void WriteRecord(string gameDir, string coreVer, string coreSha, string asmSha)
    {
        string exeSha = "";
        try { exeSha = CoreSha256(Assembly.GetExecutingAssembly().Location); }
        catch (Exception) { }
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"loaderVersion\": \"" + JsonEscape(LoaderVersion) + "\",");
        sb.AppendLine("  \"coreVersion\": \"" + JsonEscape(coreVer) + "\",");
        sb.AppendLine("  \"coreSha256\": \"" + JsonEscape(coreSha) + "\",");
        sb.AppendLine("  \"installerSha256\": \"" + JsonEscape(exeSha) + "\",");
        sb.AppendLine("  \"assemblySha256AfterInject\": \"" + JsonEscape(asmSha) + "\",");
        sb.AppendLine("  \"installedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",");
        sb.AppendLine("  \"installerPath\": \"" + JsonEscape(Assembly.GetExecutingAssembly().Location) + "\"");
        sb.AppendLine("}");
        try
        {
            WriteJson(Path.Combine(Path.Combine(gameDir, LoaderName), RecordFileName), sb.ToString());
        }
        catch (Exception e) { Warn("写 " + RecordFileName + " 失败: " + e.Message); }
    }

    // ------------------------------------------------------------------
    // 安装
    // ------------------------------------------------------------------

    internal static int Install(string explicitDir, bool yes, bool noPause, bool allowUnsigned)
    {
        _barActive = false;
        Banner("Machine 加载器 - 安装程序  v" + LoaderVersion);

        const int total = 10;
        string dist = DistRoot();

        Step(1, total, "检查运行环境...");
        if (!File.Exists(Path.Combine(dist, InstallerExe)) || !File.Exists(Path.Combine(dist, CecilDll)))
        {
            Err("发布包不完整：本程序需要与 " + InstallerExe + " / " + CecilDll + " 放在同一目录。");
            return Fail(Installer.RcUsage, noPause);
        }
        StepOk("安装器就绪");

        Step(2, total, "校验发布包完整性与签名...");
        string coreSha, coreVer = "";
        try
        {
            coreVer = JsonString(File.Exists(Path.Combine(dist, VersionJson))
                                 ? ReadTextNoBom(Path.Combine(dist, VersionJson)) : "", "version");
            coreSha = VerifyDist(dist, yes, allowUnsigned);
        }
        catch (ExitException e)
        {
            return Fail(e.Code, noPause);
        }
        StepOk("核心 " + (coreVer.Length > 0 ? "v" + coreVer : coreSha.Substring(0, 16)));

        Step(3, total, "定位 Aviassembly 游戏目录...");
        string gameDir = PickGameDir(explicitDir, yes, allowUnsigned);
        if (string.IsNullOrEmpty(gameDir) || !LooksLikeGame(gameDir))
        {
            Err("未找到有效的 Aviassembly 游戏目录（需要 " + GameExe + " 与 " + ManagedRel + "\\" + AsmDll + "）。");
            Info("可以手动指定：MachineInstaller.exe --install \"D:\\路径\\Aviassembly\"");
            return Fail(Installer.RcUsage, noPause);
        }
        // 装到哪里一定要让用户看到并确认一次：自动定位有可能会命中
        // 另一个 Aviassembly 副本（比如 Steam 正版目录），不问一句就写进去太危险。
        if (!AskYesNo("确认安装到 " + gameDir + " ? (Y/n): ", true, yes))
        {
            Info("已取消。");
            StepSkip("用户取消");
            return Fail(Installer.RcCancelled, noPause);
        }
        Installer.InitLog(gameDir);
        Installer.LogLine("[Machine] cli install -> " + gameDir);
        StepOk(gameDir);

        Step(4, total, "检查游戏状态与文件权限...");
        List<string> pids = GameProcesses();
        if (pids.Count > 0)
        {
            Err("检测到游戏正在运行 (PID: " + string.Join(", ", pids.ToArray()) + ")，请先完全退出游戏再安装。");
            return Fail(Installer.RcGameRunning, noPause);
        }
        string managedDir = Path.Combine(gameDir, ManagedRel);
        string asmPath = Path.Combine(managedDir, AsmDll);
        if (IsFileLocked(asmPath))
        {
            Err("目标程序集被占用: " + asmPath);
            Info("请确认游戏与杀毒软件未占用该文件。");
            return Fail(Installer.RcGameRunning, noPause);
        }
        if (!CanWrite(managedDir))
        {
            Err("没有写权限: " + managedDir);
            Info("请右键 install_machine.bat -> 以管理员身份运行，或直接把游戏装在用户目录下。");
            return Fail(Installer.RcPermission, noPause);
        }
        StepOk("游戏未运行，权限正常");

        Step(5, total, "检查已有安装...");
        string installedCore = Path.Combine(managedDir, CoreDll);
        if (File.Exists(installedCore))
        {
            string old = "";
            try { old = CoreSha256(installedCore); }
            catch (Exception) { }
            Info("检测到 Machine 已安装（当前核心 " + (old.Length > 0 ? old.Substring(0, 16) : "?") + "）");
            if (string.Equals(old, coreSha, StringComparison.OrdinalIgnoreCase))
                Info("与本次要安装的版本完全相同。");
            if (!AskYesNo("覆盖安装（保留存档与配置）? (y/N): ", false, yes))
            {
                Info("已取消。");
                StepSkip("用户取消");
                return Fail(Installer.RcCancelled, noPause);
            }
            StepOk("将覆盖安装");
        }
        else StepOk("全新安装");

        Step(6, total, "注入启动钩子到 " + AsmDll + "...");
        int rc = Installer.DoInstall(gameDir, asmPath, installedCore,
                                     asmPath + ".machinebak");
        if (rc != Installer.RcOk)
        {
            Err("安装器执行失败（exit=" + rc + "）");
            Info("游戏可能仍在运行，或杀毒软件拦截了对 " + AsmDll + " 的修改。");
            Info("日志: " + Path.Combine(Path.Combine(gameDir, LoaderName), @"logs\installer.log"));
            return Fail(Installer.RcInstaller, noPause);
        }
        StepOk("启动钩子已注入");

        Step(7, total, "校验安装结果...");
        try
        {
            string got = CoreSha256(installedCore);
            if (!string.Equals(got, coreSha, StringComparison.OrdinalIgnoreCase))
            {
                Err("安装后的核心哈希与发布包不一致（期望 " + coreSha + "，实际 " + got + "）");
                Info("将回滚程序集并移除已写入的核心。");
                Installer.DoUninstall(gameDir, asmPath, installedCore, asmPath + ".machinebak");
                return Fail(Installer.RcRolledback, noPause);
            }
        }
        catch (Exception e)
        {
            Err("无法读取安装后的核心: " + e.Message);
            return Fail(Installer.RcHash, noPause);
        }
        StepOk("校验通过");

        Step(8, total, "部署 Machine 运行时文件...");
        string machineDir = Path.Combine(gameDir, LoaderName);
        string tpl = Path.Combine(dist, LoaderName);
        if (Directory.Exists(tpl))
            CopyTree(tpl, machineDir, false, new string[] { "logs", "update", "backup", "lang" });
        Directory.CreateDirectory(Path.Combine(machineDir, "logs"));
        int deployed = 0;
        foreach (string name in MachineRuntimeFiles)
        {
            string src = Path.Combine(dist, name);
            if (!File.Exists(src)) continue;
            try { File.Copy(src, Path.Combine(machineDir, name), true); deployed++; }
            catch (Exception e) { Warn("复制 " + name + " 失败: " + e.Message); }
        }
        StepOk("运行时文件已部署（" + deployed + " 个）");

        Step(9, total, "部署 mods/ 与写入安装记录...");
        string modsTpl = Path.Combine(dist, "mods");
        if (Directory.Exists(modsTpl))
        {
            CopyTree(modsTpl, Path.Combine(gameDir, "mods"), false, null);
            StepOk("mods/ 模板已部署");
        }
        else StepSkip("发布包内没有 mods/ 模板");
        string asmSha = "";
        try { if (File.Exists(asmPath)) asmSha = CoreSha256(asmPath); }
        catch (Exception) { }
        WriteRecord(gameDir, coreVer, coreSha, asmSha);

        Step(10, total, "收尾...");
        StepOk("安装完成");
        BarEnd();

        Console.WriteLine();
        Line("安装完成！现在可以启动游戏。", ConsoleColor.Green);
        Console.WriteLine();
        Console.WriteLine("提示:");
        Console.WriteLine("  1. 游戏内检查到新版本 -> 点击下载 -> 退出游戏后运行:");
        Console.WriteLine("       " + LoaderName + "\\machine_update.bat");
        Console.WriteLine("  2. 如果游戏无法启动或异常，运行 uninstall_machine.bat 一键恢复纯净版，");
        Console.WriteLine("     确认纯净版正常后再重新安装。");
        Console.WriteLine("  3. 日志: " + LoaderName + "\\logs\\Machine.log 与 " + LoaderName + "\\logs\\installer.log");
        Console.WriteLine();
        Pause(noPause);
        return Installer.RcOk;
    }

    // ------------------------------------------------------------------
    // 卸载
    // ------------------------------------------------------------------

    internal static int Uninstall(string explicitDir, bool yes, bool noPause)
    {
        _barActive = false;
        Banner("Machine 加载器 - 一键卸载");

        const int total = 8;
        Step(1, total, "定位 Aviassembly 游戏目录...");
        string gameDir = PickGameDir(explicitDir, yes, false);
        if (string.IsNullOrEmpty(gameDir) || !LooksLikeGame(gameDir))
        {
            Err("未找到有效的 Aviassembly 游戏目录。");
            Info("可以手动指定：MachineInstaller.exe --uninstall \"D:\\路径\\Aviassembly\"");
            return Fail(Installer.RcUsage, noPause);
        }
        Installer.InitLog(gameDir);
        Installer.LogLine("[Machine] cli uninstall -> " + gameDir);
        StepOk(gameDir);

        Step(2, total, "检查游戏状态...");
        List<string> pids = GameProcesses();
        if (pids.Count > 0)
        {
            Err("检测到游戏正在运行 (PID: " + string.Join(", ", pids.ToArray()) + ")，请先完全退出游戏。");
            return Fail(Installer.RcGameRunning, noPause);
        }
        StepOk("游戏未运行");

        string managedDir = Path.Combine(gameDir, ManagedRel);
        string asmPath = Path.Combine(managedDir, AsmDll);
        string machineDir = Path.Combine(gameDir, LoaderName);

        Step(3, total, "确认卸载范围...");
        BarEnd();
        Console.WriteLine("将移除以下内容:");
        Console.WriteLine("  - " + CoreDll + "（Managed 内）");
        Console.WriteLine("  - " + LoaderName + "/ 目录（日志、配置、更新缓存）");
        Console.WriteLine("  - " + AsmDll + " 的注入钩子（还原为原版）");
        Console.WriteLine("将保留:");
        Console.WriteLine("  - mods/ 目录（你的 Mod 不会被删）");
        bool purgeMods = Has(Args, "--purge-mods");
        if (purgeMods) Console.WriteLine("!! --purge-mods: mods/ 目录也会被删除");
        if (!AskYesNo("确认卸载? (y/N): ", false, yes))
        {
            Info("已取消。");
            StepSkip("用户取消");
            return Fail(Installer.RcCancelled, noPause);
        }

        Step(4, total, "备份玩家数据...");
        string backupDir = null;
        if (Has(Args, "--backup"))
        {
            backupDir = BackupPlayerData(gameDir);
            StepOk(backupDir != null ? "已备份 -> " + Path.GetFileName(backupDir) : "没有可备份的玩家数据");
        }
        else StepSkip("未要求备份（加 --backup 可开启）");

        Step(5, total, "还原 " + AsmDll + "...");
        string corePath = Path.Combine(managedDir, CoreDll);
        int rc = Installer.DoUninstall(gameDir, asmPath, corePath, asmPath + ".machinebak");
        if (rc != Installer.RcOk)
        {
            Err("还原过程中有错误（退出码 " + rc + "）。");
            Info("请关闭游戏与杀毒软件后重试；或手动用 " + AsmDll + ".machinebak 覆盖还原。");
            return Fail(Installer.RcInstaller, noPause);
        }
        StepOk("已还原为原版");

        Step(6, total, "移除 " + LoaderName + "/ 目录...");
        if (Directory.Exists(machineDir))
        {
            try { Directory.Delete(machineDir, true); StepOk("已移除"); }
            catch (Exception e)
            {
                Warn("删除 " + LoaderName + "/ 失败: " + e.Message + "（请手动删除）");
                StepSkip("请手动清理");
            }
        }
        else StepOk("不存在");

        Step(7, total, "处理 mods/ 目录...");
        string modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) StepSkip("不存在");
        else if (purgeMods)
        {
            try { Directory.Delete(modsDir, true); StepOk("已删除（--purge-mods）"); }
            catch (Exception e) { Warn("删除 mods/ 失败: " + e.Message); StepSkip("请手动清理"); }
        }
        else StepOk("已保留（如需清理请加 --purge-mods）");

        Step(8, total, "收尾...");
        StepOk("卸载完成");
        BarEnd();

        Console.WriteLine();
        Line("卸载完成！游戏已恢复为纯净版。", ConsoleColor.Green);
        Console.WriteLine();
        Console.WriteLine("卸载信息:");
        Console.WriteLine("  游戏目录:   " + gameDir);
        if (backupDir != null) Console.WriteLine("  玩家数据备份: " + Path.GetFileName(backupDir));
        Console.WriteLine();
        Console.WriteLine("下一步：");
        Console.WriteLine("  1. 启动游戏，确认纯净版可正常运行");
        Console.WriteLine("  2. 确认无误后再重新安装 Machine");
        Console.WriteLine();
        Pause(noPause);
        return Installer.RcOk;
    }

    static string BackupPlayerData(string gameDir)
    {
        string src = Path.Combine(gameDir, LoaderName);
        if (!Directory.Exists(src)) return null;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dst = Path.Combine(gameDir, "Machine_uninstalled_" + stamp);
        try { Directory.CreateDirectory(dst); }
        catch (Exception e) { Warn("无法创建备份目录 " + dst + ": " + e.Message); return null; }
        int n = 0;
        foreach (string name in PlayerDataFiles)
        {
            string p = Path.Combine(src, name);
            if (File.Exists(p))
            {
                try { File.Copy(p, Path.Combine(dst, name), true); n++; }
                catch (Exception e) { Warn("备份 " + name + " 失败: " + e.Message); }
            }
            else if (Directory.Exists(p))
            {
                try { CopyTree(p, Path.Combine(dst, name), true, null); n++; }
                catch (Exception e) { Warn("备份 " + name + " 失败: " + e.Message); }
            }
        }
        Info("已备份 " + n + " 项到 " + Path.GetFileName(dst));
        return n > 0 ? dst : dst;
    }

    // ------------------------------------------------------------------
    // 应用更新（游戏内下载完 -> 退出游戏 -> 跑 machine_update.bat）
    // ------------------------------------------------------------------

    internal static int ApplyUpdate(string explicitDir, bool yes, bool noPause, bool allowUnsigned)
    {
        _barActive = false;
        Banner("Machine 加载器 - 应用更新");

        const int total = 6;
        Step(1, total, "定位 Aviassembly 游戏目录...");
        string gameDir = PickGameDir(explicitDir, yes, allowUnsigned);
        if (string.IsNullOrEmpty(gameDir) || !LooksLikeGame(gameDir))
        {
            Err("未找到有效的 Aviassembly 游戏目录。");
            Info("可以手动指定：MachineInstaller.exe --apply-update \"D:\\路径\\Aviassembly\"");
            return Fail(Installer.RcUsage, noPause);
        }
        Installer.InitLog(gameDir);
        Installer.LogLine("[Machine] cli apply-update -> " + gameDir);
        StepOk(gameDir);

        Step(2, total, "检查游戏状态...");
        List<string> pids = GameProcesses();
        if (pids.Count > 0)
        {
            Err("检测到游戏正在运行 (PID: " + string.Join(", ", pids.ToArray()) + ")，请先完全退出游戏。");
            return Fail(Installer.RcGameRunning, noPause);
        }
        StepOk("游戏未运行");

        Step(3, total, "读取待应用的更新包...");
        string upDir = Path.Combine(Path.Combine(gameDir, LoaderName), "update");
        string src = Path.Combine(upDir, CoreDll);
        string sigPath = Path.Combine(upDir, CoreDll + ".sig");
        string applyJson = Path.Combine(upDir, "apply.json");
        if (!File.Exists(src))
        {
            Err("未找到待应用的更新: " + src);
            Info("请先在游戏内点击下载，得到新版本后再退出游戏运行本程序。");
            return Fail(Installer.RcUsage, noPause);
        }
        string expectSha = "";
        string sigInline = "";
        string newVer = "";
        if (File.Exists(applyJson))
        {
            try
            {
                string doc = ReadTextNoBom(applyJson);
                expectSha = JsonString(doc, "sha256").ToLowerInvariant();
                sigInline = JsonString(doc, "sig");
                newVer = JsonString(doc, "version");
            }
            catch (Exception e) { Warn("apply.json 读取失败: " + e.Message); }
        }
        string gotSha = CoreSha256(src);
        if (expectSha.Length > 0 && !string.Equals(expectSha, gotSha, StringComparison.OrdinalIgnoreCase))
        {
            Err("更新包哈希与清单不符（期望 " + expectSha + "，实际 " + gotSha + "），已拒绝写入。");
            return Fail(Installer.RcHash, noPause);
        }
        StepOk("更新包 v" + (newVer.Length > 0 ? newVer : "?") + "  " + gotSha.Substring(0, 16));

        Step(4, total, "校验更新包签名...");
        byte[] sig = LoadSignature(sigPath, sigInline);
        if (sig == null)
        {
            Warn("未找到更新包签名（" + CoreDll + ".sig）");
            if (!allowUnsigned && !AskYesNo("应用未签名的更新有被篡改的风险。仍要继续? (y/N): ", false, yes))
                return Fail(Installer.RcSignature, noPause);
        }
        else if (!VerifyRsaSha256(File.ReadAllBytes(src), sig))
        {
            Err("更新包签名校验失败，已拒绝应用（文件可能被替换）。");
            return Fail(Installer.RcSignature, noPause);
        }
        else StepOk("签名校验通过");

        Step(5, total, "写入 Managed（备份 + 原子替换 + 复核）...");
        string corePath = Path.Combine(Path.Combine(gameDir, ManagedRel), CoreDll);
        int rc = Installer.DoApplyUpdate(gameDir, corePath, src, gotSha);
        if (rc != Installer.RcOk)
        {
            Err("应用更新失败（退出码 " + rc + "）。");
            if (rc == Installer.RcRolledback) Info("已回滚到更新前的核心，游戏仍可用。");
            Info("日志: " + Path.Combine(Path.Combine(gameDir, LoaderName), @"logs\installer.log"));
            return Fail(rc, noPause);
        }
        StepOk("新核心已就位");

        Step(6, total, "写入安装记录...");
        string asmPath = Path.Combine(Path.Combine(gameDir, ManagedRel), AsmDll);
        string asmSha = "";
        try { if (File.Exists(asmPath)) asmSha = CoreSha256(asmPath); }
        catch (Exception) { }
        WriteRecord(gameDir, newVer, gotSha, asmSha);
        StepOk("已更新记录");
        BarEnd();

        Console.WriteLine();
        Line("更新已应用（" + (newVer.Length > 0 ? "v" + newVer : "版本未知") + "）。现在可以启动游戏。", ConsoleColor.Green);
        Console.WriteLine();
        Pause(noPause);
        return Installer.RcOk;
    }

    // ------------------------------------------------------------------

    internal static int Fail(int code, bool noPause)
    {
        BarEnd();
        Console.WriteLine();
        Line("退出码 " + code + "：" + ExitCodeText(code), ConsoleColor.Red);
        Pause(noPause);
        return code;
    }

    internal static string ExitCodeText(int code)
    {
        switch (code)
        {
            case 0: return "成功";
            case 1: return "参数或环境不正确";
            case 2: return "哈希校验失败";
            case 3: return "签名校验失败";
            case 4: return "安装器执行失败";
            case 5: return "失败并已回滚";
            case 6: return "用户取消";
            case 7: return "游戏正在运行";
            case 8: return "权限不足";
            default: return "未知";
        }
    }
}

/// <summary>用它把"校验失败"当控制流抛出去，避免一层层回传。</summary>
internal class ExitException : Exception
{
    internal readonly int Code;
    internal ExitException(int code) : base("exit " + code) { Code = code; }
}
