using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using Machine.Mod;
using Machine.Core;

namespace OptiMod
{
    /// <summary>
    /// OptiMod 入口。
    /// 注意：加载器在每次切场景时都会重新调用 OnLoad，所以必须有静态实例守卫，
    /// 否则会出现多个 OptiSystem 同帧运行（重复采样、重复写日志，本身就是性能问题）。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.opti"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("OptiMod loading...");
            if (OptiSystem.Instance != null)
            {
                api.Log("OptiMod: instance already alive, skip duplicate OnLoad");
                return;
            }
            var go = new GameObject("Machine.OptiMod");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<OptiSystem>().Init(api);
        }
    }

    /// <summary>
    /// OptiMod 2.1 —— CPU 向性能优化与归因（画质、渲染特性一律不碰，除非显式打开相关开关）。
    ///
    /// 2.1 相对 2.0 的关键变化：
    ///  A. 【归因】新增 RT 相机消融探针：把带 RenderTexture 的相机逐个关掉一小段时间，
    ///     测帧耗时差值 —— 直接量化"每个相机每帧吃掉多少 ms"。这是不开 profiler 也能
    ///     拿到的最硬的证据（实测该游戏 4 个启用相机→15ms/61fps，5 个启用相机→36ms/26fps，
    ///     几何量 195 与 6235 个 renderer 帧耗时几乎一样，说明瓶颈在"多渲染一遍场景"而不是模型数量）。
    ///  B. 【自伤修复】对象计数从"每个报告周期全扫 5 类对象"改成轮转（每周期只扫 1 类），
    ///     并默认关闭最贵的 Renderer.isVisible 逐个循环 —— 原来它自己就是每 4s 一次尖峰的来源。
    ///  C. 【降本】新增 SkinnedMeshRenderer.updateWhenOffscreen=false 等纯 CPU 项（零视觉影响），
    ///     以及按名字禁用相机（用于把探针证明"白渲染"的相机永久关掉）。
    ///
    /// 配置：mods/OptiMod/opti_config.json。数值型配置用 -1（或 &lt;=0）表示"保持游戏原值不动"。
    /// 所有 API 访问都做了容错：任何一项不可用都只记一条日志，不影响游戏。
    /// </summary>
    public class OptiSystem : MonoBehaviour
    {
        public static OptiSystem Instance;

        private const string Ver = "2.1.0";

        private IMachineApi _api;

        // ---------------- 配置：诊断 ----------------
        private bool cfgProfiler = true;
        private float cfgReportInterval = 4f;      // 报告周期（秒）
        private float cfgHitchMs = 90f;            // 超过此帧耗时记一次卡顿
        private bool cfgHitchLog = true;           // 卡顿瞬间打一条带时间戳 + FrameTiming 分解的日志
        private bool cfgCountObjects = true;       // 报告里统计场景对象数（现为轮转：每周期只统计 1 类）
        private bool cfgMeshVisible = false;       // 额外统计"可见 renderer 数"（逐个 isVisible，很贵，默认关）
        private bool cfgCamDetail = true;          // 切场景/每 60s 打一次相机清单
        private bool cfgDumpRenderers = false;     // 按"所属场景 / root 对象 / layer"统计 renderer，定位"谁刷出一堆渲染体"
        private float cfgMenuFixedDelta = 0f;      // >0 = 非飞行状态把 Time.fixedDeltaTime 降到该值（菜单不需要 50Hz 物理）
        private float _origFixedDelta = -1f;       // 进入游戏时观测到的原始 fixedDeltaTime
        private bool cfgPhaseProbe = false;        // 玩家循环分段探针：把帧耗时拆成 Early/Fixed/PreUpdate/Update/PreLate/PostLate
        private float _dumpNext;                   // 下次 dump 的时间点（realtimeSinceStartup）

        // ---------------- 配置：相机归因 / 处置 ----------------
        private bool cfgCamProbe = true;           // RT 相机消融探针（每个场景每个 RT 相机只测一次）
        private bool cfgProbeScreenCameras = false; // 连屏幕相机也探（画面会闪黑一瞬间，默认关）
        private float cfgProbeSec = 0.6f;          // 单次消融时长（秒）
        private float cfgProbeMaxBaseMs = 60f;     // 基准帧耗时超过它就跳过（加载期噪声太大，测了没意义）
        private float cfgProbeSettleSec = 4f;      // 场景切换后先静置这么久再做消融实验（转场余波里测不准，还会加重卡顿）

        // 场景静置状态：转场/读档后帧耗时又大又抖，此时做相机 A/B 消融既得不到可信基准，
        // 又会往本来就最忙的帧上再压一层。切场景即重置静置计时，计时未满不做实验。
        private float _sceneSettleT;
        private string _probeLastScene;
        private string[] cfgDisableCameras = new string[0];  // 按名字子串永久禁用的相机
        private string[] cfgKillRootRenderers = new string[0]; // 诊断：把指定 root 对象下所有 Renderer 关掉（量化它们的渲染代价）
        private string[] cfgKillComponents = new string[0];    // 诊断：按类型全名子串禁用 MonoBehaviour（区分"mod 的脚本"与"mod 创建的对象"）
        private readonly List<Behaviour> _killedComps = new List<Behaviour>();
        private readonly List<GameObject> _killedRoots = new List<GameObject>();
        private readonly List<Renderer> _killedRends = new List<Renderer>();
        private bool _killRootsTried;

        // ---------------- 配置：调优 ----------------
        private int cfgVSync = 0;                  // -1 保持；0 = 关垂直同步
        private int cfgTargetFrameRate = 0;        // -1 保持；0 = 不限帧；N = 上限 N
        private float cfgMaxDeltaTime = 0.1f;      // <=0 保持；卡顿后允许的最大追帧步长
        private float cfgMaxParticleDeltaTime = 0.03f;

        private float cfgFixedDeltaTime = -1f;     // <=0 保持；>0 改物理步长（降低 FixedUpdate 频率）
        private int cfgSolverIterations = 2;       // <=0 保持（Unity 默认 6）
        private int cfgSolverVelocityIterations = -1;
        private float cfgSleepThreshold = -1f;
        private int cfgParticleRaycastBudget = 256; // <=0 保持（Unity 默认 4096：每帧最多 4096 次粒子碰撞射线）
        private int cfgAsyncUploadTimeSlice = 4;   // <=0 保持（默认 2ms）
        private int cfgAsyncUploadBufferSize = 16; // <=0 保持（默认 4MB）
        private int cfgMaxQueuedFrames = -1;

        private bool cfgGcControl = true;          // 手动增量 GC：把长停顿摊成每帧 1~2ms 的小片
        private float cfgGcSliceMs = 1.5f;
        private float cfgGcCeilingMB = 1400f;      // 托管堆超过它就自动退回 Unity 自动 GC 30 秒
        private bool cfgStackTraceNone = true;     // 关掉 Log/Warning 的堆栈采集（Debug.Log 的主要开销）
        private bool cfgDedupCameras = true;       // 关掉"完全重复的全屏相机"（判重含场景名/位姿/FOV，最保守）
        private bool cfgKillDupMainCam = false;    // 宽松版：只比 depth/mask/clear/尺寸，保留 Camera.main 关掉其它
        private int cfgBackgroundLoadingPriority = -1;
        private float cfgEnforceInterval = 5f;     // 每 N 秒把帧率同步设置纠偏一次（游戏设置面板会改它）
        private bool cfgWarmupShaders = false;     // 预热全部 shader（消运行时编译停顿，但启动会卡一下）

        // 纯 CPU、零视觉影响的小优化
        private bool cfgSkinOffscreen = true;      // SkinnedMeshRenderer.updateWhenOffscreen=false
        private bool cfgInstancing = false;        // Material.enableInstancing=true（降 draw call；对不支持实例化的 shader 无副作用，但收益不确定）
        private int cfgAutoSyncTransforms = 0;     // -1 保持；0 = 关掉（每帧自动同步 transform->物理）
        private float cfgSweepInterval = 60f;      // 复扫周期（60s；切场景也会触发）

        // 画质相关（默认全部"保持"，只有明确要拿画质换 CPU 时才动）
        private float cfgShadowDistance = -1f;
        private float cfgLodBias = -1f;
        private int cfgPixelLightCount = -1;

        // ---------------- 反射缓存 ----------------
        private static readonly Type TTime = typeof(Time);
        private static readonly Type TApp = typeof(Application);
        private static readonly Type TQuality = typeof(QualitySettings);
        private static readonly Type TPhysics = typeof(Physics);
        private static readonly Type TGc = typeof(UnityEngine.Scripting.GarbageCollector);
        private static readonly Type TShader = typeof(Shader);
        private static readonly Dictionary<string, FieldInfo> _ftCache = new Dictionary<string, FieldInfo>();
        private static readonly FrameTiming[] _timings = new FrameTiming[16];
        private static readonly string[] SolverItNames = { "solverIterations", "defaultSolverIterations" };
        private static readonly string[] SolverVelNames = { "solverVelocityIterations", "defaultSolverVelocityIterations" };
        private bool _ftReported;

        // ---------------- 运行状态 ----------------
        private float _repT;
        private float _enforceT;
        private float _frameAccum;
        private float _frameMax;
        private int _frameCount;
        private int _hitchCount;
        private int _hitchLogged;
        private long _gc0, _gc1, _gc2;
        private double _cpuSec;
        private float _cpuWallStart;
        private bool _gcClamped;
        private float _gcRecheckT;
        private double _lastMonoMB = -1;
        private float _lastAvgMs = -1f;
        private string _lastScene = "";
        private float _camDetailT = -1f;
        private bool _fatal;
        private StreamWriter _csv;
        private bool _csvInit;

        // 真实帧时钟：Time.unscaledDeltaTime 会被 Time.maximumDeltaTime 钳住（本项目设成 0.1），
        // 于是"2~3 fps 的冻结"会被记成 100ms —— 正好把最严重的卡顿抹平。Stopwatch 不受影响。
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private long _swLast;

        // 消融探针状态
        private bool _probeActive;
        private float _probeT;
        private Camera _probeCam;
        private float _probeAccum;
        private int _probeFrames;
        private float _probeBaseMs;
        private bool _probeWasEnabled;
        private readonly HashSet<string> _probedSigs = new HashSet<string>();
        private int _probeCount;

        // 按名字禁用相机的记录（用于开关关闭时还回去）
        private readonly List<int> _disabledByName = new List<int>();

        // 场景对象计数（轮转）
        private int _cntSlot;
        private string _cntRig = "";
        private string _cntPs = "";
        private string _cntPart = "";
        private string _cntMesh = "";
        private string _cntAudio = "";
        private string _cntCam = "";
        private double _sweepT;
        private string _sweepScene = "";
        private int _skinFixed;
        private int _matInstanced;

        // ---------------- 生命周期 ----------------

        public void Init(IMachineApi api)
        {
            Instance = this;
            _api = api;

            LoadConfig();

            _api.Log("OptiMod v" + Ver + " init (CPU-only; quality/render features untouched)");
            _api.Log("OptiMod: settings BEFORE " + Snapshot());
            _api.Log("OptiMod: applied ->" + ApplyTuning());
            _api.Log("OptiMod: settings AFTER  " + Snapshot());

            if (cfgStackTraceNone)
            {
                try
                {
                    Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                    Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                    _api.Log("OptiMod: stack traces off for Log/Warning (Error/Exception kept)");
                }
                catch { }
            }

            if (cfgWarmupShaders)
            {
                try
                {
                    InvokeStatic(TShader, "WarmupAllShaders");
                    _api.Log("OptiMod: Shader.WarmupAllShaders() done");
                }
                catch { }
            }

            try
            {
                _api.Log("OptiMod: sys cores=" + SystemInfo.processorCount
                    + " freq=" + SystemInfo.processorFrequency + "MHz"
                    + " gpu=" + SystemInfo.graphicsDeviceName
                    + " vram=" + SystemInfo.graphicsMemorySize + "MB"
                    + " quality=" + QualitySettings.GetQualityLevel()
                    + " screen=" + Screen.width + "x" + Screen.height);
            }
            catch { }

            _api.Log("OptiMod: diag camProbe=" + cfgCamProbe + " probeSec=" + cfgProbeSec.ToString("0.##")
                + " meshVisible=" + cfgMeshVisible
                + " | opt skinOffscreen=" + cfgSkinOffscreen + " instancing=" + cfgInstancing
                + " autoSyncTransforms=" + (cfgAutoSyncTransforms == 0 ? "false" : "keep")
                + " phaseProbe=" + cfgPhaseProbe);

            if (cfgPhaseProbe) PatchPhaseLoop();

            _repT = cfgReportInterval;
            _enforceT = cfgEnforceInterval;
            _sw.Start();
            _swLast = _sw.ElapsedTicks;
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            _cpuWallStart = Time.realtimeSinceStartup;
            try { _cpuSec = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds; }
            catch { _cpuSec = -1; }

            SweepAux(true);
        }

        private void Update()
        {
            if (_fatal) return;
            try { Tick(); }
            catch (Exception e)
            {
                _fatal = true;
                try { _api.Log("OptiMod: FATAL in Update, sampler disabled: " + e.Message); } catch { }
            }
        }

        private void Tick()
        {
            // 真实帧间隔（不受 maximumDeltaTime 钳制）
            long nowTicks = _sw.ElapsedTicks;
            float dt = (float)((nowTicks - _swLast) / (double)System.Diagnostics.Stopwatch.Frequency);
            _swLast = nowTicks;
            if (dt <= 0.00005f) dt = 0.00005f;
            if (dt > 30f) dt = 30f;
            float dtms = dt * 1000f;

            // 手动增量 GC：每帧只花 cfgGcSliceMs，避免攒到一次长停顿（"卡几秒"的典型来源）
            if (cfgGcControl && !_gcClamped)
            {
                try { UnityEngine.Scripting.GarbageCollector.CollectIncremental((ulong)(cfgGcSliceMs * 1000000f)); }
                catch { }
            }

            // 场景静置倒计时（见 cfgProbeSettleSec 的说明）
            if (_sceneSettleT > 0f) _sceneSettleT -= dt;

            // 消融探针窗口：只累计探针自身的帧耗时，不计入常规统计（否则会被自己的实验污染）
            if (_probeActive)
            {
                _probeAccum += dt;
                _probeFrames++;
                _probeT -= dt;
                if (_probeT <= 0f) EndProbe();
            }
            else if (cfgProfiler)
            {
                _frameAccum += dt;
                if (dtms > _frameMax) _frameMax = dtms;
                _frameCount++;

                if (dtms > cfgHitchMs)
                {
                    _hitchCount++;
                    if (cfgHitchLog && _hitchLogged < 6)
                    {
                        _hitchLogged++;
                        string bd = FrameBreakdown();
                        _api.Log("OptiMod: HITCH " + Mathf.RoundToInt(dtms) + "ms @" + DateTime.Now.ToString("HH:mm:ss.fff")
                            + (bd.Length > 0 ? (" | " + bd) : ""));
                    }
                }
            }
            try { FrameTimingManager.CaptureFrameTimings(); } catch { }

            // 菜单降物理频率：主菜单/编辑器里游戏仍按 50Hz 跑 FixedUpdate（实测占 ~14ms/帧），
            // 但菜单里没有需要实时物理的东西。非飞行状态把 fixedDeltaTime 降到配置值，进飞行立即还原。
            if (cfgMenuFixedDelta > 0f)
            {
                if (_origFixedDelta < 0f)
                {
                    try { _origFixedDelta = Time.fixedDeltaTime; } catch { _origFixedDelta = 0.02f; }
                }
                bool inflight = false;
                try { inflight = Machine.Mod.MachineState.InFlight(); } catch { }
                float want = inflight ? _origFixedDelta : cfgMenuFixedDelta;
                if (!Mathf.Approximately(Time.fixedDeltaTime, want))
                {
                    try { Time.fixedDeltaTime = want; } catch { }
                }
            }

            _enforceT -= dt;
            if (_enforceT <= 0f)
            {
                _enforceT = cfgEnforceInterval > 0.5f ? cfgEnforceInterval : 99999f;
                Enforce();
            }

            // 渲染体来源普查（昂贵，默认关；只在配置打开时按 30s 节奏打一次）
            if (cfgDumpRenderers && Time.realtimeSinceStartup >= _dumpNext)
            {
                _dumpNext = Time.realtimeSinceStartup + 30f;
                DumpRenderers();
            }

            _repT -= dt;
            if (_repT > 0f) return;
            _repT = cfgReportInterval;
            Report();
        }

        private void OnDestroy()
        {
            // mod 被卸载/禁用时必须还原物理频率，否则飞行里会停留在降档的 fixedDeltaTime
            if (_origFixedDelta > 0f)
            {
                try { Time.fixedDeltaTime = _origFixedDelta; } catch { }
                _origFixedDelta = -1f;
            }
        }

        /// <summary>
        /// 统计当前所有 Renderer 的归属：按所属场景、按 root 对象名、按 layer。
        /// 用于回答"39k 个 renderer 到底是谁刷出来的"——是游戏本体还是某个 mod。
        /// 全量遍历 + 逐对象取 root 较贵，所以只在诊断开关打开时按 30s 跑一次，
        /// 并把这一帧从帧耗时统计里剔除（否则自己制造 HITCH）。
        /// </summary>
        private void DumpRenderers()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all == null || all.Length == 0) { _api.Log("OptiMod: dump renderers=0"); return; }

                var byScene = new Dictionary<string, int>();
                var byRoot = new Dictionary<string, int>();
                var byLayer = new Dictionary<int, int>();

                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null) continue;
                    string sc = "?";
                    string rt = "?";
                    int ly = 0;
                    try { sc = r.gameObject.scene.name; } catch { }
                    try { var tr = r.transform; if (tr.root != null) rt = tr.root.name; } catch { }
                    try { ly = r.gameObject.layer; } catch { }
                    int v;
                    byScene[sc] = byScene.TryGetValue(sc, out v) ? v + 1 : 1;
                    byRoot[rt] = byRoot.TryGetValue(rt, out v) ? v + 1 : 1;
                    byLayer[ly] = byLayer.TryGetValue(ly, out v) ? v + 1 : 1;
                }

                _api.Log("OptiMod: dump renderers total=" + all.Length + " | scenes=" + SceneName());
                foreach (var kv in byScene) _api.Log("OptiMod: dump byScene " + kv.Key + " = " + kv.Value);
                foreach (var kv in byLayer) _api.Log("OptiMod: dump byLayer " + kv.Key + " = " + kv.Value);

                var tops = new List<KeyValuePair<string, int>>(byRoot);
                tops.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                int n = Mathf.Min(15, tops.Count);
                for (int i = 0; i < n; i++)
                    _api.Log("OptiMod: dump root" + (i + 1) + " = " + tops[i].Value + "  '" + tops[i].Key + "'");
            }
            catch (Exception e) { _api.Log("OptiMod: dump failed " + e.Message); }
            // 这次普查本身花掉的时间不计入下一帧的帧耗时
            _swLast = _sw.ElapsedTicks;
        }

        // ---------------- 配置 ----------------

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "OptiMod", "opti_config.json");
                if (!File.Exists(path)) { _api.Log("OptiMod: no config, built-in defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("OptiMod: bad config json, defaults used"); return; }

                cfgProfiler = root.GetBool("profiler", cfgProfiler);
                cfgReportInterval = (float)root.GetNumber("reportInterval", cfgReportInterval);
                cfgHitchMs = (float)root.GetNumber("hitchMs", cfgHitchMs);
                cfgHitchLog = root.GetBool("hitchLog", cfgHitchLog);
                cfgCountObjects = root.GetBool("countObjects", cfgCountObjects);
                cfgMeshVisible = root.GetBool("meshVisible", cfgMeshVisible);
                cfgCamDetail = root.GetBool("camDetail", cfgCamDetail);
                cfgDumpRenderers = root.GetBool("dumpRenderers", cfgDumpRenderers);
                cfgPhaseProbe = root.GetBool("phaseProbe", cfgPhaseProbe);
                cfgMenuFixedDelta = (float)root.GetNumber("menuFixedDelta", cfgMenuFixedDelta);

                cfgCamProbe = root.GetBool("camProbe", cfgCamProbe);
                cfgProbeScreenCameras = root.GetBool("probeScreenCameras", cfgProbeScreenCameras);
                cfgProbeSec = (float)root.GetNumber("probeSec", cfgProbeSec);
                cfgProbeMaxBaseMs = (float)root.GetNumber("probeMaxBaseMs", cfgProbeMaxBaseMs);
                cfgDisableCameras = root.GetStringArray("disableCameras");
                cfgKillRootRenderers = root.GetStringArray("killRootRenderers");
                cfgKillComponents = root.GetStringArray("killComponents");

                cfgVSync = (int)root.GetNumber("vSync", cfgVSync);
                cfgTargetFrameRate = (int)root.GetNumber("targetFrameRate", cfgTargetFrameRate);
                cfgMaxDeltaTime = (float)root.GetNumber("maxDeltaTime", cfgMaxDeltaTime);
                cfgMaxParticleDeltaTime = (float)root.GetNumber("maxParticleDeltaTime", cfgMaxParticleDeltaTime);

                cfgFixedDeltaTime = (float)root.GetNumber("fixedDeltaTime", cfgFixedDeltaTime);
                cfgSolverIterations = (int)root.GetNumber("solverIterations", cfgSolverIterations);
                cfgSolverVelocityIterations = (int)root.GetNumber("solverVelocityIterations", cfgSolverVelocityIterations);
                cfgSleepThreshold = (float)root.GetNumber("sleepThreshold", cfgSleepThreshold);
                cfgParticleRaycastBudget = (int)root.GetNumber("particleRaycastBudget", cfgParticleRaycastBudget);
                cfgAsyncUploadTimeSlice = (int)root.GetNumber("asyncUploadTimeSlice", cfgAsyncUploadTimeSlice);
                cfgAsyncUploadBufferSize = (int)root.GetNumber("asyncUploadBufferSize", cfgAsyncUploadBufferSize);
                cfgMaxQueuedFrames = (int)root.GetNumber("maxQueuedFrames", cfgMaxQueuedFrames);

                cfgGcControl = root.GetBool("gcControl", cfgGcControl);
                cfgGcSliceMs = (float)root.GetNumber("gcSliceMs", cfgGcSliceMs);
                cfgGcCeilingMB = (float)root.GetNumber("gcCeilingMB", cfgGcCeilingMB);
                cfgStackTraceNone = root.GetBool("stackTraceNone", cfgStackTraceNone);
                cfgDedupCameras = root.GetBool("dedupCameras", cfgDedupCameras);
                cfgKillDupMainCam = root.GetBool("killDupMainCam", cfgKillDupMainCam);
                cfgBackgroundLoadingPriority = (int)root.GetNumber("backgroundLoadingPriority", cfgBackgroundLoadingPriority);
                cfgEnforceInterval = (float)root.GetNumber("enforceInterval", cfgEnforceInterval);
                cfgWarmupShaders = root.GetBool("warmupShaders", cfgWarmupShaders);

                cfgSkinOffscreen = root.GetBool("skinOffscreen", cfgSkinOffscreen);
                cfgInstancing = root.GetBool("instancing", cfgInstancing);
                cfgAutoSyncTransforms = (int)root.GetNumber("autoSyncTransforms", cfgAutoSyncTransforms);
                cfgSweepInterval = (float)root.GetNumber("sweepInterval", cfgSweepInterval);

                cfgShadowDistance = (float)root.GetNumber("shadowDistance", cfgShadowDistance);
                cfgLodBias = (float)root.GetNumber("lodBias", cfgLodBias);
                cfgPixelLightCount = (int)root.GetNumber("pixelLightCount", cfgPixelLightCount);

                _api.Log("OptiMod: config loaded from " + path);
            }
            catch (Exception e) { _api.Log("OptiMod: config error " + e.Message); }
        }

        // ---------------- 调优 ----------------

        /// <summary>把配置里非"保持"的项写进去，返回实际改动的清单。</summary>
        private string ApplyTuning()
        {
            var sb = new StringBuilder();
            int n = 0;

            if (cfgVSync >= 0 && SetStatic(TQuality, "vSyncCount", cfgVSync))
            { sb.Append(" vSync=").Append(cfgVSync); n++; }

            if (cfgTargetFrameRate >= 0)
            {
                int v = cfgTargetFrameRate == 0 ? -1 : cfgTargetFrameRate;
                if (SetStatic(TApp, "targetFrameRate", v)) { sb.Append(" targetFps=").Append(v); n++; }
            }

            if (cfgMaxDeltaTime > 0f && SetStatic(TTime, "maximumDeltaTime", cfgMaxDeltaTime))
            { sb.Append(" maxDeltaTime=").Append(cfgMaxDeltaTime.ToString("0.###")); n++; }

            if (cfgMaxParticleDeltaTime > 0f && SetStatic(TTime, "maximumParticleDeltaTime", cfgMaxParticleDeltaTime))
            { sb.Append(" maxParticleDeltaTime=").Append(cfgMaxParticleDeltaTime.ToString("0.###")); n++; }

            if (cfgFixedDeltaTime > 0f && SetStatic(TTime, "fixedDeltaTime", cfgFixedDeltaTime))
            { sb.Append(" fixedDeltaTime=").Append(cfgFixedDeltaTime.ToString("0.####")); n++; }

            // Unity 6 里这两个属性叫 defaultSolverIterations / defaultSolverVelocityIterations
            // （旧名 solverIterations 在部分版本不存在），所以两个名字都试一遍。
            if (cfgSolverIterations > 0 && SetStaticAny(TPhysics, SolverItNames, cfgSolverIterations))
            { sb.Append(" solverIterations=").Append(cfgSolverIterations); n++; }

            if (cfgSolverVelocityIterations > 0 && SetStaticAny(TPhysics, SolverVelNames, cfgSolverVelocityIterations))
            { sb.Append(" solverVelocityIterations=").Append(cfgSolverVelocityIterations); n++; }

            if (cfgSleepThreshold > 0f && SetStatic(TPhysics, "sleepThreshold", cfgSleepThreshold))
            { sb.Append(" sleepThreshold=").Append(cfgSleepThreshold.ToString("0.####")); n++; }

            if (cfgParticleRaycastBudget > 0 && SetStatic(TQuality, "particleRaycastBudget", cfgParticleRaycastBudget))
            { sb.Append(" particleRaycastBudget=").Append(cfgParticleRaycastBudget); n++; }

            if (cfgAsyncUploadTimeSlice > 0 && SetStatic(TQuality, "asyncUploadTimeSlice", cfgAsyncUploadTimeSlice))
            { sb.Append(" asyncUploadTimeSlice=").Append(cfgAsyncUploadTimeSlice); n++; }

            if (cfgAsyncUploadBufferSize > 0 && SetStatic(TQuality, "asyncUploadBufferSize", cfgAsyncUploadBufferSize))
            { sb.Append(" asyncUploadBufferSize=").Append(cfgAsyncUploadBufferSize); n++; }

            if (cfgMaxQueuedFrames >= 0 && SetStatic(TQuality, "maxQueuedFrames", cfgMaxQueuedFrames))
            { sb.Append(" maxQueuedFrames=").Append(cfgMaxQueuedFrames); n++; }

            if (cfgBackgroundLoadingPriority >= 0 && SetStatic(TApp, "backgroundLoadingPriority", cfgBackgroundLoadingPriority))
            { sb.Append(" backgroundLoadingPriority=").Append(cfgBackgroundLoadingPriority); n++; }

            // Physics.autoSyncTransforms 默认 true 时，任何 transform 改动都会触发一次物理同步；关掉是纯 CPU 收益。
            if (cfgAutoSyncTransforms == 0 && SetStatic(TPhysics, "autoSyncTransforms", false))
            { sb.Append(" autoSyncTransforms=false"); n++; }

            if (cfgShadowDistance > 0f && SetStatic(TQuality, "shadowDistance", cfgShadowDistance))
            { sb.Append(" shadowDistance=").Append(cfgShadowDistance.ToString("0.#")); n++; }

            if (cfgLodBias > 0f && SetStatic(TQuality, "lodBias", cfgLodBias))
            { sb.Append(" lodBias=").Append(cfgLodBias.ToString("0.##")); n++; }

            if (cfgPixelLightCount > 0 && SetStatic(TQuality, "pixelLightCount", cfgPixelLightCount))
            { sb.Append(" pixelLightCount=").Append(cfgPixelLightCount); n++; }

            if (cfgGcControl)
            {
                // GCMode: 0=Disabled 1=Enabled(自动增量) 2=Manual(由我们每帧驱动)
                if (SetStatic(TGc, "incrementalTimeSliceNanoseconds", (ulong)(cfgGcSliceMs * 1000000f)))
                { sb.Append(" gcSliceNs=").Append((ulong)(cfgGcSliceMs * 1000000f)); n++; }
                if (SetStatic(TGc, "GCMode", 2))
                { sb.Append(" gcMode=Manual"); n++; }
            }

            if (n == 0) return " (nothing changed - all knobs set to 'leave as-is')";
            return sb.ToString();
        }

        /// <summary>周期性纠偏：游戏设置面板/其它代码可能把帧率同步改回去。</summary>
        private void Enforce()
        {
            try
            {
                if (cfgVSync >= 0)
                {
                    object cur = GetStatic(TQuality, "vSyncCount");
                    if (!EqInt(cur, cfgVSync))
                    {
                        SetStatic(TQuality, "vSyncCount", cfgVSync);
                        _api.Log("OptiMod: drift fixed vSync " + Fmt(cur) + " -> " + cfgVSync);
                    }
                }
                if (cfgTargetFrameRate >= 0)
                {
                    int want = cfgTargetFrameRate == 0 ? -1 : cfgTargetFrameRate;
                    object cur = GetStatic(TApp, "targetFrameRate");
                    if (!EqInt(cur, want))
                    {
                        SetStatic(TApp, "targetFrameRate", want);
                        _api.Log("OptiMod: drift fixed targetFrameRate " + Fmt(cur) + " -> " + want);
                    }
                }
                if (cfgMaxDeltaTime > 0f && Math.Abs(Time.maximumDeltaTime - cfgMaxDeltaTime) > 0.0005f)
                    Time.maximumDeltaTime = cfgMaxDeltaTime;

                // 切场景后相机会重建，所以周期性再查一遍重复相机
                DedupCameras();
                ApplyDisableByName();
                ApplyKillRootRenderers();
                ApplyKillComponents();

                // 复扫（新生成的对象不在上次扫描范围内）
                SweepAux(false);
            }
            catch { }
        }

        // ---------------- 全局降本（纯 CPU，零视觉影响） ----------------

        /// <summary>
        /// 场景级小优化复扫：
        ///  · SkinnedMeshRenderer.updateWhenOffscreen=false —— 关掉它就不必为"屏幕外"的蒙皮网格
        ///    每帧重算骨骼变形，这是 Unity 官方推荐项，画面完全一致（只影响看不见的东西）。
        ///  · Material.enableInstancing —— 让同 mesh+材质的物体有机会合成一个 draw call（降 CPU 提交开销）。
        /// 成本：一次全场景扫描。所以只在切场景 + 每 sweepInterval 秒做一次，不在每帧做。
        /// </summary>
        private void SweepAux(bool force)
        {
            try
            {
                string scene = SceneName();
                double now = Time.realtimeSinceStartup;
                if (!force)
                {
                    if (scene == _sweepScene && now < _sweepT) return;
                }
                _sweepScene = scene;
                double iv = cfgSweepInterval > 5f ? cfgSweepInterval : 60.0;
                _sweepT = now + iv;
            }
            catch { }

            if (cfgSkinOffscreen)
            {
                try
                {
                    var sms = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    int n = 0;
                    if (sms != null)
                    {
                        for (int i = 0; i < sms.Length; i++)
                        {
                            var s = sms[i];
                            if (s == null) continue;
                            try { if (s.updateWhenOffscreen) { s.updateWhenOffscreen = false; n++; } } catch { }
                        }
                    }
                    _skinFixed += n;
                    if (force || n > 0)
                        _api.Log("OptiMod: sweep updateWhenOffscreen=false fixed=" + n
                            + " total=" + (sms == null ? 0 : sms.Length) + " scene=" + _sweepScene);
                }
                catch { }
            }

            if (cfgInstancing)
            {
                try
                {
                    var mats = Resources.FindObjectsOfTypeAll<Material>();
                    int n = 0;
                    if (mats != null)
                    {
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var m = mats[i];
                            if (m == null || m.shader == null) continue;
                            try { if (!m.enableInstancing) { m.enableInstancing = true; n++; } } catch { }
                        }
                    }
                    _matInstanced += n;
                    if (force || n > 0)
                        _api.Log("OptiMod: sweep enableInstancing=true changed=" + n + " materials=" + (mats == null ? 0 : mats.Length));
                }
                catch { }
            }
        }

        // ---------------- 相机处置 ----------------

        /// <summary>
        /// 诊断用：按类型全名子串把 MonoBehaviour 的 enabled 关掉（Unity 不再给它们派发
        /// Update/LateUpdate/OnGUI）。用来区分一个 mod 的开销到底来自"它自己的每帧脚本"
        /// 还是"它创建出来的对象/组件"。注意：绝不能把 OptiMod 自己列进去。
        /// </summary>
        private void ApplyKillComponents()
        {
            if (cfgKillComponents == null || cfgKillComponents.Length == 0) return;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all == null) return;
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null || !mb.enabled) continue;
                    string tn;
                    try { tn = mb.GetType().FullName; } catch { continue; }
                    if (string.IsNullOrEmpty(tn)) continue;
                    bool match = false;
                    for (int k = 0; k < cfgKillComponents.Length; k++)
                    {
                        string pat = cfgKillComponents[k];
                        if (string.IsNullOrEmpty(pat)) continue;
                        if (tn.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    }
                    if (!match) continue;
                    mb.enabled = false;
                    _killedComps.Add(mb);
                    n++;
                }
                if (n > 0) _api.Log("OptiMod: killComp disabled " + n + " component(s) matching [" + string.Join(",", cfgKillComponents) + "]");
            }
            catch (Exception e) { _api.Log("OptiMod: killComp failed " + e.Message); }
        }

        /// <summary>
        /// 诊断用：把配置里列出的 root 对象（名字子串匹配）下的所有 Renderer 关掉。
        /// 目的不是"修"，而是量化——把 4 万个渲染体的渲染/剔除代价直接减掉，看帧耗时掉多少。
        /// 只关 Renderer（不动 GameObject），避免破坏游戏自身的池化逻辑；
        /// 每 enforceInterval 秒复扫一次，因为游戏可能自己把这些 renderer 重新启用。
        /// </summary>
        private void ApplyKillRootRenderers()
        {
            if (cfgKillRootRenderers == null || cfgKillRootRenderers.Length == 0) return;
            try
            {
                // 还没找到的 root 继续找（它可能是切场景之后才被创建的）
                for (int k = 0; k < cfgKillRootRenderers.Length; k++)
                {
                    string nm = cfgKillRootRenderers[k];
                    if (string.IsNullOrEmpty(nm)) continue;
                    bool have = false;
                    for (int i = 0; i < _killedRoots.Count; i++)
                    {
                        var g = _killedRoots[i];
                        if (g != null && g.name == nm) { have = true; break; }
                    }
                    if (have) continue;

                    var go = GameObject.Find(nm);
                    if (go == null) continue;   // 可能还没生成，下次 Enforce 再试
                    _killedRoots.Add(go);
                    var rs = go.GetComponentsInChildren<Renderer>(true);
                    int n = 0;
                    if (rs != null)
                        for (int i = 0; i < rs.Length; i++)
                        {
                            if (rs[i] == null) continue;
                            if (rs[i].enabled) rs[i].enabled = false;
                            _killedRends.Add(rs[i]);
                            n++;
                        }
                    _api.Log("OptiMod: killRoot '" + nm + "' renderers off = " + n);
                }
                _killRootsTried = true;

                // 复扫：游戏可能又把它们启用回去
                int back = 0;
                for (int i = 0; i < _killedRends.Count; i++)
                {
                    var r = _killedRends[i];
                    if (r == null) continue;
                    if (r.enabled) { r.enabled = false; back++; }
                }
                if (back > 0) _api.Log("OptiMod: killRoot re-disabled " + back + " renderer(s)");
            }
            catch (Exception e) { _api.Log("OptiMod: killRoot failed " + e.Message); }
        }

        /// <summary>按名字子串永久禁用相机（用于把探针证明"整场景白渲染一遍"的相机掉）。</summary>
        private void ApplyDisableByName()
        {
            if (cfgDisableCameras == null || cfgDisableCameras.Length == 0)
            {
                RestoreByName();
                return;
            }
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (cams == null) return;
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    if (c == null || !c.enabled) continue;
                    string nm = c.name;
                    bool match = false;
                    for (int k = 0; k < cfgDisableCameras.Length; k++)
                    {
                        string pat = cfgDisableCameras[k];
                        if (string.IsNullOrEmpty(pat)) continue;
                        if (nm.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    }
                    if (!match) continue;
                    int id = c.GetInstanceID();
                    if (_disabledByName.Contains(id)) continue;
                    _disabledByName.Add(id);
                    c.enabled = false;
                    _api.Log("OptiMod: disabled camera by name '" + nm + "' id=" + id + " (config disableCameras)");
                }
            }
            catch { }
        }

        /// <summary>配置里去掉 disableCameras 后，把之前关掉的相机还回去。</summary>
        private void RestoreByName()
        {
            if (_disabledByName.Count == 0) return;
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (cams != null)
                {
                    int n = 0;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var c = cams[i];
                        if (c == null) continue;
                        if (!_disabledByName.Contains(c.GetInstanceID())) continue;
                        if (!c.enabled) { c.enabled = true; n++; }
                    }
                    if (n > 0) _api.Log("OptiMod: re-enabled " + n + " camera(s) that disableCameras had switched off");
                }
            }
            catch { }
            _disabledByName.Clear();
        }

        /// <summary>
        /// 逐个消融"带 RenderTexture 的相机"并测帧耗时差值。
        /// 为什么只测 RT 相机：把 RT 相机 enabled=false 只会让某块离屏贴图这一小会儿是旧的/黑的，
        /// 主画面不受影响；而关主画面相机会直接黑屏，不能这么干。
        /// 每个场景每个相机只测一次，测完就不再打扰。
        /// </summary>
        private void StartProbe()
        {
                if (!cfgCamProbe || _probeActive) return;
            try
            {
                // 场景刚切换：先静置。转场/读档那一两秒帧耗时又大又抖，
                // 这时做消融既测不准（基准不可比），又正好往最忙的帧上加活。
                if (_sceneSettleT > 0f) return;
                // 加载期帧耗时巨大且抖动，基准根本不可比 —— 这会儿不测，等稳态再测
                if (cfgProbeMaxBaseMs > 0f && _lastAvgMs > cfgProbeMaxBaseMs) return;
                string scene = SceneName();
                if (scene != _probeLastScene)
                {
                    // 换了场景：本轮不做实验，先把静置计时拉起来，下个报告周期再测
                    _probeLastScene = scene;
                    _sceneSettleT = Mathf.Max(0f, cfgProbeSettleSec);
                    return;
                }
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (cams == null) return;

                Camera pick = null;
                string pickSig = null;
                bool pickRt = false;
                int rtW = 0, rtH = 0;
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    if (c == null || !c.enabled) continue;
                    bool isRt = c.targetTexture != null;
                    // 默认只处理往 RenderTexture 画的：关掉它只会让某块离屏贴图这一小会儿是旧的，
                    // 主画面不受影响。cfgProbeScreenCameras=true 时才连屏幕相机一起测（画面会闪一下）。
                    if (!isRt && !cfgProbeScreenCameras) continue;
                    if (!isRt && IsMainCamera(c)) continue;   // 主相机永远不动（很多系统代码依赖它）
                    string sig = (isRt ? "RT|" : "SC|") + scene + "|" + c.name + "|" + c.pixelWidth + "x" + c.pixelHeight;
                    if (_probedSigs.Contains(sig)) continue;
                    pick = c; pickSig = sig; pickRt = isRt;
                    if (isRt) { rtW = c.targetTexture.width; rtH = c.targetTexture.height; }
                    else { rtW = c.pixelWidth; rtH = c.pixelHeight; }
                    break;
                }
                if (pick == null) return;

                if (_probedSigs.Count > 64) _probedSigs.Clear();
                _probedSigs.Add(pickSig);

                _probeCam = pick;
                _probeBaseMs = _lastAvgMs;
                _probeAccum = 0f;
                _probeFrames = 0;
                _probeWasEnabled = pick.enabled;
                pick.enabled = false;
                _probeCount++;
                _probeActive = true;
                _probeT = Mathf.Clamp(cfgProbeSec, 0.15f, 3f);
                _api.Log("OptiMod: camProbe START '" + pick.name + "' " + (pickRt ? "rt" : "screen") + "=" + rtW + "x" + rtH
                    + " maskBits=" + MaskBits(pick.cullingMask)
                    + " base=" + _probeBaseMs.ToString("F1") + "ms scene=" + scene
                    + (pickRt ? "" : " [screen camera - picture blinks]"));
            }
            catch { }
        }

        /// <summary>结束消融：恢复相机，算出"这个相机每帧大约吃掉多少 ms"。</summary>
        private void EndProbe()
        {
            _probeActive = false;
            Camera c = _probeCam;
            _probeCam = null;

            bool fought = false;
            try
            {
                if (c != null)
                {
                    if (!c.enabled) c.enabled = true;      // 恢复（若已是 true 说明游戏/其它 mod 自己把它开回来了）
                    else fought = true;
                }
            }
            catch { }

            float avg = _probeFrames > 0 ? _probeAccum * 1000f / _probeFrames : -1f;
            float delta = (_probeBaseMs > 0f && avg > 0f) ? (_probeBaseMs - avg) : 0f;
            try
            {
                _api.Log("OptiMod: camAb '" + (c != null ? c.name : "?") + "'"
                    + " base=" + _probeBaseMs.ToString("F1") + "ms"
                    + " probe=" + avg.ToString("F1") + "ms"
                    + " n=" + _probeFrames
                    + " -> cost=" + (delta >= 0f ? "+" : "") + delta.ToString("F1") + "ms/frame"
                    + (fought ? " [re-enabled by game - result unreliable]" : (delta > 3f ? " [EXPENSIVE]" : " [cheap]")));
            }
            catch { }
        }

        /// <summary>相机清单（切场景/每 60s）。RT 相机 = 每个都往贴图里重画一遍场景，是主线程最容易被忽略的大头。</summary>
        private void LogCameras()
        {
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (cams == null || cams.Length == 0) { _api.Log("OptiMod: cameras=none"); return; }

                // 一次性统计每个 layer 上有多少个 Renderer，再按每个相机的 cullingMask 求和 ——
                // 这样不用真关相机就能看出"哪个相机每帧要剔除多少东西"（culls=N/total）。
                int[] layerCount = new int[32];
                int totalR = 0;
                try
                {
                    var rs = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    if (rs != null)
                    {
                        totalR = rs.Length;
                        for (int i = 0; i < rs.Length; i++)
                        {
                            var r = rs[i];
                            if (r == null) continue;
                            int l = r.gameObject.layer;
                            if (l >= 0 && l < 32) layerCount[l]++;
                        }
                    }
                }
                catch { }
                _api.Log("OptiMod: renderers total=" + totalR);

                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    if (c == null) continue;
                    string rt = "-";
                    try { if (c.targetTexture != null) rt = c.targetTexture.width + "x" + c.targetTexture.height; } catch { }
                    Vector3 p = c.transform.position;
                    string sc = "";
                    try { sc = c.gameObject.scene.name; } catch { }
                    bool isMain = false;
                    try { isMain = IsMainCamera(c); } catch { }
                    int mask = c.cullingMask;
                    int n = 0;
                    for (int b = 0; b < 32; b++) if ((mask & (1 << b)) != 0) n += layerCount[b];
                    _api.Log("OptiMod: cam[" + i + "] '" + c.name + "' en=" + c.enabled
                        + " depth=" + c.depth
                        + " px=" + c.pixelWidth + "x" + c.pixelHeight
                        + " rt=" + rt
                        + " maskBits=" + MaskBits(mask)
                        + " culls=" + n + "/" + totalR
                        + " clear=" + c.clearFlags
                        + " fov=" + c.fieldOfView.ToString("F0")
                        + " far=" + c.farClipPlane.ToString("F0")
                        + " hdr=" + c.allowHDR
                        + " msaa=" + c.allowMSAA
                        + " occl=" + c.useOcclusionCulling
                        + " pos=(" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ")"
                        + " scene=" + sc
                        + " isMain=" + isMain);
                }
            }
            catch { }
        }

        private static int MaskBits(int mask)
        {
            int n = 0;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) n++;
            return n;
        }

        private static bool IsMainCamera(Camera c)
        {
            try { return ReferenceEquals(c, Camera.main); } catch { return false; }
        }

        private static string SceneName()
        {
            try
            {
                var act = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                int n = UnityEngine.SceneManagement.SceneManager.sceneCount;
                // 叠加场景（Fader 等）加载得更晚时会抢走 GetActiveScene，
                // 只报活跃场景名会把所有采样都标成 "Fader"，这里列出全部并把活跃的那台标 *。
                if (n <= 1) return act.name;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (i > 0) sb.Append('+');
                    sb.Append(s.name);
                    if (s == act) sb.Append('*');
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        // ---------------- 玩家循环分段探针 ----------------
        //
        // 为什么需要它：非 development 构建里 FrameTimingManager 返回 0，拿不到 CPU/GPU 分解，
        // 而"帧耗时到底花在 Update 脚本、LateUpdate 脚本还是渲染"是优化方向的分水岭。
        // 做法：往 PlayerLoop 的 6 个顶层阶段（EarlyUpdate / FixedUpdate / PreUpdate / Update /
        // PreLateUpdate / PostLateUpdate）前面各插一个空系统当标记，标记只记 Stopwatch 时间戳。
        // 相邻标记相减 = 该阶段耗时；最后一阶段到下一帧首标记之间 = 渲染 + 帧间空隙。

        private static readonly long[] _ph = new long[6];
        private static readonly List<UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction> _phKeepAlive
            = new List<UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction>();
        private static double _phEarly, _phFixed, _phPre, _phUpd, _phLate, _phPost;
        private static int _phFrames;
        private static bool _phPatched;

        /// <summary>把标记系统插进玩家循环。失败时静默放弃，绝不改动原始循环。</summary>
        private void PatchPhaseLoop()
        {
            if (_phPatched) return;
            try
            {
                var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                var subs = loop.subSystemList;
                if (subs == null || subs.Length == 0) { _api.Log("OptiMod: phaseProbe failed (empty loop)"); return; }

                // 目标阶段类型 → 标记下标
                var want = new Type[] {
                    typeof(UnityEngine.PlayerLoop.EarlyUpdate),
                    typeof(UnityEngine.PlayerLoop.FixedUpdate),
                    typeof(UnityEngine.PlayerLoop.PreUpdate),
                    typeof(UnityEngine.PlayerLoop.Update),
                    typeof(UnityEngine.PlayerLoop.PreLateUpdate),
                    typeof(UnityEngine.PlayerLoop.PostLateUpdate)
                };

                var list = new List<UnityEngine.LowLevel.PlayerLoopSystem>(subs.Length + want.Length);
                int inserted = 0;
                for (int i = 0; i < subs.Length; i++)
                {
                    for (int k = 0; k < want.Length; k++)
                    {
                        if (subs[i].type != want[k]) continue;
                        var mark = new UnityEngine.LowLevel.PlayerLoopSystem();
                        mark.type = typeof(OptiPhaseMarker);
                        int slot = k;
                        UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction fn = delegate { PhaseMark(slot); };
                        _phKeepAlive.Add(fn);
                        mark.updateDelegate = fn;
                        list.Add(mark);
                        inserted++;
                        break;
                    }
                    list.Add(subs[i]);
                }
                if (inserted < want.Length) { _api.Log("OptiMod: phaseProbe found " + inserted + "/6 phases, skipped"); return; }

                loop.subSystemList = list.ToArray();
                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
                _phPatched = true;
                _api.Log("OptiMod: phaseProbe installed (" + inserted + " markers)");
            }
            catch (Exception e) { _api.Log("OptiMod: phaseProbe install failed " + e.Message); }
        }

        /// <summary>标记回调（由玩家循环调用，主线程）。</summary>
        private static void PhaseMark(int idx)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (idx == 0)
            {
                if (_ph[5] != 0) { _phPost += TsMs(t - _ph[5]); _phFrames++; }
                _ph[0] = t;
                _ph[5] = 0;
                return;
            }
            _ph[idx] = t;
            if (idx == 1 && _ph[0] != 0) _phEarly += TsMs(t - _ph[0]);
            else if (idx == 2 && _ph[1] != 0) _phFixed += TsMs(t - _ph[1]);
            else if (idx == 3 && _ph[2] != 0) _phPre += TsMs(t - _ph[2]);
            else if (idx == 4 && _ph[3] != 0) _phUpd += TsMs(t - _ph[3]);
            else if (idx == 5 && _ph[4] != 0) _phLate += TsMs(t - _ph[4]);
        }

        private static double TsMs(long dt)
        {
            return dt * 1000.0 / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>取出并清零分段累计值，返回可直接拼进日志的一行。</summary>
        private static string TakePhaseStats()
        {
            int n = _phFrames;
            if (n <= 0) return "phase n=0";
            string s = "phase n=" + n
                + " early=" + (_phEarly / n).ToString("0.0")
                + " fixed=" + (_phFixed / n).ToString("0.0")
                + " preUpd=" + (_phPre / n).ToString("0.0")
                + " upd=" + (_phUpd / n).ToString("0.0")
                + " preLate=" + (_phLate / n).ToString("0.0")
                + " postLate=" + (_phPost / n).ToString("0.0");
            _phEarly = _phFixed = _phPre = _phUpd = _phLate = _phPost = 0;
            _phFrames = 0;
            return s;
        }

        /// <summary>标记系统用的占位类型（玩家循环只把它当标识）。</summary>
        private struct OptiPhaseMarker { }

        // ---------------- 诊断 ----------------

        private void Report()
        {
            float fps = _frameCount > 0 ? _frameCount / Mathf.Max(0.0001f, _frameAccum) : 0f;
            float avgMs = _frameCount > 0 ? _frameAccum * 1000f / Mathf.Max(1, _frameCount) : 0f;
            if (avgMs > 0f) _lastAvgMs = avgMs;

            long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            double d0 = g0 - _gc0, d1 = g1 - _gc1, d2 = g2 - _gc2;
            _gc0 = g0; _gc1 = g1; _gc2 = g2;

            double monoMB = -1, allocMB = -1, resMB = -1, managedMB = -1;
            try { monoMB = Profiler.GetMonoUsedSizeLong() / 1048576.0; } catch { }
            try { allocMB = Profiler.GetTotalAllocatedMemoryLong() / 1048576.0; } catch { }
            try { resMB = Profiler.GetTotalReservedMemoryLong() / 1048576.0; } catch { }
            try { managedMB = GC.GetTotalMemory(false) / 1048576.0; } catch { }

            double cpuPct = -1;
            try
            {
                double sec = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
                float wall = Time.realtimeSinceStartup - _cpuWallStart;
                if (_cpuSec >= 0 && wall > 0.2 && sec >= _cpuSec)
                {
                    cpuPct = (sec - _cpuSec) / wall * 100.0;
                    _cpuSec = sec;
                    _cpuWallStart = Time.realtimeSinceStartup;
                }
            }
            catch { }

            string scene = SceneName();

            int cntSlot = UpdateCounts();

            string obj = "";
            if (cfgCountObjects)
            {
                obj = " obj[cnt" + cntSlot + " rig=" + _cntRig + " ps=" + _cntPs + " particle=" + _cntPart
                    + " mesh=" + _cntMesh + " audio=" + _cntAudio + " cam=" + _cntCam + "]";
            }

            // 注：Unity 用 Boehm GC，GC.CollectionCount 的 0/1/2 三代数值相同，没有分代意义，
            // 所以只报 gen0 次数 + 托管堆增量，堆不涨就说明 GC 没在积累垃圾。
            double dHeap = (_lastMonoMB >= 0 && monoMB >= 0) ? (monoMB - _lastMonoMB) : 0;
            _lastMonoMB = monoMB;

            _api.Log("OptiMod: rep t=" + Time.realtimeSinceStartup.ToString("F0") + "s scene=" + scene
                + " fps=" + fps.ToString("F1")
                + " avg=" + avgMs.ToString("F1") + "ms max=" + Mathf.RoundToInt(_frameMax) + "ms"
                + " n=" + _frameCount
                + " hitch=" + _hitchCount + (cfgHitchMs > 0f ? ("(>" + Mathf.RoundToInt(cfgHitchMs) + "ms)") : "")
                + " gc=" + d0 + " dHeap=" + (dHeap >= 0 ? "+" : "") + dHeap.ToString("F1") + "MB"
                + " mono=" + Num1(monoMB) + "MB alloc=" + Num1(allocMB) + "MB res=" + Num1(resMB) + "MB"
                + (cpuPct >= 0 ? (" cpu=" + cpuPct.ToString("F0") + "%") : " cpu=n/a")
                + obj);

            // 玩家循环分段（phaseProbe=true 时有值；末段含渲染 + vSync 等待）
            if (cfgPhaseProbe)
            {
                string ph = TakePhaseStats();
                if (ph != "phase n=0") _api.Log("OptiMod: " + ph);
            }

            if (cfgCamDetail && (scene != _lastScene || Time.realtimeSinceStartup >= _camDetailT))
            {
                _lastScene = scene;
                _camDetailT = Time.realtimeSinceStartup + 60f;
                LogCameras();
            }

            WriteCsv(scene, fps, avgMs, _frameMax, _frameCount, _hitchCount, d0, monoMB, allocMB, resMB, cpuPct, obj);

            _frameAccum = 0f;
            _frameMax = 0f;
            _frameCount = 0;
            _hitchCount = 0;
            _hitchLogged = 0;

            GcGuard(monoMB);

            // 报告刚结束、基准帧耗时已知 —— 这是做消融实验最合适的时刻
            StartProbe();
        }

        /// <summary>
        /// 场景对象计数：原来每 4s 一口气跑 5 次 FindObjectsByType（其中 MeshRenderer 一次就返回几万个元素，
        /// 再逐个读 isVisible），这本身就是"每 4s 一次尖峰"的来源。现在改成每周期只统计 1 类，轮转。
        /// </summary>
        private int UpdateCounts()
        {
            if (!cfgCountObjects) return -1;
            int slot = _cntSlot++ % 4;
            try
            {
                if (slot == 0)
                {
                    var a = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    _cntRig = (a == null ? 0 : a.Length).ToString();
                }
                else if (slot == 1)
                {
                    var b = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    int total = 0;
                    if (b != null) for (int i = 0; i < b.Length; i++) { try { total += b[i].particleCount; } catch { } }
                    _cntPs = (b == null ? 0 : b.Length).ToString();
                    _cntPart = total.ToString();
                }
                else if (slot == 2)
                {
                    var c = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    if (c != null)
                    {
                        if (cfgMeshVisible)
                        {
                            int vis = 0;
                            for (int i = 0; i < c.Length; i++)
                            {
                                try { if (c[i].enabled && c[i].isVisible) vis++; } catch { }
                            }
                            _cntMesh = c.Length + "/" + vis;
                        }
                        else _cntMesh = c.Length.ToString();
                    }
                }
                else
                {
                    var d = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    _cntAudio = (d == null ? 0 : d.Length).ToString();
                }
            }
            catch { }

            // 相机数很便宜（场景里只有几个），每周期都刷
            try
            {
                var e = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                int on = 0;
                if (e != null) for (int i = 0; i < e.Length; i++) { try { if (e[i].enabled) on++; } catch { } }
                _cntCam = (e == null ? 0 : e.Length) + "/" + on;
            }
            catch { }
            return slot;
        }

        private readonly List<int> _disabledCamsStrict = new List<int>();
        private readonly List<int> _disabledCamsLoose = new List<int>();

        /// <summary>
        /// 关掉"完全重复的全屏相机"。
        /// 每个往屏幕画的启用相机都会对全场景跑一遍剔除 + 一次完整提交；两个参数完全一致、
        /// 且都 clear=Skybox 的相机叠在一起时，后画的会把前一个整个盖掉 —— 前一个的工作 100% 浪费。
        /// 判重很严格：depth / cullingMask / clearFlags / 像素尺寸 / 视口 / FOV / 近远裁剪 /
        /// 正交开关 / 位置 / 旋转 全部相同才算重复；重复组里优先保留 Camera.main。
        /// </summary>
        private void DedupCameras()
        {
            // 开关关掉时要把之前关掉的相机还回去 —— 否则上一次误关的相机会一直黑着（游戏自己不会再开）
            if (cfgDedupCameras) DedupCamerasCore(false, _disabledCamsStrict);
            else RestoreDisabledCams(_disabledCamsStrict);

            if (cfgKillDupMainCam) DedupCamerasCore(true, _disabledCamsLoose);
            else RestoreDisabledCams(_disabledCamsLoose);
        }

        /// <summary>把本 Mod 之前关掉的相机重新启用（开关关闭后纠偏）。</summary>
        private void RestoreDisabledCams(List<int> done)
        {
            if (done.Count == 0) return;
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (cams != null)
                {
                    int n = 0;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var c = cams[i];
                        if (c == null) continue;
                        if (!done.Contains(c.GetInstanceID())) continue;
                        if (!c.enabled) { c.enabled = true; n++; }
                    }
                    if (n > 0) _api.Log("OptiMod: re-enabled " + n + " camera(s) that dedup had disabled");
                }
            }
            catch { }
            done.Clear();
        }

        private void DedupCamerasCore(bool loose, List<int> done)
        {
            try
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (cams == null || cams.Length < 2) return;

                Camera mainCam = null;
                try { mainCam = Camera.main; } catch { }
                if (loose && mainCam == null) return;   // 宽松模式必须以 Camera.main 为保留基准

                var sigs = new List<string>();
                var groups = new List<List<Camera>>();
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    if (c == null || !c.enabled) continue;
                    if (c.targetTexture != null) continue;   // 只处理直接往屏幕画的
                    Rect r = c.rect;
                    if (r.x != 0f || r.y != 0f || r.width != 1f || r.height != 1f) continue; // 只处理全屏
                    string sig = CamSig(c, loose);
                    int gi = sigs.IndexOf(sig);
                    if (gi < 0)
                    {
                        sigs.Add(sig);
                        var g = new List<Camera>();
                        g.Add(c);
                        groups.Add(g);
                    }
                    else groups[gi].Add(c);
                }

                for (int g = 0; g < groups.Count; g++)
                {
                    var grp = groups[g];
                    if (grp.Count < 2) continue;
                    Camera keep = null;
                    for (int k = 0; k < grp.Count; k++)
                        if (keep == null || ReferenceEquals(grp[k], mainCam)) keep = grp[k];
                    if (loose && !ReferenceEquals(keep, mainCam)) continue;

                    for (int k = 0; k < grp.Count; k++)
                    {
                        var c = grp[k];
                        if (ReferenceEquals(c, keep)) continue;
                        int id = c.GetInstanceID();
                        if (done.Contains(id)) continue;   // 已经处理过就不再动，避免和游戏抢
                        done.Add(id);
                        c.enabled = false;
                        _api.Log("OptiMod: disabled duplicate full-screen camera '" + c.name + "' id=" + id
                            + " (identical to kept '" + keep.name + "'"
                            + (ReferenceEquals(keep, mainCam) ? " = Camera.main" : "") + ")");
                    }
                }
            }
            catch { }
        }

        /// <summary>loose=true 时只比 depth/cullingMask/clearFlags/像素尺寸，忽略位姿与 FOV（用于 killDupMainCam）。
        /// ⚠ 两种模式都带上了"所属场景"：不同场景里的同名相机不是重复，而是各管一段
        /// （实测 Fader 场景那台是静止的，Flying 场景那台才是跟机的游戏相机，
        ///  早期版本不看场景，宽松判重直接把飞行相机当"重复"关掉了 —— 视角全错）。</summary>
        private static string CamSig(Camera c, bool loose)
        {
            string sc;
            try { sc = c.gameObject.scene.name; } catch { sc = "?"; }
            string head = sc + "|" + c.depth + "|" + c.cullingMask + "|" + (int)c.clearFlags
                          + "|" + c.pixelWidth + "x" + c.pixelHeight;
            if (loose) return head;
            Vector3 p = c.transform.position;
            Quaternion q = c.transform.rotation;
            return head
                + "|" + c.fieldOfView.ToString("F3") + "|" + c.nearClipPlane.ToString("F3") + "|" + c.farClipPlane.ToString("F3")
                + "|" + (c.orthographic ? "O" + c.orthographicSize.ToString("F3") : "P")
                + "|" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2")
                + "|" + q.x.ToString("F3") + "," + q.y.ToString("F3") + "," + q.z.ToString("F3") + "," + q.w.ToString("F3");
        }

        /// <summary>卡顿瞬间的 CPU 主线程 / 渲染线程 / GPU 分解（判断到底卡在哪一侧）。</summary>
        private string FrameBreakdown()
        {
            try
            {
                uint n = FrameTimingManager.GetLatestTimings((uint)_timings.Length, _timings);
                if (n == 0)
                {
                    // 非 development 构建通常拿不到 FrameTiming，只在第一次说明一次，别每帧刷屏
                    if (!_ftReported) { _ftReported = true; return "frameTiming=unavailable(non-dev build)"; }
                    return "";
                }
                double cm = 0, cr = 0, gp = 0, ct = 0;
                for (int i = 0; i < n; i++)
                {
                    double a = FT(_timings[i], "cpuMainThreadFrameTime"); if (a > cm) cm = a;
                    double b = FT(_timings[i], "cpuRenderThreadFrameTime"); if (b > cr) cr = b;
                    double c = FT(_timings[i], "gpuFrameTime"); if (c > gp) gp = c;
                    double d = FT(_timings[i], "cpuFrameTime"); if (d > ct) ct = d;
                }
                return "ft[n=" + n + " cpuMain=" + cm.ToString("F1") + " cpuRender=" + cr.ToString("F1")
                    + " gpu=" + gp.ToString("F1") + " cpuAll=" + ct.ToString("F1") + "]";
            }
            catch { return "frameTiming=err"; }
        }

        /// <summary>手动 GC 的安全阀：托管堆涨过上限就退回 Unity 自动 GC，30 秒后再试。</summary>
        private void GcGuard(double monoMB)
        {
            if (!cfgGcControl) return;
            if (_gcClamped)
            {
                if (Time.realtimeSinceStartup < _gcRecheckT) return;
                _gcClamped = false;
                SetStatic(TGc, "GCMode", 2);
                _api.Log("OptiMod: GC manual mode re-armed");
                return;
            }
            if (monoMB > cfgGcCeilingMB)
            {
                SetStatic(TGc, "GCMode", 1);
                _gcClamped = true;
                _gcRecheckT = Time.realtimeSinceStartup + 30f;
                _api.Log("OptiMod: mono " + monoMB.ToString("F0") + "MB > ceiling " + cfgGcCeilingMB.ToString("F0")
                    + "MB -> GC back to auto for 30s");
            }
        }

        // ---------------- CSV ----------------

        /// <summary>2.1 起写到 opti_perf2.csv（列与 2.0 的 opti_perf.csv 不同，避免混在一张表里对不齐）。</summary>
        private void WriteCsv(string scene, float fps, float avgMs, float maxMs, int frames, int hitch,
            double d0, double monoMB, double allocMB, double resMB, double cpuPct, string obj)
        {
            try
            {
                if (!_csvInit)
                {
                    _csvInit = true;
                    string dir = null;
                    try { dir = Path.GetFullPath(Path.Combine(_api.GetModsDirectory(), "..", "Machine", "logs")); } catch { }
                    if (dir == null || !Directory.Exists(dir)) dir = Path.Combine(_api.GetModsDirectory(), "OptiMod");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _csv = new StreamWriter(Path.Combine(dir, "opti_perf2.csv"), true);
                    _csv.WriteLine("t_s,scene,fps,avg_ms,max_ms,frames,hitch,gc0,mono_mb,alloc_mb,res_mb,cpu_pct,cam,objects");
                    _csv.Flush();
                }
                _csv.WriteLine(
                    Time.realtimeSinceStartup.ToString("F1") + "," + scene + "," +
                    fps.ToString("F1") + "," + avgMs.ToString("F2") + "," + maxMs.ToString("F0") + "," +
                    frames + "," + hitch + "," +
                    d0.ToString("F0") + "," +
                    Num1(monoMB) + "," + Num1(allocMB) + "," + Num1(resMB) + "," +
                    (cpuPct >= 0 ? cpuPct.ToString("F0") : "") + "," +
                    _cntCam + "," +
                    obj.Replace(",", ";"));
                _csv.Flush();
            }
            catch { }
        }

        // ---------------- 小工具 ----------------

        private string Snapshot()
        {
            var sb = new StringBuilder();
            sb.Append("targetFps=").Append(Fmt(GetStatic(TApp, "targetFrameRate")));
            sb.Append(" vSync=").Append(Fmt(GetStatic(TQuality, "vSyncCount")));
            sb.Append(" fixedDt=").Append(Fmt(GetStatic(TTime, "fixedDeltaTime")));
            sb.Append(" maxDt=").Append(Fmt(GetStatic(TTime, "maximumDeltaTime")));
            sb.Append(" maxParticleDt=").Append(Fmt(GetStatic(TTime, "maximumParticleDeltaTime")));
            sb.Append(" solverIterations=").Append(Fmt(GetStaticAny(TPhysics, SolverItNames)));
            sb.Append(" solverVelocityIterations=").Append(Fmt(GetStaticAny(TPhysics, SolverVelNames)));
            sb.Append(" sleepThreshold=").Append(Fmt(GetStatic(TPhysics, "sleepThreshold")));
            sb.Append(" particleRaycastBudget=").Append(Fmt(GetStatic(TQuality, "particleRaycastBudget")));
            sb.Append(" asyncUploadTimeSlice=").Append(Fmt(GetStatic(TQuality, "asyncUploadTimeSlice")));
            sb.Append(" asyncUploadBufferSize=").Append(Fmt(GetStatic(TQuality, "asyncUploadBufferSize")));
            sb.Append(" maxQueuedFrames=").Append(Fmt(GetStatic(TQuality, "maxQueuedFrames")));
            sb.Append(" gcMode=").Append(Fmt(GetStatic(TGc, "GCMode")));
            sb.Append(" gcSliceNs=").Append(Fmt(GetStatic(TGc, "incrementalTimeSliceNanoseconds")));
            sb.Append(" bgLoading=").Append(Fmt(GetStatic(TApp, "backgroundLoadingPriority")));
            sb.Append(" autoSyncTransforms=").Append(Fmt(GetStatic(TPhysics, "autoSyncTransforms")));
            sb.Append(" shadowDistance=").Append(Fmt(GetStatic(TQuality, "shadowDistance")));
            sb.Append(" lodBias=").Append(Fmt(GetStatic(TQuality, "lodBias")));
            return sb.ToString();
        }

        private static object GetStatic(Type t, string name)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (p != null && p.CanRead) return p.GetValue(null, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        private static bool SetStatic(Type t, string name, object val)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(null, Coerce(val, p.PropertyType), null); return true; }
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(null, Coerce(val, f.FieldType)); return true; }
            }
            catch { }
            return false;
        }

        private static object GetStaticAny(Type t, string[] names)
        {
            foreach (string n in names)
            {
                object v = GetStatic(t, n);
                if (v != null) return v;
            }
            return null;
        }

        private static bool SetStaticAny(Type t, string[] names, object val)
        {
            foreach (string n in names) if (SetStatic(t, n, val)) return true;
            return false;
        }

        private static void InvokeStatic(Type t, string name)
        {
            MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (m != null) m.Invoke(null, null);
        }

        private static object Coerce(object val, Type target)
        {
            if (val == null) return null;
            if (target.IsEnum) return Enum.ToObject(target, Convert.ToInt64(val));
            if (target == typeof(int)) return Convert.ToInt32(val);
            if (target == typeof(uint)) return Convert.ToUInt32(val);
            if (target == typeof(long)) return Convert.ToInt64(val);
            if (target == typeof(ulong)) return Convert.ToUInt64(val);
            if (target == typeof(float)) return Convert.ToSingle(val);
            if (target == typeof(double)) return Convert.ToDouble(val);
            if (target == typeof(bool)) return Convert.ToBoolean(val);
            return Convert.ChangeType(val, target);
        }

        private static double FT(FrameTiming t, string name)
        {
            try
            {
                FieldInfo f;
                if (!_ftCache.TryGetValue(name, out f)) { f = typeof(FrameTiming).GetField(name); _ftCache[name] = f; }
                if (f == null) return -1;
                object v = f.GetValue(t);
                return v == null ? -1 : Convert.ToDouble(v);
            }
            catch { return -1; }
        }

        private static bool EqInt(object v, int want)
        {
            try { return v != null && Convert.ToInt32(v) == want; }
            catch { return false; }
        }

        private static string Fmt(object v)
        {
            if (v == null) return "n/a";
            if (v is float) return ((float)v).ToString("0.####");
            if (v is double) return ((double)v).ToString("0.####");
            return v.ToString();
        }

        private static string Num1(double v)
        {
            return v < 0 ? "n/a" : v.ToString("F1");
        }
    }
}
