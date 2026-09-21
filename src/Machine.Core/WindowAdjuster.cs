using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Machine.Core
{
    /// <summary>
    /// 窗口位置调整界面。列出所有注册到 WindowRegistry 的窗口，
    /// 玩家可以拖拽调整各窗口位置，保存后下次启动生效。
    /// </summary>
    public class WindowAdjuster : MonoBehaviour
    {
        private GameObject _root;
        private GameObject _panel;
        private RectTransform _content;
        private string _draggingId = null;
        private Vector2 _dragOffset;
        private TMP_FontAsset _font;

        private static WindowAdjuster _instance;

        public static void Show(Transform canvas, TMP_FontAsset font)
        {
            if (_instance != null) { _instance._root.SetActive(true); return; }
            var go = new GameObject("WindowAdjuster");
            go.transform.SetParent(canvas, false);
            _instance = go.AddComponent<WindowAdjuster>();
            _instance._font = font;
            _instance.Build();
        }

        public static void Hide()
        {
            if (_instance != null) _instance._root.SetActive(false);
        }

        private void Build()
        {
            _root = UiFactory.NewRect("WARoot", transform).gameObject;
            UiFactory.Stretch(_root.GetComponent<RectTransform>());

            // 遮罩
            var mask = UiFactory.NewRect("Mask", _root.transform);
            UiFactory.Stretch(mask);
            var maskImg = mask.gameObject.AddComponent<Image>();
            maskImg.color = new Color(0f, 0f, 0f, 0.5f);

            // 面板
            _panel = UiFactory.NewRect("Panel", _root.transform).gameObject;
            var prt = (RectTransform)_panel.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(600f, 700f);
            var pimg = _panel.AddComponent<Image>();
            pimg.sprite = UiFactory.RoundedSprite();
            pimg.type = Image.Type.Sliced;
            pimg.color = new Color(0.96f, 0.96f, 0.97f, 0.98f);

            // 标题
            var title = UiFactory.NewText(_panel.transform, "WINDOW POSITION ADJUSTER", 28, new Color(0.13f, 0.14f, 0.17f, 1f), _font);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, -24f);
            trt.sizeDelta = new Vector2(500f, 40f);
            title.alignment = TextAlignmentOptions.Center;

            // 说明
            var hint = UiFactory.NewText(_panel.transform, "Drag a window to move it. Click Save to persist positions.", 16, new Color(0.4f, 0.42f, 0.48f, 1f), _font);
            var hrt = (RectTransform)hint.transform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 1f);
            hrt.anchoredPosition = new Vector2(0f, -60f);
            hrt.sizeDelta = new Vector2(540f, 24f);
            hint.alignment = TextAlignmentOptions.Center;

            // 滚动区域
            var scrollGo = UiFactory.NewRect("Scroll", _panel.transform).gameObject;
            var srt = (RectTransform)scrollGo.transform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.offsetMin = new Vector2(20f, 80f);
            srt.offsetMax = new Vector2(-20f, -90f);
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            var vpImg = scrollGo.AddComponent<Image>();
            vpImg.color = new Color(0.9f, 0.92f, 0.95f, 0.5f);

            // 内容
            var contentGo = UiFactory.NewRect("Content", scrollGo.transform).gameObject;
            _content = (RectTransform)contentGo.transform;
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = new Vector2(0f, 400f);
            scroll.content = _content;
            scroll.viewport = srt;

            RebuildList();

            // 底部按钮
            Button saveBtn = BigBtn(_panel.transform, "SAVE POSITIONS", delegate () {
                WindowRegistry.Save();
                if (_instance != null) _instance._root.SetActive(false);
            });
            BigBtnSize(saveBtn, 240f, 56f);
            var sbrt = (RectTransform)saveBtn.transform;
            sbrt.anchorMin = sbrt.anchorMax = new Vector2(0.5f, 0f);
            sbrt.anchoredPosition = new Vector2(-130f, 20f);

            Button resetBtn = BigBtn(_panel.transform, "RESET ALL", delegate () {
                WindowRegistry.ResetAll();
                RebuildList();
            });
            BigBtnSize(resetBtn, 160f, 56f);
            var rbrt = (RectTransform)resetBtn.transform;
            rbrt.anchorMin = rbrt.anchorMax = new Vector2(0.5f, 0f);
            rbrt.anchoredPosition = new Vector2(80f, 20f);

            Button closeBtn = BigBtn(_panel.transform, "CLOSE", delegate () {
                if (_instance != null) _instance._root.SetActive(false);
            });
            BigBtnSize(closeBtn, 120f, 48f);
            var cbrt = (RectTransform)closeBtn.transform;
            cbrt.anchorMin = cbrt.anchorMax = new Vector2(1f, 1f);
            cbrt.anchoredPosition = new Vector2(-16f, -16f);
        }

        private void RebuildList()
        {
            // 清除旧内容
            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            var windows = WindowRegistry.GetAll();
            float y = 0f;
            float rowH = 64f;

            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                // 行背景
                var row = UiFactory.NewRect("Row_" + w.Id, _content).gameObject;
                var rrt = (RectTransform)row.transform;
                rrt.anchorMin = new Vector2(0f, 1f);
                rrt.anchorMax = new Vector2(1f, 1f);
                rrt.pivot = new Vector2(0.5f, 1f);
                rrt.anchoredPosition = new Vector2(0f, -y);
                rrt.sizeDelta = new Vector2(0f, rowH - 6f);
                var rowImg = row.AddComponent<Image>();
                rowImg.color = i % 2 == 0 ? new Color(1f, 1f, 1f, 0.6f) : new Color(0.92f, 0.94f, 0.97f, 0.6f);

                // 窗口名
                var name = UiFactory.NewText(row.transform, w.DisplayName, 20, new Color(0.13f, 0.14f, 0.17f, 1f), _font);
                var nrt = (RectTransform)name.transform;
                nrt.anchorMin = new Vector2(0f, 0f);
                nrt.anchorMax = new Vector2(1f, 1f);
                nrt.offsetMin = new Vector2(16f, 0f);
                nrt.offsetMax = new Vector2(-200f, 0f);
                name.alignment = TextAlignmentOptions.MidlineLeft;

                // 位置显示
                var pos = UiFactory.NewText(row.transform, "(" + w.CurrentPos.x.ToString("F0") + ", " + w.CurrentPos.y.ToString("F0") + ")", 14, new Color(0.4f, 0.42f, 0.48f, 1f), _font);
                var prt2 = (RectTransform)pos.transform;
                prt2.anchorMin = new Vector2(0f, 0f);
                prt2.anchorMax = new Vector2(1f, 1f);
                prt2.offsetMin = new Vector2(16f, -18f);
                prt2.offsetMax = new Vector2(-200f, 0f);
                pos.alignment = TextAlignmentOptions.BottomLeft;

                // 拖拽按钮
                string wid = w.Id;
                Button dragBtn = BigBtn(row.transform, "DRAG", delegate () {
                    _draggingId = wid;
                    var entry = WindowRegistry.Get(wid);
                    if (entry != null && entry.Root != null)
                    {
                        Vector2 lp;
                        RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            (RectTransform)entry.Root.transform.parent, Input.mousePosition, null, out lp);
                        _dragOffset = lp - entry.CurrentPos;
                    }
                });
                BigBtnSize(dragBtn, 80f, 36f);
                var dbrt = (RectTransform)dragBtn.transform;
                dbrt.anchorMin = dbrt.anchorMax = new Vector2(1f, 0.5f);
                dbrt.anchoredPosition = new Vector2(-100f, 0f);

                // 重置按钮
                Button resetBtn = BigBtn(row.transform, "RESET", delegate () {
                    WindowRegistry.ResetPosition(wid);
                    RebuildList();
                });
                BigBtnSize(resetBtn, 80f, 36f);
                var rbrt2 = (RectTransform)resetBtn.transform;
                rbrt2.anchorMin = rbrt2.anchorMax = new Vector2(1f, 0.5f);
                rbrt2.anchoredPosition = new Vector2(-12f, 0f);

                y += rowH;
            }

            _content.sizeDelta = new Vector2(0f, Mathf.Max(400f, y));
        }

        void Update()
        {
            // 拖拽窗口
            if (_draggingId != null && Input.GetMouseButton(0))
            {
                var entry = WindowRegistry.Get(_draggingId);
                if (entry != null && entry.Root != null)
                {
                    Vector2 lp;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        (RectTransform)entry.Root.transform.parent, Input.mousePosition, null, out lp);
                    WindowRegistry.SetPosition(_draggingId, lp - _dragOffset);
                    RebuildList();
                }
            }
            else if (_draggingId != null && Input.GetMouseButtonUp(0))
            {
                _draggingId = null;
            }
        }

        // 辅助方法（与 OnlineUI 一致）
        private Button BigBtn(Transform parent, string label, Action onClick)
        {
            return UiFactory.NewButton(parent, label, onClick, _font, true);
        }

        private void BigBtnSize(Button b, float w, float h)
        {
            var rt = (RectTransform)b.transform;
            rt.sizeDelta = new Vector2(w, h);
            var txt = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null) txt.fontSize = Mathf.Min(22, h * 0.45f);
        }
    }
}
