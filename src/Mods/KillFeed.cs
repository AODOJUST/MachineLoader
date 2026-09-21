using System;
using System.Collections.Generic;
using UnityEngine;
using Machine.Mod;

namespace KillFeed
{
    /// <summary>
    /// 战斗事件表（画面正上方）：
    /// 记录战况——"型号 (呼号, 阵营) 击落 型号 (呼号, 阵营)" / "型号 (呼号, 阵营) 坠毁"。
    /// 括号里的呼号与阵营名按所属阵营着色，颜色直接取 FactionSystem 的阵营色
    /// （Snow=蓝 / Desert=橙红 / 其余=森林绿，落败阵营压暗、存活阵营提亮，与积分条文字同款）。
    /// 新事件在顶部平滑滑入，先出现的事件向下挤；持续 5 秒后渐隐消失；
    /// 最多同时 8 条，超过时最先出现的先消失。
    /// 由 MachineAAM（击落/坠毁）通过 FeedApi 上报。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.killfeed"; } }

        public void OnLoad(IMachineApi api)
        {
            // 加载器每次切场景都会重跑 OnLoad；不加守卫会挂出第二份 FeedSystem（事件表画两遍）
            if (FeedSystem.Live != null)
            {
                api.Log("KillFeed already live, skip OnLoad");
                return;
            }
            api.Log("KillFeed loading...");
            var go = new GameObject("Machine.KillFeed");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FeedSystem>().Init(api);
        }
    }

    /// <summary>跨 mod 静态入口（MachineAAM 反射调用，KillFeed 未加载时静默）。</summary>
    public static class FeedApi
    {
        private static IMachineApi _api;
        public static void Bind(IMachineApi api) { _api = api; }

        /// <summary>击落事件：击杀方 导弹型号 击毁 被击杀方。</summary>
        public static void PostKill(string kModel, string kCall, int kFaction, string missileName, string vModel, string vCall, int vFaction)
        {
            var s = FeedSystem.Live;
            if (s == null) { if (_api != null) _api.Log("KillFeed: PostKill but FeedSystem not live"); return; }
            var segs = new List<FeedSystem.FeedSeg>();
            AppendDesc(segs, kModel, kCall, kFaction);
            segs.Add(FeedSystem.Plain(" "));
            // 导弹型号用黄色高亮
            if (!string.IsNullOrEmpty(missileName))
            {
                segs.Add(FeedSystem.Tinted(missileName, new Color(1f, 0.85f, 0.2f, 1f)));
                segs.Add(FeedSystem.Plain(" "));
            }
            segs.Add(FeedSystem.Plain("击毁 "));
            AppendDesc(segs, vModel, vCall, vFaction);
            string d = PlainOf(segs);
            s.Add(segs, d);
            if (_api != null) _api.Log("KillFeed event: " + d);
        }

        /// <summary>兼容旧版PostKill（无导弹型号）。</summary>
        public static void PostKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)
        {
            PostKill(kModel, kCall, kFaction, null, vModel, vCall, vFaction);
        }

        /// <summary>坠毁事件。</summary>
        public static void PostCrash(string model, string call, int faction)
        {
            var s = FeedSystem.Live;
            if (s == null) { if (_api != null) _api.Log("KillFeed: PostCrash but FeedSystem not live"); return; }
            var segs = new List<FeedSystem.FeedSeg>();
            AppendDesc(segs, model, call, faction);
            segs.Add(FeedSystem.Plain(" 坠毁"));
            string d = PlainOf(segs);
            s.Add(segs, d);
            if (_api != null) _api.Log("KillFeed event: " + d);
        }

        /// <summary>魔法击杀事件（/kill指令）：格式与坠毁相同，后缀"被魔法杀死了"。</summary>
        public static void PostMagicKill(string model, string call, int faction)
        {
            var s = FeedSystem.Live;
            if (s == null) { if (_api != null) _api.Log("KillFeed: PostMagicKill but FeedSystem not live"); return; }
            var segs = new List<FeedSystem.FeedSeg>();
            AppendDesc(segs, model, call, faction);
            segs.Add(FeedSystem.Plain(" 被魔法杀死了"));
            string d = PlainOf(segs);
            s.Add(segs, d);
            if (_api != null) _api.Log("KillFeed event: " + d);
        }

        /// <summary>批量清除实体事件（/kill @missile等，不含AI/玩家）："清除 N 个实体"。</summary>
        public static void PostClearEntities(int count)
        {
            var s = FeedSystem.Live;
            if (s == null) { if (_api != null) _api.Log("KillFeed: PostClearEntities but FeedSystem not live"); return; }
            string d = "清除 " + count + " 个实体";
            s.Add(d);
            if (_api != null) _api.Log("KillFeed event: " + d);
        }

        /// <summary>机型用中性色，括号里的名称用阵营色。格式：机型(名称)</summary>
        private static void AppendDesc(List<FeedSystem.FeedSeg> segs, string model, string call, int faction)
        {
            string m = string.IsNullOrEmpty(model) ? "飞机" : model;
            string c = string.IsNullOrEmpty(call) ? "?" : call;
            Color fc = FeedSystem.FactionTint(faction);
            segs.Add(FeedSystem.Plain(m + "("));
            segs.Add(FeedSystem.Tinted(c, fc));
            segs.Add(FeedSystem.Plain(")"));
        }

        private static string PlainOf(List<FeedSystem.FeedSeg> segs)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < segs.Count; i++) sb.Append(segs[i].Text);
            return sb.ToString();
        }
    }

    public class FeedSystem : MonoBehaviour
    {
        public static FeedSystem Live;
        private IMachineApi _api;

        private const float Life = 5f;      // 展示时长（秒）
        private const float Fade = 0.8f;    // 渐隐时长
        private const int Max = 8;          // 最多同时显示条数
        private const float SlideIn = 0.35f; // 顶部滑入时长

        /// <summary>中性文字色（机型 / "击落" / 标点）。</summary>
        private static readonly Color PlainColor = new Color(1f, 0.96f, 0.88f, 1f);

        /// <summary>一行里的一段：整行由多段拼成，便于给不同部分上不同颜色。</summary>
        public class FeedSeg
        {
            public string Text = "";
            public Color Color = PlainColor;
            public bool Tinted;     // true = 阵营色（天空背景下加描边保证可读）
            public float W;         // 测量宽度（首帧算一次后缓存）
        }

        public static FeedSeg Plain(string t)
        {
            return new FeedSeg { Text = t, Color = PlainColor, Tinted = false };
        }

        public static FeedSeg Tinted(string t, Color c)
        {
            return new FeedSeg { Text = t, Color = c, Tinted = true };
        }

        public class FeedEntry
        {
            public List<FeedSeg> Segs = new List<FeedSeg>();
            public string Text = "";    // 纯文本（日志用）
            public float Born;
            public float TotalW = -1f;  // <0 = 还没测量
        }

        private readonly List<FeedEntry> _entries = new List<FeedEntry>();

        // =================================================================
        // 阵营名 / 阵营色（反射 FactionSystem，缺失时降级）
        // =================================================================
        private static readonly Dictionary<int, string> _factionNames = new Dictionary<int, string>();
        private static bool _factionChecked;
        private static System.Reflection.MethodInfo _miGetFaction;   // FactionApi.GetFaction(int)
        private static System.Reflection.MethodInfo _miColor;        // FactionSystem.FactionColor(string)
        private static System.Reflection.FieldInfo _fiFactionName;
        private static System.Reflection.FieldInfo _fiEliminated;

        public static string FactionName(int id)
        {
            string cached;
            if (_factionNames.TryGetValue(id, out cached)) return cached;
            string n = ResolveFactionName(id);
            // 解析失败（阵营 mod 还没加载）时不写缓存，下次事件再试
            if (n != null) _factionNames[id] = n;
            return n == null ? ("F" + id) : n;
        }

        private static string ResolveFactionName(int id)
        {
            try
            {
                EnsureFactionTypes();
                if (_miGetFaction == null) return null;
                var fd = _miGetFaction.Invoke(null, new object[] { id });
                if (fd == null) return null;
                if (_fiFactionName == null) _fiFactionName = fd.GetType().GetField("Name");
                if (_fiFactionName == null) return null;
                var v = _fiFactionName.GetValue(fd) as string;
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch { return null; }
        }

        /// <summary>
        /// 阵营色：与 FactionSystem 积分条文字同一套（阵营色；落败压暗 0.62，存活向白提亮 0.22）。
        /// FactionSystem 不在时用同名映射 + 按 id 的兜底色，保证三个阵营仍然可区分。
        /// </summary>
        public static Color FactionTint(int id)
        {
            try
            {
                EnsureFactionTypes();
                if (_miGetFaction == null) return FallbackColor(id);

                var fd = _miGetFaction.Invoke(null, new object[] { id });
                if (fd == null) return FallbackColor(id);

                string name = null;
                bool elim = false;
                if (_fiFactionName == null) _fiFactionName = fd.GetType().GetField("Name");
                if (_fiFactionName != null) name = _fiFactionName.GetValue(fd) as string;
                if (_fiEliminated == null) _fiEliminated = fd.GetType().GetField("Eliminated");
                if (_fiEliminated != null)
                {
                    try { elim = (bool)_fiEliminated.GetValue(fd); } catch { elim = false; }
                }

                Color c;
                if (_miColor != null)
                {
                    try { c = (Color)_miColor.Invoke(null, new object[] { name }); }
                    catch { c = LocalColor(name); }
                }
                else c = LocalColor(name);

                // 与积分条一致：落败转暗，存活向白提亮（深色/明亮背景都能看清）
                if (elim) c = Color.Lerp(c, new Color(0.42f, 0.42f, 0.45f, 1f), 0.62f);
                else c = Color.Lerp(c, Color.white, 0.22f);
                return c;
            }
            catch { return FallbackColor(id); }
        }

        /// <summary>按类型全名找 FactionApi / FactionSystem（绝不按 Assembly.GetName().Name 匹配）。</summary>
        private static void EnsureFactionTypes()
        {
            if (_factionChecked && _miGetFaction != null) return;
            try
            {
                Type api = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                Type sys = Type.GetType("Machine.Faction.FactionSystem, FactionSystem");
                if (api == null || sys == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (api == null) api = asm.GetType("Machine.Faction.FactionApi");
                            if (sys == null) sys = asm.GetType("Machine.Faction.FactionSystem");
                        }
                        catch { }
                        if (api != null && sys != null) break;
                    }
                }
                if (api != null) _miGetFaction = api.GetMethod("GetFaction", new Type[] { typeof(int) });
                if (sys != null) _miColor = sys.GetMethod("FactionColor", new Type[] { typeof(string) });
                // 两个都找到才算完成；否则下次事件继续找（阵营 mod 可能后加载）
                _factionChecked = (_miGetFaction != null && _miColor != null);
            }
            catch { }
        }

        private static Color LocalColor(string name)
        {
            string n = name == null ? "" : name.ToLowerInvariant();
            if (n.Contains("snow") || n.Contains("ice") || n.Contains("arctic") || n.Contains("winter"))
                return new Color(0.20f, 0.52f, 0.92f, 1f);
            if (n.Contains("desert") || n.Contains("sand") || n.Contains("dune"))
                return new Color(0.95f, 0.42f, 0.16f, 1f);
            return new Color(0.22f, 0.62f, 0.30f, 1f);
        }

        private static Color FallbackColor(int id)
        {
            if (id == 0) return new Color(0.20f, 0.52f, 0.92f, 1f);
            if (id == 1) return new Color(0.95f, 0.42f, 0.16f, 1f);
            if (id == 2) return new Color(0.22f, 0.62f, 0.30f, 1f);
            return new Color(0.86f, 0.86f, 0.88f, 1f);
        }

        public void Init(IMachineApi api)
        {
            _api = api;
            Live = this;
            FeedApi.Bind(api);
            _api.Log("KillFeed ready");
        }

        public void Add(List<FeedSeg> segs, string plain)
        {
            var e = new FeedEntry { Segs = segs, Text = plain, Born = Time.time };
            _entries.Insert(0, e);
            while (_entries.Count > Max) _entries.RemoveAt(_entries.Count - 1);   // 超出 8 条，最先出现的先消失
            if (_api != null) _api.Log("KillFeed Add: entries=" + _entries.Count + " text=" + plain);
        }

        /// <summary>兼容旧的单段调用（整行中性色）。</summary>
        public void Add(string text)
        {
            var segs = new List<FeedSeg>();
            segs.Add(Plain(text));
            Add(segs, text);
        }

        private float _diagT = 10f;
        private void Update()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (Time.time - _entries[i].Born > Life + Fade) _entries.RemoveAt(i);
            }
            UpdateSelfTest();
            // 自检日志：每 10 秒输出一次显示条件与条目数（排查"事件表不显示"）
            _diagT -= Time.deltaTime;
            if (_diagT <= 0f)
            {
                _diagT = 10f;
                if (_api != null)
                {
                    bool pure = Machine.Mod.MachineState.PureMode;
                    bool inF = false;
                    try { inF = Machine.Mod.MachineState.InFlight(); } catch { }
                    _api.Log("KillFeed diag: live=" + (Live != null) + " entries=" + _entries.Count
                             + " pure=" + pure + " inflight=" + inF + " selftest=" + SelfTestOn);
                }
            }
        }

        // =================================================================
        // 内置自测：mods/KillFeed/_feed_test.flag 存在时，注入三个阵营的假事件并截图
        // （主菜单也能跑：测试模式下跳过 InFlight 守卫）
        // =================================================================
        private static bool SelfTestOn;
        private float _testT;
        private int _testShot;
        private bool _testLogged;

        private static string FlagPath()
        {
            string dir = System.IO.Path.Combine(Application.dataPath, "..");
            return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(dir, "mods"), "KillFeed"), "_feed_test.flag");
        }

        private void UpdateSelfTest()
        {
            string flag = FlagPath();
            bool on = System.IO.File.Exists(flag);
            if (on && !SelfTestOn)
            {
                SelfTestOn = true;
                _testT = 0f;
                _testShot = 0;
                if (_api != null) _api.Log("KillFeed selftest: flag found, injecting sample events");
            }
            if (!on) { SelfTestOn = false; _testLogged = false; return; }

            _testT -= Time.deltaTime;
            if (_testT > 0f) return;
            _testT = 4f;

            // 三条：0 击落 1 / 2 击落 0 / 1 坠毁
            FeedApi.PostKill("FALCON", "SNOW-1", 0, "VIPER", "DESERT-3", 1);
            FeedApi.PostKill("HAWK", "FOREST-7", 2, "FALCON", "SNOW-2", 0);
            FeedApi.PostCrash("VIPER", "DESERT-9", 1);

            if (_api != null)
            {
                _api.Log("KillFeed selftest: injected 3 entries, colors f0=" + Fmt(FactionTint(0))
                         + " f1=" + Fmt(FactionTint(1)) + " f2=" + Fmt(FactionTint(2))
                         + " names=" + FactionName(0) + "/" + FactionName(1) + "/" + FactionName(2));
            }

            if (_testShot < 3)
            {
                StartCoroutine(Shot());
                _testShot++;
            }
        }

        private static string Fmt(Color c)
        {
            return "#" + ((int)(c.r * 255f)).ToString("X2") + ((int)(c.g * 255f)).ToString("X2") + ((int)(c.b * 255f)).ToString("X2");
        }

        private System.Collections.IEnumerator Shot()
        {
            yield return new WaitForSeconds(1.4f);   // 截图异步落盘，留足时间
            string dir = System.IO.Path.Combine(System.IO.Path.Combine(
                System.IO.Path.Combine(Application.dataPath, ".."), "Machine"), "logs");
            dir = System.IO.Path.Combine(dir, "killfeed_shots");
            try { if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir); } catch { }
            string file = System.IO.Path.Combine(dir, "kf_" + (_testShot - 1).ToString("00") + ".png");
            ScreenCapture.CaptureScreenshot(file);
            if (_api != null) _api.Log("KillFeed selftest: shot -> " + file);
            yield return new WaitForSeconds(1.0f);   // 等异步落盘
            if (_testShot >= 3 && !_testLogged)
            {
                _testLogged = true;
                if (_api != null) _api.Log("KILLFEED_SELFTEST_DONE shots=" + _testShot);
            }
        }

        private static GUIStyle _st;
        private static GUIStyle _stShadow;

        private void OnGUI()
        {
            if (_entries.Count == 0) return;
            if (Machine.Mod.MachineState.PureMode) return;
            if (!Machine.Mod.MachineState.InFlight() && !SelfTestOn) return;   // 主菜单/编辑器/停机坪不显示

            if (_st == null)
            {
                _st = new GUIStyle(GUI.skin.label);
                _st.fontSize = 15;
                _st.fontStyle = FontStyle.Bold;
                _st.alignment = TextAnchor.MiddleLeft;
                _st.wordWrap = false;
                _st.clipping = TextClipping.Overflow;
                _st.normal.textColor = PlainColor;

                _stShadow = new GUIStyle(_st);
                _stShadow.normal.textColor = new Color(0f, 0f, 0f, 1f);
            }

            float cx = Screen.width * 0.5f;
            float rowH = 24f;
            float yStart = 14f;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                float age = Time.time - e.Born;
                // 5 秒后渐隐
                float alpha = age > Life ? Mathf.Clamp01((Life + Fade - age) / Fade) : 1f;
                // 顶部平滑滑入
                float yOff = age < SlideIn ? -24f * (1f - age / SlideIn) : 0f;
                float y = yStart + i * rowH + yOff;

                Measure(e);
                float x = cx - e.TotalW * 0.5f;

                for (int k = 0; k < e.Segs.Count; k++)
                {
                    var sg = e.Segs[k];
                    if (sg.W <= 0f) continue;
                    var r = new Rect(x, y, sg.W + 2f, rowH);
                    if (sg.Tinted) DrawOutline(r, sg.Text, alpha);
                    var c = sg.Color;
                    c.a = alpha;
                    _st.normal.textColor = c;
                    GUI.Label(r, sg.Text, _st);
                    x += sg.W;
                }
            }
            _st.normal.textColor = PlainColor;
        }

        private void Measure(FeedEntry e)
        {
            if (e.TotalW >= 0f) return;
            var content = new GUIContent();
            float total = 0f;
            for (int i = 0; i < e.Segs.Count; i++)
            {
                content.text = e.Segs[i].Text;
                float w = _st.CalcSize(content).x;
                e.Segs[i].W = w;
                total += w;
            }
            e.TotalW = total;
        }

        /// <summary>彩色段加一圈黑描边：天空/雪地背景上纯色小字很容易糊掉。</summary>
        private void DrawOutline(Rect r, string text, float alpha)
        {
            var c = new Color(0f, 0f, 0f, alpha * 0.72f);
            _stShadow.normal.textColor = c;
            GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), text, _stShadow);
            GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), text, _stShadow);
            GUI.Label(new Rect(r.x, r.y - 1f, r.width, r.height), text, _stShadow);
            GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), text, _stShadow);
        }
    }
}
