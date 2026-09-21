using System;
using UnityEngine;

namespace Machine.Mod
{
    /// <summary>
    /// 代码 Mod 接口。把实现了该接口的 DLL 放进 mods/&lt;Mod&gt;/code/ 并写入 mod.json 即可被 Machine 加载。
    /// </summary>
    public interface IMachineMod
    {
        /// <summary>Mod 唯一 ID（建议形如 author.modname）。</summary>
        string Id { get; }
        /// <summary>加载回调：注册内容、挂接事件、创建对象都在这里。</summary>
        void OnLoad(IMachineApi api);
    }

    /// <summary>Machine 提供给代码 Mod 的 API 面。</summary>
    public interface IMachineApi
    {
        /// <summary>写入 Machine 日志。</summary>
        void Log(string message);
        /// <summary>注册一种新货物（会出现在机场与任务里）。</summary>
        void RegisterCargo(CargoDefinition definition);
        /// <summary>注册一种新部件（会出现在建造部件栏）。</summary>
        void RegisterPart(PartDefinition definition);
        /// <summary>注册一个贴花材质（进飞机涂装面板）。</summary>
        void RegisterDecal(string decalName, byte[] pngData);
        /// <summary>把 PNG 字节解码成 Texture2D（供 Mod 自行使用）。</summary>
        Texture2D LoadTexture(string name, byte[] pngData);
        /// <summary>获取 mods 文件夹路径。</summary>
        string GetModsDirectory();
        /// <summary>在主菜单追加一个按钮。</summary>
        void AddMainMenuButton(string text, Action onClick);
        /// <summary>打开 Machine 的 Mod 管理器界面。</summary>
        void OpenModManager();
        /// <summary>按定义构建部件预制体（不自动注册，便于高级 Mod 二次定制）。</summary>
        GameObject CreatePartPrefab(PartDefinition definition);
    }

    /// <summary>
    /// 加载器运行时状态（供所有代码 Mod 查询）。
    /// PureMode = true 时所有 Mod 应休眠（不生成 AI、不渲染 HUD、不注入功能），
    /// 让通过游戏原生 Load / New Game / Sandbox 入口进入的玩家体验纯净原版。
    /// </summary>
    public static class MachineState
    {
        /// <summary>纯净模式：true = 只玩原版内容（mod 休眠）；false = mod 全功能。</summary>
        public static bool PureMode;
        /// <summary>本次进入游戏场景是否来自 Mod Saves 入口（Mod Saves 窗口加载/新建）。</summary>
        public static bool ViaModSaves;

        /// <summary>
        /// 玩家飞机是否处于"飞行模式"（起飞后）。
        /// false 覆盖：主菜单、飞机编辑器、停机坪地面、商店界面——这些场景不显示 mod HUD。
        /// </summary>
        ///
        /// <remarks>
        /// ★ 性能关键：本方法被所有 mod 的 Update/OnGUI 每帧调用（OnGUI 每帧 2~3 个事件各一次）。
        ///   游戏 Singleton&lt;T&gt;.Instance 在 m_Instance 为 null 时会走 Object.FindFirstObjectByType
        ///   全场景扫描（主菜单里 PlaneContainer 不存在 → Instance 永远是 null），单次 2~4ms。
        ///   实测（Radar 自计时探针，2026-09-13）主菜单里仅此一处就吃掉 ~10ms/帧。
        ///   缓存策略：true 时每帧重算（Instance 非空，访问便宜）；false 时缓存 5 帧
        ///   （进飞行后 HUD 最晚 5 帧出现，不可感知）。
        /// </remarks>
        private static int _ifUntilFrame = -1;
        private static bool _ifVal;

        public static bool InFlight()
        {
            int f = UnityEngine.Time.frameCount;
            if (f < _ifUntilFrame) return _ifVal;
            _ifVal = ComputeInFlight();
            _ifUntilFrame = f + (_ifVal ? 1 : 5);
            return _ifVal;
        }

        private static bool ComputeInFlight()
        {
            try
            {
                // 主菜单/启动场景：即使 PlaneContainer 残留也不视为飞行
                string sn = "";
                try { sn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                if (string.IsNullOrEmpty(sn)) return false;
                if (sn.IndexOf("Menu", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (sn.IndexOf("Boot", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (PlaneContainer.Instance == null) return false;
                return PlaneContainer.Instance.FlightModeInitialized;
            }
            catch { return false; }
        }
    }

    /// <summary>新货物定义。</summary>
    [Serializable]
    public class CargoDefinition
    {
        public string Id = "";
        public string Name = "";
        public float Price = 100f;
        public float Weight = 1f;
        public int CargoSpace = 1;
        public bool Fragile = false;
        public bool Expires = false;
        public float ExpirationTime = 60f;
        /// <summary>可选：货物图标 PNG 字节。</summary>
        public byte[] IconPng = null;
    }

    /// <summary>新部件定义（模型 + 数据）。</summary>
    [Serializable]
    public class PartDefinition
    {
        public string Id = "";
        public string Name = "";
        public float Price = 50f;
        public float Weight = 1f;
        public float Scale = 1f;
        /// <summary>可选：Wavefront OBJ 模型文本（UTF-8 字节）。</summary>
        public byte[] ModelObj = null;
        /// <summary>可选：部件贴图 PNG 字节。</summary>
        public byte[] TexturePng = null;
        /// <summary>部件行为类（默认 Machine.Core.MachinePart，继承自游戏 PlanePart）。</summary>
        public string PartClass = "Machine.Core.MachinePart";
    }
}
