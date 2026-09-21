using System;
using UnityEngine;
using UnityEngine.UI;
using Machine.Core;
using Machine.Mod;

namespace Example.AddButton
{
    /// <summary>
    /// Add Button 示例：在主菜单添加自定义按钮。
    ///
    /// 功能：
    /// 1. 在主菜单添加 "My Mod" 按钮
    /// 2. 点击按钮打开一个简单窗口
    /// 3. 演示 UI 创建和销毁
    /// </summary>
    public class AddButtonMod : MachineModBase
    {
        public override string Id { get { return "example.addbutton"; } }
        public override string Name { get { return "Add Button Example"; } }
        public override string Version { get { return "1.0.0"; } }

        private GameObject _window;

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("AddButton Mod loaded.");
        }

        public override void OnEnable()
        {
            base.OnEnable();

            // 在主菜单添加按钮
            if (Api != null)
            {
                Api.AddMainMenuButton("My Mod", OnMyButtonClicked);
                Log("Main menu button added.");
            }
        }

        private void OnMyButtonClicked()
        {
            Log("My Mod button clicked!");

            if (_window != null)
            {
                // 窗口已打开，关闭
                UnityEngine.Object.Destroy(_window);
                _window = null;
            }
            else
            {
                // 打开窗口
                CreateWindow();
            }
        }

        private void CreateWindow()
        {
            // 创建 Canvas
            var canvasObj = new GameObject("MyModWindow");
            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            // 创建背景面板
            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasObj.transform, false);
            var image = panel.AddComponent<Image>();
            image.color = new Color(0, 0, 0, 0.85f);
            var rect = panel.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(400, 200);
            rect.anchoredPosition = Vector2.zero;

            // 创建标题文本
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(panel.transform, false);
            var title = titleObj.AddComponent<Text>();
            title.text = "My Mod Window";
            title.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            title.fontSize = 24;
            title.alignment = TextAnchor.UpperCenter;
            title.color = Color.white;
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(380, 40);
            titleRect.anchoredPosition = new Vector2(0, -10);

            // 创建内容文本
            var contentObj = new GameObject("Content");
            contentObj.transform.SetParent(panel.transform, false);
            var content = contentObj.AddComponent<Text>();
            content.text = "Hello from My Mod!\n\nThis is a custom window.";
            content.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            content.fontSize = 18;
            content.alignment = TextAnchor.MiddleCenter;
            content.color = Color.white;
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(380, 100);
            contentRect.anchoredPosition = new Vector2(0, -50);

            // 创建关闭按钮
            var btnObj = new GameObject("CloseButton");
            btnObj.transform.SetParent(panel.transform, false);
            var btnImage = btnObj.AddComponent<Image>();
            btnImage.color = new Color(0.3f, 0.3f, 0.3f, 1f);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.sizeDelta = new Vector2(100, 35);
            btnRect.anchoredPosition = new Vector2(0, -150);
            var btn = btnObj.AddComponent<Button>();
            btn.onClick.AddListener(OnMyButtonClicked);

            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(btnObj.transform, false);
            var btnText = btnTextObj.AddComponent<Text>();
            btnText.text = "Close";
            btnText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            btnText.fontSize = 16;
            btnText.alignment = TextAnchor.MiddleCenter;
            btnText.color = Color.white;
            var btnTextRect = btnText.GetComponent<RectTransform>();
            btnTextRect.sizeDelta = new Vector2(90, 30);

            _window = canvasObj;
            Log("Window created.");
        }

        public override void OnDisable()
        {
            // 销毁窗口
            if (_window != null)
            {
                UnityEngine.Object.Destroy(_window);
                _window = null;
            }
            base.OnDisable();
        }
    }
}
