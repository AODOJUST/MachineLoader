using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// Machine 分级日志系统。
    /// 级别：ERROR > WARN > INFO > DEBUG > TRACE
    /// 支持日志轮转（5MB切分，保留5个）、崩溃报告、统一格式。
    /// </summary>
    public static class Log
    {
        // 日志级别枚举
        public enum Level
        {
            ERROR = 0,
            WARN = 1,
            INFO = 2,
            DEBUG = 3,
            TRACE = 4
        }

        private static string _filePath;
        private static string _logDir;
        private static bool _ready;
        private static readonly object _lock = new object();

        // 当前日志级别（默认WARN，减少日志量提升性能）
        private static Level _currentLevel = Level.WARN;

        // 缓冲写盘
        private static StreamWriter _writer;
        private static int _pending;
        private static DateTime _lastFlush = DateTime.MinValue;
        private const int FlushLines = 256;
        private const int FlushMs = 2000;

        // 日志轮转配置
        private const long MaxLogSize = 5 * 1024 * 1024; // 5MB
        private const int MaxLogFiles = 5;

        // 最近错误记录（用于诊断命令）
        private static readonly List<string> _recentErrors = new List<string>();
        private const int MaxRecentErrors = 50;

        // 日志计数（用于诊断）
        private static readonly Dictionary<Level, int> _levelCounts = new Dictionary<Level, int>();

        // Mod列表获取委托（由Bootstrap设置，避免循环引用）
        public delegate List<string> GetModListDelegate();
        public static GetModListDelegate GetModList;

        // 加载器版本获取委托
        public delegate string GetVersionDelegate();
        public static GetVersionDelegate GetLoaderVersion;

        /// <summary>获取加载器版本（通过委托，避免循环引用）。</summary>
        private static string GetVersion()
        {
            try
            {
                if (GetLoaderVersion != null)
                    return GetLoaderVersion();
            }
            catch { }
            return "unknown";
        }

        /// <summary>获取Mod列表（通过委托）。</summary>
        private static List<string> GetMods()
        {
            try
            {
                if (GetModList != null)
                    return GetModList();
            }
            catch { }
            return new List<string>();
        }

        /// <summary>当前日志级别。</summary>
        public static Level CurrentLevel
        {
            get { return _currentLevel; }
            set { _currentLevel = value; }
        }

        /// <summary>设置日志级别（从字符串解析）。</summary>
        public static void SetLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return;
            try
            {
                _currentLevel = (Level)Enum.Parse(typeof(Level), level.ToUpperInvariant());
            }
            catch { _currentLevel = Level.WARN; }
        }

        /// <summary>检查某级别是否启用。</summary>
        public static bool IsLevelEnabled(Level level)
        {
            return (int)level <= (int)_currentLevel;
        }

        public static void Init(string logDir)
        {
            try
            {
                _logDir = logDir;
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                _filePath = Path.Combine(logDir, "Machine.log");

                // 初始化日志计数
                foreach (Level l in Enum.GetValues(typeof(Level)))
                    _levelCounts[l] = 0;

                // 如果日志文件已存在且超过大小限制，先轮转
                if (File.Exists(_filePath))
                {
                    FileInfo fi = new FileInfo(_filePath);
                    if (fi.Length >= MaxLogSize)
                        RotateLogs();
                }

                if (!File.Exists(_filePath))
                    File.WriteAllText(_filePath, "=== Machine Loader Log ===\r\n" +
                        "=== Version: " + GetVersion() + " ===\r\n" +
                        "=== Log Level: " + _currentLevel + " ===\r\n\r\n");

                _ready = true;
                Info("Logger ready -> " + _filePath + " (level: " + _currentLevel + ")");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Machine] Log init failed: " + e.Message);
                _ready = false;
            }
        }

        // 分级日志方法
        public static void Error(string msg) { Write(Level.ERROR, msg, ""); }
        public static void Error(string module, string msg) { Write(Level.ERROR, msg, module); }
        public static void Warn(string msg) { Write(Level.WARN, msg, ""); }
        public static void Warn(string module, string msg) { Write(Level.WARN, msg, module); }
        public static void Info(string msg) { Write(Level.INFO, msg, ""); }
        public static void Info(string module, string msg) { Write(Level.INFO, msg, module); }
        public static void Debug(string msg) { Write(Level.DEBUG, msg, ""); }
        public static void Debug(string module, string msg) { Write(Level.DEBUG, msg, module); }
        public static void Trace(string msg) { Write(Level.TRACE, msg, ""); }
        public static void Trace(string module, string msg) { Write(Level.TRACE, msg, module); }

        private static void Write(Level level, string msg, string module)
        {
            // 级别过滤
            if (!IsLevelEnabled(level)) return;

            // 计数
            lock (_levelCounts) { _levelCounts[level]++; }

            // 记录错误
            if (level == Level.ERROR)
            {
                lock (_recentErrors)
                {
                    _recentErrors.Add(DateTime.Now.ToString("HH:mm:ss") + " " + msg);
                    if (_recentErrors.Count > MaxRecentErrors)
                        _recentErrors.RemoveAt(0);
                }
            }

            // 统一格式：时间 [级别] 模块: 消息
            string levelStr = level.ToString().PadRight(5);
            string moduleStr = string.IsNullOrEmpty(module) ? "" : " [" + module + "]";
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                         " [" + levelStr + "]" + moduleStr + " " + msg;

            if (_ready)
            {
                lock (_lock)
                {
                    try
                    {
                        if (_writer == null)
                            _writer = new StreamWriter(_filePath, true);

                        _writer.WriteLine(line);
                        _pending++;

                        DateTime now = DateTime.Now;
                        bool urgent = (level == Level.ERROR);
                        if (urgent || _pending >= FlushLines || (now - _lastFlush).TotalMilliseconds >= FlushMs)
                        {
                            _writer.Flush();
                            _pending = 0;
                            _lastFlush = now;

                            // 检查是否需要轮转
                            CheckAndRotate();
                        }
                    }
                    catch { }
                }
            }

            // Unity控制台镜像
            if (level == Level.ERROR) UnityEngine.Debug.LogError("[Machine] " + msg);
            else if (level == Level.WARN) UnityEngine.Debug.LogWarning("[Machine] " + msg);
            else if (level == Level.INFO) UnityEngine.Debug.Log("[Machine] " + msg);
            // DEBUG和TRACE不输出到Unity控制台，避免刷屏
        }

        /// <summary>检查日志文件大小并轮转。</summary>
        private static void CheckAndRotate()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                FileInfo fi = new FileInfo(_filePath);
                if (fi.Length >= MaxLogSize)
                {
                    RotateLogs();
                }
            }
            catch { }
        }

        /// <summary>轮转日志文件：Machine.log -> Machine.log.1 -> ... -> Machine.log.5（删除最旧的）。</summary>
        private static void RotateLogs()
        {
            try
            {
                // 关闭当前writer
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Close();
                    _writer = null;
                }

                // 删除最旧的文件
                string oldest = _filePath + "." + MaxLogFiles;
                if (File.Exists(oldest)) File.Delete(oldest);

                // 轮转：.4 -> .5, .3 -> .4, ..., .log -> .1
                for (int i = MaxLogFiles - 1; i >= 1; i--)
                {
                    string src = _filePath + "." + i;
                    string dst = _filePath + "." + (i + 1);
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }

                // 当前日志 -> .1
                if (File.Exists(_filePath))
                {
                    string bak = _filePath + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_filePath, bak);
                }

                // 创建新日志文件
                File.WriteAllText(_filePath, "=== Machine Loader Log (rotated) ===\r\n" +
                    "=== Version: " + GetVersion() + " ===\r\n" +
                    "=== Log Level: " + _currentLevel + " ===\r\n\r\n");

                Info("Log rotated (previous log archived)");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Machine] Log rotation failed: " + e.Message);
            }
        }

        /// <summary>强制刷新缓冲（退出/卸载前可调用，确保日志落盘）。</summary>
        public static void Flush()
        {
            lock (_lock)
            {
                try
                {
                    if (_writer != null)
                    {
                        _writer.Flush();
                        _pending = 0;
                        _lastFlush = DateTime.Now;
                    }
                }
                catch { }
            }
        }

        /// <summary>生成崩溃报告。</summary>
        public static string GenerateCrashReport(Exception ex, string context = "")
        {
            try
            {
                Flush();

                StringBuilder report = new StringBuilder();
                report.AppendLine("=== Machine Loader Crash Report ===");
                report.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Loader Version: " + GetVersion());
                report.AppendLine("Log Level: " + _currentLevel);
                report.AppendLine("Context: " + (string.IsNullOrEmpty(context) ? "N/A" : context));
                report.AppendLine();

                // 异常信息
                if (ex != null)
                {
                    report.AppendLine("=== Exception ===");
                    report.AppendLine("Type: " + ex.GetType().FullName);
                    report.AppendLine("Message: " + ex.Message);
                    report.AppendLine("StackTrace:");
                    report.AppendLine(ex.StackTrace);
                    report.AppendLine();

                    if (ex.InnerException != null)
                    {
                        report.AppendLine("=== Inner Exception ===");
                        report.AppendLine("Type: " + ex.InnerException.GetType().FullName);
                        report.AppendLine("Message: " + ex.InnerException.Message);
                        report.AppendLine();
                    }
                }

                // Mod列表
                report.AppendLine("=== Loaded Mods ===");
                try
                {
                    List<string> mods = GetMods();
                    if (mods.Count == 0)
                        report.AppendLine("  (no mods loaded or mod list unavailable)");
                    else
                        foreach (string mod in mods)
                            report.AppendLine("  " + mod);
                }
                catch (Exception e)
                {
                    report.AppendLine("  Failed to list mods: " + e.Message);
                }
                report.AppendLine();

                // 日志统计
                report.AppendLine("=== Log Statistics ===");
                lock (_levelCounts)
                {
                    foreach (Level l in Enum.GetValues(typeof(Level)))
                    {
                        report.AppendLine("  " + l + ": " + _levelCounts[l]);
                    }
                }
                report.AppendLine();

                // 最近错误
                report.AppendLine("=== Recent Errors ===");
                lock (_recentErrors)
                {
                    if (_recentErrors.Count == 0)
                        report.AppendLine("  (none)");
                    else
                        foreach (string err in _recentErrors)
                            report.AppendLine("  " + err);
                }
                report.AppendLine();

                // 最后200行日志
                report.AppendLine("=== Last 200 Log Lines ===");
                try
                {
                    if (File.Exists(_filePath))
                    {
                        string[] lines = File.ReadAllLines(_filePath);
                        int start = Math.Max(0, lines.Length - 200);
                        for (int i = start; i < lines.Length; i++)
                            report.AppendLine(lines[i]);
                    }
                    else
                    {
                        report.AppendLine("  (log file not found)");
                    }
                }
                catch (Exception e)
                {
                    report.AppendLine("  Failed to read log: " + e.Message);
                }

                // 保存崩溃报告
                string crashPath = Path.Combine(_logDir, "crash_report_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(crashPath, report.ToString());

                Error("Crash report generated: " + crashPath);
                return crashPath;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[Machine] Failed to generate crash report: " + e.Message);
                return "";
            }
        }

        /// <summary>获取诊断信息（用于/machine diag命令）。</summary>
        public static string GetDiagnosticInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Machine Loader Diagnostic ===");
            sb.AppendLine("Loader Version: " + GetVersion());
            sb.AppendLine("API Version: " + MachineApi.ApiVersion);
            sb.AppendLine("Log Level: " + _currentLevel);
            sb.AppendLine();

            // 更新通道
            try
            {
                string updateConfigPath = Path.Combine(Path.GetDirectoryName(_logDir), "update.json");
                if (File.Exists(updateConfigPath))
                {
                    string json = File.ReadAllText(updateConfigPath);
                    JsonValue jv = JsonValue.Parse(json);
                    if (jv != null)
                    {
                        sb.AppendLine("Update Channel: " + jv.GetString("channel", "stable"));
                        sb.AppendLine("Update Repo: " + jv.GetString("repo", "N/A"));
                        sb.AppendLine("Auto Update: " + jv.GetBool("enabled", true));
                    }
                }
            }
            catch { }
            sb.AppendLine();

            // Mod列表
            sb.AppendLine("=== Loaded Mods ===");
            try
            {
                List<string> mods = GetMods();
                if (mods.Count == 0)
                {
                    sb.AppendLine("  (no mods loaded or mod list unavailable)");
                }
                else
                {
                    int enabled = 0, disabled = 0;
                    foreach (string mod in mods)
                    {
                        sb.AppendLine("  " + mod);
                        if (mod.Contains("[OK]") || mod.Contains("[ENABLED]")) enabled++;
                        else if (mod.Contains("[DISABLED]")) disabled++;
                    }
                    sb.AppendLine();
                    sb.AppendLine("Total: " + mods.Count +
                                 " (Enabled: " + enabled + ", Disabled: " + disabled + ")");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  Failed: " + e.Message);
            }
            sb.AppendLine();

            // 日志统计
            sb.AppendLine("=== Log Statistics ===");
            lock (_levelCounts)
            {
                foreach (Level l in Enum.GetValues(typeof(Level)))
                    sb.AppendLine("  " + l + ": " + _levelCounts[l]);
            }
            sb.AppendLine();

            // 最近错误
            sb.AppendLine("=== Recent Errors ===");
            lock (_recentErrors)
            {
                if (_recentErrors.Count == 0)
                    sb.AppendLine("  (none)");
                else
                {
                    int count = Math.Min(10, _recentErrors.Count);
                    for (int i = _recentErrors.Count - count; i < _recentErrors.Count; i++)
                        sb.AppendLine("  " + _recentErrors[i]);
                }
            }

            return sb.ToString();
        }

        /// <summary>获取最近错误列表。</summary>
        public static List<string> GetRecentErrors()
        {
            lock (_recentErrors)
                return new List<string>(_recentErrors);
        }
    }
}
