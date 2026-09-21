using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Machine 安装器：把启动钩子注入游戏 Assembly-CSharp.dll（幂等、可卸载）。
///
/// 用法:
///   MachineInstaller.exe &lt;游戏目录&gt;                              安装
///   MachineInstaller.exe &lt;游戏目录&gt; --uninstall                   卸载（还原程序集）
///   MachineInstaller.exe &lt;游戏目录&gt; --update [--source &lt;dll&gt;]
///                        [--expect-sha256 &lt;hex&gt;]                   应用更新
///
/// 返回码（供 .bat / Python 判断，绝不再"失败当成功"）:
///   0 = 成功
///   1 = 参数/环境错误（目录不存在、缺文件）
///   2 = 未预期的异常
///   3 = 哈希校验失败（更新包被替换/损坏）
///   4 = 写入失败（文件被占用、无权限）
///   5 = 更新失败并已回滚
///
/// 更新流程的健壮性保证：
///   备份旧 DLL → 写临时文件 → 校验 → 原子替换 → 复核 → 失败回滚
///   全过程写入 Machine/logs/installer.log。
/// </summary>
public class MachineInstaller
{
    private const string InjectedType = "Machine.Injected.BootstrapRuntime";
    private const string CoreDll = "Machine.Core.dll";

    internal const int RcOk = 0;
    internal const int RcUsage = 1;
    internal const int RcException = 2;
    internal const int RcHash = 3;
    internal const int RcWrite = 4;
    internal const int RcRolledBack = 5;
    internal const int RcCancelled = 6;
    internal const int RcGameRunning = 7;
    internal const int RcPermission = 8;
    internal const int RcInstaller = 4;
    internal const int RcRolledback = 5;
    internal const int RcSignature = 3;

    /// <summary>false 时只写日志文件，不往控制台刷（--quiet）。</summary>
    internal static bool Verbose = true;

    private static string _logPath;
    private static readonly object _logLock = new object();

    // ------------------------------------------------------------------
    // 日志
    // ------------------------------------------------------------------

    internal static void LogLine(string text)
    {
        if (Verbose)
        {
            // 进度条正在原地重画时，日志行必须先结束那一行，否则两者会互相冲花。
            Cli.BeforeLogLine();
            Console.WriteLine(text);
        }
        try
        {
            lock (_logLock)
            {
                if (_logPath == null) return;
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception) { /* 日志失败不能影响主流程 */ }
    }

    internal static void InitLog(string gameDir)
    {
        try
        {
            string dir = Path.Combine(Path.Combine(gameDir, "Machine"), "logs");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "installer.log");
            LogLine("[Machine] installer start: args=[" + string.Join(" ", Environment.GetCommandLineArgs()) + "]");
        }
        catch (Exception) { _logPath = null; }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    static int Main(string[] args)
    {
        // 输出固定 UTF-8：否则控制台/被上层捕获时按 ANSI(936) 编码，
        // 中文会变成乱码，上级脚本与日志都会难以阅读。
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch (Exception) { /* 无控制台或环境不支持时忽略 */ }

        try
        {
            Console.Title = "Machine 加载器 v" + LoaderVersionText;
        }
        catch (Exception) { }

        try
        {
            return Cli.Run(args);
        }
        catch (Exception e)
        {
            LogLine("安装器异常: " + e);
            return RcException;
        }
    }

    internal const string LoaderVersionText = "2.4.1";

    static string GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static string Sha256Hex(string path)
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

    /// <summary>带回退的重试：文件被占用时游戏/杀毒软件可能短暂锁定。</summary>
    static bool Retry(Func<bool> action, int attempts = 12, int delayMs = 700)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { if (action()) return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (i < attempts - 1) System.Threading.Thread.Sleep(delayMs);
        }
        return false;
    }

    static bool TryCopy(string src, string dst, bool overwrite)
    {
        return Retry(delegate ()
        {
            File.Copy(src, dst, overwrite);
            return true;
        });
    }

