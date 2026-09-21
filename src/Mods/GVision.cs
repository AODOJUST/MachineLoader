using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Machine.Mod;
using Machine.Core;

namespace GVisionMod
{
    /// <summary>
    /// GVision —— G 过载视觉 Mod（纯视觉，不改变任何飞行物理）。
    ///
    /// 数据来源：GMeter Mod（machine.gmeter）计算出的过载 G 值。
    ///   优先通过反射读取 GMeterSystem.Instance.CurrentG / FlightActive；
    ///   若 GMeter 未安装/被禁用，则用与 GMeter 完全相同的公式自行兜底计算。
    ///
    /// 视觉表现（模拟飞行员 G 过载 / G-LOC 隧道视觉）：
    ///   G ≤ 9            正常，无任何遮罩。
    ///   9 &lt; G ≤ 15    四周逐渐变暗，但黑边只加深到 softMaxAlpha（默认 0.68）——**保留透明度**，
    ///                   画面始终看得清；黑边与中央亮区之间是很长的渐变过渡。
    ///   15 &lt; G &lt; 25   黑边继续加深、视窗收拢，中央也开始被压黑；过渡同样很长，不会出现硬边。
    ///   G ≥ 25           完全黑屏（窗口收缩到极限 + 全屏压黑到不透明）。
    ///   G 回落           以较慢的速度渐渐消退，视觉慢慢恢复。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.gvision"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("GVision loading...");
            var go = new GameObject("Machine.GVision");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<GVisionSystem>().Init(api);
        }
    }

    /// <summary>可调参数（mods/GVision/gvision_config.json）。</summary>
    public class GVisionConfig
    {
        public float startG = 9f;            // 开始变暗的 G
        public float blackoutG = 25f;        // 完全黑屏的 G
        public float softG = 15f;            // 黑边涨到"半透明上限"的 G（在此之前只保持通透的暗）
        public float softMaxAlpha = 0.68f;   // softG 处黑边的不透明度上限（<1 = 始终保留透明度）
        public float blackoutStartG = 18.5f; // 中心开始被压黑的 G
        public float clearRadiusMax = 0.60f; // 视窗最大清晰半径（归一化，1=画面角）
        public float clearRadiusMin = 0.12f; // 视窗最小清晰半径（G→blackoutG 时）
        public float featherLow = 0.42f;     // 黑↔亮过渡带宽度（低 G）；越大渐变越缓
        public float featherHigh = 0.62f;    // 黑↔亮过渡带宽度（高 G）
        public float attackSeconds = 0.35f;  // 变暗响应时间常数（越小反应越快）
        public float releaseSeconds = 1.30f; // 恢复时间常数（越大消退越慢）
        public int sortingOrder = 28000;     // 覆盖层画布层级（低于 Mod 管理器 30000）
        public bool pauseFadeOut = true;     // 暂停(game timeScale=0)时淡出，避免挡住菜单
        public bool debugLog = true;         // 每秒输出一次诊断日志
        public bool testSweep = false;       // 自测：用合成 G 扫掠 9→21→恢复（调视觉效果用）
        public bool captureScreenshots = false; // 自测：在指定阶段自动截图
        public string captureDir = "";       // 截图目录（留空则用 Application.temporaryCachePath）
    }

    public class GVisionSystem : MonoBehaviour
    {
        private const int TEX = 128;   // 隧道遮罩贴图边长（平滑径向渐变，128 双线性拉伸到全屏无差异；
                                       // 192 时每次重建要算 37k 像素，Mono 下实测 ~3.3ms，是 G 猛增时卡顿的主力）

        private IMachineApi _api;
        private GVisionConfig _cfg = new GVisionConfig();

        // ---- 兜底 G 计算（仅当 GMeter 缺失时使用） ----
        private PlaneContainer _plane;
        private Vector3 _prevVel;
        private bool _hasPrev;
        private float _emaG = 1f;
        private readonly float[] _rawBuf = new float[8];
        private int _rawIdx;

        // ---- 读取 GMeter 结果的反射缓存 ----
        private bool _meterResolved;
        private Type _meterType;
        private object _meterInstance;
        private PropertyInfo _propG;
        private PropertyInfo _propActive;

        // ---- 视觉状态 ----
        private float _t;                 // 平滑后的"过载进度" 0..1
        private Canvas _canvas;
        private Image _vignette;
        private Image _blackout;
        private Texture2D _vigTex;
        private Sprite _vigSprite;
        private Texture2D _guiBlack;      // IMGUI 黑幕用（覆盖其它 mod 的 OnGUI 调试面板）
        private float _lastClearR = -1f;
        private float _lastFeather = -1f;
        private float _lastAspect = -1f;
        private float _rebuildTimer;
        private float _logTimer;

        // ---- 自计时探针：定位 G 猛增时的卡顿来源 ----
        private static readonly System.Diagnostics.Stopwatch _profSw = new System.Diagnostics.Stopwatch();
        private double _profUpdMs, _profGuiMs, _profRebMs;
        private int _profRebN;
        private float _profNextLog;

        private void TickProf()
        {
            if (Time.unscaledTime < _profNextLog) return;
            _profNextLog = Time.unscaledTime + 10f;
            _api.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "GVision: prof upd={0:F1}ms gui={1:F1}ms rebuild={2:F1}ms x{3} /10s",
                _profUpdMs, _profGuiMs, _profRebMs, _profRebN));
            _profUpdMs = _profGuiMs = _profRebMs = 0;
            _profRebN = 0;
        }

        // ---- 自测阶梯 ----
        // 逐级停留 2.5s 再切换，避免扫掠过程与截图时序耦合导致的黑帧；
        // 最后几级是回落，用来确认"渐渐消退"。
        private static readonly float[] StairG = { 1f, 9f, 11f, 13f, 15f, 17f, 19f, 21f, 23f, 25f, 23f, 19f, 15f, 11f, 1f };
        private const float StepSeconds = 2.5f;
        private float _testClock;
        private readonly bool[] _capDone = new bool[StairG.Length * 2];

        public void Init(IMachineApi api)
        {
            _api = api;
            LoadConfig();
            BuildOverlay();
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "GVision", "gvision_config.json");
                if (!File.Exists(path)) { _api.Log("GVision: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("GVision: bad config json"); return; }
                _cfg.startG = (float)root.GetNumber("startG", _cfg.startG);
                _cfg.blackoutG = (float)root.GetNumber("blackoutG", _cfg.blackoutG);
                _cfg.softG = (float)root.GetNumber("softG", _cfg.softG);
                _cfg.softMaxAlpha = (float)root.GetNumber("softMaxAlpha", _cfg.softMaxAlpha);
                _cfg.blackoutStartG = (float)root.GetNumber("blackoutStartG", _cfg.blackoutStartG);
                _cfg.clearRadiusMax = (float)root.GetNumber("clearRadiusMax", _cfg.clearRadiusMax);
                _cfg.clearRadiusMin = (float)root.GetNumber("clearRadiusMin", _cfg.clearRadiusMin);
                _cfg.featherLow = (float)root.GetNumber("featherLow", _cfg.featherLow);
                _cfg.featherHigh = (float)root.GetNumber("featherHigh", _cfg.featherHigh);
                _cfg.attackSeconds = (float)root.GetNumber("attackSeconds", _cfg.attackSeconds);
                _cfg.releaseSeconds = (float)root.GetNumber("releaseSeconds", _cfg.releaseSeconds);
                _cfg.sortingOrder = Mathf.RoundToInt((float)root.GetNumber("sortingOrder", _cfg.sortingOrder));
                _cfg.pauseFadeOut = root.GetBool("pauseFadeOut", _cfg.pauseFadeOut);
                _cfg.debugLog = root.GetBool("debugLog", _cfg.debugLog);
                _cfg.testSweep = root.GetBool("testSweep", _cfg.testSweep);
                _cfg.captureScreenshots = root.GetBool("captureScreenshots", _cfg.captureScreenshots);
                _cfg.captureDir = root.GetString("captureDir", _cfg.captureDir);
                if (_cfg.blackoutG <= _cfg.startG + 0.5f) _cfg.blackoutG = _cfg.startG + 0.5f;
                _api.Log("GVision: config loaded startG=" + _cfg.startG + " blackoutG=" + _cfg.blackoutG
                         + " softG=" + _cfg.softG + " softMaxAlpha=" + _cfg.softMaxAlpha
                         + " feather=" + _cfg.featherLow + "/" + _cfg.featherHigh
                         + " testSweep=" + _cfg.testSweep);
            }
            catch (Exception e) { _api.Log("GVision: config error " + e.Message); }
        }

        // ------------------------------------------------------------------
        // 读取 GMeter 的计算结果
        // ------------------------------------------------------------------
        private bool TryGetMeterG(out float g, out bool active)
        {
            g = 1f; active = false;
            try
            {
                if (!_meterResolved)
                {
                    _meterResolved = true;
                    var asms = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < asms.Length; i++)
                    {
                        try
                        {
                            Type t = asms[i].GetType("GMeterMod.GMeterSystem");
                            if (t != null) { _meterType = t; break; }
                        }
                        catch { }
                    }
                    if (_meterType != null)
                        _api.Log("GVision: found G Meter provider " + _meterType.FullName);
                    else
                        _api.Log("GVision: G Meter provider not found, using built-in fallback");
                }
                if (_meterType == null) return false;

                if (_meterInstance == null)
                {
                    var f = _meterType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (f != null) _meterInstance = f.GetValue(null);
                }
                if (_meterInstance == null) return false;

                if (_propG == null) _propG = _meterType.GetProperty("CurrentG", BindingFlags.Public | BindingFlags.Instance);
                if (_propActive == null) _propActive = _meterType.GetProperty("FlightActive", BindingFlags.Public | BindingFlags.Instance);
                if (_propG == null) return false;

                g = (float)_propG.GetValue(_meterInstance, null);
                if (_propActive != null) active = (bool)_propActive.GetValue(_meterInstance, null);
                return true;
            }
            catch
            {
                // 实例可能已销毁 → 下一帧重新解析
                _meterInstance = null;
                return false;
            }
        }

        /// <summary>兜底：与 GMeter 完全一致的 G 计算（GMeter 缺失时使用）。</summary>
        private void FixedUpdate()
        {
            if (_plane == null) return;
            if (!_plane.FlightModeInitialized) { _hasPrev = false; return; }

            Vector3 vel = _plane.GetVelocity();
            if (_hasPrev)
            {
                float dt = Time.fixedDeltaTime;
                if (dt > 0.0001f)
                {
                    Vector3 accel = (vel - _prevVel) / dt;
                    float realG = Mathf.Max(0.5f, _plane.RealGravity);
                    Vector3 aNoG = accel - new Vector3(0f, -realG, 0f);
                    float rawG = Mathf.Abs(Vector3.Dot(aNoG, _plane.transform.up) / realG);
                    if (rawG < 0.2f) rawG = 0.2f;
                    _rawBuf[_rawIdx] = rawG;
                    _rawIdx = (_rawIdx + 1) % _rawBuf.Length;
                    float sum = 0f;
                    for (int i = 0; i < _rawBuf.Length; i++) sum += _rawBuf[i];
                    _emaG = Mathf.Lerp(_emaG, sum / _rawBuf.Length, 0.28f);
                }
            }
            _prevVel = vel;
            _hasPrev = true;
        }

        // ------------------------------------------------------------------
        // 主循环
        // ------------------------------------------------------------------
        private float _findT = 0f;   // 场景查找限流：避免主菜单每帧全场景扫描

        private void Update()
        {
            if (!_profSw.IsRunning) _profSw.Start();
            long __t0 = _profSw.ElapsedTicks;
            try { UpdateImpl(); }
            finally
            {
                _profUpdMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                TickProf();
            }
        }

        private void UpdateImpl()
        {
            // 纯净模式守卫：原版档不叠加 G 视觉
            if (Machine.Mod.MachineState.PureMode) { SetOverlayVisible(false); return; }

            if (_canvas == null) BuildOverlay();

            // 自测模式：与真实飞行无关，直接按合成 G 阶梯驱动（便于确定性验证）
            if (_cfg.testSweep) { UpdateTestSweep(); return; }

            if (_plane == null)
            {
                _findT -= Time.unscaledDeltaTime;
                if (_findT > 0f) return;
                _findT = 1f;   // 每秒最多查找一次（FindFirstObjectByType 全场景扫描很贵）
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                if (!inGame) { SetOverlayVisible(false); return; }   // 主菜单/非游戏场景不扫描
                _plane = (PlaneContainer)UnityEngine.Object.FindFirstObjectByType(typeof(PlaneContainer));
                if (_plane == null) { SetOverlayVisible(false); return; }
            }

            float g; bool active;
            if (!TryGetMeterG(out g, out active))
            {
                g = _emaG;
                active = _plane != null && _plane.FlightModeInitialized && _hasPrev;
            }

            bool paused = _cfg.pauseFadeOut && Time.timeScale <= 0.001f;
            if (paused || !active) g = 1f;

            float target = Mathf.Clamp01((g - _cfg.startG) / Mathf.Max(0.5f, _cfg.blackoutG - _cfg.startG));
            Apply(target, g, active && !paused, Time.unscaledDeltaTime);

            if (_cfg.debugLog)
            {
                _logTimer -= Time.unscaledDeltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = 1f;
                    _api.Log("GVision diag: G=" + g.ToString("F2") + " active=" + active
                             + " progress=" + _t.ToString("F2") + " edge=" + CurrentEdge().ToString("F2")
                             + " black=" + CurrentBlack().ToString("F2")
                             + " canvas=" + (_canvas != null && _canvas.enabled));
                }
            }
        }

        /// <summary>
        /// 显示/隐藏整块覆盖层。
        /// 只切换 Canvas.enabled，**绝不 SetActive(false)** —— 一旦把 Canvas 所在 GameObject
        /// 停用，就没有任何地方会再把它激活，会造成"G 视觉永久失效"。
        /// </summary>
        private void SetOverlayVisible(bool visible)
        {
            if (_canvas == null) return;
            if (!_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(true);
            if (_canvas.enabled != visible) _canvas.enabled = visible;
            if (!visible) _t = 0f;   // 隐藏时清空进度，下次进入飞行从 0 开始淡入
        }

        /// <summary>自测阶梯：逐级停留，覆盖 1G→21G 上升段与 21G→1G 回落段，循环播放。</summary>
        private void UpdateTestSweep()
        {
            _testClock += Time.unscaledDeltaTime;
            float total = StepSeconds * StairG.Length;
            float cyc = _testClock % total;
            int idx = Mathf.FloorToInt(cyc / StepSeconds);
            if (idx >= StairG.Length) idx = StairG.Length - 1;
            float phase = cyc - idx * StepSeconds;
            float g = StairG[idx];

            float target = Mathf.Clamp01((g - _cfg.startG) / Mathf.Max(0.5f, _cfg.blackoutG - _cfg.startG));
            Apply(target, g, true, Time.unscaledDeltaTime);

            if (_cfg.captureScreenshots)
            {
                int a = idx * 2, b = a + 1;
                if (!_capDone[a] && phase >= 1.6f)
                {
                    _capDone[a] = true;
                    Capture("gvision_G" + g.ToString("00") + "_a");
                }
                if (!_capDone[b] && phase >= 2.2f)
                {
                    _capDone[b] = true;
                    Capture("gvision_G" + g.ToString("00") + "_b");
                }
            }

            if (_cfg.debugLog)
            {
                _logTimer -= Time.unscaledDeltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = 1f;
                    _api.Log("GVision test: idx=" + idx + " G=" + g.ToString("F1") + " progress=" + _t.ToString("F2")
                             + " edge=" + CurrentEdge().ToString("F2") + " black=" + CurrentBlack().ToString("F2"));
                }
            }
        }

        /// <summary>由平滑进度推导各视觉量并写入遮罩。</summary>
        private void Apply(float target, float g, bool active, float dt)
        {
            float tau = (target > _t) ? _cfg.attackSeconds : _cfg.releaseSeconds;
            if (tau < 0.02f) tau = 0.02f;
            float k = 1f - Mathf.Exp(-dt / tau);
            _t += (target - _t) * k;
            if (_t < 0.0005f) _t = 0f;

            if (_canvas == null || _vignette == null || _blackout == null) return;

            float edge = CurrentEdge();
            float black = CurrentBlack();

            // 视窗半径随进度收缩（隧道视觉收窄）；过渡带同步拉长，避免小窗口边缘发硬
            float radius = Mathf.Lerp(_cfg.clearRadiusMax, _cfg.clearRadiusMin, Mathf.SmoothStep(0f, 1f, _t));
            float feather = Mathf.Lerp(_cfg.featherLow, _cfg.featherHigh, _t);

            float aspect = (float)Screen.width / Mathf.Max(1f, (float)Screen.height);
            _rebuildTimer -= dt;
            // ★ 重建门槛：遮罩是双线性拉伸的平滑渐变，半径/羽化差一点根本看不出来。
            //   原阈值(0.006/0.01) + 0.05s 间隔 → G 猛增时每秒最多 20 次重建 × 3.3ms + 147KB 垃圾，
            //   是"加速度猛增严重卡顿"的主力。放宽到 0.02/0.04 + 0.15s 后视觉无差异、开销降 ~8 倍。
            if (_vigTex == null || _vigSprite == null
                || Mathf.Abs(radius - _lastClearR) > 0.02f
                || Mathf.Abs(feather - _lastFeather) > 0.04f
                || Mathf.Abs(aspect - _lastAspect) > 0.02f)
            {
                if (_rebuildTimer <= 0f || _vigTex == null)
                {
                    _rebuildTimer = 0.15f;
                    RebuildVignette(radius, feather, aspect);
                }
            }

            _vignette.color = new Color(0f, 0f, 0f, edge);
            _blackout.color = new Color(0f, 0f, 0f, black);
            bool visible = edge > 0.002f || black > 0.002f;
            // 兜底：确保 Canvas 所在 GameObject 处于激活状态（只切 enabled 控制显隐）
            if (!_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(true);
            if (_canvas.enabled != visible) _canvas.enabled = visible;
        }

        /// <summary>
        /// 黑边不透明度。
        ///   0 → softT      从 0 渐增到 softMaxAlpha（这一段**保留透明度**，画面始终看得清）；
        ///   softT → 1      再从 softMaxAlpha 继续加深到 1（也就是到 blackoutG 才完全不透明）。
        /// </summary>
        private float CurrentEdge()
        {
            float span = Mathf.Max(0.5f, _cfg.blackoutG - _cfg.startG);
            float softT = Mathf.Clamp((_cfg.softG - _cfg.startG) / span, 0.05f, 0.95f);
            float cap = Mathf.Clamp01(_cfg.softMaxAlpha);
            if (_t <= softT)
                return cap * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t / softT));
            return Mathf.Lerp(cap, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_t - softT) / (1f - softT))));
        }

        /// <summary>中央压黑量：从 blackoutStartG 开始，到 blackoutG 变成完全不透明。</summary>
        private float CurrentBlack()
        {
            float span = Mathf.Max(0.5f, _cfg.blackoutG - _cfg.startG);
            float s = Mathf.Clamp01((_cfg.blackoutStartG - _cfg.startG) / span);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_t - s) / Mathf.Max(0.01f, 1f - s)));
        }

        // ------------------------------------------------------------------
        // 遮罩渲染
        // ------------------------------------------------------------------
        /// <summary>
        /// IMGUI 黑幕：Unity 的 OnGUI 永远绘制在所有 Canvas 之上，
        /// 所以高 G 时其它 mod 的 OnGUI 面板（如记分板）会浮在全黑之上。
        /// 这里用更小的 GUI.depth 再盖一层黑，保证"G≥21 什么都看不见"。
        /// 只在中心压黑明显时绘制，成本可忽略；暂停(开菜单)时不画，避免挡菜单。
        /// </summary>
        private void OnGUI()
        {
            long __t0 = _profSw.ElapsedTicks;
            try { OnGuiImpl(); }
            finally { _profGuiMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        private void OnGuiImpl()
        {
            if (_cfg == null || _canvas == null) return;
            if (Machine.Mod.MachineState.PureMode) return;
            if (_cfg.testSweep == false && !Machine.Mod.MachineState.InFlight()) return;
            float black = CurrentBlack();
            // 只在"即将全黑"时才开始叠这层 IMGUI 黑幕：
            // 既保证 G→21 时其它 mod 的 OnGUI 面板也一起看不见，
            // 又不会叠加到中间档、把 18~20G 的隧道窗口提前压成纯黑。
            float guiA = Mathf.Clamp01((black - 0.80f) / 0.20f);
            guiA = guiA * guiA;
            if (guiA <= 0.002f) return;
            if (_cfg.pauseFadeOut && Time.timeScale <= 0.001f) return;

            if (_guiBlack == null)
            {
                _guiBlack = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _guiBlack.SetPixel(0, 0, Color.black);
                _guiBlack.Apply();
                _guiBlack.hideFlags = HideFlags.DontSave;
            }
            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, guiA);
            var full = new Rect(0f, 0f, Screen.width, Screen.height);
            // Unity 官方文档说 depth 越小越靠上，但不同版本/不同 IMGUI 控件(如 GUI.Window)
            // 的实测排序并不总是一致，所以两个极端各画一次，确保一定盖在最上层。
            GUI.depth = -30000;
            GUI.DrawTexture(full, _guiBlack);
            GUI.depth = 30000;
            GUI.DrawTexture(full, _guiBlack);
            GUI.color = oldColor;
            GUI.depth = oldDepth;

            if (_cfg.debugLog)
            {
                _logTimer -= Time.unscaledDeltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = 1f;
                    _api.Log("GVision imgui: black=" + black.ToString("F2") + " guiAlpha=" + guiA.ToString("F2"));
                }
            }
        }

        private void BuildOverlay()
        {
            try
            {
                var cgo = new GameObject("Machine.GVisionCanvas", typeof(Canvas));
                UnityEngine.Object.DontDestroyOnLoad(cgo);
                _canvas = cgo.GetComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = _cfg.sortingOrder;

                _vignette = NewFullScreenImage(cgo.transform, "Vignette");
                _vignette.sprite = EnsureVignetteSprite();

                _blackout = NewFullScreenImage(cgo.transform, "Blackout");
                _blackout.sprite = UiFactory.WhiteSprite();

                _canvas.enabled = false;
                _api.Log("GVision: overlay built (sortingOrder=" + _cfg.sortingOrder + ")");
            }
            catch (Exception e) { _api.Log("GVision: build overlay failed " + e.Message); }
        }

        private static Image NewFullScreenImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.color = new Color(0f, 0f, 0f, 0f);
            return img;
        }

        private Sprite EnsureVignetteSprite()
        {
            if (_vigTex == null)
            {
                _vigTex = new Texture2D(TEX, TEX, TextureFormat.RGBA32, false);
                _vigTex.hideFlags = HideFlags.DontSave;
                _vigTex.wrapMode = TextureWrapMode.Clamp;
                _vigTex.filterMode = FilterMode.Bilinear;
            }
            if (_vigSprite == null)
            {
                _vigSprite = Sprite.Create(_vigTex, new Rect(0f, 0f, TEX, TEX), new Vector2(0.5f, 0.5f));
                _vigSprite.hideFlags = HideFlags.DontSave;
            }
            return _vigSprite;
        }

        /// <summary>生成径向隧道遮罩：中心透明、四周黑，alpha 由 clearR 向外平滑升到 1。</summary>
        private void RebuildVignette(float clearR, float feather, float aspect)
        {
            long __t0 = _profSw.ElapsedTicks;
            try { RebuildVignetteImpl(clearR, feather, aspect); }
            finally
            {
                _profRebMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _profRebN++;
            }
        }

        private void RebuildVignetteImpl(float clearR, float feather, float aspect)
        {
            try
            {
                EnsureVignetteSprite();
                // 复用缓冲，绝不每次 new Color32[TEX*TEX]（147KB/次 × 高 G 期间每秒多次 = 持续喂 GC）
                if (_pxBuf == null) _pxBuf = new Color32[TEX * TEX];
                var px = _pxBuf;
                if (aspect < 0.01f) aspect = 1f;
                float rmax = Mathf.Sqrt(aspect * aspect + 1f); // 使 r=1 落在画面四角
                float invFeather = 1f / Mathf.Max(0.0001f, feather);
                for (int y = 0; y < TEX; y++)
                {
                    float ny = ((y + 0.5f) / TEX) * 2f - 1f;
                    int row = y * TEX;
                    for (int x = 0; x < TEX; x++)
                    {
                        float nx = ((((x + 0.5f) / TEX) * 2f - 1f)) * aspect;
                        float r = Mathf.Sqrt(nx * nx + ny * ny) / rmax;
                        float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - clearR) * invFeather));
                        px[row + x] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(a * 255f));
                    }
                }
                _vigTex.SetPixels32(px);
                _vigTex.Apply(false);
                _lastClearR = clearR;
                _lastFeather = feather;
                _lastAspect = aspect;
            }
            catch (Exception e) { _api.Log("GVision: vignette rebuild failed " + e.Message); }
        }

        private Color32[] _pxBuf;   // 遮罩像素缓冲（一次分配终身复用）

        private void Capture(string tag)
        {
            try
            {
                string dir = string.IsNullOrEmpty(_cfg.captureDir) ? Application.temporaryCachePath : _cfg.captureDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, tag + "_" + DateTime.Now.ToString("HHmmss") + ".png");
                UnityEngine.ScreenCapture.CaptureScreenshot(file);
                _api.Log("GVision: screenshot -> " + file);
            }
            catch (Exception e) { _api.Log("GVision: capture failed " + e.Message); }
        }
    }
}
