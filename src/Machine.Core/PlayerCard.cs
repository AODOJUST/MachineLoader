using System;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// 玩家主页明信片：屏幕靠右区域显示玩家名、ID 与游玩时间。
    /// 仅在 mod 模式（非纯净模式）下显示。简洁半透明风格。
    /// </summary>
    public class PlayerCard : MonoBehaviour
    {
        private MachineRuntime _rt;
        private GUIStyle _titleStyle;
        private GUIStyle _idStyle;
        private GUIStyle _subStyle;
        private GUIStyle _oidStyle;
        private Texture2D _bgTex;
        private Texture2D _borderTex;
        private bool _stylesInit;

        public void Init(MachineRuntime rt)
        {
            _rt = rt;
        }

        private void EnsureStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;
            _bgTex = MakeTex(1, 1, new Color(0.05f, 0.08f, 0.12f, 0.82f));
            _borderTex = MakeTex(1, 1, new Color(0.2f, 0.7f, 0.9f, 0.9f));
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.85f, 0.95f, 1f, 1f) }
            };
            _oidStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(1f, 0.84f, 0.2f, 1f) }
            };
            _idStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.5f, 0.6f, 0.7f, 1f) }
            };
            _subStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.65f, 0.8f, 0.9f, 1f) }
            };
        }

        private void OnGUI()
        {
            if (_rt == null) return;
            // 纯净模式不显示
            if (Machine.Mod.MachineState.PureMode) return;
            // 只在主菜单显示，进入游戏后不显示
            bool inMainMenu = false;
            try { inMainMenu = UnityEngine.Object.FindFirstObjectByType(typeof(MainMenu)) != null; } catch { }
            if (!inMainMenu) return;
            // 玩家名未锁定不显示
            var mem = _rt.Memory;
            if (mem == null || string.IsNullOrEmpty(mem.PlayerName)) return;

            EnsureStyles();

            float cardW = 210f;
            float cardH = 96f;
            float margin = 16f;
            float x = Screen.width - cardW - margin;
            float y = margin + 40f; // 避开顶部可能的其他 UI

            // 背景
            GUI.DrawTexture(new Rect(x, y, cardW, cardH), _bgTex);
            // 边框（左竖线作为装饰）
            GUI.DrawTexture(new Rect(x, y, 3f, cardH), _borderTex);

            float padX = x + 14f;
            float curY = y + 10f;

            // 玩家名
            GUI.Label(new Rect(padX, curY, cardW - 24f, 24f), mem.PlayerName, _titleStyle);
            curY += 24f;

            // OID（原始开发者，金色）
            if (mem.IsOriginalDeveloper)
            {
                GUI.Label(new Rect(padX, curY, cardW - 24f, 18f), "OID: " + mem.OID + "  (Original Developer)", _oidStyle);
                curY += 18f;
            }
            else if (mem.IsAdmin)
            {
                GUI.Label(new Rect(padX, curY, cardW - 24f, 18f), "RID: " + mem.RID + "  (Admin)", _oidStyle);
                curY += 18f;
            }

            // UID（灰色小字）
            if (!string.IsNullOrEmpty(mem.UID))
            {
                GUI.Label(new Rect(padX, curY, cardW - 24f, 16f), "UID: " + mem.UID, _idStyle);
                curY += 16f;
            }

            // 游玩时间
            GUI.Label(new Rect(padX, curY, cardW - 24f, 16f), "Playtime: " + mem.PlayTimeFormatted, _subStyle);
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