    static bool TryDelete(string path)
    {
        return Retry(delegate ()
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        });
    }

    /// <summary>
    /// 原子替换：同目录写 .new → 校验大小 → File.Replace/Move 落位。
    /// 避免"覆盖到一半断电/被杀进程"导致目标 DLL 半截损坏。
    /// </summary>
    static bool AtomicReplace(string src, string dst, out string error)
    {
        error = "";
        string dir = Path.GetDirectoryName(dst);
        if (string.IsNullOrEmpty(dir)) { error = "invalid destination path"; return false; }
        string tmp = Path.Combine(dir, Path.GetFileName(dst) + ".new");
        try
        {
            if (File.Exists(tmp) && !TryDelete(tmp)) { error = "cannot remove stale temp file"; return false; }
            if (!TryCopy(src, tmp, true)) { error = "cannot write temp file (file locked or no permission)"; return false; }
            long a = new FileInfo(src).Length, b = new FileInfo(tmp).Length;
            if (a != b)
            {
                error = "temp file size mismatch (" + b + " != " + a + ")";
                TryDelete(tmp);
                return false;
            }
            bool moved = Retry(delegate ()
            {
                if (File.Exists(dst))
                {
                    try
                    {
                        File.Replace(tmp, dst, null);
                    }
                    catch (Exception)
                    {
                        // Mono/某些文件系统不支持 Replace：退化为删除后改名
                        File.Delete(dst);
                        File.Move(tmp, dst);
                    }
                }
                else
                {
                    File.Move(tmp, dst);
                }
                return true;
            });
            if (!moved) { error = "cannot put file in place (locked or no permission)"; TryDelete(tmp); return false; }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            TryDelete(tmp);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 应用更新
    // ------------------------------------------------------------------

    /// <summary>
    /// 把已校验的新 DLL 替换到 Managed。
    /// 备份 → 原子替换 → 复核哈希 → 失败回滚。any 一步失败都会尝试恢复原状。
    /// </summary>
    internal static int DoApplyUpdate(string gameDir, string corePath, string source, string expectSha)
    {
        string machineDir = Path.Combine(gameDir, "Machine");
        string upDir = Path.Combine(machineDir, "update");
        string src = string.IsNullOrEmpty(source) ? Path.Combine(upDir, CoreDll) : Path.GetFullPath(source);

        if (!File.Exists(src))
        {
            LogLine("[Machine] 未找到待应用的更新: " + src);
            return RcUsage;
        }

        // 1) 调用方（Python）已验签；这里再自查一次哈希，防止"校验之后、写入之前"被替换
        string srcSha = "";
        try { srcSha = Sha256Hex(src); }
        catch (Exception e) { LogLine("[Machine] 错误: 无法读取更新包: " + e.Message); return RcWrite; }

        if (!string.IsNullOrEmpty(expectSha))
        {
            if (!string.Equals(srcSha, expectSha, StringComparison.OrdinalIgnoreCase))
            {
                LogLine("[Machine] 错误: 更新包哈希与预期不符（期望 " + expectSha + "，实际 " + srcSha + "），已拒绝写入");
                return RcHash;
            }
            LogLine("[Machine] 更新包哈希校验通过: " + srcSha);
        }
        else
        {
            LogLine("[Machine] 警告: 未提供 --expect-sha256，跳过哈希比对（实际 " + srcSha + "）");
        }

        string ver = "?";
        try
        {
            var an = System.Reflection.AssemblyName.GetAssemblyName(src);
            if (an != null && an.Version != null) ver = an.Version.ToString();
        }
        catch (Exception) { }

        // 2) 备份当前核心
        string backup = null;
        if (File.Exists(corePath))
        {
            try
            {
                string bakDir = Path.Combine(machineDir, "backup");
                if (!Directory.Exists(bakDir)) Directory.CreateDirectory(bakDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backup = Path.Combine(bakDir, CoreDll + "." + stamp + ".bak");
                if (!TryCopy(corePath, backup, true))
                {
                    LogLine("[Machine] 错误: 无法备份现有 " + CoreDll + "（文件被占用？）。为安全起见中止。");
                    return RcWrite;
                }
                LogLine("[Machine] 已备份现有核心 -> " + backup);
                PruneBackups(bakDir, 5);
            }
            catch (Exception e)
            {
                LogLine("[Machine] 错误: 备份失败 " + e.Message + "。为安全起见中止。");
                return RcWrite;
            }
        }
        else
        {
            LogLine("[Machine] 提示: 未发现已安装的 " + CoreDll + "，按首次安装处理（无备份可回滚）");
        }

        // 3) 原子替换
        string err;
        if (!AtomicReplace(src, corePath, out err))
        {
            LogLine("[Machine] 错误: 无法应用更新 - " + err);
            LogLine("[Machine] 提示: 请确认游戏已完全退出，且杀毒软件未锁定该文件。");
            if (backup != null)
            {
                if (AtomicReplace(backup, corePath, out err)) LogLine("[Machine] 已回滚到更新前的核心");
                else LogLine("[Machine] 回滚失败: " + err + "（备份仍在 " + backup + "）");
            }
            return RcRolledBack;
        }

        // 4) 复核写入结果
        try
        {
            string dstSha = Sha256Hex(corePath);
            if (!string.Equals(dstSha, srcSha, StringComparison.OrdinalIgnoreCase))
            {
                LogLine("[Machine] 错误: 写入后的哈希与更新包不符（" + dstSha + " != " + srcSha + "）");
                if (backup != null)
                {
                    string err2;
                    if (AtomicReplace(backup, corePath, out err2)) LogLine("[Machine] 已回滚到更新前的核心");
                    else LogLine("[Machine] 回滚失败: " + err2 + "（备份仍在 " + backup + "）");
                }
                return RcRolledBack;
            }
            LogLine("[Machine] 写入复核通过: " + dstSha);
        }
        catch (Exception e)
        {
            LogLine("[Machine] 警告: 写入复核失败（无法读取结果）: " + e.Message);
        }

        // 5) 清理更新包（仅在与默认位置一致时）
        if (string.Equals(src, Path.Combine(upDir, CoreDll), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(src);
            TryDelete(Path.Combine(upDir, "apply.json"));
            TryDelete(Path.Combine(upDir, CoreDll + ".sig"));
        }

        LogLine("[Machine] 更新已应用: " + CoreDll + " (v" + ver + ")");
        LogLine("[Machine] 现在可以重新启动游戏，新版加载器已生效。");
        return RcOk;
    }

    static void PruneBackups(string dir, int keep)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.bak");
            Array.Sort(files, delegate (FileInfo a, FileInfo b)
            {
                return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            });
            for (int i = keep; i < files.Length; i++)
            {
                try
                {
                    files[i].Delete();
                    LogLine("[Machine] 清理旧备份: " + files[i].Name);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------
    // 安装
    // ------------------------------------------------------------------

    const string RuntimeInitEntry = "\"assemblyName\":\"Assembly-CSharp\",\"nameSpace\":\"Machine.Injected\",\"className\":\"BootstrapRuntime\",\"methodName\":\"Initialize\",\"loadTypes\":0,\"isUnityClass\":false";

    /// <summary>把注入的启动钩子登记进引擎的 RuntimeInitializeOnLoads.json（幂等）。</summary>
    static bool RegisterRuntimeInitJson(string dataDir, bool add)
    {
        string path = Path.Combine(dataDir, "RuntimeInitializeOnLoads.json");
        if (!File.Exists(path)) return false;
        string json = File.ReadAllText(path);
        if (add)
        {
            if (json.Contains("Machine.Injected")) return true;
            int idx = json.LastIndexOf("]}", StringComparison.Ordinal);
            if (idx < 0) return false;
            json = json.Substring(0, idx) + "," + "{" + RuntimeInitEntry + "}" + json.Substring(idx);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return true;
        }
        else
        {
            string needle = "{" + RuntimeInitEntry + "}";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return true;
            // 删除该项及其前面的逗号（或后面的逗号）
            int comma = idx - 1;
            if (comma >= 0 && json[comma] == ',')
                json = json.Remove(comma, needle.Length + 1);
            else
                json = json.Remove(idx, needle.Length);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return true;
        }
    }

    /// <summary>写 Machine/README.txt。UTF-8 无 BOM（带 BOM 会让首个字段解析失败），且只用相对路径。</summary>
    static void WriteReadme(string machineDir)
    {
        string readme = Path.Combine(machineDir, "README.txt");
        // 已经存在就先看一眼：只有"确实是我们自己生成的"那份模板才允许刷新。
        // 早先的版本无条件跳过已存在文件，导致老版本留下的坏 README（带 BOM、
        // 或写死了开发机绝对路径）在被升级的机器上永远修不掉。
        if (File.Exists(readme))
        {
            bool ours = false;
            try
            {
                string head = File.ReadAllText(readme);
                // 中文表头是本次改的，但旧版英文表头也要认：
                // 否则老版本装过的机器上，那份英文 README 会被当成"用户自己写的"
                // 而永远刷新不掉。
                ours = head.StartsWith("Machine Mod Loader") || head.StartsWith("Machine Mod 加载器");
            }
            catch { }
            if (!ours) return;   // 用户自己写的，别碰
        }
        var sb = new StringBuilder();
        sb.AppendLine("Machine Mod 加载器");
        sb.AppendLine();
        sb.AppendLine("Mods    : <game>\\mods\\<Mod>\\mod.json");
        sb.AppendLine("Logs    : <game>\\Machine\\logs\\Machine.log");
        sb.AppendLine("          <game>\\Machine\\logs\\installer.log       (安装器)");
        sb.AppendLine("          <game>\\Machine\\logs\\machine_update.log  (命令行更新器)");
        sb.AppendLine("Config  : <game>\\Machine\\net.json      (联机 / 局域网服务器)");
        sb.AppendLine("          <game>\\Machine\\update.json   (更新仓库与通道)");
        sb.AppendLine("Backup  : <game>\\Machine\\backup\\       (旧版 Machine.Core.dll)");
        sb.AppendLine();
        sb.AppendLine("Update  : 在游戏内下载完成后，退出游戏并运行");
        sb.AppendLine("            Machine\\machine_update.bat");
        sb.AppendLine("          (会先校验哈希与 RSA 签名，通过之后才写入 Machine.Core.dll)");
        sb.AppendLine("Remove  : 运行 uninstall_machine.bat，或者");
        sb.AppendLine("            MachineInstaller.exe \"<game>\" --uninstall");
        sb.AppendLine();
        sb.AppendLine("<game> 是你的 Aviassembly 安装目录。上面的路径都是相对路径；");
        sb.AppendLine("本文件在安装时生成，不会写入任何与这台机器相关的绝对路径。");
        File.WriteAllText(readme, sb.ToString(), new UTF8Encoding(false));
    }

    internal static int DoInstall(string gameDir, string asmPath, string corePath, string backupPath)
    {
        if (!File.Exists(asmPath))
        {
            LogLine("错误: 未找到 " + asmPath);
            return RcUsage;
        }
        string dataDir = Path.GetDirectoryName(Path.GetDirectoryName(asmPath));
        string managedDir = Path.GetDirectoryName(asmPath);

        LogLine("[Machine] 安装到: " + gameDir);

        // 1) 备份原 Assembly-CSharp.dll
        if (!File.Exists(backupPath))
        {
            if (TryCopy(asmPath, backupPath, false))
                LogLine("[Machine] 已备份原程序集 -> Assembly-CSharp.dll.machinebak");
            else { LogLine("错误: 无法备份程序集（文件被占用）"); return RcWrite; }
        }

        // 2) 注入启动钩子（幂等）
        bool injected = false;
        string tmp = asmPath + ".machinetmp";
        bool needWrite = false;
        using (var asm = AssemblyDefinition.ReadAssembly(asmPath))
        {
            var module = asm.MainModule;
            if (module.Types.Any(t => t.FullName == InjectedType))
            {
                LogLine("[Machine] 已注入过，跳过程序集修改");
            }
            else
            {
                string coreModulePath = Path.Combine(managedDir, "UnityEngine.CoreModule.dll");
                string mscorlibPath = Path.Combine(managedDir, "mscorlib.dll");
                using (var core = AssemblyDefinition.ReadAssembly(coreModulePath))
                using (var mscor = AssemblyDefinition.ReadAssembly(mscorlibPath))
                {
                    var appType = core.MainModule.Types.First(t => t.FullName == "UnityEngine.Application");
                    var getDataPath = appType.Methods.First(m => m.Name == "get_dataPath");
                    var rinitType = core.MainModule.Types.First(t => t.FullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute");
                    var rinitCtor = rinitType.Methods.First(m => m.IsConstructor && m.Parameters.Count == 0);

                    var fileType = mscor.MainModule.Types.First(t => t.FullName == "System.IO.File");
                    var readAllBytes = fileType.Methods.First(m => m.Name == "ReadAllBytes" && m.Parameters.Count == 1);
                    var asmRefType = mscor.MainModule.Types.First(t => t.FullName == "System.Reflection.Assembly");
                    var loadBytes = asmRefType.Methods.First(m => m.Name == "Load" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "Byte[]");
                    var typeType = mscor.MainModule.Types.First(t => t.FullName == "System.Type");
                    var typeGetMethod = typeType.Methods.First(m => m.Name == "GetMethod" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
                    var asmGetType = asmRefType.Methods.First(m => m.Name == "GetType" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
                    var miType = mscor.MainModule.Types.First(t => t.FullName == "System.Reflection.MethodBase");
                    var invoke = miType.Methods.First(m => m.Name == "Invoke" && m.Parameters.Count == 2);
                    var strType = mscor.MainModule.Types.First(t => t.FullName == "System.String");
                    var concat = strType.Methods.First(m => m.Name == "Concat" && m.Parameters.Count == 2 && m.IsStatic);
                    var exType = mscor.MainModule.Types.First(t => t.FullName == "System.Exception");

                    var tdef = new TypeDefinition("Machine.Injected", "BootstrapRuntime",
                        TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
                    tdef.BaseType = module.TypeSystem.Object;
                    var mdef = new MethodDefinition("Initialize", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
                    tdef.Methods.Add(mdef);
                    tdef.CustomAttributes.Add(new CustomAttribute(module.ImportReference(rinitCtor)));
                    module.Types.Add(tdef);

                    var il = mdef.Body.GetILProcessor();
                    mdef.Body.InitLocals = true;
                    var locAsm = new VariableDefinition(module.ImportReference(asmRefType));
                    var locType = new VariableDefinition(module.ImportReference(typeType));
                    mdef.Body.Variables.Add(locAsm);
                    mdef.Body.Variables.Add(locType);

                    var ret = il.Create(OpCodes.Ret);
                    var handlerStart = il.Create(OpCodes.Nop);
                    var contType = il.Create(OpCodes.Nop);
                    var contMethod = il.Create(OpCodes.Nop);
                    var toStr = typeType.Methods.First(m => m.Name == "ToString" && m.Parameters.Count == 0);
                    var dbgType = core.MainModule.Types.First(t => t.FullName == "UnityEngine.Debug");
                    var logErr = dbgType.Methods.First(m => m.Name == "LogError" && m.Parameters.Count == 1);
                    var str2 = strType.Methods.First(m => m.Name == "Concat" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String" && m.Parameters[1].ParameterType.FullName == "System.String");

                    // 路径 = dataPath + "/Managed/Machine.Core.dll"（先压 dataPath 再压后缀）
                    il.Emit(OpCodes.Call, module.ImportReference(getDataPath));
                    il.Emit(OpCodes.Ldstr, "/Managed/" + CoreDll);
                    il.Emit(OpCodes.Call, module.ImportReference(str2));
                    il.Emit(OpCodes.Call, module.ImportReference(readAllBytes));
                    il.Emit(OpCodes.Call, module.ImportReference(loadBytes));
                    il.Emit(OpCodes.Stloc_0);
                    // 查找类型
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ldstr, "Machine.Core.Bootstrap");
                    il.Emit(OpCodes.Callvirt, module.ImportReference(asmGetType));
                    il.Emit(OpCodes.Stloc_1);
                    il.Emit(OpCodes.Ldloc_1);
                    il.Emit(OpCodes.Brtrue, contType);
                    // 类型为空（失败路径：记日志后返回）
                    il.Emit(OpCodes.Ldstr, "[Machine] bootstrap type not found");
                    il.Emit(OpCodes.Call, module.ImportReference(logErr));
                    il.Emit(OpCodes.Leave, ret);
                    il.Append(contType);
                    // 查找方法并调用
                    il.Emit(OpCodes.Ldloc_1);
                    il.Emit(OpCodes.Ldstr, "Initialize");
                    il.Emit(OpCodes.Callvirt, module.ImportReference(typeGetMethod));
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Brtrue, contMethod);
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldstr, "[Machine] bootstrap method not found");
                    il.Emit(OpCodes.Call, module.ImportReference(logErr));
                    il.Emit(OpCodes.Leave, ret);
                    il.Append(contMethod);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Callvirt, module.ImportReference(invoke));
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Leave, ret);
                    il.Append(handlerStart);
                    // catch: 输出异常到 Unity 日志
                    il.Emit(OpCodes.Callvirt, module.ImportReference(toStr));
                    il.Emit(OpCodes.Ldstr, "[Machine] bootstrap failed: ");
                    il.Emit(OpCodes.Call, module.ImportReference(str2));
                    il.Emit(OpCodes.Call, module.ImportReference(logErr));
                    il.Emit(OpCodes.Leave, ret);
                    il.Append(ret);

                    var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
                    {
                        TryStart = mdef.Body.Instructions[0],
                        TryEnd = handlerStart,
                        HandlerStart = handlerStart,
                        HandlerEnd = ret,
                        CatchType = module.ImportReference(exType)
                    };
                    mdef.Body.ExceptionHandlers.Add(handler);
                }
                asm.Write(tmp);
                needWrite = true;
            }
        }
        if (needWrite)
        {
            // 同样走"临时文件 -> 原子替换"，避免写坏游戏程序集
            string err;
            if (AtomicReplace(tmp, asmPath, out err)) { LogLine("[Machine] 启动钩子已注入 Assembly-CSharp.dll"); injected = true; }
            else { LogLine("错误: 无法写回程序集（文件被占用）: " + err); }
            TryDelete(tmp);
            if (!injected)
            {
                LogLine("错误: 注入失败，已中止（未修改 " + CoreDll + "）。");
                return RcWrite;
            }
        }

        // 3) 复制 Machine.Core.dll（先原子替换，再复核哈希）
        string srcCore = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), CoreDll);
        if (File.Exists(srcCore))
        {
            string err;
            if (!AtomicReplace(srcCore, corePath, out err))
            {
                LogLine("错误: 无法复制 " + CoreDll + " - " + err);
                return RcWrite;
            }
            try
            {
                string a = Sha256Hex(srcCore), b = Sha256Hex(corePath);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    LogLine("错误: " + CoreDll + " 写入后哈希不符（" + b + " != " + a + "）");
                    return RcHash;
                }
            }
            catch (Exception e) { LogLine("警告: 无法复核 " + CoreDll + " 哈希: " + e.Message); }
            LogLine("[Machine] 已复制 " + CoreDll + " -> Managed");
        }
        else
        {
            LogLine("错误: 未找到 " + CoreDll + "（应与安装器同目录）");
            return RcUsage;
        }

        // 4) 创建 mods/ 与 Machine/
        string modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);
        string machineDir = Path.Combine(gameDir, "Machine");
        if (!Directory.Exists(machineDir)) Directory.CreateDirectory(machineDir);
        string logsDir = Path.Combine(machineDir, "logs");
        if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
        WriteReadme(machineDir);

        // 5) 登记启动钩子到 RuntimeInitializeOnLoads.json
        if (RegisterRuntimeInitJson(dataDir, true))
            LogLine("[Machine] 已登记启动钩子到 RuntimeInitializeOnLoads.json");
        else
            LogLine("[Machine] 警告: 无法更新 RuntimeInitializeOnLoads.json（加载器可能不会启动）");

        LogLine("[Machine] 已创建 mods/ 与 Machine/");
        LogLine("[Machine] 安装完成（" + (injected ? "本次完成注入" : "此前已注入") + "）。");
        return RcOk;
    }

    // ------------------------------------------------------------------
    // 卸载
    // ------------------------------------------------------------------

    internal static int DoUninstall(string gameDir, string asmPath, string corePath, string backupPath)
    {
        int rc = 0;
        if (File.Exists(backupPath))
        {
            string err;
            if (AtomicReplace(backupPath, asmPath, out err))
            {
                TryDelete(backupPath);
                LogLine("[Machine] 已从备份还原 Assembly-CSharp.dll");
            }
            else { LogLine("[Machine] 还原失败（文件被占用）: " + err); rc = RcWrite; }
        }
        else if (File.Exists(asmPath))
        {
            // 无备份时把注入类型移除（需要 Cecil 重新写）
            LogLine("[Machine] 未找到备份，尝试从程序集移除注入类型…");
            try
            {
                using (var asm = AssemblyDefinition.ReadAssembly(asmPath))
                {
                    var t = asm.MainModule.Types.FirstOrDefault(x => x.FullName == InjectedType);
                    if (t != null)
                    {
                        asm.MainModule.Types.Remove(t);
                        string tmp = asmPath + ".machinetmp";
                        asm.Write(tmp);
                        string err;
                        if (AtomicReplace(tmp, asmPath, out err))
                            LogLine("[Machine] 注入类型已移除");
                        else
                        {
                            LogLine("[Machine] 移除注入失败: " + err);
                            rc = RcWrite;
                        }
                        TryDelete(tmp);
                    }
                    else LogLine("[Machine] 程序集中未找到注入类型（可能已还原过）");
                }
            }
            catch (Exception e) { LogLine("移除注入失败: " + e.Message); rc = RcWrite; }
        }
        if (File.Exists(corePath))
        {
            if (TryDelete(corePath)) LogLine("[Machine] 已删除 " + CoreDll);
            else { LogLine("[Machine] 无法删除 " + CoreDll + "（被占用，请关闭游戏后重试）"); rc = RcWrite; }
        }
        string dataDir = Path.GetDirectoryName(Path.GetDirectoryName(asmPath));
        if (RegisterRuntimeInitJson(dataDir, false))
            LogLine("[Machine] 已从 RuntimeInitializeOnLoads.json 移除启动钩子登记");
        string machineDir = Path.Combine(gameDir, "Machine");
        if (Directory.Exists(machineDir))
        {
            try
            {
                Directory.Delete(machineDir, true);
                LogLine("[Machine] 已删除 Machine/ 目录");
            }
            catch (Exception e) { LogLine("[Machine] 删除 Machine/ 失败: " + e.Message); rc = RcWrite; }
        }
        string modsDir = Path.Combine(gameDir, "mods");
        if (Directory.Exists(modsDir))
        {
            try
            {
                if (Directory.GetFileSystemEntries(modsDir).Length == 0)
                {
                    Directory.Delete(modsDir);
                    LogLine("[Machine] 已删除空的 mods/ 目录");
                }
                else LogLine("[Machine] mods/ 目录非空，保留（含你的 Mod）");
            }
            catch (Exception e) { LogLine("mods 目录处理失败: " + e.Message); }
        }
        LogLine("[Machine] 卸载完成 (rc=" + rc + ")");
        return rc;
    }
}
