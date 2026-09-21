using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace Machine.Core
{
    /// <summary>
    /// 房间内聊天框。记录系统事件（玩家进入退出）和玩家对话。
    /// `/` 键快捷打开/关闭，可拖动标题栏移动。
    /// </summary>
    public class ChatBox : MonoBehaviour
    {
        private GameObject _root;
        private GameObject _panel;
        private RectTransform _panelRt;
        private ScrollRect _scroll;
        private RectTransform _content;
        private TMP_InputField _input;
        private TextMeshProUGUI _titleLabel;
        private Button _copyBtn;
        private string _copyText = "";
        private readonly List<string> _messages = new List<string>();
        private bool _dragging;
        private Vector2 _dragOffset;
        private float _lastSlashTime;

        public bool Visible { get { return _root != null && _root.activeSelf; } }

        /// <summary>创建聊天框并附加到 canvas。</summary>
        public static ChatBox Create(Transform canvas, TMP_FontAsset font)
        {
            var go = new GameObject("MachineChatBox");
            go.transform.SetParent(canvas, false);
            var cb = go.AddComponent<ChatBox>();
            try
            {
                cb.Build(font);
                return cb;
            }
            catch (System.Exception e)
            {
                MachineLog.Error("ChatBox Build failed: " + e.Message);
                try { UnityEngine.Object.Destroy(go); } catch { }
                return null;
            }
        }

        private void Build(TMP_FontAsset font)
        {
            _root = UiFactory.NewRect("ChatRoot", transform).gameObject;
            UiFactory.Stretch(_root.GetComponent<RectTransform>());
            _root.SetActive(false);

            // 面板
            _panel = UiFactory.NewRect("Panel", _root.transform).gameObject;
            _panelRt = (RectTransform)_panel.transform;
            _panelRt.anchorMin = _panelRt.anchorMax = new Vector2(1f, 1f);  // 右上角
            _panelRt.pivot = new Vector2(1f, 1f);  // 右上角
            _panelRt.anchoredPosition = new Vector2(-30f, -30f);  // 右上角偏移30像素
            _panelRt.sizeDelta = new Vector2(560f, 420f);
            var pimg = _panel.AddComponent<Image>();
            pimg.sprite = UiFactory.RoundedSprite();
            pimg.type = Image.Type.Sliced;
            pimg.color = new Color(0.12f, 0.14f, 0.18f, 0.92f);

            // 标题栏（可拖动）
            var titleBar = UiFactory.NewRect("TitleBar", _panel.transform).gameObject;
            var tbrt = (RectTransform)titleBar.transform;
            tbrt.anchorMin = new Vector2(0f, 1f);
            tbrt.anchorMax = new Vector2(1f, 1f);
            tbrt.pivot = new Vector2(0.5f, 1f);
            tbrt.sizeDelta = new Vector2(0f, 44f);
            var tbImg = titleBar.AddComponent<Image>();
            tbImg.color = new Color(0.18f, 0.22f, 0.28f, 1f);
            _titleLabel = UiFactory.NewText(titleBar.transform, "CHAT  (/ to toggle)", 24, new Color(0.7f, 0.85f, 1f, 1f), font);
            try { _titleLabel.fontStyle = FontStyles.Bold; } catch { }
            var tlrt = (RectTransform)_titleLabel.transform;
            tlrt.anchorMin = Vector2.zero;
            tlrt.anchorMax = Vector2.one;
            tlrt.offsetMin = new Vector2(12f, 0f);
            tlrt.offsetMax = new Vector2(-110f, 0f);
            _titleLabel.alignment = TextAlignmentOptions.MidlineLeft;

            // COPY 按钮（复制加入链接）
            _copyBtn = UiFactory.NewButton(titleBar.transform, "COPY", delegate () {
                try
                {
                    if (_copyText.Length > 0)
                    {
                        GUIUtility.systemCopyBuffer = _copyText;
                        if (_titleLabel != null) _titleLabel.text = "COPIED: " + _copyText;
                    }
                }
                catch { }
            }, font, true);
            var cbrt = (RectTransform)_copyBtn.transform;
            cbrt.anchorMin = new Vector2(1f, 0f);
            cbrt.anchorMax = new Vector2(1f, 1f);
            cbrt.pivot = new Vector2(1f, 0.5f);
            cbrt.anchoredPosition = new Vector2(-8f, 0f);
            cbrt.sizeDelta = new Vector2(90f, 34f);
            _copyBtn.gameObject.SetActive(false);  // 默认隐藏，设置了复制文本后显示

            // 拖动触发
            var trigger = titleBar.AddComponent<EventTrigger>();
            var dragEntry = new EventTrigger.Entry { eventID = EventTriggerType.BeginDrag };
            dragEntry.callback.AddListener(delegate (BaseEventData d) {
                _dragging = true;
                Vector2 lp;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)_root.transform, Input.mousePosition, null, out lp);
                _dragOffset = lp - _panelRt.anchoredPosition;
            });
            trigger.triggers.Add(dragEntry);
            var dragEntry2 = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            dragEntry2.callback.AddListener(delegate (BaseEventData d) {
                if (!_dragging) return;
                Vector2 lp;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)_root.transform, Input.mousePosition, null, out lp);
                _panelRt.anchoredPosition = lp - _dragOffset;
            });
            trigger.triggers.Add(dragEntry2);
            var dragEntry3 = new EventTrigger.Entry { eventID = EventTriggerType.EndDrag };
            dragEntry3.callback.AddListener(delegate (BaseEventData d) { _dragging = false; });
            trigger.triggers.Add(dragEntry3);

            // 消息滚动区域
            var scrollGo = UiFactory.NewRect("Scroll", _panel.transform).gameObject;
            var srt = (RectTransform)scrollGo.transform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.offsetMin = new Vector2(8f, 60f);
            srt.offsetMax = new Vector2(-8f, -48f);
            _scroll = scrollGo.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            var vpImg = scrollGo.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0.3f);

            // 内容
            var contentGo = UiFactory.NewRect("Content", scrollGo.transform).gameObject;
            _content = (RectTransform)contentGo.transform;
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = new Vector2(0f, 200f);
            _scroll.content = _content;
            _scroll.viewport = srt;

            // 输入框
            _input = NewChatInput(_panel.transform, font);
            var irt = (RectTransform)_input.transform;
            irt.anchorMin = new Vector2(0f, 0f);
            irt.anchorMax = new Vector2(1f, 0f);
            irt.pivot = new Vector2(0.5f, 0f);
            irt.anchoredPosition = new Vector2(0f, 10f);
            irt.sizeDelta = new Vector2(-16f, 44f);
            _input.onEndEdit.AddListener(delegate (string v) {
                if (v.Length > 0 && Input.GetKeyDown(KeyCode.Return)) SendChat(v);
            });

            AddSystemMessage("Chat connected. Press / to toggle.");
        }

        private TMP_InputField NewChatInput(Transform parent, TMP_FontAsset font)
        {
            var go = UiFactory.NewRect("Input", parent).gameObject;
            var img = go.AddComponent<Image>();
            img.sprite = UiFactory.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = new Color(0.2f, 0.24f, 0.3f, 0.95f);

            var txtGo = UiFactory.NewRect("Text", go.transform).gameObject;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            if (font != null) txt.font = font;   // null 字体会让 TMP 抛 NRE
            txt.fontSize = 26;
            try { txt.fontStyle = FontStyles.Bold; } catch { }
            txt.color = new Color(0.9f, 0.95f, 1f, 1f);
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            var trt = (RectTransform)txtGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 4f);
            trt.offsetMax = new Vector2(-10f, -4f);

            var phGo = UiFactory.NewRect("Placeholder", go.transform).gameObject;
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            if (font != null) ph.font = font;
            ph.fontSize = 22;
            ph.color = new Color(0.5f, 0.55f, 0.6f, 1f);
            ph.text = "Type message... (Enter to send)";
            ph.alignment = TextAlignmentOptions.MidlineLeft;
            var prt = (RectTransform)phGo.transform;
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(10f, 4f);
            prt.offsetMax = new Vector2(-10f, -4f);

            var input = go.AddComponent<TMP_InputField>();
            input.textComponent = txt;
            input.placeholder = ph;
            input.text = "";
            return input;
        }

        public void Toggle()
        {
            if (_root == null) return;
            _root.SetActive(!_root.activeSelf);
            if (_root.activeSelf && _input != null) _input.ActivateInputField();
        }

        public void Show() { if (_root != null) _root.SetActive(true); }
        public void Hide() { if (_root != null) _root.SetActive(false); }

        /// <summary>设置可复制的文本（如加入链接），显示 COPY 按钮。</summary>
        public void SetCopyText(string text)
        {
            _copyText = text ?? "";
            if (_copyBtn != null) _copyBtn.gameObject.SetActive(_copyText.Length > 0);
        }

        public void AddMessage(string sender, string message)
        {
            AddRawMessage("<color=#7ac7ff>" + UiFactory.Safe(sender) + "</color>: " + UiFactory.Safe(message));
        }

        /// <summary>单参数消息（无发送者前缀），用于 /machine diag 那类系统输出。</summary>
        public void AddMessage(string message)
        {
            AddSystemMessage(message);
        }

        public void AddSystemMessage(string message)
        {
            AddRawMessage("<color=#88ff88>* " + UiFactory.Safe(message) + "</color>");
        }

        private void AddRawMessage(string richText)
        {
            _messages.Add(richText);
            if (_messages.Count > 100) _messages.RemoveAt(0);
            RebuildMessages();
        }

        private void RebuildMessages()
        {
            if (_content == null) return;
            // 清除旧消息
            for (int i = _content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);

            float y = 0f;
                var msgFont = _titleLabel != null ? _titleLabel.font : null;
                for (int i = 0; i < _messages.Count; i++)
                {
                    var go = UiFactory.NewRect("Msg" + i, _content).gameObject;
                    var rt = (RectTransform)go.transform;
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, -y);
                    rt.sizeDelta = new Vector2(-8f, 36f);
                    var txt = go.AddComponent<TextMeshProUGUI>();
                    if (msgFont != null) txt.font = msgFont;   // null 字体会让 TMP 抛 NRE
                txt.fontSize = 30;
                try { txt.fontStyle = FontStyles.Bold; } catch { }
                txt.color = new Color(0.85f, 0.9f, 0.95f, 1f);
                txt.alignment = TextAlignmentOptions.TopLeft;
                txt.enableWordWrapping = true;
                txt.richText = true;
                txt.text = _messages[i];
                float h = Mathf.Max(36f, txt.preferredHeight);
                rt.sizeDelta = new Vector2(-8f, h);
                y += h + 4f;
            }
            _content.sizeDelta = new Vector2(0f, Mathf.Max(300f, y));
            // 滚动到底部
            if (_scroll != null) _scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>最近一条以 / 开头的指令（供其他mod读取，如 /ai 呼出阵营面板）。读取后会被清空。</summary>
        public static string LastCommand;

        private void SendChat(string message)
        {
            if (message.Length == 0) return;
            // 检测指令（以 / 开头），存入 LastCommand 供其他mod读取
            if (message.StartsWith("/"))
            {
                LastCommand = message;

                // Machine内置诊断命令
                if (message.Equals("/machine diag", StringComparison.OrdinalIgnoreCase) ||
                    message.Equals("/machine diag", StringComparison.Ordinal))
                {
                    try
                    {
                        string diag = Log.GetDiagnosticInfo();
                        // 在聊天框中显示诊断信息
                        foreach (string line in diag.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            AddMessage("[DIAG] " + line);
                        }
                        // 添加性能报告
                        string perf = ModProfiler.GetReport();
                        foreach (string line in perf.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            AddMessage("[PERF] " + line);
                        }
                        // 添加对象池统计
                        string pools = PoolManager.GetStats();
                        foreach (string line in pools.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            AddMessage("[POOL] " + line);
                        }
                    }
                    catch (Exception e)
                    {
                        AddMessage("[DIAG] Error: " + e.Message);
                    }
                    if (_input != null) { _input.text = ""; _input.ActivateInputField(); }
                    return;
                }
            }
            try
            {
                var c = Net.Client;
                if (c != null && c.Connected && c.InRoom)
                {
                    c.Send("CHAT|" + message);
                }
            }
            catch { }
            if (_input != null) { _input.text = ""; _input.ActivateInputField(); }
        }

        void Update()
        {
            // `/` 键切换（防止输入框聚焦时触发）
            if (Input.GetKeyDown(KeyCode.Slash) && (_input == null || !_input.isFocused))
            {
                Toggle();
            }
            // Enter 发送
            if (_input != null && _input.isFocused && Input.GetKeyDown(KeyCode.Return))
            {
                SendChat(_input.text);
            }
        }

        void OnDestroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}
