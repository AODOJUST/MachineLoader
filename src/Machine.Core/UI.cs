using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Machine.Core
{
    /// <summary>加载器自身信息（名称 + 版本号，主菜单左下角展示）。</summary>
    public static class MachineLoader
    {
        public const string Name = "Machine";
        public const string Version = "2.4.1";
    }

    /// <summary>UI 通用工厂（矩形、文本、按钮、贴图）。</summary>
    public static class UiFactory
    {
        private static Texture2D _whiteTex;
        private static Sprite _whiteSprite;
        private static Texture2D _roundedTex;
        private static Sprite _roundedSprite;

        /// <summary>把任意文本转成游戏字体可安全显示的 ASCII（游戏字体不含中文，非 ASCII 一律替换为 '?'，避免方块与异常）。</summary>
        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c < 0x20 || c > 0x7E) chars[i] = '?';
            }
            return new string(chars);
        }

        public static Texture2D WhiteTexture()
        {
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var px = new Color[] { Color.white, Color.white, Color.white, Color.white };
                _whiteTex.SetPixels(px);
                _whiteTex.Apply();
            }
            return _whiteTex;
        }

        public static Sprite WhiteSprite()
        {
            if (_whiteSprite == null)
                _whiteSprite = Sprite.Create(WhiteTexture(), new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f));
            return _whiteSprite;
        }

        /// <summary>圆角 9-slice 精灵（游戏窗口白底圆角风格）。</summary>
        public static Sprite RoundedSprite()
        {
            if (_roundedSprite == null)
            {
                int size = 64;
                float radius = 14f;
                _roundedTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float max = size - 1 - radius;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float cx = 0f, cy = 0f;
                        if (x < radius && y < radius) { cx = radius; cy = radius; }
                        else if (x > max && y < radius) { cx = max; cy = radius; }
                        else if (x < radius && y > max) { cx = radius; cy = max; }
                        else if (x > max && y > max) { cx = max; cy = max; }
                        bool inside = true;
                        if (cx > 0f)
                        {
                            float dx = x - cx, dy = y - cy;
                            inside = (dx * dx + dy * dy) <= radius * radius;
                        }
                        _roundedTex.SetPixel(x, y, inside ? Color.white : Color.clear);
                    }
                }
                _roundedTex.Apply();
                var border = new Vector4(radius, radius, radius, radius);
                _roundedSprite = Sprite.Create(_roundedTex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            }
            return _roundedSprite;
        }

        public static TMP_FontAsset HarvestFont()
        {
            try
            {
                var tmp = UnityEngine.Object.FindFirstObjectByType<TextMeshProUGUI>();
                if (tmp != null && tmp.font != null) return tmp.font;
            }
            catch { }
            try { if (TMP_Settings.defaultFontAsset != null) return TMP_Settings.defaultFontAsset; } catch { }
            return null;
        }

        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public static TextMeshProUGUI NewText(Transform parent, string text, int size, Color color, TMP_FontAsset font)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = Safe(text);
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.Midline;
            if (font != null) t.font = font;
            return t;
        }

        /// <summary>游戏风格按钮：白色圆角 + 深色文字（primary 为深色底白字）。</summary>
        public static Button NewButton(Transform parent, string label, Action onClick, TMP_FontAsset font)
        {
            return NewButton(parent, label, onClick, font, false);
        }

        public static Button NewButton(Transform parent, string label, Action onClick, TMP_FontAsset font, bool primary)
        {
            var rt = NewRect("Btn", parent);
            rt.sizeDelta = new Vector2(180f, 40f);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = primary ? new Color(0.20f, 0.22f, 0.27f, 1f) : new Color(1f, 1f, 1f, 1f);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(delegate () { if (onClick != null) onClick(); });
            var t = NewText(rt, label, 16, primary ? Color.white : new Color(0.16f, 0.17f, 0.20f, 1f), font);
            Stretch((RectTransform)t.transform);
            return b;
        }
    }

    /// <summary>主菜单 Mod 按钮注入器（轮询场景直到主菜单出现）。</summary>
    public class MenuInjector
    {
        public class ExtraButtonDef
        {
            public string Text;
            public Action Action;
        }

        private MachineRuntime _rt;
        private bool _done;
        private float _cooldown;
        private List<ExtraButtonDef> _extra = new List<ExtraButtonDef>();
        private List<Button> _pendingEnable = new List<Button>();
        private List<Button> _injected = new List<Button>();

        public MenuInjector(MachineRuntime rt) { _rt = rt; }

        public void Start() { _cooldown = 1f; }

        public void AddExtraButton(string text, Action onClick)
        {
            var d = new ExtraButtonDef();
            d.Text = text;
            d.Action = onClick;
            _extra.Add(d);
        }

        public void TickEnable()
        {
            if (_pendingEnable.Count == 0) return;
            for (int i = _pendingEnable.Count - 1; i >= 0; i--)
            {
                if (_pendingEnable[i] == null) { _pendingEnable.RemoveAt(i); continue; }
                if (_pendingEnable[i].interactable) { _pendingEnable.RemoveAt(i); continue; }
                // 注入 2.5 秒后才可交互，避免启动瞬间被误触
                if (Time.unscaledTime - _injectTime >= 2.5f)
                {
                    _pendingEnable[i].interactable = true;
                    _pendingEnable.RemoveAt(i);
                }
            }
        }

        private float _injectTime;

        public void Poll()
        {
            MainMenu mm = (MainMenu)UnityEngine.Object.FindFirstObjectByType(typeof(MainMenu));
            if (mm == null || mm.mainMenu == null) return;
            if (_done)
            {
                // 场景切换后主菜单重建、旧按钮被销毁 → 需要重新注入
                bool anyAlive = false;
                for (int i = 0; i < _injected.Count; i++)
                {
                    if (_injected[i] != null) { anyAlive = true; break; }
                }
                if (anyAlive) return;
                _done = false;
                _injected.Clear();
                _modSavesInjected = false;   // 场景重载后 Play 子菜单的 Mod Saves 按钮也需重新注入
            }
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
            _cooldown = 0.5f;
            Button src = PickButton(mm.mainMenu.transform);
            if (src == null)
            {
                MachineLog.Warn("MenuInjector: no Button found under " + mm.mainMenu.name);
                _done = true;
                return;
            }
            try
            {
                Inject(mm.mainMenu, src);
                _done = true;
                MachineLog.Info("Mods button injected into main menu");
            }
            catch (Exception e) { MachineLog.Error("MenuInjector failed: " + e); _done = true; }
        }

        // ================= 临时诊断：主菜单结构探查（Play 子菜单） =================
        private bool _probePlayed;
        private float _probeDumpT;

        private void ProbeMainMenu(Transform root)
        {
            try
            {
                MachineLog.Info("MM probe: root=" + root.name + " buttons:");
                var btns = root.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] == null) continue;
                    var rt = btns[i].GetComponent<RectTransform>();
                    string pos = rt != null ? rt.anchoredPosition.ToString("F0") : "?";
                    string active = btns[i].gameObject.activeInHierarchy ? "on" : "off";
                    MachineLog.Info("  btn[" + i + "] name=" + btns[i].name + " active=" + active + " pos=" + pos + " parent=" + (btns[i].transform.parent != null ? btns[i].transform.parent.name : "?"));
                    DumpClick(btns[i]);
                }
                // 显示 Play 子菜单（New Game / Load / Sandbox）：不 Invoke（会连带其他副作用），
                // 直接反射 ToggleGameObjectButton 的目标并 SetActive(true)
                if (!_probePlayed)
                {
                    for (int i = 0; i < btns.Length; i++)
                    {
                        if (btns[i] != null && btns[i].name.ToLower().Contains("play"))
                        {
                            _probePlayed = true;
                            _probeDumpT = 1.5f;
                            MachineLog.Info("MM probe: Play found, revealing toggle target");
                            ProbePlaySubmenu(btns[i]);
                            break;
                        }
                    }
                }
            }
            catch (Exception e) { MachineLog.Warn("MM probe error " + e.Message); }
        }

        private void ProbeDumpSubmenu()
        {
            _probeDumpT -= Time.deltaTime;
            if (_probeDumpT > 0f) return;
            _probeDumpT = 1f;
            if (_probeDumped) return;
            _probeDumped = true;
            try
            {
                var mm = (MainMenu)UnityEngine.Object.FindFirstObjectByType(typeof(MainMenu));
                if (mm == null || mm.mainMenu == null) return;
                MachineLog.Info("MM probe: post-Play hierarchy:");
                DumpHierarchy(mm.mainMenu.transform, 0, 2);
            }
            catch (Exception e) { MachineLog.Warn("MM probe dump error " + e.Message); }
        }

        private bool _probeDumped;

        private void DumpHierarchy(Transform t, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                string pad = new string(' ', depth * 2);
                bool hasBtn = c.GetComponent<Button>() != null;
                bool hasTmp = c.GetComponentInChildren<TextMeshProUGUI>(true) != null;
                string label = "";
                var tmp = c.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null) label = " \"" + tmp.text + "\"";
                MachineLog.Info("  " + pad + c.name + " btn=" + hasBtn + (hasTmp ? label : "") + " act=" + c.gameObject.activeInHierarchy);
                DumpHierarchy(c, depth + 1, maxDepth);
            }
        }

        private void ProbeLoadPanel()
        {
            try
            {
                if (_loadPanelProbed) return;
                _loadPanelProbed = true;
                var all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || all[i].name != "LoadPanel") continue;
                    MachineLog.Info("    LoadPanel found, activating + dumping");
                    all[i].SetActive(true);
                    DumpSubButtons(all[i].transform);
                    // 打印 LoadPanel 组件的方法（找加载/保存存档入口）
                    var comps = all[i].GetComponents<MonoBehaviour>();
                    for (int c = 0; c < comps.Length; c++)
                    {
                        if (comps[c] == null) continue;
                        MachineLog.Info("    LoadPanel comp=" + comps[c].GetType().Name);
                        var methods = comps[c].GetType().GetMethods(System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        for (int m = 0; m < methods.Length; m++)
                        {
                            var mi = methods[m];
                            string n = mi.Name.ToLower();
                            if (n.Contains("load") || n.Contains("save") || n.Contains("open") || n.Contains("select"))
                            {
                                string ps = "";
                                var pars = mi.GetParameters();
                                for (int p = 0; p < pars.Length; p++) ps += (p > 0 ? "," : "") + pars[p].ParameterType.Name;
                                MachineLog.Info("      m " + mi.Name + "(" + ps + ")");
                            }
                        }
                        // 字段
                        var fields = comps[c].GetType().GetFields(System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        for (int fi = 0; fi < fields.Length; fi++)
                        {
                            string fn = fields[fi].Name.ToLower();
                            if (fn.Contains("load") || fn.Contains("save") || fn.Contains("slot") || fn.Contains("cargo"))
                            {
                                MachineLog.Info("      f " + fields[fi].Name + " : " + fields[fi].FieldType.Name);
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception e) { MachineLog.Warn("loadpanel probe " + e.Message); }
        }

        private bool _loadPanelProbed;

        private void DumpSubButtons(Transform root)
        {
            try
            {
                var btns = root.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] == null) continue;
                    var rt = btns[i].GetComponent<RectTransform>();
                    string pos = rt != null ? rt.anchoredPosition.ToString("F0") : "?";
                    MachineLog.Info("    subbtn[" + i + "] " + btns[i].name + " pos=" + pos + " act=" + btns[i].gameObject.activeInHierarchy);
                    DumpClick(btns[i]);
                }
                MachineLog.Info("    submenu total buttons=" + btns.Length);
            }
            catch (Exception e) { MachineLog.Warn("subbtn dump " + e.Message); }
        }

        private void ProbePlaySubmenu(Button b)
        {
            try
            {
                // 从 onClick 持久化监听里拿 ToggleGameObjectButton 实例，反射其目标字段
                var ub = b.onClick;
                var fPersist = typeof(UnityEngine.Events.UnityEventBase).GetField("m_PersistentCalls",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fPersist != null)
                {
                    var persist = fPersist.GetValue(ub);
                    if (persist != null)
                    {
                        var fCalls = persist.GetType().GetField("m_Calls",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (fCalls != null)
                        {
                            var calls = fCalls.GetValue(persist) as System.Collections.IList;
                            if (calls != null)
                            {
                                for (int i = 0; i < calls.Count; i++)
                                {
                                    var call = calls[i];
                                    if (call == null) continue;
                                    var fT = call.GetType().GetField("m_Target",
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                    object tgt = fT != null ? fT.GetValue(call) : null;
                                    if (tgt == null) continue;
                                    string tn = tgt.GetType().Name;
                                    MachineLog.Info("  play call target type=" + tn);
                                    if (tn.Contains("Toggle"))
                                    {
                                        // dump 目标组件字段，找 GameObject 目标
                                        var fields = tgt.GetType().GetFields(System.Reflection.BindingFlags.Instance |
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                        for (int fi = 0; fi < fields.Length; fi++)
                                        {
                                            var f = fields[fi];
                                            object v = null;
                                            try { v = f.GetValue(tgt); } catch { }
                                            string vs = v != null ? (v.GetType().Name + ":" + v.ToString()) : "null";
                                            if (vs.Length > 130) vs = vs.Substring(0, 130);
                                            MachineLog.Info("    f " + f.Name + " = " + vs);
                                            // 目标 GameObject（Play Menu）→ dump 其下按钮
                                            if (v is GameObject)
                                            {
                                                GameObject go = (GameObject)v;
                                                MachineLog.Info("    -> submenu " + go.name + " active=" + go.activeInHierarchy);
                                                DumpSubButtons(go.transform);
                                                if (go.name == "Play Menu") ProbeLoadPanel();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e) { MachineLog.Warn("toggle dump " + e.Message); }
        }

        private void DumpToggleTargets(Button b)
        {
            try
            {
                var all = b.GetComponents<Component>();
                for (int c = 0; c < all.Length; c++)
                {
                    var comp = all[c];
                    if (comp == null) continue;
                    if (comp.GetType().Name != "ToggleGameObjectButton") continue;
                    MachineLog.Info("  ToggleGameObjectButton fields:");
                    var fields = comp.GetType().GetFields(System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    for (int fi = 0; fi < fields.Length; fi++)
                    {
                        var f = fields[fi];
                        object v = null;
                        try { v = f.GetValue(comp); } catch { }
                        string vs = v != null ? (v.GetType().Name + ":" + v.ToString()) : "null";
                        if (vs.Length > 120) vs = vs.Substring(0, 120);
                        MachineLog.Info("    f " + f.Name + " = " + vs);
                    }
                }
            }
            catch (Exception e) { MachineLog.Warn("toggle dump " + e.Message); }
        }

        private void DumpClick(Button b)
        {
            try
            {
                var ub = b.onClick;
                var fPersist = typeof(UnityEngine.Events.UnityEventBase).GetField("m_PersistentCalls",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fPersist == null) return;
                var persist = fPersist.GetValue(ub);
                if (persist == null) return;
                var fCalls = persist.GetType().GetField("m_Calls",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fCalls == null) return;
                var calls = fCalls.GetValue(persist) as System.Collections.IList;
                if (calls == null) return;
                for (int i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    if (call == null) continue;
                    var fT = call.GetType().GetField("m_Target",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var fM = call.GetType().GetField("m_MethodName",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    object tgt = fT != null ? fT.GetValue(call) : null;
                    string mn = fM != null ? (string)fM.GetValue(call) : "?";
                    string argInfo = "";
                    // 持久化参数（OpenModal 的 modal 名称等）
                    try
                    {
                        var fMode = call.GetType().GetField("m_Mode",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var fArgs = call.GetType().GetField("m_Arguments",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        int mode = fMode != null ? (int)fMode.GetValue(call) : 0;
                        if (fArgs != null)
                        {
                            var args = fArgs.GetValue(call);
                            if (args != null)
                            {
                                var fStr = args.GetType().GetField("m_StringArgument",
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                var fObj = args.GetType().GetField("m_ObjectArgument",
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                if (fStr != null)
                                {
                                    string s = (string)fStr.GetValue(args);
                                    if (!string.IsNullOrEmpty(s)) argInfo += " str=\"" + s + "\"";
                                }
                                if (fObj != null)
                                {
                                    object o = null;
                                    try { o = fObj.GetValue(args); } catch { }
                                    if (o != null) argInfo += " obj=" + o.GetType().Name + ":" + o.ToString();
                                }
                            }
                        }
                        argInfo += " mode=" + mode;
                    }
                    catch { }
                    MachineLog.Info("    click-> " + (tgt != null ? tgt.GetType().Name : "?") + "." + mn + argInfo);
                }
            }
            catch (Exception e) { MachineLog.Warn("clickdump " + e.Message); }
        }

        private Button PickButton(Transform root)
        {
            Button[] all = root.GetComponentsInChildren<Button>(true);
            if (all == null || all.Length == 0) return null;
            // 优先挑一个既不是退出也不是设置的按钮作为模板
            for (int i = all.Length - 1; i >= 0; i--)
            {
                string n = all[i].name.ToLower();
                if (!n.Contains("quit") && !n.Contains("exit") && !n.Contains("setting") && !n.Contains("option") && !n.Contains("credits"))
                    return all[i];
            }
            return all[all.Length - 1];
        }

        private void Inject(GameObject root, Button src)
        {
            _injectTime = Time.unscaledTime;

            // 原版 Quit 移到主菜单最底部，避免玩家误触退出
            int insertIndex = -1;
            try
            {
                Button quit = FindButtonByName(root, "quit");
                if (quit != null)
                {
                    insertIndex = quit.transform.GetSiblingIndex();
                    quit.transform.SetAsLastSibling();
                    MachineLog.Info("MenuInjector: Quit moved to bottom (was index " + insertIndex + ")");
                }
            }
            catch (Exception e) { MachineLog.Warn("MenuInjector quit move failed " + e.Message); }

            Button b = CloneButton(root, src, "MachineModsButton", "Mods", delegate () { _rt.OpenModManager(); });
            b.interactable = false;
            _pendingEnable.Add(b);
            _injected.Add(b);
            if (insertIndex >= 0) b.transform.SetSiblingIndex(insertIndex);
            for (int i = 0; i < _extra.Count; i++)
            {
                ExtraButtonDef def = _extra[i];
                Button eb = CloneButton(root, src, "MachineModButton_" + i, def.Text, delegate () { if (def.Action != null) def.Action(); });
                eb.interactable = false;
                _pendingEnable.Add(eb);
                _injected.Add(eb);
                if (insertIndex >= 0) eb.transform.SetSiblingIndex(insertIndex + 1 + i);
            }
            EnsureVersionLabel(root);
            InjectModSavesButton(root);
        }

        private Button FindButtonByName(GameObject root, string token)
        {
            var btns = root.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < btns.Length; i++)
            {
                if (btns[i] == null) continue;
                if (btns[i].name.ToLower().Contains(token)) return btns[i];
            }
            return null;
        }

        /// <summary>Play 子菜单注入 "Mod Saves" 按钮（克隆 Load 按钮，点击打开 mod 存档窗口）。</summary>
        private ModSavesUI _modSaves;
        private bool _modSavesInjected;

        private void InjectModSavesButton(GameObject root)
        {
            try
            {
                // 找 Play Menu（Play 的 ToggleGameObjectButton 目标）
                GameObject playMenu = null;
                var all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (all[i].name == "Play Menu") { playMenu = all[i]; break; }
                }
                if (playMenu == null) return;
                // 场景重载后 Play Menu 重建、按钮被销毁；按钮还在则不重复注入
                if (_modSavesInjected && playMenu.transform.Find("MachineModSavesButton") != null) return;
                if (_modSavesInjected)
                    MachineLog.Info("ModSaves: button lost after scene reload, re-injecting");

                // 找 Load 按钮做模板
                Button loadBtn = null;
                var btns = playMenu.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] != null && btns[i].name == "Load") { loadBtn = btns[i]; break; }
                }
                if (loadBtn == null) return;

                if (_modSaves == null) _modSaves = new ModSavesUI(_rt);
                Button nb = CloneButton(playMenu, loadBtn, "MachineModSavesButton", "Mod Saves",
                    delegate () { _modSaves.Show(); });
                nb.interactable = true;   // 模板按钮未激活时克隆可能不可交互，强制可用
                MachineLog.Info("ModSaves btn interactable=" + nb.interactable + " listeners=" + nb.onClick.GetPersistentEventCount());
                // 插在 Back 之前（Back 应保持在最后）
                Button backBtn = null;
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] != null && btns[i].name == "Back") { backBtn = btns[i]; break; }
                }
                if (backBtn != null) nb.transform.SetSiblingIndex(backBtn.transform.GetSiblingIndex());
                _modSavesInjected = true;
                MachineLog.Info("Mod Saves button injected into Play Menu");
            }
            catch (Exception e) { MachineLog.Warn("ModSaves inject failed " + e.Message); }
        }

        /// <summary>主菜单左下角显示加载器名称与版本号（Machine Loader vX.Y.Z），旁边有logo按钮。</summary>
        private TextMeshProUGUI _versionLabel;
        private Button _logoBtn;
        private GameObject _logoMenu;
        private void EnsureVersionLabel(GameObject root)
        {
            try
            {
                if (_versionLabel != null) return;   // Unity 销毁后重载的 == 返回 true，会自动重建
                Canvas cv = root.GetComponentInParent<Canvas>();
                if (cv == null) return;

                // Logo 按钮（在版本号左边）
                var logoGo = new GameObject("MachineLogoBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                logoGo.transform.SetParent(cv.transform, false);
                var logoRt = (RectTransform)logoGo.transform;
                logoRt.anchorMin = Vector2.zero;
                logoRt.anchorMax = Vector2.zero;
                logoRt.pivot = Vector2.zero;
                logoRt.anchoredPosition = new Vector2(18f, 44f);
                logoRt.sizeDelta = new Vector2(36f, 36f);
                var logoImg = logoGo.GetComponent<Image>();
                // 加载 logo 图片
                try
                {
                    string logoPath = System.IO.Path.Combine(Application.dataPath, "..", "Machine", "logo.png");
                    if (System.IO.File.Exists(logoPath))
                    {
                        byte[] pngData = System.IO.File.ReadAllBytes(logoPath);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        tex.LoadImage(pngData);
                        logoImg.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        logoImg.color = Color.white;
                    }
                    else
                    {
                        logoImg.color = new Color(0.3f, 0.6f, 1f, 0.9f);
                    }
                }
                catch { logoImg.color = new Color(0.3f, 0.6f, 1f, 0.9f); }
                var logoBtn = logoGo.GetComponent<Button>();
                logoBtn.onClick.AddListener(delegate () { ToggleLogoMenu(cv); });
                _logoBtn = logoBtn;

                // 版本号标签（在logo右边）
                var go = new GameObject("MachineVersionLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(cv.transform, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                rt.anchoredPosition = new Vector2(62f, 48f);   // 避开logo
                rt.sizeDelta = new Vector2(520f, 32f);
                var txt = go.GetComponent<TextMeshProUGUI>();
                txt.text = UiFactory.Safe(MachineLoader.Name + " Loader v" + MachineLoader.Version);
                txt.font = UiFactory.HarvestFont();
                txt.fontSize = 20;
                txt.color = new Color(0.85f, 0.88f, 0.95f, 0.9f);
                txt.alignment = TextAlignmentOptions.BottomLeft;
                txt.raycastTarget = false;
                _versionLabel = txt;
                MachineLog.Info("Machine loader version label + logo button shown (v" + MachineLoader.Version + ")");
            }
            catch (Exception e) { MachineLog.Warn("version label failed: " + e.Message); }
        }

        /// <summary>切换 logo 菜单（语言选择 + 窗口布局调整）。</summary>
        private void ToggleLogoMenu(Canvas cv)
        {
            try
            {
                if (_logoMenu != null)
                {
                    _logoMenu.SetActive(!_logoMenu.activeSelf);
                    return;
                }
                // 创建菜单面板
                _logoMenu = new GameObject("MachineLogoMenu", typeof(RectTransform), typeof(Image));
                _logoMenu.transform.SetParent(cv.transform, false);
                var mrt = (RectTransform)_logoMenu.transform;
                mrt.anchorMin = Vector2.zero;
                mrt.anchorMax = Vector2.zero;
                mrt.pivot = Vector2.zero;
                mrt.anchoredPosition = new Vector2(18f, 88f);
                mrt.sizeDelta = new Vector2(600f, 560f);
                var mimg = _logoMenu.GetComponent<Image>();
                mimg.sprite = UiFactory.RoundedSprite();
                mimg.type = Image.Type.Sliced;
                mimg.color = new Color(0.15f, 0.17f, 0.22f, 0.95f);

                var font = UiFactory.HarvestFont();
                float y = 20f;  // 从顶部开始的距离

                // 标题（top-left anchor）
                var title = UiFactory.NewText(_logoMenu.transform, "Machine Settings", 28, new Color(0.9f, 0.92f, 1f, 1f), font);
                var titleRt = (RectTransform)title.transform;
                titleRt.anchorMin = titleRt.anchorMax = new Vector2(0f, 1f);
                titleRt.pivot = new Vector2(0f, 1f);
                titleRt.anchoredPosition = new Vector2(20f, -y);
                y += 48f;

                // 语言选择标签
                var langLabel = UiFactory.NewText(_logoMenu.transform, "Language: " + MachineLang.CurrentLanguage.ToUpper(), 22, new Color(0.7f, 0.75f, 0.85f, 1f), font);
                var langRt = (RectTransform)langLabel.transform;
                langRt.anchorMin = langRt.anchorMax = new Vector2(0f, 1f);
                langRt.pivot = new Vector2(0f, 1f);
                langRt.anchoredPosition = new Vector2(20f, -y);
                y += 40f;

                // 语言按钮（两行，每行4个，每个130×52）
                float btnW = 130f, btnH = 52f, gap = 16f;
                string[,] langBtns = {
                    { "中文", "zh" }, { "EN", "en" }, { "RU", "ru" }, { "日本語", "ja" },
                    { "한국어", "ko" }, { "DE", "de" }, { "FR", "fr" }, { "NL", "nl" }
                };
                for (int row = 0; row < 2; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        int idx = row * 4 + col;
                        string label = langBtns[idx, 0];
                        string code = langBtns[idx, 1];
                        float bx = 20f + col * (btnW + gap);
                        float by = y + row * (btnH + gap);
                        var btn = MakeSmallBtn(_logoMenu.transform, label, btnW, btnH, font,
                            delegate () { MachineLang.SetLanguage(code); RefreshLogoMenu(); });
                        btn.anchoredPosition = new Vector2(bx, -by);
                    }
                }
                y += 2 * (btnH + gap) + 16f;

                // 窗口布局调整按钮（全宽）
                var wlBtn = MakeSmallBtn(_logoMenu.transform, "Window Layout", 560f, 60f, font,
                    delegate ()
                    {
                        WindowAdjuster.Show(cv.transform, font);
                        _logoMenu.SetActive(false);
                    }, true);
                wlBtn.anchoredPosition = new Vector2(20f, -y);
                y += 76f;

                // 关闭按钮（全宽）
                var closeBtn = MakeSmallBtn(_logoMenu.transform, "Close", 560f, 60f, font,
                    delegate () { _logoMenu.SetActive(false); });
                closeBtn.anchoredPosition = new Vector2(20f, -y);
            }
            catch (Exception e) { MachineLog.Warn("logo menu failed: " + e.Message); }
        }

        /// <summary>创建小按钮（top-left anchor，指定大小）。</summary>
        private RectTransform MakeSmallBtn(Transform parent, string label, float w, float h, TMP_FontAsset font, Action onClick, bool primary = false)
        {
            var go = new GameObject("SmallBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            var img = go.GetComponent<Image>();
            img.sprite = UiFactory.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = primary ? new Color(0.25f, 0.45f, 0.75f, 0.95f) : new Color(0.30f, 0.33f, 0.40f, 0.9f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(delegate () { if (onClick != null) onClick(); });
            // 按钮文字
            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = (RectTransform)txtGo.transform;
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero;
            txtRt.offsetMax = Vector2.zero;
            var txt = txtGo.GetComponent<TextMeshProUGUI>();
            txt.text = label;
            txt.font = font;
            txt.fontSize = 20;
            txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Center;
            txt.raycastTarget = false;
            return rt;
        }

        private void RefreshLogoMenu()
        {
            if (_logoMenu == null) return;
            // 简单刷新：销毁重建
            UnityEngine.Object.Destroy(_logoMenu);
            _logoMenu = null;
            if (_logoBtn != null) _logoBtn.onClick.Invoke();
        }

        /// <summary>克隆游戏按钮作为模板，但剥离游戏自带的点击组件，只保留 UnityEngine UI Button。</summary>
        private Button CloneButton(GameObject root, Button src, string name, string label, Action onClick)
        {
            GameObject clone = (GameObject)UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            clone.name = name;

            // 剥离游戏自定义组件：这些组件携带场景序列化的回调（如退出），克隆后必须移除
            Component[] kill = clone.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < kill.Length; i++)
            {
                Type ty = kill[i].GetType();
                if (ty.Name == "CustomButton" || ty.Name == "OrangeHoverButton" || ty.Name == "ButtonAudio"
                    || ty.Name == "ButtonHighlight" || ty.Name == "ButtonPrompt" || ty.Name == "MenuAudio")
                {
                    UnityEngine.Object.Destroy(kill[i]);
                }
            }

            Button b = clone.GetComponent<Button>();
            if (b != null)
            {
                // 清除持久化点击回调（场景序列化的 m_PersistentCalls，如 OpenModal），RemoveAllListeners 清不掉它
                try
                {
                    var ub = b.onClick;
                    var fPersist = typeof(UnityEngine.Events.UnityEventBase).GetField("m_PersistentCalls",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fPersist != null)
                    {
                        var persist = fPersist.GetValue(ub);
                        if (persist != null)
                        {
                            var fCalls = persist.GetType().GetField("m_Calls",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (fCalls != null)
                            {
                                var calls = fCalls.GetValue(persist) as System.Collections.IList;
                                if (calls != null) calls.Clear();
                            }
                        }
                    }
                }
                catch { }
                b.onClick.RemoveAllListeners();
                b.onClick.AddListener(delegate () { if (onClick != null) onClick(); });
            }
            SetButtonLabel(clone, label);

            // 把克隆按钮排到模板按钮之后，并确保不在布局末尾（避免被游戏当作最后一个按钮处理）
            var srcRt = src.GetComponent<RectTransform>();
            var dstRt = clone.GetComponent<RectTransform>();
            if (srcRt != null && dstRt != null)
            {
                Transform parent = clone.transform.parent;
                VerticalLayoutGroup vlg = null;
                if (parent != null) vlg = parent.GetComponent<VerticalLayoutGroup>();
                if (vlg == null)
                {
                    // 无布局时：放在模板正下方并整体下移，绝不与任何原按钮重叠
                    dstRt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0f, -srcRt.rect.height - 40f);
                }
                else
                {
                    clone.transform.SetAsLastSibling();
                }
            }
            return b;
        }

        private void SetButtonLabel(GameObject go, string label)
        {
            label = UiFactory.Safe(label);
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) { tmp.text = label; return; }
            var legacy = go.GetComponentInChildren<UnityEngine.UI.Text>(true);
            if (legacy != null) { legacy.text = label; return; }
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                var t = UiFactory.NewText(rt, label, 16, Color.white, UiFactory.HarvestFont());
                UiFactory.Stretch((RectTransform)t.transform);
            }
        }
    }

    /// <summary>Mod 管理器窗口（全屏覆盖层）。</summary>
    public class ModManagerUI
    {
        private MachineRuntime _rt;
        private GameObject _root;
        private RectTransform _content;
        private TMP_FontAsset _font;
        private bool _built;

        public ModManagerUI(MachineRuntime rt) { _rt = rt; }

        public void Show()
        {
            if (!_built) Build();
            if (_root == null) return;
            RefreshList();
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private void Reload()
        {
            _rt.Mods.LoadAll(_rt.ModsDir, _rt.MachineDir, _rt.Api);
            RefreshList();
        }

        private void Build()
        {
            _built = true;
            try
            {
                _font = UiFactory.HarvestFont();
                var canvasGo = new GameObject("Machine.ModManager", typeof(Canvas), typeof(GraphicRaycaster), typeof(CanvasGroup));
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                canvasGo.GetComponent<CanvasGroup>().blocksRaycasts = true;
                _root = canvasGo;

                var bg = UiFactory.NewRect("Bg", canvasGo.transform);
                UiFactory.Stretch(bg);
                var bgImg = bg.gameObject.AddComponent<Image>();
                bgImg.sprite = UiFactory.WhiteSprite();
                bgImg.color = new Color(0f, 0f, 0f, 0.5f);

                var panel = UiFactory.NewRect("Panel", canvasGo.transform);
                panel.anchorMin = new Vector2(0.5f, 0.5f);
                panel.anchorMax = new Vector2(0.5f, 0.5f);
                panel.sizeDelta = new Vector2(780f, 540f);
                panel.anchoredPosition = Vector2.zero;
                var pImg = panel.gameObject.AddComponent<Image>();
                pImg.sprite = UiFactory.RoundedSprite();
                pImg.type = Image.Type.Sliced;
                pImg.color = new Color(0.96f, 0.96f, 0.97f, 1f);

                var title = UiFactory.NewText(panel, "Machine Mod Manager", 28, new Color(0.13f, 0.14f, 0.17f, 1f), _font);
                var titleRt = (RectTransform)title.transform;
                titleRt.anchorMin = new Vector2(0f, 1f);
                titleRt.anchorMax = new Vector2(1f, 1f);
                titleRt.pivot = new Vector2(0.5f, 1f);
                titleRt.offsetMin = new Vector2(0f, -48f);
                titleRt.offsetMax = new Vector2(0f, -8f);

                var scroll = UiFactory.NewRect("Scroll", panel);
                scroll.anchorMin = new Vector2(0f, 0f);
                scroll.anchorMax = new Vector2(1f, 1f);
                scroll.offsetMin = new Vector2(20f, 76f);
                scroll.offsetMax = new Vector2(-20f, -56f);
                var sr = scroll.gameObject.AddComponent<ScrollRect>();
                sr.horizontal = false;
                sr.vertical = true;
                sr.scrollSensitivity = 30f;

                var viewport = UiFactory.NewRect("Viewport", scroll);
                UiFactory.Stretch(viewport);
                viewport.gameObject.AddComponent<RectMask2D>();
                sr.viewport = viewport;

                var content = UiFactory.NewRect("Content", viewport);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.offsetMin = Vector2.zero;
                content.offsetMax = Vector2.zero;
                var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                vlg.childAlignment = TextAnchor.UpperCenter;
                vlg.childControlWidth = true;
                vlg.childControlHeight = false;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.spacing = 8f;
                vlg.padding = new RectOffset(4, 4, 4, 4);
                var csf = content.gameObject.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                sr.content = content;
                _content = content;

                var bottom = UiFactory.NewRect("BottomBar", panel);
                bottom.anchorMin = new Vector2(0f, 0f);
                bottom.anchorMax = new Vector2(1f, 0f);
                bottom.offsetMin = new Vector2(20f, 16f);
                bottom.offsetMax = new Vector2(-20f, 58f);
                var hlg = bottom.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 10f;
                hlg.childControlWidth = false;
                hlg.childForceExpandWidth = false;
                hlg.childControlHeight = true;
                hlg.childForceExpandHeight = true;
                hlg.childAlignment = TextAnchor.MiddleLeft;

                UiFactory.NewButton(bottom, "Open Mods Folder", delegate () { Application.OpenURL("file:///" + _rt.ModsDir); }, _font);
                UiFactory.NewButton(bottom, "Reload", delegate () { Reload(); }, _font);
                UiFactory.NewButton(bottom, "Window Positions", delegate () {
                    try { WindowAdjuster.Show(canvasGo.transform, _font); }
                    catch (Exception e) { MachineLog.Error("WindowAdjuster open: " + e); }
                }, _font);
                UiFactory.NewButton(bottom, "Close", delegate () { Hide(); }, _font, true);
            }
            catch (Exception e) { MachineLog.Error("ModManagerUI build failed: " + e); }
        }

        private void RefreshList()
        {
            if (_content == null) return;
            foreach (Transform c in _content) UnityEngine.Object.Destroy(c.gameObject);
            var mods = _rt.Mods.Mods;
            if (mods == null || mods.Count == 0)
            {
                var empty = UiFactory.NewText(_content, "No mods installed. Put a mod folder into " + _rt.ModsDir + " then press Reload.", 14, new Color(0.35f, 0.36f, 0.4f, 1f), _font);
                var emptyRt = (RectTransform)empty.transform;
                emptyRt.sizeDelta = new Vector2(700f, 40f);
                return;
            }
            foreach (var m in mods) CreateRow(m);
        }

        private void CreateRow(LoadedMod m)
        {
            // 每个 Mod 一块浅蓝灰色块（与窗口白底区分），背景白做天然分隔线
            var row = UiFactory.NewRect("Row_" + m.Info.id, _content);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 112f;
            var rowImg = row.gameObject.AddComponent<Image>();
            rowImg.sprite = UiFactory.RoundedSprite();
            rowImg.type = Image.Type.Sliced;
            rowImg.color = new Color(0.85f, 0.88f, 0.93f, 1f);

            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 10f;
            h.childControlWidth = false;
            h.childForceExpandWidth = false;
            h.childControlHeight = true;
            h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.padding = new RectOffset(12, 12, 8, 8);

            var left = UiFactory.NewRect("Info", row);
            left.sizeDelta = new Vector2(530f, 0f);
            var lv = left.gameObject.AddComponent<VerticalLayoutGroup>();
            lv.spacing = 3f;
            lv.childControlHeight = false;
            lv.childForceExpandHeight = false;
            lv.childAlignment = TextAnchor.UpperLeft;

            string author = string.IsNullOrEmpty(m.Info.author) ? "?" : m.Info.author;
            UiFactory.NewText(left, m.Info.name + "  v" + m.Info.version + "  by " + author, 17, new Color(0.12f, 0.13f, 0.16f, 1f), _font);

            var desc = UiFactory.NewText(left, string.IsNullOrEmpty(m.Info.description) ? "(no description)" : m.Info.description, 13, new Color(0.36f, 0.38f, 0.44f, 1f), _font);
            var descTmp = desc.GetComponent<TextMeshProUGUI>();
            descTmp.alignment = TextAlignmentOptions.TopLeft;
            try { descTmp.textWrappingMode = TextWrappingModes.Normal; } catch { }
            try { descTmp.overflowMode = TextOverflowModes.Ellipsis; } catch { }
            var descRt = (RectTransform)desc.transform;
            descRt.sizeDelta = new Vector2(0f, 34f);

            string status;
            Color sc;
            if (!m.Enabled) { status = "Disabled"; sc = new Color(0.45f, 0.46f, 0.5f, 1f); }
            else if (m.HasErrors) { status = "Load failed: " + string.Join(" | ", m.Errors.ToArray()); sc = new Color(0.78f, 0.18f, 0.16f, 1f); }
            else { status = "Loaded"; sc = new Color(0.16f, 0.55f, 0.28f, 1f); }
            UiFactory.NewText(left, status, 13, sc, _font);

            string label = m.Enabled ? "Disable" : "Enable";
            UiFactory.NewButton(row, label, delegate () { _rt.Mods.SetEnabled(m.Info.id, !m.Enabled); RefreshList(); }, _font);
        }
    }

    /// <summary>Mod Saves 存档窗口：列出 mod 存档（Machine_Mod 区，经 junction 即活动路径），
    /// 点击加载（复用游戏 LoadPanel 的 SelectFile/Load），底部 + 新建 mod 存档（复用 New Game 流程）。</summary>
    public class ModSavesUI
    {
        private MachineRuntime _rt;
        private GameObject _root;
        private RectTransform _listRoot;
        private List<Button> _slotButtons = new List<Button>();
        private TMP_FontAsset _font;

        public ModSavesUI(MachineRuntime rt) { _rt = rt; }

        public void Show()
        {
            try
            {
                Build(); RefreshList(); if (_root != null) _root.SetActive(true);
                MachineLog.Info("ModSaves: window shown, slots=" + _slotButtons.Count);
            }
            catch (Exception e) { MachineLog.Error("ModSaves show failed " + e); }
        }
        public void Hide()
        {
            try { if (_root != null) _root.SetActive(false); }
            catch (Exception e) { MachineLog.Error("ModSaves hide failed " + e); }
        }

        private string SaveDir()
        {
            // 固定 mod 存档区：LocalLow\Aviassembly\Machine_Mod\Aviassembly\SaveGames
            // 不随启动器 junction（活动路径）切换而变化，Mod Saves 永远管理 mod 版存档
            string p = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "AppData"), "LocalLow"), "Aviassembly");
            return System.IO.Path.Combine(p, "Machine_Mod", "Aviassembly", "SaveGames");
        }

        private Transform FindCanvas()
        {
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                Canvas best = null;
                int bestOrder = int.MinValue;
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas cv = canvases[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy) continue;
                    if (cv.renderMode == RenderMode.ScreenSpaceOverlay && cv.sortingOrder > bestOrder)
                    {
                        bestOrder = cv.sortingOrder;
                        best = cv;
                    }
                }
                if (best == null)
                {
                    var any = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                    if (any != null) { MachineLog.Info("ModSaves canvas fallback=" + any.name + " order=" + any.sortingOrder); return any.transform; }
                    return null;
                }
                MachineLog.Info("ModSaves canvas=" + best.name + " order=" + best.sortingOrder);
                return best.transform;
            }
            catch (Exception e) { MachineLog.Warn("ModSaves canvas " + e.Message); return null; }
        }

        private void Build()
        {
            if (_root != null) return;
            try
            {
                Transform canvas = FindCanvas();
                if (canvas == null) return;
                _font = UiFactory.HarvestFont();

                _root = UiFactory.NewRect("MachineModSaves", canvas).gameObject;
                UiFactory.Stretch(_root.GetComponent<RectTransform>());
                _root.transform.SetAsLastSibling();   // 确保盖在所有 UI 之上

                var mask = UiFactory.NewRect("Mask", _root.transform);
                UiFactory.Stretch(mask);
                var maskImg = mask.gameObject.AddComponent<Image>();
                maskImg.color = new Color(0f, 0f, 0f, 0.55f);

                var panel = UiFactory.NewRect("Panel", _root.transform);
                var prt = (RectTransform)panel.transform;
                prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(700f, 660f);
                var pimg = panel.gameObject.AddComponent<Image>();
                pimg.sprite = UiFactory.RoundedSprite();
                pimg.type = Image.Type.Sliced;
                pimg.color = new Color(0.96f, 0.96f, 0.97f, 0.97f);

                var title = UiFactory.NewText(panel.transform, "Mod Saves", 24, new Color(0.13f, 0.14f, 0.17f, 1f), _font);
                var trt = (RectTransform)title.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.anchoredPosition = new Vector2(0f, -22f);
                trt.sizeDelta = new Vector2(300f, 34f);

                _listRoot = UiFactory.NewRect("Slots", panel.transform);
                var lrt = (RectTransform)_listRoot.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.anchoredPosition = new Vector2(0f, 30f);
                lrt.sizeDelta = new Vector2(640f, 460f);

                var addBtn = UiFactory.NewButton(panel.transform, "+ New Mod Save", delegate () { OnAddNew(); }, _font, true);
                var art = (RectTransform)addBtn.transform;
                art.anchorMin = art.anchorMax = new Vector2(0.5f, 0f);
                art.anchoredPosition = new Vector2(0f, 20f);
                art.sizeDelta = new Vector2(400f, 56f);
                try { var at = addBtn.GetComponentInChildren<TextMeshProUGUI>(true); if (at != null) at.fontSize = 19; } catch { }

                var closeBtn = UiFactory.NewButton(panel.transform, "Close", delegate () { Hide(); }, _font);
                var crt = (RectTransform)closeBtn.transform;
                crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
                crt.anchoredPosition = new Vector2(-18f, -18f);
                crt.sizeDelta = new Vector2(110f, 40f);
            }
            catch (Exception e) { MachineLog.Error("ModSavesUI build failed " + e); }
        }

        private void RefreshList()
        {
            if (_root == null) return;
            try
            {
                for (int i = 0; i < _slotButtons.Count; i++)
                {
                    if (_slotButtons[i] != null) UnityEngine.Object.Destroy(_slotButtons[i].gameObject);
                }
                _slotButtons.Clear();
                string dir = SaveDir();
                if (!System.IO.Directory.Exists(dir)) return;
                string[] files = System.IO.Directory.GetFiles(dir, "*.plane");
                System.Array.Sort(files);
                float y = 0f;
                for (int i = 0; i < files.Length; i++)
                {
                    string fn = System.IO.Path.GetFileNameWithoutExtension(files[i]);
                    string full = files[i];
                    Button b = UiFactory.NewButton(_listRoot, fn, delegate () { OnLoadSave(fn, full); }, _font);
                    var rt = (RectTransform)b.transform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, -y);
                    rt.sizeDelta = new Vector2(620f, 56f);
                    // 两行：存档名 + 修改时间（游戏 Load 风格的大按钮；绕开 Safe 直接写含换行文本）
                    try
                    {
                        var tmp = b.GetComponentInChildren<TextMeshProUGUI>(true);
                        if (tmp != null)
                        {
                            string stamp = "";
                            try { stamp = System.IO.File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm"); } catch { }
                            tmp.text = fn + "\n" + stamp;
                            tmp.fontSize = 18;
                            tmp.alignment = TextAlignmentOptions.Center;
                        }
                    }
                    catch { }
                    y += 64f;
                    _slotButtons.Add(b);
                }
                if (files.Length == 0)
                {
                    var nt = UiFactory.NewText(_listRoot, "(no mod saves yet - use + New Mod Save)", 14, new Color(0.5f, 0.52f, 0.58f, 1f), _font);
                    var nrt = (RectTransform)nt.transform;
                    nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 1f);
                    nrt.pivot = new Vector2(0.5f, 1f);
                    nrt.anchoredPosition = new Vector2(0f, -8f);
                    nrt.sizeDelta = new Vector2(480f, 24f);
                }
            }
            catch (Exception e) { MachineLog.Error("ModSaves list failed " + e); }
        }

        private void OnLoadSave(string saveName, string fullPath)
        {
            try
            {
                Hide();
                MachineLog.Info("ModSaves: loading save '" + saveName + "'");
                // 标记本次进入来自 Mod Saves → 进游戏后 mod 全功能（非纯净）
                Machine.Mod.MachineState.ViaModSaves = true;
                var lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0) { MachineLog.Warn("ModSaves: LoadPanel not found"); return; }
                object lp = lps[0];
                var ty = lp.GetType();
                var sf = ty.GetMethod("SelectFile", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var ld = ty.GetMethod("Load", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (sf != null) sf.Invoke(lp, new object[] { saveName });
                if (ld != null) ld.Invoke(lp, null);
                MachineLog.Info("ModSaves: load invoked");
            }
            catch (Exception e) { MachineLog.Error("ModSaves load failed " + e); }
        }

        private void OnAddNew()
        {
            try
            {
                Hide();
                // 标记本次新游戏来自 Mod Saves → 进游戏后 mod 全功能（非纯净）
                Machine.Mod.MachineState.ViaModSaves = true;
                // 复用游戏 New Game 流程：打开 Difficulty 模态（新档会保存到当前 mod 存档区）
                var mmmArr = UnityEngine.Object.FindObjectsOfType<MenuModalManager>(true);
                if (mmmArr == null || mmmArr.Length == 0) { MachineLog.Warn("ModSaves: MenuModalManager not found"); return; }
                object mmm = mmmArr[0];
                GameObject difficulty = null;
                var all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].name == "Difficulty") { difficulty = all[i]; break; }
                }
                if (difficulty == null) { MachineLog.Warn("ModSaves: Difficulty modal not found"); return; }
                var om = mmm.GetType().GetMethod("OpenModal", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (om != null) om.Invoke(mmm, new object[] { difficulty });
                MachineLog.Info("ModSaves: new mod save flow opened (Difficulty)");
            }
            catch (Exception e) { MachineLog.Error("ModSaves new-save failed " + e); }
        }
    }
}
