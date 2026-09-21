using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// Mod 性能分析器。
    /// 记录每个Mod的初始化时间、每帧耗时、帧预算报警。
    /// </summary>
    public static class ModProfiler
    {
        /// <summary>单个Mod的性能统计。</summary>
        public class ModStats
        {
            public string ModId;
            public double InitTimeMs;          // 初始化耗时(ms)
            public double LastUpdateMs;        // 最近一帧Update耗时(ms)
            public double LastFixedUpdateMs;   // 最近一帧FixedUpdate耗时(ms)
            public double AvgUpdateMs;         // 平均Update耗时(ms)
            public double PeakUpdateMs;        // 峰值Update耗时(ms)
            public double AvgFixedUpdateMs;    // 平均FixedUpdate耗时(ms)
            public double PeakFixedUpdateMs;   // 峰值FixedUpdate耗时(ms)
            public int FrameBudgetWarnings;    // 帧预算报警次数
            public int UpdateErrorCount;       // Update异常次数
            public int FixedUpdateErrorCount;  // FixedUpdate异常次数
            public bool TimedOut;              // 是否启动超时

            // 内部统计用
            internal double _updateAccum;
            internal int _updateFrames;
            internal double _fixedUpdateAccum;
            internal int _fixedUpdateFrames;
        }

        private static readonly Dictionary<string, ModStats> _stats = new Dictionary<string, ModStats>();
        private static readonly Stopwatch _sw = new Stopwatch();
        private static bool _enabled = true;

        // 帧预算配置
        public static double FrameBudgetMs = 2.0;      // 每个Mod每帧最多消耗2ms
        public static double InitTimeoutMs = 5000.0;    // 单个Mod初始化超时5秒
        public static int WarningLogThrottle = 300;     // 每300帧最多报一次警告（避免刷屏）
        private static int _frameCount;

        /// <summary>是否启用性能分析。</summary>
        public static bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>获取所有Mod的性能统计。</summary>
        public static Dictionary<string, ModStats> GetAllStats()
        {
            return new Dictionary<string, ModStats>(_stats);
        }

        /// <summary>获取指定Mod的性能统计。</summary>
        public static ModStats GetStats(string modId)
        {
            ModStats s;
            if (_stats.TryGetValue(modId, out s)) return s;
            return null;
        }

        /// <summary>开始计时（用于Mod初始化）。</summary>
        public static void BeginInit(string modId)
        {
            if (!_enabled) return;
            GetOrCreate(modId);
            _sw.Restart();
        }

        /// <summary>结束初始化计时。</summary>
        public static void EndInit(string modId)
        {
            if (!_enabled) return;
            _sw.Stop();
            ModStats s = GetOrCreate(modId);
            s.InitTimeMs = _sw.Elapsed.TotalMilliseconds;

            if (s.InitTimeMs > InitTimeoutMs)
            {
                s.TimedOut = true;
                Log.Warn("Profiler", "mod [" + modId + "] init took " + s.InitTimeMs.ToString("F1") +
                    "ms (timeout threshold: " + InitTimeoutMs + "ms)");
            }
            else
            {
                Log.Debug("Profiler", "mod [" + modId + "] init: " + s.InitTimeMs.ToString("F1") + "ms");
            }
        }

        /// <summary>开始Update计时。</summary>
        public static void BeginUpdate(string modId)
        {
            if (!_enabled) return;
            _sw.Restart();
        }

        /// <summary>结束Update计时并记录。</summary>
        public static void EndUpdate(string modId)
        {
            if (!_enabled) return;
            _sw.Stop();
            double ms = _sw.Elapsed.TotalMilliseconds;

            ModStats s = GetOrCreate(modId);
            s.LastUpdateMs = ms;
            s._updateAccum += ms;
            s._updateFrames++;
            if (ms > s.PeakUpdateMs) s.PeakUpdateMs = ms;

            // 帧预算检查（节流，避免每帧都报警）
            if (ms > FrameBudgetMs)
            {
                s.FrameBudgetWarnings++;
                _frameCount++;
                if (_frameCount % WarningLogThrottle == 0)
                {
                    Log.Warn("Profiler", "mod [" + modId + "] frame budget exceeded: " +
                        ms.ToString("F2") + "ms (budget: " + FrameBudgetMs + "ms)");
                }
            }
        }

        /// <summary>开始FixedUpdate计时。</summary>
        public static void BeginFixedUpdate(string modId)
        {
            if (!_enabled) return;
            _sw.Restart();
        }

        /// <summary>结束FixedUpdate计时并记录。</summary>
        public static void EndFixedUpdate(string modId)
        {
            if (!_enabled) return;
            _sw.Stop();
            double ms = _sw.Elapsed.TotalMilliseconds;

            ModStats s = GetOrCreate(modId);
            s.LastFixedUpdateMs = ms;
            s._fixedUpdateAccum += ms;
            s._fixedUpdateFrames++;
            if (ms > s.PeakFixedUpdateMs) s.PeakFixedUpdateMs = ms;
        }

        /// <summary>记录Update异常。</summary>
        public static void RecordUpdateError(string modId)
        {
            ModStats s = GetOrCreate(modId);
            s.UpdateErrorCount++;
        }

        /// <summary>记录FixedUpdate异常。</summary>
        public static void RecordFixedUpdateError(string modId)
        {
            ModStats s = GetOrCreate(modId);
            s.FixedUpdateErrorCount++;
        }

        /// <summary>计算平均值（每N帧调用一次）。</summary>
        public static void ComputeAverages()
        {
            foreach (var kvp in _stats)
            {
                ModStats s = kvp.Value;
                if (s._updateFrames > 0)
                {
                    s.AvgUpdateMs = s._updateAccum / s._updateFrames;
                    s._updateAccum = 0;
                    s._updateFrames = 0;
                }
                if (s._fixedUpdateFrames > 0)
                {
                    s.AvgFixedUpdateMs = s._fixedUpdateAccum / s._fixedUpdateFrames;
                    s._fixedUpdateAccum = 0;
                    s._fixedUpdateFrames = 0;
                }
            }
        }

        /// <summary>获取性能报告（用于诊断命令）。</summary>
        public static string GetReport()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Mod Performance Report ===");
            sb.AppendLine("Frame Budget: " + FrameBudgetMs + "ms/mod");
            sb.AppendLine("Init Timeout: " + InitTimeoutMs + "ms");
            sb.AppendLine();

            double totalAvg = 0;
            double totalPeak = 0;

            foreach (var kvp in _stats)
            {
                ModStats s = kvp.Value;
                totalAvg += s.AvgUpdateMs;
                if (s.PeakUpdateMs > totalPeak) totalPeak = s.PeakUpdateMs;

                string warning = s.FrameBudgetWarnings > 0 ? " [WARN:" + s.FrameBudgetWarnings + "]" : "";
                string timeout = s.TimedOut ? " [TIMEOUT]" : "";
                string errors = (s.UpdateErrorCount + s.FixedUpdateErrorCount) > 0 ?
                    " [ERR:" + (s.UpdateErrorCount + s.FixedUpdateErrorCount) + "]" : "";

                sb.AppendLine(string.Format("  {0,-25} init:{1,7:F1}ms  avg:{2,6:F2}ms  peak:{3,6:F2}ms{4}{5}{6}",
                    s.ModId, s.InitTimeMs, s.AvgUpdateMs, s.PeakUpdateMs, warning, timeout, errors));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("  TOTAL: avg={0:F2}ms/frame  peak={1:F2}ms", totalAvg, totalPeak));
            sb.AppendLine("  Mods tracked: " + _stats.Count);

            return sb.ToString();
        }

        /// <summary>重置所有统计。</summary>
        public static void Reset()
        {
            _stats.Clear();
            _frameCount = 0;
        }

        private static ModStats GetOrCreate(string modId)
        {
            ModStats s;
            if (!_stats.TryGetValue(modId, out s))
            {
                s = new ModStats { ModId = modId };
                _stats[modId] = s;
            }
            return s;
        }
    }
}
