using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Machine.Mod;
using Machine.Core;

namespace VoiceAlertsMod
{
    /// <summary>语音报警系统 Mod：STALL / OVERSPEED / SINK RATE / BINGO / FUEL LOW / PITCH / ROLL。</summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.voicealerts"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("VoiceAlerts loading...");
            var go = new GameObject("Machine.VoiceAlerts");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<VoiceAlertSystem>().Init(api);
        }
    }

    /// <summary>语音播报静态 API（其他 Mod 可反射调用，如雷达 Lock / Fox 1）。</summary>
    public static class VoiceAlertsApi
    {
        private static VoiceAlertSystem _sys;
        internal static void Bind(VoiceAlertSystem sys) { _sys = sys; }
        public static bool Available { get { return _sys != null; } }
        /// <summary>播报指定语音（clip 名为音频文件名，如 "lock"/"fox1"；cooldown 秒内不重复）。</summary>
        public static void Play(string clipName, float cooldown)
        {
            if (_sys != null) _sys.Play(clipName, cooldown);
        }
        /// <summary>设置雷达威胁告警等级：0=无威胁（停止）；1=低频（被敌机雷达锁定）；2=中频（附近有导弹）；3=高频（被导弹锁定）。
        /// 只保留一个循环通道播放当前最高等级；新等级 >= 当前等级时切换（高覆盖低），否则保持。</summary>
        public static void SetRwr(int level)
        {
            if (_sys != null) _sys.SetRwr(level);
        }
        /// <summary>切换警报语言（en/zh/ru）。战斗部语言选项调用此接口。</summary>
        public static void SetLanguage(string lang)
        {
            if (_sys != null) _sys.SetLanguage(lang);
        }
        /// <summary>当前警报语言（en/zh/ru）。</summary>
        public static string GetLanguage()
        {
            return _sys != null ? _sys.CurrentLanguage : "en";
        }
    }

    public class VoiceAlertSystem : MonoBehaviour
    {
        private IMachineApi _api;
        private PlaneContainer _plane;
        private AudioSource _src;
        private readonly List<AudioSource> _srcPool = new List<AudioSource>();
        private AudioSource _rwrSrc;
        private int _rwrLevel;
        // 语音警报全局音量倍数（默认2.5倍，盖过引擎声）
        private float _voiceVolume = 2.5f;
        private Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private readonly Dictionary<string, AudioClip> _clipsZh = new Dictionary<string, AudioClip>();  // 中文语言集（缺词回退英文）
        private readonly Dictionary<string, AudioClip> _clipsRu = new Dictionary<string, AudioClip>();  // 俄语语言集（缺词回退英文）
        private Dictionary<string, float> _lastPlay = new Dictionary<string, float>();
        private string _audioDir;
        private bool _audioReady;
        private string _language = "en";   // 当前语言：en/zh/ru（zh/ru 有专属音频，缺词回退英文）
        private string _langPath;

        // 中文语言集文件名映射：警报词 -> 中文音频文件名（以语音内容命名）
        private static readonly Dictionary<string, string> ZhAudioNames = new Dictionary<string, string>
        {
            { "stall", "最小速度" }, { "overspeed", "过载超限" }, { "pullup", "拉起" },
            { "bingo", "剩余油量" }, { "fuellow", "燃油告警" }, { "missile", "敌导弹" },
            { "fox1", "发射" }, { "angle", "最大迎角" }, { "pitch", "迎角过大" },
            { "lock", "锁定" }, { "roll", "姿态仪失效" }, { "fuel45", "燃油检测" },
            { "sinkrate", "高度" }, { "fuel35", "注意油量" }, { "overg", "注意过载" },
            { "bandit", "敌机" }, { "overg14", "最大过载三次" }, { "engine_failure", "发动机熄火" },
            { "flare", "flare" },   // 投放热诱弹：中文语音文件为 audio/zh/flare.mp3（内容为"干扰"）
        };

        // 触发参数（可通过 mods/VoiceAlerts/voice_config.json 调整）
        private float stallSpeed = 60f;        // 兜底失速速度（knots，主要判定已改为减速率）
        private float stallDecelKnots = 190f;  // 2秒内减速超过该值触发 STALL（节）
        private float stallDecelWindow = 2f;   // 减速统计窗口（秒）
        private float overspeedStructuralMult = 1f; // 结构极限乘数（=1 即 Wing.maxSpeed 最小值为极限）
        private float fuelLowRatio = 0.15f;    // 燃油低阈值（比例）
        private float airportRadius = 3000f;   // 机场空域半径（m）：BINGO(内) vs FUEL LOW(外)
        private float overspeedThreshold = 793f;   // 结构极限速度（节 knots，1.2 马赫 ≈ 793 knots；持续超速会损坏结构）
        private float overspeedDuration = 5f;      // 持续超速该秒数后判定结构将损坏 → OVERSPEED
        private float gpwsAltMin = 15f;        // GPWS 高度下限（m，约50ft）
        private float gpwsAltMax = 760f;       // GPWS 高度上限（m，约2500ft）
        private float pitchUp = 40f;           // 俯仰向上告警（度）
        private float pitchDown = -30f;        // 俯仰向下告警（度）
        private float angleUp = 50f;           // ANGLE（pitch 升级版）向上阈值（度，更苛刻）
        private float angleDown = -40f;        // ANGLE（pitch 升级版）向下阈值（度，更苛刻）
        private float rollRateLimit = 270f;    // 滚转角速度极限（度/秒）
        private float overgThreshold = 7f;     // 过载极限（G，估算），超过触发 overg

        private List<Vector3> _airportPositions = new List<Vector3>();
        private float _airportScanTimer;
        private AirportManager _airportMgr;
        private float _overspeedTimer;

        private List<KeyValuePair<float, float>> _speedHist = new List<KeyValuePair<float, float>>();
        private float _prevRoll;
        private bool _hasPrevRoll;
        private Vector3 _prevVelDir;
        private float _prevVelDirTime;
        private bool _hasPrevVelDir;
        private float _structuralLimit = float.MaxValue;
        private float _structuralScanTimer;

        // 超速告警锁存：只在进入超速态的瞬间播报/打日志一次，避免每个物理帧重复刷屏
        private bool _overspeedAlerted;
        // 45% 燃油提醒锁存：进入 45% 以下播报一次，加油回升到 50% 复位
        private bool _fuel45Alerted;
        // 35% 燃油提醒锁存：进入 35% 以下播报一次，加油回升到 40% 复位
        private bool _fuel35Alerted;
        // 发动机熄火告警锁存：燃油耗尽导致发动机熄火时播报一次，重新点火后复位
        private bool _engineFailureAlerted;
        // 起落架实例缓存：FindObjectsByType 全场景扫描很贵，不能每物理帧做
        private RetractableLandingGear[] _gearCache;
        private float _gearScanT;
        // 反射 FieldInfo 缓存：避免每物理帧 GetField
        private static System.Reflection.FieldInfo _fiGearRetracted;
        private static System.Reflection.FieldInfo _fiPlaneEngines;
        private static readonly Dictionary<Type, System.Reflection.FieldInfo> _rotorFieldCache = new Dictionary<Type, System.Reflection.FieldInfo>();
        private static readonly Dictionary<Type, System.Reflection.FieldInfo> _visualFieldCache = new Dictionary<Type, System.Reflection.FieldInfo>();

        public void Init(IMachineApi api)
        {
            _api = api;
            _audioDir = Path.Combine(api.GetModsDirectory(), "VoiceAlerts", "audio");
            _langPath = Path.Combine(api.GetModsDirectory(), "VoiceAlerts", "language.json");
            LoadConfig();
            var srcGo = new GameObject("VoiceSource");
            srcGo.transform.SetParent(transform, false);
            _src = srcGo.AddComponent<AudioSource>();
            _src.spatialBlend = 0f;
            _src.volume = _voiceVolume;
            _src.bypassReverbZones = true;
            _srcPool.Add(_src);
            // 音频池：多路警报可同时播放，互不打断
            for (int i = 1; i < 6; i++)
            {
                var g = new GameObject("VoiceSource" + i);
                g.transform.SetParent(transform, false);
                var s = g.AddComponent<AudioSource>();
                s.spatialBlend = 0f;
                s.volume = _voiceVolume;
                s.bypassReverbZones = true;
                _srcPool.Add(s);
            }
            // RWR 专用循环通道：雷达威胁告警只用一个源循环播放，避免堆叠嘈杂
            var rwrGo = new GameObject("RwrSource");
            rwrGo.transform.SetParent(transform, false);
            _rwrSrc = rwrGo.AddComponent<AudioSource>();
            _rwrSrc.spatialBlend = 0f;
            _rwrSrc.volume = _voiceVolume * 0.8f;  // RWR稍低，避免过于刺耳
            _rwrSrc.bypassReverbZones = true;
            _rwrSrc.loop = true;
            _rwrLevel = 0;
            int listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(UnityEngine.FindObjectsSortMode.None).Length;
            _api.Log("VoiceAlerts: AudioListener count=" + listeners + " (no fallback created)");
            // 不创建fallback AudioListener：避免与游戏原有AudioListener冲突导致自然音效消失
            StartCoroutine(LoadClips());
            VoiceAlertsApi.Bind(this);
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "VoiceAlerts", "voice_config.json");
                if (!File.Exists(path)) { _api.Log("VoiceAlerts: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("VoiceAlerts: bad config json"); return; }
                stallSpeed = (float)root.GetNumber("stallSpeed", stallSpeed);
                stallDecelKnots = (float)root.GetNumber("stallDecelKnots", stallDecelKnots);
                stallDecelWindow = (float)root.GetNumber("stallDecelWindow", stallDecelWindow);
                overspeedStructuralMult = (float)root.GetNumber("overspeedStructuralMult", overspeedStructuralMult);
                overspeedThreshold = (float)root.GetNumber("overspeedThreshold", overspeedThreshold);
                overspeedDuration = (float)root.GetNumber("overspeedDuration", overspeedDuration);
                fuelLowRatio = (float)root.GetNumber("fuelLowRatio", fuelLowRatio);
                airportRadius = (float)root.GetNumber("airportRadius", airportRadius);
                gpwsAltMin = (float)root.GetNumber("gpwsAltMin", gpwsAltMin);
                gpwsAltMax = (float)root.GetNumber("gpwsAltMax", gpwsAltMax);
                pitchUp = (float)root.GetNumber("pitchUp", pitchUp);
                pitchDown = (float)root.GetNumber("pitchDown", pitchDown);
                angleUp = (float)root.GetNumber("angleUp", angleUp);
                angleDown = (float)root.GetNumber("angleDown", angleDown);
                rollRateLimit = (float)root.GetNumber("rollRateLimit", rollRateLimit);
                overgThreshold = (float)root.GetNumber("overgThreshold", overgThreshold);
                _api.Log("VoiceAlerts: config loaded");
            }
            catch (Exception e) { _api.Log("VoiceAlerts: config error " + e.Message); }
        }

        private IEnumerator LoadClips()
        {
            yield return null;
            string[] names = { "stall", "overspeed", "sinkrate", "pullup", "bingo", "fuellow", "pitch", "angle", "overg", "overg14", "roll", "lock", "fox1", "missile", "bandit", "rwr_low", "rwr_mid", "rwr_high", "fuel45", "fuel35", "engine_failure", "flare" };
            foreach (var n in names)
            {
                string path = Path.Combine(_audioDir, n + ".wav");
                if (!File.Exists(path)) path = Path.Combine(_audioDir, n + ".mp3");
                if (!File.Exists(path)) { _api.Log("VoiceAlerts: missing audio " + n); continue; }
                AudioClip clip = WavUtil.Decode(path);
                if (clip != null)
                {
                    _clips[n] = clip;
                    _api.Log("VoiceAlerts: audio loaded " + n + " len=" + clip.length.ToString("F1") + "s");
                }
                else _api.Log("VoiceAlerts: decode failed " + n);
            }
            // 中文语言集（audio/zh/，文件名以语音内容中文命名）：有专属音频的词用中文，缺失的警报词回退英文
            string zhDir = Path.Combine(_audioDir, "zh");
            foreach (var n in names)
            {
                string fname;
                if (!ZhAudioNames.TryGetValue(n, out fname)) continue;
                string p = Path.Combine(zhDir, fname + ".wav");
                if (!File.Exists(p)) p = Path.Combine(zhDir, fname + ".mp3");
                if (!File.Exists(p)) continue;
                AudioClip clip = WavUtil.Decode(p);
                if (clip != null) { _clipsZh[n] = clip; _api.Log("VoiceAlerts: zh audio loaded " + n + " <- " + fname); }
                else _api.Log("VoiceAlerts: zh decode failed " + fname);
            }
            // 俄语语言集（audio/ru/，文件名直接用英文警报名）：有专属音频的词用俄语，缺失的警报词回退英文
            string ruDir = Path.Combine(_audioDir, "ru");
            foreach (var n in names)
            {
                string p = Path.Combine(ruDir, n + ".wav");
                if (!File.Exists(p)) p = Path.Combine(ruDir, n + ".mp3");
                if (!File.Exists(p)) continue;
                AudioClip clip = WavUtil.Decode(p);
                if (clip != null) { _clipsRu[n] = clip; _api.Log("VoiceAlerts: ru audio loaded " + n); }
                else _api.Log("VoiceAlerts: ru decode failed " + n);
            }
            LoadLanguage();
            _audioReady = _clips.Count > 0;
        }

        public string CurrentLanguage { get { return _language; } }

        /// <summary>按当前语言解析警报音频；zh/ru 缺词回退英文。
        /// src 回传实际命中的语音包（"zh"/"ru"/"en"），供日志区分"俄语包真的生效"还是"静默回退英文"。</summary>
        private AudioClip ResolveClip(string name, out string src)
        {
            AudioClip c = null;
            if (_language == "zh" && _clipsZh.TryGetValue(name, out c) && c != null) { src = "zh"; return c; }
            if (_language == "ru" && _clipsRu.TryGetValue(name, out c) && c != null) { src = "ru"; return c; }
            // 缺词回退英文集
            src = "en";
            _clips.TryGetValue(name, out c);
            return c;
        }

        private void LoadLanguage()
        {
            try
            {
                if (File.Exists(_langPath))
                {
                    var root = JsonValue.Parse(File.ReadAllText(_langPath));
                    if (root != null)
                    {
                        string v = root.GetString("language", "");
                        if (v == "zh" || v == "ru" || v == "en") _language = v;
                    }
                }
                _api.Log("VoiceAlerts: language=" + _language);
            }
            catch { }
        }

        /// <summary>切换警报语言（en/zh/ru），并持久化到 language.json。</summary>
        public void SetLanguage(string lang)
        {
            if (lang != "en" && lang != "zh" && lang != "ru") return;
            if (_language == lang) return;
            _language = lang;
            try { File.WriteAllText(_langPath, "{\"language\":\"" + _language + "\"}"); }
            catch { }
            _api.Log("VoiceAlerts: language -> " + _language);
        }

        private float _findT = 0f;   // 场景查找限流：避免主菜单每物理帧全场景扫描

        private void FixedUpdate()
        {
            // 纯净模式守卫：原版档无语音报警
            if (Machine.Mod.MachineState.PureMode) return;
            if (!_audioReady) return;
            if (_plane == null)
            {
                _findT -= Time.fixedDeltaTime;
                if (_findT > 0f) return;
                _findT = 1f;   // 每秒最多查找一次（FindFirstObjectByType 全场景扫描很贵）
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                if (!inGame) return;   // 主菜单/非游戏场景不扫描
                _plane = (PlaneContainer)UnityEngine.Object.FindFirstObjectByType(typeof(PlaneContainer));
                if (_plane == null) return;
                _airportMgr = (AirportManager)UnityEngine.Object.FindFirstObjectByType(typeof(AirportManager));
            }
            else if (_airportMgr == null)
            {
                _findT -= Time.fixedDeltaTime;
                if (_findT > 0f) return;
                _findT = 1f;
                _airportMgr = (AirportManager)UnityEngine.Object.FindFirstObjectByType(typeof(AirportManager));
            }
            // 仅在真正处于飞行模式时报警（避免编辑器停机坪误报）
            if (!_plane.FlightModeInitialized) return;

            // AI 驾驶的实体（空空导弹 / AI 靶机 / 未来的 AI 飞机）绝不触发玩家语音告警
            if (IsAiEntity(_plane.gameObject)) return;

            float speed = _plane.GetVelocityMagintude();
            Vector3 vel = _plane.GetVelocity();
            Transform t = _plane.transform;
            float pitch = Wrap180(t.eulerAngles.x);
            float roll = Wrap180(t.eulerAngles.z);
            float fuelRatio = (_plane.fuelCapacity > 0f) ? (_plane.fuel / _plane.fuelCapacity) : 1f;
            float ra = t.position.y;
            float sink = vel.y;

            // 记录速度历史（减速率判定）
            _speedHist.Add(new KeyValuePair<float, float>(Time.time, speed));
            while (_speedHist.Count > 0 && _speedHist[0].Key < Time.time - stallDecelWindow) _speedHist.RemoveAt(0);

            ScanAirports();
            ScanStructuralLimit();

            // STALL：起落架已收起（或不可收起）+ 已离开机场 + 2秒内减速超过190节 + 喷气发动机
            bool gearOk = GearRetracted();
            bool awayFromAirport = !NearAnyAirport(t.position);
            bool decelHit = DecelerationExceedsKnots(stallDecelKnots, speed);
            if (gearOk && awayFromAirport && decelHit && HasJetEngine())
            {
                Play("stall", 3f);
            }
            else if (speed < stallSpeed && gearOk && awayFromAirport && HasJetEngine())
            {
                // 兜底：速度低于失速速度仍触发（仅喷气 + 离机场 + 起落架收起）
                Play("stall", 3f);
            }

            // OVERSPEED：持续超速（超过结构极限）累积计时 → 结构将损坏
            // 注意：Wing.maxSpeed 是气动极限(3300)不可当结构阈值，结构极限由配置 overspeedThreshold 控制（1.2 马赫）
            float overspeedLimit = overspeedThreshold;
            if (speed > overspeedLimit)
            {
                _overspeedTimer += Time.fixedDeltaTime;
            }
            else
            {
                _overspeedTimer = Mathf.Max(0f, _overspeedTimer - Time.fixedDeltaTime * 2f);
            }
            if (_overspeedTimer >= overspeedDuration)
            {
                if (!_overspeedAlerted)
                {
                    _overspeedAlerted = true;
                    Play("overspeed", 6f);
                    _api.Log("VoiceAlerts: OVERSPEED triggered speed=" + Mathf.RoundToInt(speed) + " limit=" + Mathf.RoundToInt(overspeedLimit));
                }
            }
            else if (_overspeedTimer < 1f)
            {
                _overspeedAlerted = false;   // 回落到安全区间后重新武装
            }

            if (ra >= gpwsAltMin && ra <= gpwsAltMax)
            {
                float limit = 3f + ra * 0.02f; // 简化 GPWS 包线：越高允许的下沉率越大
                if (sink < -limit) Play("sinkrate", 8f);
            }

            // PULL UP：SINK RATE 的升级版，触发更苛刻（高度上限更低 + 需要 2.2 倍下沉率）
            if (ra >= gpwsAltMin && ra <= 400f)   // 战斗机：再严苛两档（原 460m）
            {
                float baseLimit = 3f + ra * 0.02f;
                if (sink < -(baseLimit * 2.2f)) Play("pullup", 8f);
            }

            // FUEL 45%：机载燃油降到 45% 时播报一次（比 BINGO/FUEL LOW 更早提醒；加油回升到 50% 复位）
            if (fuelRatio < 0.45f && fuelRatio > 0.01f)
            {
                if (!_fuel45Alerted) { _fuel45Alerted = true; Play("fuel45", 30f); }
            }
            else if (fuelRatio >= 0.50f) _fuel45Alerted = false;

            // FUEL 35%：油量低于 35% 时播报一次（45% 之后、15% 之前的第二级提醒；回升到 40% 复位）
            if (fuelRatio < 0.35f && fuelRatio > 0.01f)
            {
                if (!_fuel35Alerted) { _fuel35Alerted = true; Play("fuel35", 30f); }
            }
            else if (fuelRatio >= 0.40f) _fuel35Alerted = false;

            // 燃油：机场空域内 → BINGO；机场空域外 → FUEL LOW
            if (fuelRatio < fuelLowRatio)
            {
                if (NearAnyAirport(t.position)) Play("bingo", 12f);
                else Play("fuellow", 12f);
            }

            // ENGINE FAILURE：燃油耗尽导致发动机熄火（燃油 < 1% 时触发，只播报一次，加油后复位）
            if (fuelRatio <= 0.01f && _plane.fuelCapacity > 0f)
            {
                if (!_engineFailureAlerted)
                {
                    _engineFailureAlerted = true;
                    Play("engine_failure", 15f);
                    _api.Log("VoiceAlerts: ENGINE FAILURE - fuel depleted");
                }
            }
            else if (fuelRatio >= 0.05f)
            {
                _engineFailureAlerted = false;  // 加油回升到 5% 以上后重新武装
            }

            // PITCH：俯仰超限
            if (pitch > pitchUp || pitch < pitchDown) Play("pitch", 6f);

            // ANGLE：PITCH 的升级版，触发更苛刻（阈值更大）
            if (pitch > angleUp || pitch < angleDown) Play("angle", 6f);

            // ROLL：每秒翻转角度超过极限（角速度 270°/s）
            if (_hasPrevRoll)
            {
                float dr = Mathf.Abs(Mathf.DeltaAngle(_prevRoll, roll));
                float rollRate = dr / Time.fixedDeltaTime;
                if (rollRate > rollRateLimit) Play("roll", 6f);
            }
            _prevRoll = roll;
            _hasPrevRoll = true;

            // OVERG：用速度矢量方向变化率估算过载（转向/拉起都会让速度方向转动）
            // n ≈ sqrt(1 + (v*ω/g)^2)，ω 为速度方向角速度（rad/s），g 取重力 9.81
            // 注意：speed 单位是节（knots），需转换为 m/s（1节 = 0.5144 m/s）
            if (speed > 1f && _hasPrevVelDir)
            {
                float dt = Time.time - _prevVelDirTime;
                if (dt > 0.02f)
                {
                    Vector3 dir = vel.normalized;
                    float omega = Vector3.Angle(_prevVelDir, dir) * Mathf.Deg2Rad / dt;
                    float speedMs = speed * 0.5144f;  // 节转换为m/s
                    float aCent = speedMs * omega;
                    float g = 9.81f;
                    float gLoad = Mathf.Sqrt(1f + (aCent * aCent) / (g * g));
                    if (gLoad > overgThreshold) Play("overg", 6f);
                    // OVERG14：G 大于 14 时播报更严重的过载告警（"最大过载"）
                    if (gLoad > 14f) Play("overg14", 6f);
                }
            }
            _prevVelDir = vel.normalized;
            _prevVelDirTime = Time.time;
            _hasPrevVelDir = true;
        }

        /// <summary>
        /// 该物体是不是"AI 驾驶"的实体。判定顺序：
        ///   1) 自身或父级挂着 AiEntityMarker（约定类型名，任何 Mod 都能用，不需要编译期依赖）；
        ///   2) 查 MachineAAM 的静默表 SilentAi.IsSilent(GameObject)（反射，缺失即跳过）。
        /// </summary>
        private static bool IsAiEntity(GameObject go)
        {
            if (go == null) return false;
            try
            {
                if (go.GetComponent("AiEntityMarker") != null) return true;
                Transform p = go.transform.parent;
                int guard = 0;
                while (p != null && guard++ < 16)
                {
                    if (p.GetComponent("AiEntityMarker") != null) return true;
                    p = p.parent;
                }
            }
            catch { }

            try
            {
                if (_silentAiMethod == null && !_silentAiScanned)
                {
                    _silentAiScanned = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = asm.GetType("Machine.AAM.SilentAi");
                        if (t == null) continue;
                        _silentAiMethod = t.GetMethod("IsSilent", new Type[] { typeof(GameObject) });
                        if (_silentAiMethod != null) break;
                    }
                }
                if (_silentAiMethod != null && (bool)_silentAiMethod.Invoke(null, new object[] { go })) return true;
            }
            catch { }
            return false;
        }

        private static bool _silentAiScanned;
        private static System.Reflection.MethodInfo _silentAiMethod;

        /// <summary>起落架判定：hasRetractableGear 为 false（无法收起）→ 不限制；否则需已收起。</summary>
        private bool GearRetracted()        {
            try
            {
                if (!_plane.hasRetractableGear) return true;
                if (_fiGearRetracted == null)
                    _fiGearRetracted = typeof(RetractableLandingGear).GetField("retracted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                // 缓存起落架实例：全场景扫描很贵，每 3s 重扫一次（实例失效/场景重载时也会自然触发）
                _gearScanT -= Time.fixedDeltaTime;
                if (_gearCache == null || _gearScanT <= 0f)
                {
                    _gearScanT = 3f;
                    _gearCache = UnityEngine.Object.FindObjectsByType<RetractableLandingGear>(UnityEngine.FindObjectsSortMode.None);
                }
                if (_gearCache == null || _gearCache.Length == 0) return false; // 有可收起起落架但没找到实例 → 视为未收起
                for (int i = 0; i < _gearCache.Length; i++)
                {
                    var g = _gearCache[i];
                    if (g == null) continue;
                    bool retracted = _fiGearRetracted != null && (bool)_fiGearRetracted.GetValue(g);
                    if (g.gameObject.activeInHierarchy && !retracted) return false;
                }
                return true;
            }
            catch { return true; }
        }

        /// <summary>发动机判定：任一引擎无螺旋桨视觉（PropellerVisual）→ 喷气；纯螺旋桨/旋翼 → 不触发。</summary>
        private bool HasJetEngine()
        {
            try
            {
                if (_fiPlaneEngines == null)
                    _fiPlaneEngines = typeof(PlaneContainer).GetField("engines", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (_fiPlaneEngines == null) return true;
                var engines = _fiPlaneEngines.GetValue(_plane) as Array;
                if (engines == null || engines.Length == 0) return true;
                bool hasProp = false;
                foreach (var e in engines)
                {
                    if (e == null) continue;
                    Type et = e.GetType();
                    var rotorField = GetCachedField(et, "helicopterRotor", _rotorFieldCache);
                    bool rotor = rotorField != null && (bool)rotorField.GetValue(e);
                    if (rotor) { hasProp = true; continue; }
                    var visualField = GetCachedField(et, "visual", _visualFieldCache);
                    var visual = visualField != null ? visualField.GetValue(e) : null;
                    if (visual == null) return true; // 无螺旋桨视觉 → 喷气
                    hasProp = true;
                }
                return !hasProp;
            }
            catch { return true; }
        }

        private static System.Reflection.FieldInfo GetCachedField(Type t, string name, Dictionary<Type, System.Reflection.FieldInfo> cache)
        {
            System.Reflection.FieldInfo f;
            if (cache.TryGetValue(t, out f)) return f;
            f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            cache[t] = f;
            return f;
        }

        /// <summary>2 秒窗口内减速超过指定节数（速度单位即为 knots，直接比较）。</summary>
        private bool DecelerationExceedsKnots(float knots, float curSpeed)
        {
            if (_speedHist.Count < 2) return false;
            float earliest = _speedHist[0].Value;
            return (earliest - curSpeed) > knots;
        }

        private void ScanAirports()
        {
            _airportScanTimer -= Time.fixedDeltaTime;
            if (_airportScanTimer > 0f) return;
            _airportScanTimer = 3f;
            _airportPositions.Clear();
            try
            {
                if (_airportMgr != null && _airportMgr.airports != null)
                {
                    foreach (var a in _airportMgr.airports)
                    {
                        if (a != null) _airportPositions.Add(a.position);
                    }
                }
                if (_airportPositions.Count == 0)
                {
                    // 反射读 Map.airportIconInstances（internal）
                    var map = (Map)UnityEngine.Object.FindFirstObjectByType(typeof(Map));
                    if (map != null)
                    {
                        var fi = typeof(Map).GetField("airportIconInstances", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (fi != null)
                        {
                            var list = fi.GetValue(map) as System.Collections.IEnumerable;
                            if (list != null)
                            {
                                foreach (var item in list)
                                {
                                    if (item == null) continue;
                                    var ap = item.GetType().GetField("airport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    var airport = ap != null ? ap.GetValue(item) : null;
                                    if (airport == null) continue;
                                    var posProp = airport.GetType().GetProperty("position");
                                    if (posProp != null) _airportPositions.Add((Vector3)posProp.GetValue(airport, null));
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private bool NearAnyAirport(Vector3 pos)
        {
            if (_airportPositions.Count == 0) return false;
            for (int i = 0; i < _airportPositions.Count; i++)
            {
                if (Vector3.Distance(pos, _airportPositions[i]) < airportRadius) return true;
            }
            return false;
        }

        private void ScanStructuralLimit()
        {
            _structuralScanTimer -= Time.fixedDeltaTime;
            if (_structuralScanTimer > 0f) return;
            _structuralScanTimer = 2f;
            float limit = float.MaxValue;
            try
            {
                if (_plane.planeParts != null)
                {
                    foreach (var p in _plane.planeParts)
                    {
                        if (p == null) continue;
                        Wing w = p as Wing;
                        if (w != null && w.maxSpeed > 0f && w.maxSpeed < limit) limit = w.maxSpeed;
                    }
                }
            }
            catch { }
            if (limit == float.MaxValue) limit = 100000f; // 无 Wing 数据时不限制（结构阈值由配置 overspeedThreshold 控制）
            if (Mathf.Abs(limit - _structuralLimit) > 1f)
            {
                _structuralLimit = limit;
                _api.Log("VoiceAlerts: structural limit (min Wing.maxSpeed)=" + Mathf.RoundToInt(limit) + " m/s");
            }
        }

        public void Play(string name, float cooldown)
        {
            string src;
            AudioClip c = ResolveClip(name, out src);
            if (c == null) return;
            float last;
            if (_lastPlay.TryGetValue(name, out last) && (Time.time - last) < cooldown) return;
            _lastPlay[name] = Time.time;
            if (c == null || _srcPool.Count == 0) return;
            // 找空闲音源播放；全忙则不打断正在播放的警报（该次播报顺延丢弃）
            AudioSource target = null;
            for (int i = 0; i < _srcPool.Count; i++)
            {
                if (!_srcPool[i].isPlaying) { target = _srcPool[i]; break; }
            }
            if (target == null) return;
            target.volume = _voiceVolume;
            target.clip = c;
            target.Play();
            _api.Log("VoiceAlerts: played " + name + " [" + src + "] len=" + c.length.ToString("F1") + "s vol=" + _voiceVolume.ToString("F1"));
        }

        /// <summary>雷达威胁告警专用通道：单循环播放，高等级覆盖低等级，等级 0 停止。
        /// 与普通语音警报（stall/overspeed 等）互不干扰，可同时播放。</summary>
        public void SetRwr(int level)
        {
            if (_rwrSrc == null) return;
            if (level <= 0)
            {
                if (_rwrSrc.isPlaying) _rwrSrc.Stop();
                _rwrLevel = 0;
                return;
            }
            if (level > 3) level = 3;
            // 高覆盖低：新等级 >= 当前才切换（不降级），保持最高威胁告警
            if (_rwrLevel >= level && _rwrSrc.isPlaying) return;
            string clipName = level == 1 ? "rwr_low" : (level == 2 ? "rwr_mid" : "rwr_high");
            string src;
            AudioClip c = ResolveClip(clipName, out src);
            if (c == null) return;
            _rwrSrc.clip = c;
            _rwrSrc.loop = true;
            _rwrSrc.Play();
            _rwrLevel = level;
            _api.Log("VoiceAlerts: RWR level=" + level + " (" + clipName + ") [" + src + "]");
        }

        private static float Wrap180(float v)
        {
            v = v % 360f;
            if (v > 180f) v -= 360f;
            if (v < -180f) v += 360f;
            return v;
        }
    }

    /// <summary>手动解析 WAV（PCM 8/16/24/32bit），不依赖 UnityWebRequest，规避解码失败。</summary>
    public static class WavUtil
    {
        public static AudioClip Decode(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 4) return null;

                // 检测 MP3 格式（ID3 标签或 MP3 帧同步）
                bool isMp3 = false;
                if (bytes.Length >= 3 && System.Text.Encoding.ASCII.GetString(bytes, 0, 3) == "ID3")
                    isMp3 = true;
                else if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
                    isMp3 = true;

                if (isMp3)
                {
                    // 用 UnityWebRequest 加载 MP3（同步等待）
                    string url = "file:///" + path.Replace("\\", "/");
                    using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(url, UnityEngine.AudioType.MPEG))
                    {
                        var op = request.SendWebRequest();
                        // 同步等待完成（最多等5秒）
                        float waitT = 0f;
                        while (!op.isDone && waitT < 5f)
                        {
                            waitT += 0.01f;
                            System.Threading.Thread.Sleep(10);
                        }
                        if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                        {
                            AudioClip mp3Clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                            if (mp3Clip != null)
                            {
                                UnityEngine.Debug.Log("WavUtil: MP3 loaded " + path + " len=" + mp3Clip.length);
                                return mp3Clip;
                            }
                        }
                        UnityEngine.Debug.Log("WavUtil: MP3 load failed " + path + " result=" + request.result);
                    }
                    return null;
                }

                if (bytes.Length < 44) return null;
                if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF") return null;
                if (System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") return null;

                int pos = 12;
                int channels = 2, sampleRate = 44100, bits = 16;
                int dataOffset = -1, dataLen = 0;
                while (pos + 8 <= bytes.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    if (id == "fmt ")
                    {
                        if (pos + 24 <= bytes.Length)
                        {
                            short fmt = BitConverter.ToInt16(bytes, pos + 8);
                            if (fmt != 1) return null; // 仅支持 PCM
                            channels = BitConverter.ToInt16(bytes, pos + 10);
                            sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                            bits = BitConverter.ToInt16(bytes, pos + 22);
                        }
                    }
                    else if (id == "data")
                    {
                        dataOffset = pos + 8;
                        dataLen = size;
                        break;
                    }
                    pos += 8 + size + (size % 2);
                    if (size <= 0) break;
                }
                if (dataOffset < 0) return null;
                // 非标准 wav：data size 可能为 -1（0xFFFFFFFF）或超界，一律按剩余长度
                if (dataLen <= 0 || dataLen > bytes.Length - dataOffset) dataLen = bytes.Length - dataOffset;
                int bytesPerSample = bits / 8;
                int sampleCount = dataLen / bytesPerSample / channels;
                if (sampleCount <= 0 || channels <= 0 || channels > 8) return null;

                float[] samples = new float[sampleCount * channels];
                for (int i = 0; i < samples.Length; i++)
                {
                    int o = dataOffset + i * bytesPerSample;
                    if (o + bytesPerSample > bytes.Length) break;
                    if (bits == 16)
                    {
                        samples[i] = BitConverter.ToInt16(bytes, o) / 32768f;
                    }
                    else if (bits == 8)
                    {
                        samples[i] = (bytes[o] - 128) / 128f;
                    }
                    else if (bits == 24)
                    {
                        int v = (bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16));
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        samples[i] = v / 8388608f;
                    }
                    else if (bits == 32)
                    {
                        samples[i] = BitConverter.ToInt32(bytes, o) / 2147483648f;
                    }
                    else return null;
                }
                AudioClip clip = AudioClip.Create(Path.GetFileName(path), sampleCount, channels, sampleRate, false);
                clip.SetData(samples, 0);
                return clip;
            }
            catch (Exception e) { UnityEngine.Debug.Log("WavUtil error " + path + " " + e.Message); return null; }
        }
    }
}
