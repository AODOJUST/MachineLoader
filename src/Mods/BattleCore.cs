using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Machine.Mod;
using Machine.Core;

namespace Machine.BattleCore
{
    /// <summary>
    /// 战斗部（BattleCore）：信息集散基础 Mod。
    /// 其他 Mod 通过静态 API 上报/查询信息，BattleCore 汇总并以 HUD 面板输出，
    /// 实现 Mod 联动。它是后续 Mod 的前置（依赖方在 mod.json 声明 loadAfter）。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.battlecore"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("BattleCore loading...");
            var go = new GameObject("Machine.BattleCore");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<BattleCoreSystem>().Init(api);
        }
    }

    /// <summary>单条信息条目。</summary>
    public class InfoEntry
    {
        public string Source;
        public string Key;
        public string Value;
        public int Severity;      // 0-10：>=9 红、>=5 黄、否则灰
        public float Time;

        public InfoEntry(string s, string k, string v, int sev)
        {
            Source = s; Key = k; Value = v;
            Severity = sev < 0 ? 0 : (sev > 10 ? 10 : sev);
            Time = UnityEngine.Time.unscaledTime;
        }
    }

    /// <summary>
    /// 静态集散 API。其他 Mod 依赖 BattleCore 时调用（可反射，也可直接引用本程序集）。
    /// </summary>
    public static class Core
    {
        private static BattleCoreSystem _sys;

        internal static void Bind(BattleCoreSystem sys) { _sys = sys; }

        /// <summary>BattleCore 是否已加载并可上报。</summary>
        public static bool Available { get { return _sys != null; } }

        /// <summary>上报一条信息（source 为来源 Mod 名，如 "GMeter"）。</summary>
        public static void Post(string source, string key, string value, int severity)
        {
            if (_sys != null) _sys.Add(source, key, value, severity);
        }

        /// <summary>查询某来源某键的最新值，未找到返回 null。</summary>
        public static string Get(string source, string key)
        {
            return _sys != null ? _sys.Query(source, key) : null;
        }

        /// <summary>当前全部条目快照（新->旧），BattleCore 未加载时为 null。</summary>
        public static IList<InfoEntry> Entries
        {
            get { return _sys != null ? _sys.Snapshot() : null; }
        }
    }

    public class BattleCoreSystem : MonoBehaviour
    {
        private IMachineApi _api;
        private readonly List<InfoEntry> _entries = new List<InfoEntry>();
        private const int MaxEntries = 200;
        private const int MaxShown = 10;

        private Canvas _canvas;
        private RectTransform _panel;
        private Image _panelBg;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _bodyText;
        private float _refreshTimer;
        private float _inGameT;      // inGame 判定节流（菜单里 Instance 访问贵）
        private bool _inGameCache;
        private bool _collapsed;
        private bool _dragging;
        private bool _hidden;
        private bool _keybindInjected;
        private float _keybindTimer;

        public void Init(IMachineApi api)
        {
            _api = api;
            Core.Bind(this);
            BuildUI();
            _api.Log("BattleCore: ready (info hub active)");
        }

        /// <summary>面板是否可见（玩家用 ~ 切换）。</summary>
        public bool PanelVisible { get { return !_hidden; } }

        /// <summary>外部 Mod 可调用：切换面板显隐。</summary>
        public void TogglePanel()
        {
            _hidden = !_hidden;
            if (_panel != null) _panel.gameObject.SetActive(!_hidden);
            if (_api != null) _api.Log("BattleCore: panel " + (_hidden ? "hidden" : "shown"));
        }

        public void Add(string source, string key, string value, int severity)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(key)) return;
            _entries.Add(new InfoEntry(source, key, value, severity));
            while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }

        public string Query(string source, string key)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Source == source && e.Key == key) return e.Value;
            }
            return null;
        }

        public IList<InfoEntry> Snapshot()
        {
            var list = new List<InfoEntry>(_entries);
            list.Reverse();
            return list;
        }

        private void BuildUI()
        {
            try
            {
                TMP_FontAsset font = UiFactory.HarvestFont();

                var canvasGo = new GameObject("Machine.BattleCoreCanvas", typeof(Canvas));
                _canvas = canvasGo.GetComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 19000;
                canvasGo.transform.SetParent(transform, false);

                _panel = UiFactory.NewRect("Panel", canvasGo.transform);
                _panel.anchorMin = new Vector2(0f, 0f);
                _panel.anchorMax = new Vector2(0f, 0f);
                _panel.pivot = new Vector2(0f, 0f);
                _panel.anchoredPosition = new Vector2(18f, 18f);
                _panel.sizeDelta = new Vector2(330f, 250f);

                _panelBg = _panel.gameObject.AddComponent<Image>();
                _panelBg.sprite = UiFactory.RoundedSprite();
                _panelBg.type = Image.Type.Sliced;
                _panelBg.color = new Color(0.96f, 0.96f, 0.97f, 0.92f);

                // 可拖动：鼠标左键按住面板任意处拖动移动窗口（BeginDrag/Drag/EndDrag 完整事件链）
                var et = _panel.gameObject.AddComponent<EventTrigger>();
                var beginEntry = new EventTrigger.Entry();
                beginEntry.eventID = EventTriggerType.BeginDrag;
                beginEntry.callback.AddListener(delegate (BaseEventData data) { _dragging = true; });
                et.triggers.Add(beginEntry);
                var dragEntry = new EventTrigger.Entry();
                dragEntry.eventID = EventTriggerType.Drag;
                dragEntry.callback.AddListener(delegate (BaseEventData data) { OnPanelDrag((PointerEventData)data); });
                et.triggers.Add(dragEntry);
                var endEntry = new EventTrigger.Entry();
                endEntry.eventID = EventTriggerType.EndDrag;
                endEntry.callback.AddListener(delegate (BaseEventData data) { _dragging = false; });
                et.triggers.Add(endEntry);

                // 点击折叠/展开
                var btn = _panel.gameObject.AddComponent<Button>();
                btn.targetGraphic = _panelBg;
                btn.onClick.AddListener(delegate () { _collapsed = !_collapsed; ApplyLayout(); });

                // 标题
                _titleText = UiFactory.NewText(_panel, "BattleCore", 16, new Color(0.13f, 0.14f, 0.17f, 1f), font);
                var titleRt = (RectTransform)_titleText.transform;
                titleRt.anchorMin = new Vector2(0f, 1f);
                titleRt.anchorMax = new Vector2(1f, 1f);
                titleRt.pivot = new Vector2(0.5f, 1f);
                titleRt.offsetMin = new Vector2(14f, -30f);
                titleRt.offsetMax = new Vector2(-14f, -6f);
                _titleText.alignment = TextAlignmentOptions.MidlineLeft;

                // 内容
                _bodyText = UiFactory.NewText(_panel, "", 17, new Color(0.20f, 0.21f, 0.25f, 1f), font);
                var bodyRt = (RectTransform)_bodyText.transform;
                bodyRt.anchorMin = new Vector2(0f, 0f);
                bodyRt.anchorMax = new Vector2(1f, 1f);
                bodyRt.offsetMin = new Vector2(14f, 10f);
                bodyRt.offsetMax = new Vector2(-14f, -36f);
                _bodyText.alignment = TextAlignmentOptions.TopLeft;
                _bodyText.textWrappingMode = TextWrappingModes.Normal;
                _bodyText.richText = true;

                ApplyLayout();
                _api.Log("BattleCore: UI built");
            }
            catch (Exception e) { _api.Log("BattleCore: build UI failed " + e.Message); }
        }

        private void ApplyLayout()
        {
            if (_panel == null) return;
            if (_collapsed)
            {
                _panel.sizeDelta = new Vector2(330f, 34f);
                if (_titleText != null) _titleText.text = "BattleCore (collapsed - click)";
            }
            else
            {
                _panel.sizeDelta = new Vector2(330f, 250f);
                if (_titleText != null) _titleText.text = "BattleCore (click to collapse)";
            }
            RefreshBody();
        }

        private void RefreshBody()
        {
            if (_bodyText == null) return;
            if (_collapsed) { _bodyText.text = ""; return; }
            var snap = Snapshot();
            if (snap == null || snap.Count == 0)
            {
                _bodyText.text = "No data yet.\nMods report via BattleCore.Post(...)";
                return;
            }
            // 每个来源+键只显示最新一条，避免高频上报刷屏
            var latest = new Dictionary<string, InfoEntry>();
            foreach (var e in snap)
            {
                string k = e.Source + "\u0001" + e.Key;
                if (!latest.ContainsKey(k)) latest[k] = e;
            }
            var sb = new System.Text.StringBuilder();
            // 顶部：油量 + 油门（实时）
            string fuelThr = ReadFuelThrottle();
            if (fuelThr.Length > 0)
                sb.Append("<color=#2f6fd0><size=19><b>").Append(UiFactory.Safe(fuelThr)).Append("</b></size></color>\n\n");
            int shown = 0;
            foreach (var kv in latest)
            {
                if (shown >= MaxShown) break;
                shown++;
                var e = kv.Value;
                string color = e.Severity >= 9 ? "#ff6a5e" : (e.Severity >= 5 ? "#e8a13a" : "#45464d");
                sb.Append("<color=").Append(color).Append('>');
                sb.Append(UiFactory.Safe(e.Source)).Append(" | ").Append(UiFactory.Safe(e.Key)).Append(": ").Append(UiFactory.Safe(e.Value));
                sb.Append("</color>\n");
            }
            if (latest.Count > MaxShown)
                sb.Append("<color=#9a9ba3>... ").Append(latest.Count - MaxShown).Append(" more</color>");
            _bodyText.text = sb.ToString();
        }

        /// <summary>读取玩家飞机油量与油门大小（反射试常用字段名，找不到返回空串）。</summary>
        private string ReadFuelThrottle()
        {
            try
            {
                var pc = PlaneContainer.Instance;
                if (pc == null) return "";
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                float fuel = -1f;
                string[] fNames = { "fuel", "fuelLevel", "fuelAmount", "currentFuel", "fuelTank" };
                var t = pc.GetType();
                for (int i = 0; i < fNames.Length; i++)
                {
                    var f = t.GetField(fNames[i], BF);
                    if (f != null)
                    {
                        var v = f.GetValue(pc);
                        if (v is float) { fuel = (float)v; break; }
                    }
                }
                // 油门：InputManager.throttleInput
                float thr = -1f;
                try
                {
                    var im = pc.GetComponent<InputManager>();
                    if (im != null)
                    {
                        var f = im.GetType().GetField("throttleInput", BF);
                        if (f != null)
                        {
                            var v = f.GetValue(im);
                            if (v is float) thr = (float)v;
                        }
                    }
                }
                catch { }
                var sb = new System.Text.StringBuilder();
                if (fuel >= 0f) sb.Append("Fuel ").Append(Mathf.RoundToInt(fuel));
                if (thr >= 0f)
                {
                    if (sb.Length > 0) sb.Append("    ");
                    sb.Append("Throttle ").Append(Mathf.RoundToInt(thr * 100f)).Append("%");
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private void Update()
        {
            // 主菜单/非游戏场景守卫：不显示面板、不响应切换键（PlaneContainer 或机场存在才算游戏内）
            // ★ 节流：游戏 Singleton 在 m_Instance 为 null 时会走全场景 FindFirstObjectByType，
            //   主菜单里每次访问 = 一次全场景扫描（2~4ms）。inGame 判定 0.25s 轮询足够（只影响面板显隐）。
            _inGameT -= Time.unscaledDeltaTime;
            if (_inGameT <= 0f)
            {
                _inGameT = 0.25f;
                try { _inGameCache = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { _inGameCache = false; }
            }
            bool inGame = _inGameCache;
            if (!inGame)
            {
                if (!_hidden && _panel != null && _panel.gameObject.activeSelf)
                {
                    _panel.gameObject.SetActive(false);
                    _api.Log("BattleCore: hidden (not in game)");
                }
                _hidden = true;
                _refreshTimer = 0f;
                return;
            }
            // 纯净模式守卫：原版档隐藏战斗部面板
            if (Machine.Mod.MachineState.PureMode)
            {
                if (!_hidden && _panel != null) _panel.gameObject.SetActive(false);
                _hidden = true;
                return;
            }
            // 编辑器/停机坪守卫：未进入飞行模式隐藏战斗部面板
            if (!Machine.Mod.MachineState.InFlight())
            {
                if (!_hidden && _panel != null) _panel.gameObject.SetActive(false);
                _hidden = true;
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = 0.5f;
                RefreshBody();
            }

            // ~ 键切换面板显隐（默认 BackQuote，游戏按键设置中可看到该绑定项）
            if (Input.GetKeyDown(KeyCode.BackQuote)) TogglePanel();

            // 注入游戏按键设置中的绑定显示项
            if (!_keybindInjected)
            {
                _keybindTimer -= Time.unscaledDeltaTime;
                if (_keybindTimer <= 0f)
                {
                    _keybindTimer = 2f;
                    TryInjectKeybindField();
                }
            }
        }

        private void OnPanelDrag(PointerEventData data)
        {
            if (_panel == null || data == null) return;
            _panel.anchoredPosition += data.delta;
        }
        /// <summary>
        /// 在游戏的 Settings -> Key Bindings 面板中注入一项 "Toggle BattleCore"（显示默认键 ~）。
        /// 克隆现有 KeybindField 做纯展示，禁用其重绑交互，避免污染游戏自身按键绑定。
        /// </summary>
        private void TryInjectKeybindField()
        {
            try
            {
                var kp = UnityEngine.Object.FindFirstObjectByType<KeybindPanel>();
                if (kp == null) return;
                var fld = typeof(KeybindPanel).GetField("keybindFields",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (fld == null) return;
                var arr = fld.GetValue(kp) as Array;
                if (arr == null || arr.Length == 0) return;
                var template = arr.GetValue(0) as Component;
                if (template == null) return;

                var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = "MachineKeybind_ToggleBattleCore";
                var cf = clone.GetComponent<KeybindField>();
                if (cf == null) return;
                cf.enabled = false;   // 阻止 Start 覆盖显示与重绑逻辑

                var knF = typeof(KeybindField).GetField("keyName",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (knF != null)
                {
                    var kn = knF.GetValue(cf) as TextMeshProUGUI;
                    if (kn != null) kn.text = "Toggle BattleCore";
                }
                var kvF = typeof(KeybindField).GetField("keyValue",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (kvF != null)
                {
                    var kv = kvF.GetValue(cf) as KeyIcon;
                    if (kv != null)
                    {
                        bool setOk = false;
                        try
                        {
                            var sv = typeof(KeyIcon).GetMethod("SetValue",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (sv != null) { sv.Invoke(kv, new object[] { "~" }); setOk = true; }
                        }
                        catch { }
                        if (!setOk)
                        {
                            // 降级：直接改键值文本组件
                            var kvText = kv.GetComponentInChildren<TextMeshProUGUI>(true);
                            if (kvText != null) kvText.text = "~";
                        }
                    }
                }
                // 禁掉克隆体上所有按钮交互，防止误触游戏重绑流程
                var btns = clone.GetComponentsInChildren<Button>(true);
                foreach (var b in btns) b.interactable = false;
                _keybindInjected = true;
                _api.Log("BattleCore: keybind field injected into Settings");
            }
            catch (Exception e)
            {
                _api.Log("BattleCore: keybind inject failed " + e.Message);
            }
        }

        private void OnDestroy()
        {
            Core.Bind(null);
        }
    }
}
