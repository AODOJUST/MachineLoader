using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Machine.Core
{
    /// <summary>
    /// 游客模式：玩家设置联机昵称。
    /// 名字只能是文字（中文 / 英文 / 数字 / 空格），禁止特殊符号与 emoji；
    /// 确认后即锁定（写入 Machine 记忆系统 profile.json），并做二次确认提示。
    /// </summary>
    public class PlayerNameUI
    {
        private MachineRuntime _rt;
        private GameObject _root;
        private TMP_FontAsset _font;
        private TMP_InputField _nameInput;
        private TextMeshProUGUI _status;
        private GameObject _confirmPanel;
        private TextMeshProUGUI _confirmText;
        private string _pendingName = "";

        public PlayerNameUI(MachineRuntime rt) { _rt = rt; }

        public void Show()
        {
            Build();
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            _pendingName = "";
            if (_confirmPanel != null) _confirmPanel.SetActive(false);
            if (_status != null) _status.text = "";
        }

        public void Hide()
        {
            try { if (_root != null) _root.SetActive(false); }
            catch (Exception e) { MachineLog.Error("PlayerNameUI hide failed " + e); }
        }

        // ---------- 名字校验：仅中文 / 英文 / 数字 / 空格 ----------
        public static bool ValidName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length > 20) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ')
                    continue;
                if (c >= 0x4e00 && c <= 0x9fff) continue; // 常用中文
                return false;
            }
            return true;
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
                if (best == null) best = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                return best != null ? best.transform : null;
            }
            catch { return null; }
        }

        private TextMeshProUGUI Label(Transform parent, string text, int size, Color color)
        {
            var t = UiFactory.NewText(parent, text, size, color, _font);
            try { t.fontStyle = FontStyles.Bold; } catch { }
            return t;
        }

        private Button Btn(Transform parent, string label, Action onClick, bool primary = false)
        {
            Button b = UiFactory.NewButton(parent, label, onClick, _font, primary);
            try
            {
                var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null) { t.fontSize = 30; t.fontStyle = FontStyles.Bold; }
            }
            catch { }
            return b;
        }

        private void Build()
        {
            if (_root != null) return;
            try
            {
                Transform canvas = FindCanvas();
                if (canvas == null) return;
                _font = UiFactory.HarvestFont();

                _root = UiFactory.NewRect("MachineName", canvas).gameObject;
                UiFactory.Stretch(_root.GetComponent<RectTransform>());
                _root.transform.SetAsLastSibling();

                var mask = UiFactory.NewRect("Mask", _root.transform);
                UiFactory.Stretch(mask);
                var maskImg = mask.gameObject.AddComponent<Image>();
                maskImg.color = new Color(0f, 0f, 0f, 0.55f);

                var panel = UiFactory.NewRect("Panel", _root.transform);
                var prt = (RectTransform)panel.transform;
                prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(900f, 620f);
                var pimg = panel.gameObject.AddComponent<Image>();
                pimg.sprite = UiFactory.RoundedSprite();
                pimg.type = Image.Type.Sliced;
                pimg.color = new Color(0.96f, 0.96f, 0.97f, 0.97f);

                var title = Label(panel.transform, "GUEST NAME", 36, new Color(0.13f, 0.14f, 0.17f, 1f));
                var trt = (RectTransform)title.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.anchoredPosition = new Vector2(0f, -24f);
                trt.sizeDelta = new Vector2(600f, 48f);
                title.alignment = TextAlignmentOptions.Center;

                var tip = Label(panel.transform, "ENTER A NAME TO JOIN ONLINE PLAY", 24, new Color(0.25f, 0.28f, 0.33f, 1f));
                var tpt = (RectTransform)tip.transform;
                tpt.anchorMin = tpt.anchorMax = new Vector2(0.5f, 1f);
                tpt.anchoredPosition = new Vector2(0f, -96f);
                tpt.sizeDelta = new Vector2(800f, 36f);
                tip.alignment = TextAlignmentOptions.Center;

                var rule = Label(panel.transform, "LETTERS / NUMBERS / CHINESE ONLY - NO SYMBOLS OR EMOJI", 22, new Color(0.55f, 0.3f, 0.3f, 1f));
                var rrt = (RectTransform)rule.transform;
                rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 1f);
                rrt.anchoredPosition = new Vector2(0f, -138f);
                rrt.sizeDelta = new Vector2(820f, 32f);
                rule.alignment = TextAlignmentOptions.Center;

                // 输入框
                _nameInput = NewInput(panel.transform, "YOUR NAME", _font);
                var irt = (RectTransform)_nameInput.transform;
                irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
                irt.anchoredPosition = new Vector2(0f, 70f);
                irt.sizeDelta = new Vector2(640f, 60f);

                Button ok = Btn(panel.transform, "Confirm", delegate () { OnConfirm(); }, true);
                var ort = (RectTransform)ok.transform;
                ort.anchorMin = ort.anchorMax = new Vector2(0.5f, 0.5f);
                ort.anchoredPosition = new Vector2(0f, -20f);
                ort.sizeDelta = new Vector2(460f, 72f);

                _status = Label(panel.transform, "", 24, new Color(0.7f, 0.25f, 0.25f, 1f));
                var srt = (RectTransform)_status.transform;
                srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0f);
                srt.anchoredPosition = new Vector2(0f, 92f);
                srt.sizeDelta = new Vector2(840f, 36f);
                _status.alignment = TextAlignmentOptions.Center;

                // 二次确认面板
                _confirmPanel = UiFactory.NewRect("ConfirmPanel", _root.transform).gameObject;
                var crt = (RectTransform)_confirmPanel.transform;
                crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
                crt.sizeDelta = new Vector2(760f, 360f);
                var cimg = _confirmPanel.AddComponent<Image>();
                cimg.sprite = UiFactory.RoundedSprite();
                cimg.type = Image.Type.Sliced;
                cimg.color = new Color(0.92f, 0.93f, 0.95f, 0.98f);

                _confirmText = Label(_confirmPanel.transform, "", 28, new Color(0.13f, 0.14f, 0.17f, 1f));
                var ctrt = (RectTransform)_confirmText.transform;
                ctrt.anchorMin = ctrt.anchorMax = new Vector2(0.5f, 0.5f);
                ctrt.anchoredPosition = new Vector2(0f, 78f);
                ctrt.sizeDelta = new Vector2(700f, 120f);
                _confirmText.alignment = TextAlignmentOptions.Center;

                Button yes = Btn(_confirmPanel.transform, "YES, LOCK IT", delegate () { OnYes(); }, true);
                var yrt = (RectTransform)yes.transform;
                yrt.anchorMin = yrt.anchorMax = new Vector2(0.5f, 0.5f);
                yrt.anchoredPosition = new Vector2(-130f, -70f);
                yrt.sizeDelta = new Vector2(240f, 64f);

                Button no = Btn(_confirmPanel.transform, "NO, CHANGE", delegate () { OnNo(); });
                var nrt = (RectTransform)no.transform;
                nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
                nrt.anchoredPosition = new Vector2(130f, -70f);
                nrt.sizeDelta = new Vector2(240f, 64f);

                _confirmPanel.SetActive(false);
            }
            catch (Exception e) { MachineLog.Error("PlayerNameUI build failed " + e); }
        }

        private void OnConfirm()
        {
            string name = _nameInput != null ? _nameInput.text.Trim() : "";
            if (!ValidName(name))
            {
                if (_status != null) _status.text = "INVALID NAME - LETTERS / NUMBERS / CHINESE ONLY, NO SYMBOLS OR EMOJI";
                return;
            }
            _pendingName = name;
            if (_confirmText != null)
                _confirmText.text = "CONFIRM NAME:  " + UiFactory.Safe(name) + "\n\nIT WILL BE LOCKED TO THIS MACHINE\n(AFTER THIS YOU CANNOT CHANGE IT)";
            if (_confirmPanel != null) _confirmPanel.SetActive(true);
        }

        private void OnYes()
        {
            if (_pendingName.Length == 0) return;
            _rt.Memory.PlayerName = _pendingName;
            Net.SetPlayerName(_pendingName);
            MachineLog.Info("player name locked: " + _pendingName);
            Hide();
            _rt.OpenOnline();
        }

        private void OnNo()
        {
            if (_confirmPanel != null) _confirmPanel.SetActive(false);
            if (_status != null) _status.text = "";
        }

        private TMP_InputField NewInput(Transform parent, string placeholder, TMP_FontAsset font)
        {
            var go = UiFactory.NewRect("Input", parent).gameObject;
            var img = go.AddComponent<Image>();
            img.sprite = UiFactory.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.95f);

            var txtGo = UiFactory.NewRect("Text", go.transform).gameObject;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.font = font;
            txt.fontSize = 30;
            txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(0.1f, 0.1f, 0.1f, 1f);
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            var trt = (RectTransform)txtGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(14f, 6f);
            trt.offsetMax = new Vector2(-14f, -6f);

            var phGo = UiFactory.NewRect("Placeholder", go.transform).gameObject;
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            ph.font = font;
            ph.fontSize = 26;
            ph.color = new Color(0.55f, 0.58f, 0.63f, 1f);
            ph.text = UiFactory.Safe(placeholder);
            ph.alignment = TextAlignmentOptions.MidlineLeft;
            var prt = (RectTransform)phGo.transform;
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(14f, 6f);
            prt.offsetMax = new Vector2(-14f, -6f);

            var input = go.AddComponent<TMP_InputField>();
            input.textComponent = txt;
            input.placeholder = ph;
            input.text = "";
            return input;
        }
    }
}
