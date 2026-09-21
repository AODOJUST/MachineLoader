using System;
using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using Machine.Mod;
using Machine.Core;

namespace GMeterMod
{
    /// <summary>G 计算 Mod：G>5 屏幕右上角显示 G 值（如 5G/8G），G>9 触发 OVER G 语音警告。</summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.gmeter"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("GMeter loading...");
            var go = new GameObject("Machine.GMeter");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<GMeterSystem>().Init(api);
        }
    }

    public class GMeterSystem : MonoBehaviour
    {
        private IMachineApi _api;
        private PlaneContainer _plane;
        private float _findT;   // 场景查找限流：FindFirstObjectByType 全场景扫描很贵（菜单里每帧裸扫实测数 ms）
        private Vector3 _prevVel;
        private bool _hasPrev;
        private TextMeshProUGUI _gText;
        private AudioSource _src;
        private AudioClip _overg;
        private float _overgCooldown = -10f;
        private float _gravity = 9.81f;
        private float _emaG = 1f;      // 平滑后的 G 值
        private float[] _rawBuf = new float[8];
        private int _rawIdx;

        // 可调参数
        private float showThreshold = 5f;
        private float overgThreshold = 9f;

        // ------------------------------------------------------------------
        // 对外公开的计算结果（供其它 Mod 读取，例如视觉过载 Mod GVision）。
        // 这是本 Mod 的“数据接口”：其它 Mod 通过 GMeterSystem.Instance.CurrentG 取值。
        // 这些成员只读、不改变本 Mod 原有行为。
        // ------------------------------------------------------------------
        /// <summary>当前存活的 GMeter 实例（由 Init 注册）。</summary>
        public static GMeterSystem Instance;
        /// <summary>平滑后的当前过载值（单位 G）。</summary>
        public float CurrentG { get { return _emaG; } }
        /// <summary>是否处于飞行中且 G 数据有效。</summary>
        public bool FlightActive { get { return _plane != null && _plane.FlightModeInitialized && _hasPrev; } }
        /// <summary>OVER G 告警阈值（默认 9G，来自 g_config.json）。</summary>
        public float OverGThreshold { get { return overgThreshold; } }

        // ---- BattleCore（战斗部）联动：反射调用，前置缺失时自动降级 ----
        private static bool _bcChecked;
        private static bool _bcAvailable;
        private static System.Reflection.MethodInfo _bcPost;

        private static void CheckBattleCore()
        {
            if (_bcChecked) return;
            _bcChecked = true;
            try
            {
                // 方案 1：Type.GetType 直接按程序集名解析
                var t = Type.GetType("Machine.BattleCore.Core, BattleCore");
                if (t == null)
                {
                    // 方案 2：遍历已加载程序集
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "BattleCore")
                        {
                            t = asm.GetType("Machine.BattleCore.Core");
                            break;
                        }
                    }
                }
                if (t != null)
                {
                    _bcPost = t.GetMethod("Post", new Type[] { typeof(string), typeof(string), typeof(string), typeof(int) });
                    _bcAvailable = _bcPost != null;
                    if (_bcPost == null)
                    {
                        foreach (var m in t.GetMethods())
                            if (m.Name == "Post") { _bcPost = m; _bcAvailable = true; break; }
                    }
                }
            }
            catch { }
        }

        private int _lastReportedG = -1;
        private void PostToBattleCore(float g)
        {
            if (!_bcChecked) CheckBattleCore();
            if (!_bcAvailable || _bcPost == null) return;
            int gi = Mathf.RoundToInt(g);
            if (gi == _lastReportedG) return;   // 值未变化不重复上报，避免刷屏
            _lastReportedG = gi;
            int sev = g > overgThreshold ? 10 : (g > showThreshold ? 6 : 2);
            try
            {
                _bcPost.Invoke(null, new object[] { "GMeter", "G", gi + "G", sev });
            }
            catch { }
        }

        public void Init(IMachineApi api)
        {
            _api = api;
            Instance = this;
            LoadConfig();
            BuildUI();
            CheckBattleCore();
            _api.Log("GMeter: BattleCore available=" + _bcAvailable);
            StartCoroutine(LoadOverG());
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "GMeter", "g_config.json");
                if (!File.Exists(path)) { _api.Log("GMeter: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("GMeter: bad config json"); return; }
                showThreshold = (float)root.GetNumber("showThreshold", showThreshold);
                overgThreshold = (float)root.GetNumber("overgThreshold", overgThreshold);
                _api.Log("GMeter: config loaded");
            }
            catch (Exception e) { _api.Log("GMeter: config error " + e.Message); }
        }

        private void BuildUI()
        {
            try
            {
                var canvasGo = new GameObject("Machine.GMeterCanvas", typeof(Canvas));
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 20000;

                var textGo = new GameObject("GText", typeof(RectTransform));
                textGo.transform.SetParent(canvasGo.transform, false);
                var rt = (RectTransform)textGo.transform;
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-110f, -80f);
                rt.sizeDelta = new Vector2(200f, 64f);
                var tmp = textGo.AddComponent<TextMeshProUGUI>();
                tmp.fontSize = 44;
                tmp.fontStyle = FontStyles.Bold;
                tmp.alignment = TextAlignmentOptions.Right;
                tmp.color = new Color(1f, 0.85f, 0.25f, 1f);
                TMP_FontAsset font = UiFactory.HarvestFont();
                if (font != null) tmp.font = font;
                tmp.text = "";
                _gText = tmp;

                var srcGo = new GameObject("OverGSource");
                srcGo.transform.SetParent(transform, false);
                _src = srcGo.AddComponent<AudioSource>();
                _src.spatialBlend = 0f;
                _src.volume = 1f;
                _src.bypassReverbZones = true;
                // 不创建fallback AudioListener：避免与游戏原有AudioListener冲突导致自然音效消失
                // AudioSource.spatialBlend=0 时是2D音效，不需要额外的AudioListener
                int listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(UnityEngine.FindObjectsSortMode.None).Length;
                _api.Log("GMeter: AudioListener count=" + listeners + " (no fallback created)");

                _api.Log("GMeter: UI built");
            }
            catch (Exception e) { _api.Log("GMeter: build UI failed " + e.Message); }
        }

        private IEnumerator LoadOverG()
        {
            yield return null;
            string path = Path.Combine(_api.GetModsDirectory(), "GMeter", "audio", "overg.wav");
            if (!File.Exists(path)) { _api.Log("GMeter: missing audio " + path); yield break; }
            _overg = WavUtil.Decode(path);
            if (_overg != null) _api.Log("GMeter: OVER G audio loaded len=" + _overg.length.ToString("F1") + "s");
            else _api.Log("GMeter: decode failed overg");
        }

        private void FixedUpdate()
        {
            // 纯净模式守卫：原版档不显示 G 值
            if (Machine.Mod.MachineState.PureMode) { if (_gText != null) _gText.text = ""; return; }
            if (_gText == null) return;
            if (_plane == null)
            {
                // 节流：找不到飞机（主菜单/编辑器）时每秒最多扫一次全场景
                _findT -= Time.fixedDeltaTime;
                if (_findT > 0f) { if (_gText != null) _gText.text = ""; return; }
                _findT = 1f;
                _plane = (PlaneContainer)UnityEngine.Object.FindFirstObjectByType(typeof(PlaneContainer));
                if (_plane == null) { if (_gText != null) _gText.text = ""; return; }
            }
            if (_plane == null) { _gText.text = ""; return; }
            // 仅在飞行模式计算 G（避免编辑器内 Rigidbody 抖动导致误报）
            if (!_plane.FlightModeInitialized) { _gText.text = ""; return; }

            Vector3 vel = _plane.GetVelocity();
            if (_hasPrev)
            {
                float dt = Time.fixedDeltaTime;
                if (dt > 0.0001f)
                {
                    // 速度矢量差分得到真实加速度（含向心加速度）
                    Vector3 accel = (vel - _prevVel) / dt;
                    float realG = Mathf.Max(0.5f, _plane.RealGravity);
                    Vector3 gravityVec = new Vector3(0f, -realG, 0f);
                    Vector3 aNoG = accel - gravityVec;   // 非重力加速度（升力/推力产生）
                    // 总过载 = 非重力加速度矢量模长 / 重力加速度
                    // 包含法向（升力，沿up）和切向（推力，沿forward）两个分量
                    // 加速平飞时：法向≈1G + 切向（推力/质量）→ 总G > 1G
                    float rawG = aNoG.magnitude / realG;
                    if (rawG < 0.2f) rawG = 0.2f;
                    _rawBuf[_rawIdx] = rawG;
                    _rawIdx = (_rawIdx + 1) % _rawBuf.Length;
                    float sum = 0f;
                    for (int i = 0; i < _rawBuf.Length; i++) sum += _rawBuf[i];
                    float avg = sum / _rawBuf.Length;    // 8 帧窗口平均抑制瞬态噪音
                    _emaG = Mathf.Lerp(_emaG, avg, 0.28f);
                    UpdateDisplay(_emaG);
                }
            }
            _prevVel = vel;
            _hasPrev = true;
        }

        private void UpdateDisplay(float g)
        {
            if (g > 25f) g = 25f;   // 显示限幅（修正确算法后正常机动应 <12G，限幅仅防极端）
            int gi = Mathf.RoundToInt(g);
            // ★ 变化触发：高 G 期间 G 值每 FixedUpdate 都在变，但文本只在整数 G 或颜色档位
            //   变化时才需要刷新（每次 text 赋值 = 字符串拼接 + UGUI 重建，50Hz 白做）
            int state = g > overgThreshold ? 2 : (g > showThreshold ? 1 : 0);
            if (state == _lastState && gi == _lastGi) return;
            _lastState = state;
            _lastGi = gi;
            PostToBattleCore(g);
            if (state == 2)
            {
                _gText.text = gi + "G";
                _gText.color = OVERG_COLOR;
                if (_src != null && _overg != null && (Time.time - _overgCooldown) > 8f)
                {
                    _overgCooldown = Time.time;
                    _src.clip = _overg;
                    _src.Play();
                    _api.Log("GMeter: OVER G " + gi + "G voice triggered len=" + _overg.length.ToString("F1") + "s");
                }
            }
            else if (state == 1)
            {
                _gText.text = gi + "G";
                _gText.color = WARN_COLOR;
            }
            else
            {
                _gText.text = "";
            }
        }

        private static readonly Color OVERG_COLOR = new Color(1f, 0.22f, 0.18f, 1f);
        private static readonly Color WARN_COLOR = new Color(1f, 0.82f, 0.2f, 1f);
        private int _lastState = -1;
        private int _lastGi = -1;
    }

    /// <summary>手动解析 WAV（PCM），不依赖 UnityWebRequest。</summary>
    public static class WavUtil
    {
        public static AudioClip Decode(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
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
                            if (fmt != 1) return null;
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
            catch (Exception e) { UnityEngine.Debug.Log("GMeter WavUtil error " + path + " " + e.Message); return null; }
        }
    }
}
