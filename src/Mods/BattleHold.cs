// ---------------------------------------------------------------------------
// BattleHold (战斗仓库) - Machine 基础 Mod
// ---------------------------------------------------------------------------
// 背景
//   原版飞机的货仓由 CargoInventory 单例持有：一个 Dictionary<CargoType,int> currentCargo
//   加上 CurrentVolume / MaxVolume 两个体积量。货仓 UI 上的“清空货仓”按钮最终调用
//   CargoInventory.ClearInventory()，它会把 currentCargo 里的所有货物一次性清掉。
//   这对“战斗部件 / 弹药”这类不该被随手清掉的物资很危险。
//
// 本 Mod 的解法（不修改游戏程序集、不依赖 Harmony、纯运行时反射）
//   1) 战斗仓库把货物存在自己的字典 _combat 里，物理上不在 currentCargo 中，
//      因此 ClearInventory() 天然清不到它 —— 无需任何 IL 补丁或方法钩子。
//   2) 两者共享飞机的“总货仓”：
//        CargoInventory.Update() 每帧只用飞机重算 MaxVolume，不会重算 CurrentVolume；
//      于是本 Mod 每帧把 CurrentVolume 重设为 (原货仓占用 + 战斗仓库占用)。
//      -> 原版 AddCargo / EnoughSpace 的容量判断（CurrentVolume+cargoSpace<=MaxVolume）
//         自动把战斗仓库的体积算进去，两个仓库共享同一个 MaxVolume，超载会被正确拒绝。
//   3) 玩家在机场正常取货时，若该货物被打上“战斗”标记，会被本 Mod 从 currentCargo
//      中“搬”进战斗仓库（不重复计质量：AddCargo 已经 ChangeMass 过了）。
//
// 给后续 Mod 的接口（可直接引用本程序集，也可反射调用，缺失时请降级）
//   Machine.BattleHold.BattleHoldApi
//     Available / TotalCapacity / NormalVolume / CombatVolume / FreeSpace
//     MarkCombat(CargoType | string cargoName) / IsCombat(CargoType)
//     RegisterCombatCargo(api, CargoDefinition)      // 注册一种货物并标记为战斗货物
//     Count / Contains / CanAdd / Add / Remove / Clear / Snapshot
//     event Changed                                   // 内容或容量变化
//
// HUD 面板（v1.1）
//   - 默认停在屏幕右下角（压在游戏自带金额 HUD 上方），不再遮挡原版左上角按钮。
//   - 整个面板可用鼠标左键拖动（含四周边框）移动；位置、锚点写入 battlehold_config.json。
//   - 折叠按钮独立放在标题栏右侧，避免“点击折叠”和“拖动移动”互相打架。
//   - 开关面板的热键默认 1，可在 <设置 - 按键设置> 里改：
//     做法是克隆游戏原生 KeybindField 行、剥掉原生组件，改用本 Mod 的 rebind 流程
//     （不往游戏 InputActionAsset 里塞 action，避免运行时 actionId 变化导致存档失效）。
//
// 已知边界（v1.1）
//   - 战斗仓库的货物不写入游戏存档（游戏 Save/Load 只处理 currentCargo）；
//     本 Mod 用 mods/BattleHold/battlehold_state.json 自行持久化。
//   - 飞机被替换（PlaneContainer 实例变化）时战斗仓库会清空，避免把旧机物资带到新机。
//   - 战斗货物建议 expires=false：expires/fragile 的自动清仓逻辑只作用于 currentCargo，
//     战斗仓库中的货物不会被它们清除（这正是本 Mod 想要的效果，但请知悉语义差异）。
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Machine.Mod;
using Machine.Core;

namespace Machine.BattleHold
{
    /// <summary>Mod 入口。</summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.battlehold"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("BattleHold loading...");
            var go = new GameObject("Machine.BattleHold");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<BattleHoldSystem>().Init(api);
        }
    }

    /// <summary>
    /// 战斗仓库对外静态门面。后续 Mod 依赖它；提供方缺失时 Available 为 false，
    /// 调用方应据此降级（所有方法在未就绪时都安全返回默认值）。
    /// </summary>
    public static class BattleHoldApi
    {
        private static BattleHoldSystem _sys;

        internal static void Bind(BattleHoldSystem sys) { _sys = sys; }

        /// <summary>战斗仓库是否已加载并可用。</summary>
        public static bool Available { get { return _sys != null; } }

        /// <summary>当前 BattleHoldSystem 实例（供需要实例方法的调用方反射使用）。</summary>
        public static BattleHoldSystem System { get { return _sys; } }

        // ---- 容量（与原货仓共享同一个飞机总货仓） ----
        /// <summary>飞机总货仓容量（原货仓 + 战斗仓库 之和的上限）。</summary>
        public static float TotalCapacity { get { return _sys != null ? _sys.TotalCapacity : 0f; } }
        /// <summary>原货仓已占用体积。</summary>
        public static float NormalVolume { get { return _sys != null ? _sys.NormalVolume : 0f; } }
        /// <summary>战斗仓库已占用体积。</summary>
        public static float CombatVolume { get { return _sys != null ? _sys.CombatVolume : 0f; } }
        /// <summary>剩余可用体积。</summary>
        public static float FreeSpace { get { return _sys != null ? _sys.FreeSpace : 0f; } }

        // ---- 标记 / 查询 ----
        /// <summary>把某个货物类型标记为“战斗货物”（进入战斗仓库）。</summary>
        public static void MarkCombat(CargoType type) { if (_sys != null) _sys.MarkCombat(type); }
        /// <summary>按货物名标记（可在货物注册完成前调用，注册是延后的）。</summary>
        public static void MarkCombat(string cargoName) { if (_sys != null) _sys.MarkCombat(cargoName); }
        /// <summary>该货物类型是否属于战斗仓库。</summary>
        public static bool IsCombat(CargoType type) { return _sys != null && _sys.IsCombat(type); }

        /// <summary>注册一种货物并同时标记为战斗货物。返回 null 表示未就绪或注册失败。</summary>
        public static void RegisterCombatCargo(IMachineApi api, CargoDefinition def)
        {
            if (_sys != null) _sys.RegisterCombatCargo(api, def);
        }

        // ---- 内容操作 ----
        public static int Count(CargoType type) { return _sys != null ? _sys.Count(type) : 0; }
        public static bool Contains(CargoType type) { return _sys != null && _sys.Count(type) > 0; }
        /// <summary>是否放得下 n 个（考虑与原货仓共享的容量）。</summary>
        public static bool CanAdd(CargoType type, int n) { return _sys != null && _sys.CanAdd(type, n); }
        /// <summary>放入 n 个战斗货物；容量不足返回 false。</summary>
        public static bool Add(CargoType type, int n) { return _sys != null && _sys.TryAdd(type, n); }
        /// <summary>取出 n 个战斗货物（同步退还质量）。</summary>
        public static bool Remove(CargoType type, int n) { return _sys != null && _sys.TryRemove(type, n); }
        /// <summary>清空战斗仓库。</summary>
        public static void Clear() { if (_sys != null) _sys.ClearCombat(); }
        /// <summary>内容快照。</summary>
        public static List<KeyValuePair<CargoType, int>> Snapshot()
        {
            return _sys != null ? _sys.Snapshot() : new List<KeyValuePair<CargoType, int>>();
        }

        /// <summary>内容或占用发生变化时触发。</summary>
        public static event Action Changed;
        internal static void RaiseChanged() { Action h = Changed; if (h != null) h(); }
    }

    /// <summary>战斗仓库运行时（仓库本体 + 容量共享 + HUD + 自测）。</summary>
    public class BattleHoldSystem : MonoBehaviour
    {
        // ---------------- 配置 ----------------
        private bool cfgDemoCargo = true;      // 是否注册一个示例战斗货物（便于观察与测试）
        private bool cfgAutoTest = false;      // 启动后自动跑一次核心行为自测
        private bool cfgCaptureShots = false;  // 自测时截图
        private bool cfgShowPanel = true;
        private bool cfgPanelVisible = true;       // 面板是否可见（可被热键切换，并持久化）
        private string cfgAnchor = "bottom-right"; // top-left / top-right / bottom-left / bottom-right
        private float cfgPanelX = -20f;
        private float cfgPanelY = 96f;
        private bool cfgPersist = true;        // 战斗仓库内容写入 Mod 侧存档文件
        private string cfgToggleKey = "<Keyboard>/1";  // 开关面板的热键（设置界面里可改）
        private bool cfgInjectKeybind = true;  // 是否向游戏“设置-按键”里注入本 Mod 的按键行
        private bool cfgTestKeybind = false;   // 自测时额外验证按键行注入（会打开设置界面）
        private string cfgShotDir = "";
        private string cfgLanguage = "en";     // 界面/警报语言：en / zh / ru（俄语界面文字，音频回退英文）

        private const string DemoCargoName = "Ammo Crate";

        private IMachineApi _api;

        // ---------------- 反射（游戏侧 CargoInventory，保持零编译期耦合） ----------------
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private Type _ciType;
        private FieldInfo _fCurrentCargo;      // Dictionary<CargoType,int>
        private PropertyInfo _pCurrentVolume;  // getter public / setter private
        private PropertyInfo _pMaxVolume;      // getter public
        private MethodInfo _mSetCurrentVolume;
        private MethodInfo _mClearInventory;
        private MethodInfo _mAddCargo;         // AddCargo(CargoType)：游戏原版装货（自动写字典/容量/ChangeMass）
        private MethodInfo _mRemoveCargo;      // RemoveCargo(CargoType,int)：游戏原版卸货（自动减质量）
        private bool _ciResolved;

        private Component _ci;                 // CargoInventory 实例
        private PlaneContainer _plane;
        private int _planeInstanceId;

        // ---------------- 战斗仓库本体 ----------------
        private readonly Dictionary<CargoType, int> _combat = new Dictionary<CargoType, int>();
        private readonly HashSet<string> _combatNames = new HashSet<string>();

        private float _normalVol;
        private float _combatVol;
        private float _totalVol;

        // ---------------- 持久化（Mod 侧存档） ----------------
        private bool _stateDirty;
        private float _saveTimer;
        private int _restoredPlaneId;
        private bool _selfTestRunning;
        private int _shotIdx;

        // ---------------- HUD ----------------
        private Canvas _canvas;
        private RectTransform _panel;
        private Image _panelBg;
        private RectTransform _missileRow;
        private System.Collections.Generic.List<GameObject> _missileButtons =
            new System.Collections.Generic.List<GameObject>();
        private float _missileBtnTimer = 12f;   // 启动延迟 12 秒再刷新按钮（等全部 mod 反射热身，避免中文路径首次反射异常）
        private int _missileQueryFail;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _hint;
        private TextMeshProUGUI _body;
        private TextMeshProUGUI _collapseLabel;
        private Button _clearBtn;
        private Button _collapseBtn;
        private Button _langBtn;                    // 语言切换按钮（EN/中文/RU 循环）
        private TextMeshProUGUI _clearLabel;
        private TextMeshProUGUI _langLabel;
        private TMP_FontAsset _localizedFont;       // 动态本地化字体（微软雅黑，支持中/英/俄）
        private float _uiTimer;
        private bool _collapsed;
        private bool _dragging;

        // ---------------- 正文滚动区（弹种一多正文就会超长，必须能滚） ----------------
        private RectTransform _bodyView;      // 视口：挂 RectMask2D 做裁剪（本工程统一做法，不用 Mask）
        private RectTransform _bodyContent;   // 内容：正文文本挂在它下面，滚动 = 改它的 anchoredPosition.y
        private RectTransform _scrollTrack;   // 右侧滚动条轨道（正文不超长时整条隐藏）
        private Image _scrollHandle;          // 滑块
        private float _scrollPos;             // 当前滚动偏移（px，0 = 顶部）
        private float _scrollMax;             // 最大可滚动距离（<=0 = 不需要滚）
        private float _panelHeight = 316f;    // 当前面板高度（随正文长度自适应，不再写死）
        private int _missileRows = 1;         // 弹种按钮占了几行（决定正文底部要留多少）
        private string _missileSig = "";      // 弹种按钮签名：没变就不重建（原来是每 0.5s 销毁重建）
        private float _bodyContentH = 60f;    // 上量到的正文高度（滚动条要用）
        private float _bodyViewH = 60f;       // 正文视口高度
        private float _loggedPanelH = -1f;    // 布局诊断日志去重（只在尺寸真变了时打一条）
        private int _loggedRows = -1;

        // ---------------- 设置界面按键绑定 ----------------
        private Type _kbPanelType, _kbFieldType, _keyIconType;
        private bool _kbTypesResolved;
        private bool _kbInjected;
        private float _kbPoll = 0.5f;
        private GameObject _kbRowGo;
        private Component _kbIcon;
        private bool _rebinding;
        private float _rebindStart;
        private float _keyRetry;
        private UnityEngine.InputSystem.Controls.ButtonControl _toggleKey;

        // ---------------- 质量跟踪（观察日志用；质量本身已全部交还游戏管理） ----------------
        private float _flightMass;        // 飞行中记录的飞机质量（仅诊断日志）
        private bool _wasAirborne;
        private float _lastMassLog = -99f;
        private float _lastMassField = -1f;
        private float _lastJumpDecomp = -99f;

        private string ConfigPath
        {
            get { return Path.Combine(_api.GetModsDirectory(), "BattleHold", "battlehold_config.json"); }
        }

        // =====================================================================
        // 生命周期
        // =====================================================================
        public void Init(IMachineApi api)
        {
            _api = api;
            BattleHoldApi.Bind(this);
            LoadConfig();
            ResolveToggleKey();
            if (cfgDemoCargo) RegisterDemoCargo();
            if (cfgShowPanel) BuildUI();
            StartCoroutine(SyncLanguageLater());   // 启动后把语言同步给语音警报 mod
            api.Log("BattleHold: ready (shared-capacity combat hold active; toggleKey="
                    + cfgToggleKey + ", panel=" + (cfgPanelVisible ? "shown" : "hidden")
                    + ", anchor=" + cfgAnchor + ")");
            if (cfgAutoTest) StartCoroutine(SelfTest());
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "BattleHold", "battlehold_config.json");
                if (!File.Exists(path)) { _api.Log("BattleHold: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("BattleHold: bad config json"); return; }
                cfgDemoCargo = root.GetBool("demoCargo", cfgDemoCargo);
                cfgAutoTest = root.GetBool("autoTest", cfgAutoTest);
                cfgCaptureShots = root.GetBool("captureScreenshots", cfgCaptureShots);
                cfgShowPanel = root.GetBool("showPanel", cfgShowPanel);
                cfgPersist = root.GetBool("persistState", cfgPersist);
                cfgInjectKeybind = root.GetBool("injectKeybindRow", cfgInjectKeybind);
                cfgTestKeybind = root.GetBool("testKeybindRow", cfgTestKeybind);
                cfgPanelVisible = root.GetBool("panelVisible", cfgPanelVisible);
                cfgToggleKey = root.GetString("toggleKey", cfgToggleKey);
                cfgPanelX = (float)root.GetNumber("panelX", cfgPanelX);
                cfgPanelY = (float)root.GetNumber("panelY", cfgPanelY);
                cfgShotDir = root.GetString("shotDir", cfgShotDir);
                cfgLanguage = root.GetString("language", cfgLanguage);
                if (cfgLanguage != "zh" && cfgLanguage != "ru") cfgLanguage = "en";

                // 旧版配置没有 panelAnchor：说明面板还在左上角（会遮挡游戏按钮），迁移到新默认位置
                bool hasAnchor = root.Get("panelAnchor") != null;
                if (hasAnchor) cfgAnchor = root.GetString("panelAnchor", cfgAnchor);
                else
                {
                    cfgAnchor = "bottom-right";
                    cfgPanelX = -20f;
                    cfgPanelY = 96f;
                    SaveConfig();
                    _api.Log("BattleHold: legacy layout migrated -> bottom-right");
                }
                _api.Log("BattleHold: config loaded (demoCargo=" + cfgDemoCargo + ", autoTest=" + cfgAutoTest
                         + ", toggleKey=" + cfgToggleKey + ")");
            }
            catch (Exception e) { _api.Log("BattleHold: config error " + e.Message); }
        }

        /// <summary>把当前配置写回 battlehold_config.json（位置 / 热键 / 面板可见性会随操作变化）。</summary>
        private void SaveConfig()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                sb.Append("{\n");
                sb.Append("  \"demoCargo\": ").Append(cfgDemoCargo ? "true" : "false").Append(",\n");
                sb.Append("  \"autoTest\": ").Append(cfgAutoTest ? "true" : "false").Append(",\n");
                sb.Append("  \"captureScreenshots\": ").Append(cfgCaptureShots ? "true" : "false").Append(",\n");
                sb.Append("  \"testKeybindRow\": ").Append(cfgTestKeybind ? "true" : "false").Append(",\n");
                sb.Append("  \"showPanel\": ").Append(cfgShowPanel ? "true" : "false").Append(",\n");
                sb.Append("  \"injectKeybindRow\": ").Append(cfgInjectKeybind ? "true" : "false").Append(",\n");
                sb.Append("  \"panelVisible\": ").Append(cfgPanelVisible ? "true" : "false").Append(",\n");
                sb.Append("  \"persistState\": ").Append(cfgPersist ? "true" : "false").Append(",\n");
                sb.Append("  \"panelAnchor\": \"").Append(cfgAnchor).Append("\",\n");
                sb.Append("  \"panelX\": ").Append(cfgPanelX.ToString(ci)).Append(",\n");
                sb.Append("  \"panelY\": ").Append(cfgPanelY.ToString(ci)).Append(",\n");
                sb.Append("  \"toggleKey\": \"").Append(Esc(cfgToggleKey)).Append("\",\n");
                sb.Append("  \"shotDir\": \"").Append(Esc(cfgShotDir)).Append("\",\n");
                sb.Append("  \"language\": \"").Append(cfgLanguage).Append("\"\n");
                sb.Append("}\n");
                File.WriteAllText(ConfigPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { _api.Log("BattleHold: save config failed " + e.Message); }
        }

        private void OnDestroy()
        {
            BattleHoldApi.Bind(null);
        }

        // =====================================================================
        // 反射解析
        // =====================================================================
        private void ResolveCargoInventoryType()
        {
            if (_ciResolved) return;
            _ciResolved = true;
            try
            {
                // CargoInventory 与 PlaneContainer 同在 Assembly-CSharp；用 PlaneContainer 拿程序集
                // 再按名字取类型，避免依赖游戏类型的可见性。
                var asm = typeof(PlaneContainer).Assembly;
                _ciType = asm.GetType("CargoInventory");
                if (_ciType == null) { _api.Log("BattleHold: CargoInventory type not found"); return; }
                _fCurrentCargo = _ciType.GetField("currentCargo", BF);
                _pCurrentVolume = _ciType.GetProperty("CurrentVolume", BF);
                _pMaxVolume = _ciType.GetProperty("MaxVolume", BF);
                if (_pCurrentVolume != null) _mSetCurrentVolume = _pCurrentVolume.GetSetMethod(true);
                _mClearInventory = _ciType.GetMethod("ClearInventory", BF, null, Type.EmptyTypes, null);
                _mAddCargo = _ciType.GetMethod("AddCargo", BF, null, new Type[] { typeof(CargoType) }, null);
                _mRemoveCargo = _ciType.GetMethod("RemoveCargo", BF, null, new Type[] { typeof(CargoType), typeof(int) }, null);
                bool ok = _fCurrentCargo != null && _pCurrentVolume != null && _pMaxVolume != null
                          && _mAddCargo != null && _mRemoveCargo != null;
                _api.Log("BattleHold: CargoInventory resolved=" + ok
                         + " (field=" + (_fCurrentCargo != null)
                         + ", volProp=" + (_pCurrentVolume != null)
                         + ", addCargo=" + (_mAddCargo != null)
                         + ", removeCargo=" + (_mRemoveCargo != null) + ")");
            }
            catch (Exception e) { _api.Log("BattleHold: resolve failed " + e.Message); }
        }

        /// <summary>解析/刷新实例引用。返回是否可用。</summary>
        private bool ResolveRefs()
        {
            ResolveCargoInventoryType();
            if (_ciType == null) return false;

            if (_ci == null)
            {
                try { _ci = UnityEngine.Object.FindFirstObjectByType(_ciType) as Component; }
                catch { _ci = null; }
                if (_ci == null) return false;
            }
            if (_plane == null)
            {
                _plane = FindPlayerPlane();
                if (_plane != null && _plane.GetInstanceID() != _planeInstanceId)
                {
                    if (_planeInstanceId != 0)
                    {
                        // 换机：清空战斗仓库，避免旧机物资带到新机
                        if (_combat.Count > 0)
                        {
                            _combat.Clear();
                            _api.Log("BattleHold: plane replaced, combat hold cleared");
                            BattleHoldApi.RaiseChanged();
                        }
                    }
                    _planeInstanceId = _plane.GetInstanceID();
                }
            }
            return true;
        }

        /// <summary>解析玩家飞机（PlaneContainer.Instance 优先，回退排除 AI 标记的 PlaneContainer）。</summary>
        private PlaneContainer FindPlayerPlane()
        {
            try
            {
                var inst = PlaneContainer.Instance;
                if (inst != null && inst.GetComponent("AiEntityMarker") == null) return inst;
            }
            catch { }
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PlaneContainer>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (all[i].GetComponent("AiEntityMarker") != null) continue;
                    return all[i];
                }
            }
            catch { }
            return null;
        }

        private PlaneContainer Plane()
        {
            if (_plane == null) _plane = FindPlayerPlane();
            return _plane;
        }

        private IDictionary RawDict()
        {
            if (_ci == null || _fCurrentCargo == null) return null;
            try { return _fCurrentCargo.GetValue(_ci) as IDictionary; }
            catch { return null; }
        }

        private float GameCurrentVolume()
        {
            if (_ci == null || _pCurrentVolume == null) return 0f;
            try { object v = _pCurrentVolume.GetValue(_ci, null); return v == null ? 0f : Convert.ToSingle(v); }
            catch { return 0f; }
        }

        private float GameMaxVolume()
        {
            if (_ci == null || _pMaxVolume == null) return 0f;
            try { object v = _pMaxVolume.GetValue(_ci, null); return v == null ? 0f : Convert.ToSingle(v); }
            catch { return 0f; }
        }

        private void GameSetCurrentVolume(float v)
        {
            if (_ci == null || _mSetCurrentVolume == null) return;
            try { _mSetCurrentVolume.Invoke(_ci, new object[] { v }); }
            catch { }
        }

        // =====================================================================
        // 每帧同步：搬货 + 共享容量
        // =====================================================================
        private void Update()
        {
            // 主菜单/非游戏场景守卫：隐藏面板并跳过游戏内逻辑，进游戏后按配置恢复
            bool inGame = false;
            try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
            if (!inGame)
            {
                if (_canvas != null && _canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
                return;
            }
            // 纯净模式守卫：原版档隐藏战斗货仓面板
            if (Machine.Mod.MachineState.PureMode)
            {
                if (_canvas != null && _canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
                return;
            }
            // 编辑器/停机坪守卫：未进入飞行模式隐藏面板（起飞后按配置恢复）
            if (!Machine.Mod.MachineState.InFlight())
            {
                if (_canvas != null && _canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
                return;
            }
            if (_canvas != null && !_canvas.gameObject.activeSelf && cfgPanelVisible)
                _canvas.gameObject.SetActive(true);

            SyncCore();

            // 质量跟踪：飞行中记录质量（观察日志，质量本身由游戏管理）
            TrackMass();

            // 热键开关面板 / 设置界面按键行注入 / 正在等待重新绑键
            PollToggleKey();
            PollKeybindInjection();
            UpdateRebind();

            // Mod 侧存档：内容变化后延迟写盘，避免每帧 IO
            if (_stateDirty)
            {
                _saveTimer -= Time.unscaledDeltaTime;
                if (_saveTimer <= 0f) { _stateDirty = false; SaveState(); }
            }

            if (_canvas != null && !_dragging)
            {
                _uiTimer -= Time.unscaledDeltaTime;
                if (_uiTimer <= 0f) { _uiTimer = 0.25f; RefreshBody(); }
                _missileBtnTimer -= Time.unscaledDeltaTime;
                if (_missileBtnTimer <= 0f) { _missileBtnTimer = 0.5f; RefreshMissileButtons(); }
                PollBodyScroll();
            }
        }

        private void LateUpdate()
        {
            // LateUpdate 兜底：若本帧游戏清了货仓（回基地重置等），下一帧 SyncCore 的
            // 清单收敛会用游戏原版 AddCargo 把战斗货物补回（质量/容量由游戏自动记账）。
            SyncCore();
        }

        // ---------------- 质量架构（09-13 定稿：质量全部交还游戏管理） ----------------
        // 背景：此前三代方案（手写质量 → 台账对账 → 自重锚点）都在和游戏"抢"质量字段，
        // 实测存在未定位的外部写入者（~+1.7/事件）且会赢过我们的每帧纠回（HUD 显示它的值）。
        // IL 反汇编证明游戏 AddCargo/RemoveCargo 自带正确的质量/容量记账（weight/15），
        // 因此：战斗货仓字典只当"清单+存档"，所有装卸直接走游戏原版路径，我方绝不写质量。

        // 清单收敛（补回被游戏清掉的战斗货物）：检测到缺失后延迟 0.5s 补，失败按 5s 冷却重试
        private const float RESYNC_DELAY = 0.5f;
        private const float RESYNC_RETRY = 5f;
        private readonly Dictionary<CargoType, float> _resyncSince = new Dictionary<CargoType, float>();
        private readonly Dictionary<CargoType, float> _resyncRetryAt = new Dictionary<CargoType, float>();
        private float _lastResyncLog = -99f;

        // ---------------- 游戏原版装卸桥接（质量/容量全部由游戏记账） ----------------

        /// <summary>游戏货仓里某货物的当前数量（原版字典）。</summary>
        private int GameCargoCount(CargoType t)
        {
            IDictionary dict = RawDict();
            if (dict == null || t == null) return 0;
            try { if (!dict.Contains(t)) return 0; return Convert.ToInt32(dict[t]); }
            catch { return 0; }
        }

        /// <summary>
        /// 经游戏原版 AddCargo 装一件（写字典 + CurrentVolume + ChangeMass(weight)，即 weight/15 质量）。
        /// 注意：游戏带"机场已解锁"守卫，不在机场时静默拒绝——返回值按字典实增判断。
        /// </summary>
        private bool GameAddCargoOne(CargoType t)
        {
            if (_ci == null || _mAddCargo == null || t == null) return false;
            int before = GameCargoCount(t);
            try { _mAddCargo.Invoke(_ci, new object[] { t }); }
            catch (Exception e) { _api.Log("BattleHold: AddCargo invoke failed: " + e.Message); return false; }
            return GameCargoCount(t) == before + 1;
        }

        /// <summary>经游戏原版 RemoveCargo 卸货（自动按 weight/15 减质量；无位置守卫）。</summary>
        private void GameRemoveCargoGame(CargoType t, int n)
        {
            if (_ci == null || _mRemoveCargo == null || t == null || n <= 0) return;
            int have = GameCargoCount(t);
            if (have <= 0) return;   // 字典里已经没有（游戏清仓后），无需卸
            try { _mRemoveCargo.Invoke(_ci, new object[] { t, Mathf.Min(n, have) }); }
            catch (Exception e) { _api.Log("BattleHold: RemoveCargo invoke failed: " + e.Message); }
        }

        /// <summary>
        /// 直接通过反射设置 PlaneContainer.mass 字段（绕过 ChangeMass 的增益问题）。
        /// 同时修正 Rigidbody.mass。
        /// </summary>
        /// <summary>
        /// 质量观察日志（低频）：飞行中记录质量、降落检测、每 15s 打印质量/战斗货仓/原货仓。
        /// 注意：我方不再写质量——此处只用于观察游戏自身记账是否正确（诊断外部漂移）。
        /// </summary>
        private void TrackMass()
        {
            try
            {
                PlaneContainer pc = Plane();
                if (pc == null) return;
                if (_plane == null) return;

                // ---- 观察日志：我方不再写质量，仅记录游戏自身记账结果（诊断外部漂移用） ----
                float h = 0f;
                float spd = 0f;
                try { h = _plane.transform.position.y; spd = _plane.GetVelocityMagintude(); } catch { }
                bool grounded = h < 4f && spd < 6f;
                if (grounded)
                {
                    if (_wasAirborne)
                    {
                        _wasAirborne = false;
                        _api.Log("BattleHold: landing detected mass=" + pc.GetMass().ToString("F0")
                                 + " combatW=" + CombatWeight().ToString("F0"));
                    }
                }
                else
                {
                    _wasAirborne = true;
                    _flightMass = pc.GetMass();
                }

                if (Time.time - _lastMassLog > 15f)   // 诊断日志降频（5s→15s 减磁盘/锁开销）
                {
                    _api.Log("BattleHold: mass=" + pc.GetMass().ToString("F0")
                             + " combatW=" + CombatWeight().ToString("F0")
                             + " normalCargo=" + NormalCargoCount());
                    LogDecomp(pc, "");
                    _lastMassLog = Time.time;
                }

                // 跳变抓拍：mass 字段无装货操作时跳变 >0.05 -> 立即 dump 分解（5s 节流）
                try
                {
                    if (_fPcMassField != null)
                    {
                        float mf = Convert.ToSingle(_fPcMassField.GetValue(pc));
                        if (_lastMassField >= 0f && Mathf.Abs(mf - _lastMassField) > 0.05f
                            && Time.time - _lastJumpDecomp > 5f)
                        {
                            LogDecomp(pc, "JUMP");
                            _lastJumpDecomp = Time.time;
                        }
                        _lastMassField = mf;
                    }
                }
                catch { }
            }
            catch { }
        }

        // 游戏的质量换算（仅诊断/显示用）：CargoType.weight 是"游戏重量单位"，物理质量 = weight / 15。
        // 实测标定（2026-09-13 diag3）：PL-15 w=9 -> +0.600；Radar Mk5 w=5 -> +0.333；比值恒为 1/15。
        // 质量本身由游戏 AddCargo/RemoveCargo 自动记账，mod 侧不再写质量。
        private const float MASS_PER_WEIGHT = 1f / 15f;

        private FieldInfo _fPcMassField;

        /// <summary>
        /// 质量分解诊断（低频）：把 rb.mass 拆成 mass字段 + 油项 + 货物项 + 部件普查，
        /// 用于定位"重量持续增长"到底来自哪一项。游戏公式（IL 实证）：
        ///   FixedUpdate: rb.mass = mass + fuel * fuelWeight / 15
        ///   ReInitializePlane: mass = Σ(激活部件weight)/15 + GetCargoMass()/15
        /// </summary>
        private void LogDecomp(PlaneContainer pc, string tag)
        {
            try
            {
                if (_fPcMassField == null)
                    _fPcMassField = typeof(PlaneContainer).GetField("mass",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                float massField = -1f, fuel = -1f, fuelW = -1f;
                try { if (_fPcMassField != null) massField = Convert.ToSingle(_fPcMassField.GetValue(pc)); } catch { }
                try { fuel = pc.fuel; } catch { }
                try { fuelW = pc.fuelWeight; } catch { }
                float fuelTerm = (fuel >= 0f && fuelW >= 0f) ? fuel * fuelW / 15f : -1f;
                float rb = pc.GetMass();

                // 原货仓字典里非战斗货物的重量
                float normalW = 0f;
                IDictionary dict = RawDict();
                if (dict != null)
                    foreach (DictionaryEntry e in dict)
                    {
                        CargoType t = e.Key as CargoType;
                        if (t == null || IsCombat(t)) continue;
                        int n;
                        try { n = Convert.ToInt32(e.Value); } catch { continue; }
                        normalW += t.weight * n;
                    }

                // 部件普查：激活部件数/总重；foreign = 根节点不属于本机的外来部件（mod 挂的模型等）
                int partsAct = 0, partsAll = 0, foreign = 0;
                float partsW = 0f;
                string fRoots = "";
                PlanePart[] parts = pc.GetComponentsInChildren<PlanePart>(true);
                for (int i = 0; i < parts.Length; i++)
                {
                    PlanePart p = parts[i];
                    if (p == null) continue;
                    partsAll++;
                    if (!p.gameObject.activeInHierarchy) continue;
                    partsAct++;
                    partsW += p.weight;
                    try
                    {
                        if (p.transform.root != pc.transform.root)
                        {
                            foreign++;
                            if (fRoots.Length < 80) fRoots += p.transform.root.name + ";";
                        }
                    }
                    catch { }
                }

                bool singleMatch = false, ciPlaneMatch = false;
                try { singleMatch = PlaneContainer.Instance == pc; } catch { }
                try
                {
                    if (_ci != null)
                    {
                        // planeContainer 是 CargoInventory 的字段，反射比对是否指向本机
                        var fp = _ciType != null ? _ciType.GetField("planeContainer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                        if (fp != null) ciPlaneMatch = fp.GetValue(_ci) == pc;
                    }
                }
                catch { }

                _api.Log("BattleHold: decomp" + (tag.Length > 0 ? "[" + tag + "]" : "") + " rb=" + rb.ToString("F2")
                         + " massField=" + massField.ToString("F2")
                         + " fuel=" + fuel.ToString("F2") + " fuelW=" + fuelW.ToString("F2")
                         + " fuelTerm=" + fuelTerm.ToString("F2")
                         + " combatW=" + CombatWeight().ToString("F1") + " normalW=" + normalW.ToString("F1")
                         + " partsAct=" + partsAct + "/" + partsAll + " partsW=" + partsW.ToString("F2")
                         + " foreign=" + foreign + (fRoots.Length > 0 ? " [" + fRoots + "]" : "")
                         + " single=" + (singleMatch ? "Y" : "N") + " ciPlane=" + (ciPlaneMatch ? "Y" : "N"));
            }
            catch (Exception e) { _api.Log("BattleHold: decomp failed " + e.Message); }
        }

        /// <summary>战斗货仓总重量（游戏重量单位，非物理质量）。</summary>
        private float CombatWeight()
        {
            float w = 0f;
            foreach (KeyValuePair<CargoType, int> kv in _combat)
                if (kv.Key != null) w += kv.Key.weight * kv.Value;
            return w;
        }

        private int NormalCargoCount()
        {
            try
            {
                IDictionary dict = RawDict();
                return dict == null ? -1 : dict.Count;
            }
            catch { return -1; }
        }

        private void SyncCore()
        {
            if (!ResolveRefs()) return;
            IDictionary dict = RawDict();
            if (dict == null) return;

            // 0) 换机后从 Mod 侧存档恢复战斗仓库内容（每次换机只尝试一次）
            TryRestoreState();

            // 1) 收养：原货仓里的战斗货物并入清单（原版 UI 装入/游戏自加）。
            //    战斗货物现在就住在游戏字典里——游戏对它们的容量与质量记账天然正确。
            //    只向上收养（游戏数量 > 清单时），绝不清除游戏字典里的战斗货物。
            bool adopted = false;
            foreach (DictionaryEntry e in dict)
            {
                CargoType t = e.Key as CargoType;
                if (t == null || !IsCombat(t)) continue;
                int n;
                try { n = Convert.ToInt32(e.Value); } catch { continue; }
                int cur;
                _combat.TryGetValue(t, out cur);
                if (n > cur) { _combat[t] = n; adopted = true; }
            }
            if (adopted) { MarkDirty(); BattleHoldApi.RaiseChanged(); }

            // 2) 清单收敛：清单里有、游戏字典里没有（回基地重置/ClearInventory 等清仓事件）
            //    → 延迟 0.5s 经游戏原版 AddCargo 补回（质量/容量由游戏自动记账）；
            //    游戏拒绝（机场未解锁/容量不足）则按 5s 冷却重试。
            foreach (KeyValuePair<CargoType, int> kv in _combat)
            {
                if (kv.Key == null || kv.Value <= 0) continue;
                int have = GameCargoCount(kv.Key);
                if (have >= kv.Value)
                {
                    _resyncSince.Remove(kv.Key);
                    _resyncRetryAt.Remove(kv.Key);
                    continue;
                }

                float now = Time.time;
                float since;
                if (!_resyncSince.TryGetValue(kv.Key, out since)) { _resyncSince[kv.Key] = now; continue; }
                float retryAt;
                if (_resyncRetryAt.TryGetValue(kv.Key, out retryAt) && now < retryAt) continue;
                if (now - since < RESYNC_DELAY) continue;   // 宽限期：避开游戏清仓过程

                int missing = kv.Value - have;
                int added = 0;
                for (int i = 0; i < missing; i++) { if (GameAddCargoOne(kv.Key)) added++; else break; }

                if (Time.time - _lastResyncLog > 4f)
                {
                    _api.Log("BattleHold: resync via vanilla AddCargo '" + kv.Key.cargoName + "' x" + added
                             + "/" + missing + " (manifest=" + kv.Value + " gameHad=" + have + ")");
                    _lastResyncLog = Time.time;
                }
                if (added > 0) { MarkDirty(); BattleHoldApi.RaiseChanged(); }
                if (added < missing) _resyncRetryAt[kv.Key] = now + RESYNC_RETRY;
            }

            // 3) 容量统计（面板显示/共享容量判断用）：CurrentVolume 由游戏自己维护，不再强行改写
            float normal = SumNormalVolume(dict);
            float combat = SumVolume(_combat);
            float total = GameMaxVolume();

            if (!Mathf.Approximately(_normalVol, normal) || !Mathf.Approximately(_combatVol, combat))
                BattleHoldApi.RaiseChanged();

            _normalVol = normal;
            _combatVol = combat;
            _totalVol = total;
        }

        /// <summary>原货仓容量合计（跳过战斗货物——它们已在游戏字典里，单独由 _combat 统计）。</summary>
        private float SumNormalVolume(IDictionary dict)
        {
            if (dict == null) return 0f;
            float sum = 0f;
            foreach (DictionaryEntry e in dict)
            {
                CargoType t = e.Key as CargoType;
                if (t == null || IsCombat(t)) continue;
                int n;
                try { n = Convert.ToInt32(e.Value); } catch { continue; }
                sum += t.cargoSpace * n;
            }
            return sum;
        }

        private static float SumVolume(IDictionary dict)
        {
            if (dict == null) return 0f;
            float sum = 0f;
            foreach (DictionaryEntry e in dict)
            {
                CargoType t = e.Key as CargoType;
                if (t == null) continue;
                int n;
                try { n = Convert.ToInt32(e.Value); } catch { continue; }
                sum += t.cargoSpace * n;
            }
            return sum;
        }

        private static float SumVolume(Dictionary<CargoType, int> map)
        {
            float sum = 0f;
            foreach (KeyValuePair<CargoType, int> kv in map)
            {
                if (kv.Key == null) continue;
                sum += kv.Key.cargoSpace * kv.Value;
            }
            return sum;
        }

        // =====================================================================
        // 公开只读量
        // =====================================================================
        public float TotalCapacity { get { return _totalVol; } }
        public float NormalVolume { get { return _normalVol; } }
        public float CombatVolume { get { return _combatVol; } }
        public float FreeSpace { get { return Mathf.Max(0f, Mathf.RoundToInt(_totalVol) - _normalVol - _combatVol); } }

        // =====================================================================
        // 战斗货物标记
        // =====================================================================
        public void MarkCombat(CargoType type)
        {
            if (type == null) return;
            MarkCombat(type.cargoName);
        }

        public void MarkCombat(string cargoName)
        {
            string n = Norm(cargoName);
            if (n.Length == 0) return;
            if (_combatNames.Add(n)) _api.Log("BattleHold: combat cargo marked [" + cargoName + "]");
        }

        public bool IsCombat(CargoType type)
        {
            if (type == null) return false;
            return _combatNames.Contains(Norm(type.cargoName));
        }

        private static string Norm(string s)
        {
            return s == null ? "" : s.Trim().ToLowerInvariant();
        }

        /// <summary>注册一种货物并标记为战斗货物（注册由加载器延后执行，标记按名字立即生效）。</summary>
        public void RegisterCombatCargo(IMachineApi api, CargoDefinition def)
        {
            if (api == null || def == null) return;
            api.RegisterCargo(def);
            MarkCombat(def.Name);
        }

        private void RegisterDemoCargo()
        {
            var def = new CargoDefinition();
            def.Id = "battlehold.ammo";
            def.Name = DemoCargoName;
            def.Price = 260f;
            def.Weight = 3f;
            def.CargoSpace = 4;
            def.Fragile = false;
            def.Expires = false;
            RegisterCombatCargo(_api, def);
        }

        // =====================================================================
        // 战斗仓库内容操作
        // =====================================================================
        public int Count(CargoType type)
        {
            if (type == null) return 0;
            int n;
            return _combat.TryGetValue(type, out n) ? n : 0;
        }

        public bool CanAdd(CargoType type, int n)
        {
            if (type == null || n <= 0) return false;
            if (!ResolveRefs()) return false;
            float space = type.cargoSpace * n;
            float total = Mathf.RoundToInt(GameMaxVolume());
            return (_normalVol + _combatVol + space) <= total;
        }

        public bool TryAdd(CargoType type, int n)
        {
            if (type == null || n <= 0) return false;
            if (!ResolveRefs()) return false;
            if (!CanAdd(type, n))
            {
                _api.Log("BattleHold: add rejected (no shared space) "
                         + type.cargoName + " x" + n
                         + " used=" + (_normalVol + _combatVol) + "/" + Mathf.RoundToInt(GameMaxVolume()));
                return false;
            }

            // 质量与容量全部交由游戏原版 AddCargo 记账（自动 weight/15 + 字典 + CurrentVolume）
            int added = 0;
            for (int i = 0; i < n; i++) { if (GameAddCargoOne(type)) added++; else break; }

            // 清单跟随游戏字典的实际结果（游戏是唯一事实来源）
            int have = GameCargoCount(type);
            if (have > 0) _combat[type] = have; else _combat.Remove(type);

            if (added < n)
                _api.Log("BattleHold: game AddCargo accepted only " + added + "/" + n + " of "
                         + type.cargoName + " (airport unlocked? volume ok?)");

            SyncCore();
            BattleHoldApi.RaiseChanged();
            return added > 0;
        }

        public bool TryRemove(CargoType type, int n)
        {
            if (type == null || n <= 0) return false;
            int cur = Count(type);
            if (cur <= 0) return false;
            int rem = Mathf.Min(cur, n);

            // 质量与容量由游戏原版 RemoveCargo 记账（自动按 weight/15 减质量；无位置守卫，
            // 空中发射导弹也生效）
            GameRemoveCargoGame(type, rem);

            // 清单跟随游戏字典的实际结果
            int have = GameCargoCount(type);
            if (have > 0) _combat[type] = have; else _combat.Remove(type);

            SyncCore();
            BattleHoldApi.RaiseChanged();
            return true;
        }

        public void ClearCombat()
        {
            if (_combat.Count == 0) return;
            // 质量与容量由游戏原版 RemoveCargo 记账（游戏字典里没有时自动跳过）
            foreach (KeyValuePair<CargoType, int> kv in _combat)
                GameRemoveCargoGame(kv.Key, kv.Value);
            _combat.Clear();

            SyncCore();
            MarkDirty();
            BattleHoldApi.RaiseChanged();
        }

        public List<KeyValuePair<CargoType, int>> Snapshot()
        {
            var list = new List<KeyValuePair<CargoType, int>>();
            foreach (KeyValuePair<CargoType, int> kv in _combat) list.Add(kv);
            return list;
        }

        // =====================================================================
        // 持久化（Mod 侧存档文件，独立于游戏存档）
        // =====================================================================
        private string StatePath
        {
            get { return Path.Combine(_api.GetModsDirectory(), "BattleHold", "battlehold_state.json"); }
        }

        private void MarkDirty()
        {
            if (_selfTestRunning) return;   // 自测过程不写盘，避免污染玩家存档
            _stateDirty = true;
            _saveTimer = 1.5f;
        }

        private void SaveState()
        {
            if (!cfgPersist) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\n  \"capacity\": ").Append(Mathf.RoundToInt(_totalVol)).Append(",\n  \"items\": [\n");
                bool first = true;
                foreach (KeyValuePair<CargoType, int> kv in _combat)
                {
                    if (kv.Key == null || kv.Value <= 0) continue;
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("    { \"name\": \"").Append(Esc(kv.Key.cargoName)).Append("\", \"count\": ").Append(kv.Value).Append(" }");
                }
                sb.Append("\n  ]\n}\n");
                File.WriteAllText(StatePath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { _api.Log("BattleHold: save state failed " + e.Message); }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>换机后恢复一次战斗仓库内容；容量不一致（视为换了另一架飞机）则跳过。</summary>
        private void TryRestoreState()
        {
            if (!cfgPersist) return;
            if (_plane == null || _ci == null) return;
            if (_restoredPlaneId == _planeInstanceId) return;
            _restoredPlaneId = _planeInstanceId;
            try
            {
                if (!File.Exists(StatePath)) return;
                JsonValue root = JsonValue.Parse(File.ReadAllText(StatePath));
                if (root == null) return;

                float cap = (float)root.GetNumber("capacity", -1d);
                float cur = Mathf.RoundToInt(GameMaxVolume());
                if (cap >= 0f && Mathf.Abs(cap - cur) > 0.5f)
                {
                    _api.Log("BattleHold: state capacity " + cap + " != plane " + cur + " -> restore skipped");
                    return;
                }

                JsonValue items = root.Get("items");
                if (items == null || items.Arr == null) return;

                int restored = 0;
                for (int i = 0; i < items.Arr.Count; i++)
                {
                    JsonValue it = items.Arr[i];
                    if (it == null) continue;
                    string name = it.GetString("name", "");
                    int n = (int)it.GetNumber("count", 0d);
                    if (name.Length == 0 || n <= 0) continue;

                    CargoType t = FindCargoByName(name);
                    if (t == null) { _api.Log("BattleHold: restore skip unknown cargo '" + name + "'"); continue; }
                    if (!IsCombat(t)) MarkCombat(name);

                    float space = t.cargoSpace;
                    float room = Mathf.RoundToInt(GameMaxVolume()) - (_normalVol + _combatVol);
                    int fit = n;
                    if (space > 0f) fit = Mathf.Min(n, Mathf.Max(0, Mathf.FloorToInt(room / space)));
                    if (fit <= 0) continue;

                    int curN;
                    _combat.TryGetValue(t, out curN);
                    _combat[t] = curN + fit;

                    // 质量由清单收敛机制经游戏原版 AddCargo 自动记账（SyncCore 步骤 2）
                    restored += fit;
                }

                if (restored > 0)
                {
                    _api.Log("BattleHold: restored " + restored + " combat item(s) from state");
                    BattleHoldApi.RaiseChanged();
                }
            }
            catch (Exception e) { _api.Log("BattleHold: restore failed " + e.Message); }
        }

        // =====================================================================
        // 本地化（语言）：中 / 英 / 俄 —— 界面文字翻译 + 与语音警报 mod 联动
        // =====================================================================
        private string T(string en, string zh, string ru)
        {
            if (cfgLanguage == "zh") return zh;
            if (cfgLanguage == "ru") return ru;
            return en;
        }

        private string LangLabel()
        {
            return cfgLanguage == "zh" ? "中文" : (cfgLanguage == "ru" ? "RU" : "EN");
        }

        /// <summary>动态字体（微软雅黑）：支持中文 / 英文 / 俄文（含西里尔字形）；失败回退游戏字体。</summary>
        private TMP_FontAsset HarvestLocalizedFont()
        {
            if (_localizedFont != null) return _localizedFont;
            try
            {
                // 用 OS 字体名创建动态 TMP 资产：运行时按需生成字形（中文/俄文/英文）
                _localizedFont = TMP_FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 28);
                if (_localizedFont != null) return _localizedFont;
            }
            catch (Exception e) { _api.Log("BattleHold: dynamic font failed " + e.Message); }
            return UiFactory.HarvestFont();
        }

        /// <summary>循环切换语言 en -> zh -> ru -> en，并同步语音警报 mod 的警报语言。</summary>
        private void CycleLanguage()
        {
            if (cfgLanguage == "en") cfgLanguage = "zh";
            else if (cfgLanguage == "zh") cfgLanguage = "ru";
            else cfgLanguage = "en";
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            // 通知语音警报 mod（反射，避免硬依赖）；未加载时静默跳过，启动 2 秒后还会再同步一次
            try
            {
                var t = FindVoiceAlertsApiType();
                if (t != null)
                {
                    var m = t.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(string) }, null);
                    if (m != null) m.Invoke(null, new object[] { cfgLanguage });
                    _api.Log("BattleHold: VoiceAlerts language synced -> " + cfgLanguage);
                }
                else
                {
                    _api.Log("BattleHold: VoiceAlertsApi type not found, language sync skipped");
                }
            }
            catch (Exception e) { _api.Log("BattleHold: language sync error " + e.Message); }
            SaveConfig();
            if (_title != null) _title.text = T("Battle Hold", "战斗仓库", "БОЕВОЙ ОТСЕК");
            if (_clearLabel != null) _clearLabel.text = T("Clear Combat Hold", "清空战斗货仓", "Очистить отсек");
            if (_langLabel != null) _langLabel.text = LangLabel();
            RefreshBody();
            _api.Log("BattleHold: language -> " + cfgLanguage);
        }

        /// <summary>遍历所有程序集查找 VoiceAlertsApi 类型（Type.GetType 在程序集未加载时会失败）。</summary>
        private static Type FindVoiceAlertsApiType()
        {
            var t = Type.GetType("VoiceAlertsMod.VoiceAlertsApi, VoiceAlerts");
            if (t != null) return t;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name == "VoiceAlerts")
                    {
                        t = asm.GetType("VoiceAlertsMod.VoiceAlertsApi");
                        if (t != null) return t;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>启动延迟同步语言到语音警报 mod（等其协程加载完成）。</summary>
        private IEnumerator SyncLanguageLater()
        {
            yield return new WaitForSeconds(2f);
            try
            {
                var t = FindVoiceAlertsApiType();
                if (t != null)
                {
                    var m = t.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(string) }, null);
                    if (m != null) m.Invoke(null, new object[] { cfgLanguage });
                    _api.Log("BattleHold: delayed language sync -> " + cfgLanguage);
                }
            }
            catch (Exception e) { _api.Log("BattleHold: delayed language sync error " + e.Message); }
        }

        // =====================================================================
        // HUD
        // =====================================================================
        private void BuildUI()
        {
            try
            {
                TMP_FontAsset font = HarvestLocalizedFont();

                // GraphicRaycaster 必须挂上，否则面板收不到鼠标点击/拖动事件
                var canvasGo = new GameObject("Machine.BattleHoldCanvas", typeof(Canvas), typeof(GraphicRaycaster));
                _canvas = canvasGo.GetComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 19010;
                canvasGo.transform.SetParent(transform, false);

                _panel = UiFactory.NewRect("Panel", canvasGo.transform);

                _panelBg = _panel.gameObject.AddComponent<Image>();
                _panelBg.sprite = UiFactory.RoundedSprite();
                _panelBg.type = Image.Type.Sliced;
                _panelBg.color = new Color(0.96f, 0.96f, 0.97f, 0.92f);
                _panelBg.raycastTarget = true;   // 必须为 true：整块面板（含背景）才能接收鼠标拖动事件

                // 拖动：整块面板（含四周边框）按住鼠标左键即可移动窗口
                var drag = _panel.gameObject.AddComponent<PanelDragHandle>();
                drag.Target = _panel;
                drag.OnMoved = OnPanelMoved;
                drag.OnFinished = OnPanelDragEnd;

                // 标题（拖动区域，不接收点击；折叠改由右侧按钮负责）
                _title = UiFactory.NewText(_panel, T("Battle Hold", "战斗仓库", "БОЕВОЙ ОТСЕК"), 16, new Color(0.13f, 0.14f, 0.17f, 1f), font);
                var titleRt = (RectTransform)_title.transform;
                titleRt.anchorMin = new Vector2(0f, 1f);
                titleRt.anchorMax = new Vector2(1f, 1f);
                titleRt.pivot = new Vector2(0.5f, 1f);
                titleRt.offsetMin = new Vector2(14f, -30f);
                titleRt.offsetMax = new Vector2(-104f, -6f);
                _title.alignment = TextAlignmentOptions.MidlineLeft;
                _title.raycastTarget = false;

                // 标题右侧：当前热键提示，例如 [1]
                _hint = UiFactory.NewText(_panel, "", 11, new Color(0.45f, 0.46f, 0.52f, 1f), font);
                var hintRt = (RectTransform)_hint.transform;
                hintRt.anchorMin = new Vector2(1f, 1f);
                hintRt.anchorMax = new Vector2(1f, 1f);
                hintRt.pivot = new Vector2(1f, 1f);
                hintRt.anchoredPosition = new Vector2(-38f, -6f);
                hintRt.sizeDelta = new Vector2(62f, 22f);
                _hint.alignment = TextAlignmentOptions.MidlineRight;
                _hint.raycastTarget = false;

                // 折叠 / 展开
                _collapseBtn = UiFactory.NewButton(_panel, "v", delegate () { SetCollapsed(!_collapsed); }, font);
                var cbRt = (RectTransform)_collapseBtn.transform;
                cbRt.anchorMin = new Vector2(1f, 1f);
                cbRt.anchorMax = new Vector2(1f, 1f);
                cbRt.pivot = new Vector2(1f, 1f);
                cbRt.anchoredPosition = new Vector2(-8f, -4f);
                cbRt.sizeDelta = new Vector2(26f, 24f);
                _collapseLabel = _collapseBtn.GetComponentInChildren<TextMeshProUGUI>(true);

                // 正文：视口（RectMask2D 裁剪）+ 内容（可上下滚）
                // 以前正文是一整块 TMP 文本，垂直溢出模式是 Overflow —— 弹种一多文字直接画到窗口外面，
                // 还会压住底部按钮。现在正文被视口裁住，超出的部分用滚轮看。
                _bodyView = UiFactory.NewRect("BodyView", _panel);
                _bodyView.anchorMin = new Vector2(0f, 0f);
                _bodyView.anchorMax = new Vector2(1f, 1f);
                _bodyView.pivot = new Vector2(0.5f, 0.5f);
                _bodyView.offsetMin = new Vector2(14f, BodyBottomInset(1));
                _bodyView.offsetMax = new Vector2(-24f, -36f);   // 右边 24px 留给滚动条
                _bodyView.gameObject.AddComponent<RectMask2D>();

                _bodyContent = UiFactory.NewRect("BodyContent", _bodyView);
                _bodyContent.anchorMin = new Vector2(0f, 1f);
                _bodyContent.anchorMax = new Vector2(1f, 1f);
                _bodyContent.pivot = new Vector2(0.5f, 1f);
                _bodyContent.anchoredPosition = Vector2.zero;
                _bodyContent.sizeDelta = new Vector2(0f, 120f);

                _body = UiFactory.NewText(_bodyContent, "", 13, new Color(0.25f, 0.26f, 0.30f, 1f), font);
                var bodyRt = (RectTransform)_body.transform;
                bodyRt.anchorMin = new Vector2(0f, 1f);
                bodyRt.anchorMax = new Vector2(1f, 1f);
                bodyRt.pivot = new Vector2(0.5f, 1f);
                bodyRt.anchoredPosition = Vector2.zero;
                bodyRt.sizeDelta = new Vector2(0f, 120f);
                _body.alignment = TextAlignmentOptions.TopLeft;
                try { _body.textWrappingMode = TextWrappingModes.Normal; } catch { }
                _body.richText = true;
                _body.raycastTarget = false;

                // 滚动条（轨道 + 滑块）：只有正文超长时才显示，纯指示 + 可拖动范围由滚轮负责
                _scrollTrack = UiFactory.NewRect("ScrollTrack", _panel);
                _scrollTrack.anchorMin = new Vector2(1f, 0f);
                _scrollTrack.anchorMax = new Vector2(1f, 1f);
                _scrollTrack.pivot = new Vector2(1f, 0.5f);
                _scrollTrack.offsetMin = new Vector2(-19f, BodyBottomInset(1));
                _scrollTrack.offsetMax = new Vector2(-12f, -36f);
                var trackImg = _scrollTrack.gameObject.AddComponent<Image>();
                trackImg.sprite = UiFactory.RoundedSprite();
                trackImg.type = Image.Type.Sliced;
                trackImg.color = new Color(0.10f, 0.11f, 0.14f, 0.10f);
                trackImg.raycastTarget = false;

                var handleRt = UiFactory.NewRect("ScrollHandle", _scrollTrack);
                handleRt.anchorMin = new Vector2(0f, 1f);
                handleRt.anchorMax = new Vector2(1f, 1f);
                handleRt.pivot = new Vector2(0.5f, 1f);
                handleRt.sizeDelta = new Vector2(0f, 24f);
                handleRt.anchoredPosition = Vector2.zero;
                _scrollHandle = handleRt.gameObject.AddComponent<Image>();
                _scrollHandle.sprite = UiFactory.RoundedSprite();
                _scrollHandle.type = Image.Type.Sliced;
                _scrollHandle.color = new Color(0.30f, 0.32f, 0.38f, 0.55f);
                _scrollHandle.raycastTarget = false;

                // 导弹仓按钮行：鼠标左键点击弹种直接选中（在 Clear 按钮上方）
                // 宽度不够时自动换行，不再往窗口右边溢出。
                _missileRow = UiFactory.NewRect("MissileRow", _panel);
                var mrRt = (RectTransform)_missileRow.transform;
                mrRt.anchorMin = new Vector2(0f, 0f);
                mrRt.anchorMax = new Vector2(1f, 0f);
                mrRt.pivot = new Vector2(0.5f, 0f);
                mrRt.offsetMin = new Vector2(14f, 50f);
                mrRt.offsetMax = new Vector2(-14f, 50f + MissileRowHeight(1));

                // 底部按钮：清空战斗仓库（左半边）
                _clearBtn = UiFactory.NewButton(_panel, T("Clear Combat Hold", "清空战斗货仓", "Очистить отсек"),
                    delegate () { ClearCombat(); RefreshBody(); }, font);
                _clearLabel = _clearBtn.GetComponentInChildren<TextMeshProUGUI>(true);
                var btnRt = (RectTransform)_clearBtn.transform;
                btnRt.anchorMin = new Vector2(0f, 0f);
                btnRt.anchorMax = new Vector2(0.52f, 0f);
                btnRt.pivot = new Vector2(0.5f, 0f);
                btnRt.offsetMin = new Vector2(14f, 10f);
                btnRt.offsetMax = new Vector2(-8f, 44f);

                // 底部右侧：语言切换按钮（EN / 中文 / RU 循环），切换战斗部与语音警报的语言
                _langBtn = UiFactory.NewButton(_panel, LangLabel(), delegate () { CycleLanguage(); }, font);
                _langLabel = _langBtn.GetComponentInChildren<TextMeshProUGUI>(true);
                var langRt = (RectTransform)_langBtn.transform;
                langRt.anchorMin = new Vector2(0.55f, 0f);
                langRt.anchorMax = new Vector2(1f, 0f);
                langRt.pivot = new Vector2(0.5f, 0f);
                langRt.offsetMin = new Vector2(8f, 10f);
                langRt.offsetMax = new Vector2(-14f, 44f);

                ApplyLayout();
                canvasGo.SetActive(cfgPanelVisible);
                _api.Log("BattleHold: UI built (anchor=" + cfgAnchor
                         + " pos=" + cfgPanelX + "," + cfgPanelY
                         + " visible=" + cfgPanelVisible + ")");
            }
            catch (Exception e) { _api.Log("BattleHold: build UI failed " + e.Message); }
        }

        /// <summary>把面板放到配置里的锚点/偏移上（锚点固定为角，pivot 与锚点一致）。</summary>
        private void ApplyAnchor()
        {
            if (_panel == null) return;
            Vector2 anchor, pivot;
            AnchorOf(cfgAnchor, out anchor, out pivot);
            _panel.anchorMin = anchor;
            _panel.anchorMax = anchor;
            _panel.pivot = pivot;
            _panel.anchoredPosition = new Vector2(cfgPanelX, cfgPanelY);
            _panel.sizeDelta = new Vector2(PanelWidth, _collapsed ? CollapsedHeight : _panelHeight);
        }

        private const float PanelWidth = 340f;
        // 面板高度不再写死：正文短就矮一点（不浪费屏幕），正文长就长到上限，超出的部分滚轮看。
        private const float MinPanelHeight = 150f;
        private const float MaxPanelHeight = 420f;
        private const float MinBodyHeight = 60f;
        // 战斗货仓物品行的字号（正文是 13）。明显放大，让导弹数量一眼能看清。
        private const int CombatItemFontSize = 26;
        private const float CollapsedHeight = 34f;

        // ---- 布局几何（正文下面的"固定装饰"高度，弹种按钮占几行会变） ----
        private const float BodyTopInset = 36f;     // 标题栏
        private const float MissileRowTop = 50f;    // 弹种行下沿距面板底
        private const float MissileRowStep = 30f;   // 每行 24 高 + 6 间距
        private const float BodyGapToRow = 8f;      // 正文与弹种行之间的间隙

        /// <summary>n 行弹种按钮占的高度（24 高 + 6 间距）。</summary>
        private static float MissileRowHeight(int rows)
        {
            if (rows < 1) rows = 1;
            return rows * 24f + (rows - 1) * (MissileRowStep - 24f);
        }

        /// <summary>正文视口下沿距面板底的距离（弹种行 + 间隙）。</summary>
        private static float BodyBottomInset(int rows)
        {
            return MissileRowTop + MissileRowHeight(rows) + BodyGapToRow;
        }

        private static void AnchorOf(string name, out Vector2 anchor, out Vector2 pivot)
        {
            switch (name)
            {
                case "top-left": anchor = new Vector2(0f, 1f); pivot = new Vector2(0f, 1f); return;
                case "top-right": anchor = new Vector2(1f, 1f); pivot = new Vector2(1f, 1f); return;
                case "bottom-left": anchor = new Vector2(0f, 0f); pivot = new Vector2(0f, 0f); return;
                default: anchor = new Vector2(1f, 0f); pivot = new Vector2(1f, 0f); return;
            }
        }

        private void OnPanelMoved(Vector2 anchored)
        {
            _dragging = true;
            cfgPanelX = anchored.x;
            cfgPanelY = anchored.y;
        }

        private void OnPanelDragEnd()
        {
            _dragging = false;
            SaveConfig();   // 记住玩家摆好的位置
            _api.Log("BattleHold: panel moved -> " + cfgPanelX + "," + cfgPanelY + " (anchor " + cfgAnchor + ")");
        }

        /// <summary>热键开关面板。</summary>
        public void TogglePanel() { SetPanelVisible(!cfgPanelVisible); }

        public void SetPanelVisible(bool visible)
        {
            cfgPanelVisible = visible;
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
            SaveConfig();
            _api.Log("BattleHold: panel " + (visible ? "shown" : "hidden"));
        }

        private void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            if (_panel == null) return;
            ApplyAnchor();
            // 注意：这里原来写死成英文，折叠/展开一次就会把中文/俄文标题冲掉
            if (_title != null) _title.text = T("Battle Hold", "战斗仓库", "БОЕВОЙ ОТСЕК");
            if (_hint != null)
            {
                _hint.gameObject.SetActive(!_collapsed);
                _hint.text = UiFactory.Safe("[" + ToggleKeyName() + "]");
            }
            if (_collapseLabel != null) _collapseLabel.text = _collapsed ? ">" : "v";
            if (_body != null) _body.gameObject.SetActive(!_collapsed);
            if (_bodyView != null) _bodyView.gameObject.SetActive(!_collapsed);
            if (_scrollTrack != null) _scrollTrack.gameObject.SetActive(!_collapsed && _scrollMax > 1f);
            if (_missileRow != null) _missileRow.gameObject.SetActive(!_collapsed);
            if (_clearBtn != null) _clearBtn.gameObject.SetActive(!_collapsed);
            RefreshMissileButtons();
            RefreshBody();
        }

        private void RefreshBody()
        {
            if (_body == null || _collapsed) return;
            var sb = new System.Text.StringBuilder();

            if (_ci == null || _plane == null)
            {
                sb.Append("<color=#9a9ba3>").Append(T("No active plane / hold.", "没有活动的飞机 / 货仓。", "Нет активного самолёта / отсека.")).Append("</color>");
                ApplyBodyText(sb.ToString());
                return;
            }

            int total = Mathf.RoundToInt(_totalVol);
            int used = Mathf.RoundToInt(_normalVol + _combatVol);
            // 容量原来是 4 行（占了 68px 且右半边全空），压成 2 行：一行总量、一行三项明细
            sb.Append("<color=#45464d>").Append(T("Shared hold: ", "共享容量：", "Общий объём: ")).Append(used).Append(" / ").Append(total).Append("</color>\n");
            sb.Append("<color=#45464d>").Append(T("Normal: ", "普通: ", "Обычный: ")).Append(Mathf.RoundToInt(_normalVol)).Append("   </color>")
              .Append("<color=#2f6fd0>").Append(T("Combat: ", "战斗: ", "Боевой: ")).Append(Mathf.RoundToInt(_combatVol)).Append("   </color>")
              .Append("<color=#45464d>").Append(T("Free: ", "剩余: ", "Свободно: ")).Append(Mathf.RoundToInt(FreeSpace)).Append("</color>\n");
            sb.Append("\n");

            // 导弹仓（MachineAAM）：只显示玩家装备的弹种，当前选中（R 键切换）金色高亮
            string mis = QueryMissileInventory();
            if (mis.Length > 0)
            {
                sb.Append("<color=#45464d>").Append(T("Missiles (R to switch):", "导弹（R 切换）：", "Ракеты (R для смены):")).Append("</color>\n").Append(mis).Append("\n");
            }

            if (_combat.Count == 0)
            {
                sb.Append("<color=#9a9ba3>")
                  .Append(T("Combat hold empty.\nMarked cargo picked up at an airport stays here when the cargo hold is cleared.",
                            "战斗货仓为空。\n在机场拾取的标记货物在清空普通货仓时会保留在这里。",
                            "Боевой отсек пуст.\nОтмеченный груз останется здесь при очистке обычного отсека."))
                  .Append("</color>");
            }
            else
            {
                sb.Append("<color=#2f6fd0>").Append(T("Items (protected from Clear):", "物品（清空保护）：", "Предметы (защищены от очистки):")).Append("</color>\n");
                foreach (KeyValuePair<CargoType, int> kv in _combat)
                {
                    if (kv.Key == null) continue;
                    // 类型 + 数量用大字显示：这是战斗货仓里最要紧的一条信息
                    sb.Append("<size=").Append(CombatItemFontSize).Append("><b>  ")
                      .Append(UiFactory.Safe(kv.Key.cargoName))
                      .Append(" x").Append(kv.Value)
                      .Append("</b></size>\n");
                }
            }
            sb.Append("\n<color=#9a9ba3>")
              .Append(T("Drag with LMB to move - ", "左键拖动移动 - ", "ЛКМ — перемещение; "))
              .Append(UiFactory.Safe("[" + ToggleKeyName() + "]"))
              .Append(T(" hides this panel.", " 隐藏面板。", " — скрыть панель."))
              .Append("</color>");
            ApplyBodyText(sb.ToString());
        }

        /// <summary>
        /// 写入正文并按内容高度自适应面板：内容装得下就把窗口收矮（不留一大片空白），
        /// 装不下就固定到上限 + 启用滚轮，文字再也不会画出窗口外。
        /// </summary>
        private void ApplyBodyText(string text)
        {
            if (_body == null) return;
            _body.text = text;

            float contentH = 0f;
            try { contentH = _body.preferredHeight; } catch { }
            if (contentH < 8f) contentH = _body.fontSize * 2f;

            int rows = _missileRows < 1 ? 1 : _missileRows;
            float bottom = BodyBottomInset(rows);
            float chrome = BodyTopInset + bottom;

            float maxPanel = MaxPanelHeight;
            try { maxPanel = Mathf.Min(MaxPanelHeight, (float)Screen.height - 130f); } catch { }
            if (maxPanel < MinPanelHeight) maxPanel = MinPanelHeight;

            float panelH = Mathf.Clamp(chrome + contentH,
                                       Mathf.Max(MinPanelHeight, chrome + MinBodyHeight),
                                       maxPanel);
            float bodyH = panelH - chrome;
            if (bodyH < MinBodyHeight) bodyH = MinBodyHeight;

            _panelHeight = panelH;
            if (_panel != null) _panel.sizeDelta = new Vector2(PanelWidth, panelH);

            if (_bodyView != null)
            {
                _bodyView.offsetMin = new Vector2(14f, bottom);
                _bodyView.offsetMax = new Vector2(-24f, -BodyTopInset);
            }
            if (_scrollTrack != null)
            {
                _scrollTrack.offsetMin = new Vector2(-19f, bottom);
                _scrollTrack.offsetMax = new Vector2(-12f, -BodyTopInset);
            }

            _scrollMax = Mathf.Max(0f, contentH - bodyH);
            if (_scrollPos > _scrollMax) _scrollPos = _scrollMax;
            if (_scrollPos < 0f) _scrollPos = 0f;

            _bodyContentH = contentH;
            _bodyViewH = bodyH;
            if (_bodyContent != null)
            {
                _bodyContent.sizeDelta = new Vector2(0f, Mathf.Max(contentH, bodyH));
                _bodyContent.anchoredPosition = new Vector2(0f, _scrollPos);
            }
            ((RectTransform)_body.transform).sizeDelta = new Vector2(0f, contentH);
            UpdateScrollbar();

            // 布局诊断：只有尺寸真变了才打一条，方便"文字还超不超窗口"直接看日志
            if (Mathf.Abs(panelH - _loggedPanelH) > 1f || rows != _loggedRows)
            {
                _loggedPanelH = panelH;
                _loggedRows = rows;
                _api.Log("BattleHold: layout contentH=" + contentH.ToString("0")
                         + " bodyH=" + bodyH.ToString("0")
                         + " panelH=" + panelH.ToString("0")
                         + " missileRows=" + rows
                         + " scrollMax=" + _scrollMax.ToString("0"));
            }
        }

        /// <summary>滚动条：正文不超长时整条隐藏，超长时按内容比例显示滑块位置。</summary>
        private void UpdateScrollbar()
        {
            if (_scrollTrack == null || _scrollHandle == null) return;
            bool need = _scrollMax > 1f;
            if (_scrollTrack.gameObject.activeSelf != need) _scrollTrack.gameObject.SetActive(need);
            if (!need) return;
            float trackH = _bodyViewH;
            if (trackH < 10f || _bodyContentH < 1f) return;
            float hH = trackH * (_bodyViewH / _bodyContentH);
            if (hH < 22f) hH = 22f;
            if (hH > trackH) hH = trackH;
            float t = _scrollMax > 0f ? (_scrollPos / _scrollMax) : 0f;
            var rt = (RectTransform)_scrollHandle.transform;
            rt.sizeDelta = new Vector2(0f, hH);
            rt.anchoredPosition = new Vector2(0f, -t * (trackH - hH));
        }

        // =====================================================================
        // 正文滚轮
        // =====================================================================
        private const float ScrollStepPx = 360f;   // 一格滚轮（delta 0.1）滚 36px

        private void PollBodyScroll()
        {
            if (_collapsed || _bodyContent == null || _scrollMax <= 0.5f) return;
            float d = ReadWheelDelta();
            if (d == 0f) return;
            if (!IsPointerOverPanel()) return;
            // d > 0 = 滚轮向上 = 看前面的内容（内容整体往下移）
            _scrollPos = Mathf.Clamp(_scrollPos - d * ScrollStepPx, 0f, _scrollMax);
            _bodyContent.anchoredPosition = new Vector2(0f, _scrollPos);
            UpdateScrollbar();
        }

        /// <summary>本帧滚轮增量，统一归一化成 legacy 量纲（一格 ≈ 0.1）；正数 = 向上滚。</summary>
        private static float ReadWheelDelta()
        {
            float d = 0f;
            try { d = Input.mouseScrollDelta.y; } catch { d = 0f; }
            if (d != 0f) return d;
            try
            {
                // 老 Input 被关掉时退回 InputSystem（它的原始值一格是 ±120，折算成 0.1）
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null)
                {
                    float raw = m.scroll.ReadValue().y;
                    if (raw != 0f) return Mathf.Sign(raw) * 0.1f;
                }
            }
            catch { }
            return 0f;
        }

        /// <summary>鼠标是否停在面板上（只有停在面板上才抢滚轮，别的影响飞行中的其它操作）。</summary>
        private bool IsPointerOverPanel()
        {
            try
            {
                RectTransform rt = _panel != null ? _panel : _bodyView;
                if (rt == null) return false;
                Vector2 p;
                try { p = Input.mousePosition; } catch { p = Vector2.zero; }
                if (p.x == 0f && p.y == 0f)
                {
                    var m = UnityEngine.InputSystem.Mouse.current;
                    if (m == null) return false;
                    p = m.position.ReadValue();
                }
                return RectTransformUtility.RectangleContainsScreenPoint(rt, p, null);
            }
            catch { }
            return false;
        }

        /// <summary>反射读取 MachineAAM 的导弹仓清单（富文本，只含玩家装备的弹种，选中项高亮）。</summary>
        private string QueryMissileInventory()
        {
            try
            {
                var t = Type.GetType("Machine.AAM.AamSystem, MachineAAM");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "MachineAAM") { t = asm.GetType("Machine.AAM.AamSystem"); break; }
                    }
                }
                if (t == null) return "";
                var m = t.GetMethod("MissileInventoryLine", Type.EmptyTypes);
                if (m == null) return "";
                var v = m.Invoke(null, null) as string;
                return v ?? "";
            }
            catch { return ""; }
        }

        /// <summary>刷新导弹仓点击按钮区：鼠标左键点击弹种直接选中（只显示库存>0 的弹种，当前选中金色高亮）。</summary>
        private void RefreshMissileButtons()
        {
            try
            {
                if (_missileRow == null || _collapsed) return;

                string lines = "";
                try
                {
                    var t = Type.GetType("Machine.AAM.AamSystem, MachineAAM");
                    if (t == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "MachineAAM") { t = asm.GetType("Machine.AAM.AamSystem"); break; }
                        }
                    }
                    if (t != null)
                    {
                        var m = t.GetMethod("MissileButtonLines", Type.EmptyTypes);
                        if (m != null)
                        {
                            var v = m.Invoke(null, null) as string;
                            if (v != null) lines = v;
                        }
                    }
                }
                catch (Exception ie)
                {
                    // 启动首帧跨程序集反射可能在中文路径下报一次（mono 热身），第二次即恢复，静默跳过
                    if (_missileQueryFail++ >= 3)
                        _api.Log("BattleHold: missile query failed " + ie.Message);
                    return;
                }
                // 签名没变就不重建：原来每 0.5s 无条件销毁重建，正在点的按钮会被自己重建打断
                if (lines == _missileSig && _missileButtons.Count > 0) return;
                _missileSig = lines;
                for (int i = 0; i < _missileButtons.Count; i++)
                    if (_missileButtons[i] != null) UnityEngine.Object.Destroy(_missileButtons[i]);
                _missileButtons.Clear();

                if (lines.Length == 0)
                {
                    _missileRows = 1;
                    ApplyMissileRowHeight();
                    // 无弹种时显示提示
                    var hint = UiFactory.NewText(_missileRow, "", 11,
                        new Color(0.6f, 0.6f, 0.65f, 1f), HarvestLocalizedFont());
                    // 直接赋值（NewText 会把非 ASCII 刷成 '?'，中文/俄文要走本地化字体）
                    hint.text = T("No missiles - buy at airport combat shop",
                        "无导弹 - 请在机场战斗商店购买", "Нет ракет — купите в боевом магазине аэропорта");
                    var hrt = (RectTransform)hint.transform;
                    hrt.anchorMin = new Vector2(0f, 1f);
                    hrt.anchorMax = new Vector2(0f, 1f);
                    hrt.pivot = new Vector2(0f, 1f);
                    hrt.anchoredPosition = new Vector2(0f, -3f);
                    hrt.sizeDelta = new Vector2(300f, 18f);
                    hint.alignment = TextAlignmentOptions.MidlineLeft;
                    _missileButtons.Add(hint.gameObject);
                    return;
                }

                var rows = lines.Split(new char[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                // 一行排不下就换行：以前是无限往右排，弹种一多按钮直接排到窗口外面去
                float availW = PanelWidth - 28f;
                float x = 0f;
                int row = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    var parts = rows[i].Split('|');
                    if (parts.Length < 3) continue;
                    string name = UiFactory.Safe(parts[0]);   // 再过滤一遍控制字符，防启动竞态非法字节
                    int idx = 0; int.TryParse(parts[1], out idx);
                    bool sel = parts[2] == "1";
                    float w = Mathf.Clamp(name.Length * 8f + 16f, 62f, 150f);
                    if (x > 0f && x + w > availW) { row++; x = 0f; }
                    int captured = idx;
                    var btn = UiFactory.NewButton(_missileRow, name,
                        delegate () { SelectMissileFromCore(captured); }, UiFactory.HarvestFont());
                    var rt = (RectTransform)btn.transform;
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(x, -(row * MissileRowStep));
                    rt.sizeDelta = new Vector2(w, 24f);
                    if (sel)
                    {
                        var img = btn.GetComponent<Image>();
                        if (img != null) img.color = new Color(1f, 0.8f, 0.15f, 0.95f);
                        var lbl = btn.GetComponentInChildren<TextMeshProUGUI>(true);
                        if (lbl != null) lbl.color = new Color(0.1f, 0.1f, 0.1f, 1f);
                    }
                    _missileButtons.Add(btn.gameObject);
                    x += w + 8f;
                }
                _missileRows = row + 1;
                ApplyMissileRowHeight();
            }
            catch (Exception e) { _api.Log("BattleHold: missile buttons failed " + e.Message); }
        }

        /// <summary>按当前行数调整弹种按钮行的高度（正文底部留白会跟着变）。</summary>
        private void ApplyMissileRowHeight()
        {
            if (_missileRow == null) return;
            _missileRow.offsetMin = new Vector2(14f, MissileRowTop);
            _missileRow.offsetMax = new Vector2(-14f, MissileRowTop + MissileRowHeight(_missileRows));
        }

        /// <summary>点击导弹仓按钮：切换到对应弹种并刷新。</summary>
        private void SelectMissileFromCore(int idx)
        {
            try
            {
                var t = Type.GetType("Machine.AAM.AamSystem, MachineAAM");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "MachineAAM") { t = asm.GetType("Machine.AAM.AamSystem"); break; }
                    }
                }
                if (t == null) return;
                var m = t.GetMethod("SelectMissileStatic", new System.Type[] { typeof(int) });
                if (m != null) m.Invoke(null, new object[] { idx });
            }
            catch { }
            RefreshMissileButtons();
            RefreshBody();
        }

        // =====================================================================
        // 热键：开关面板（默认 1，可在游戏设置-按键里重新绑定）
        // =====================================================================
        private void ResolveToggleKey()
        {
            _toggleKey = null;
            if (string.IsNullOrEmpty(cfgToggleKey)) return;
            try
            {
                UnityEngine.InputSystem.InputControl c = UnityEngine.InputSystem.InputSystem.FindControl(cfgToggleKey);
                _toggleKey = c as UnityEngine.InputSystem.Controls.ButtonControl;
                if (c == null) _api.Log("BattleHold: toggle key path not found: " + cfgToggleKey);
                else if (_toggleKey == null) _api.Log("BattleHold: toggle key is not a button control: " + cfgToggleKey);
            }
            catch (Exception e) { _api.Log("BattleHold: toggle key resolve failed " + e.Message); }
        }

        /// <summary>热键的可读名字（"1" / "Left Shift" ...）。</summary>
        private string ToggleKeyName()
        {
            if (string.IsNullOrEmpty(cfgToggleKey)) return "";
            try
            {
                UnityEngine.InputSystem.InputControl c = UnityEngine.InputSystem.InputSystem.FindControl(cfgToggleKey);
                if (c != null && !string.IsNullOrEmpty(c.displayName)) return c.displayName;
            }
            catch { }
            return cfgToggleKey;
        }

        private void PollToggleKey()
        {
            if (!cfgShowPanel || _rebinding) return;
            if (_toggleKey == null)
            {
                // 设备/按键可能晚于 Init 就绪，低频重试直到拿到控制
                _keyRetry -= Time.unscaledDeltaTime;
                if (_keyRetry > 0f) return;
                _keyRetry = 2f;
                ResolveToggleKey();
                if (_toggleKey == null) return;
            }
            bool pressed;
            try { pressed = _toggleKey.wasPressedThisFrame; } catch { return; }
            if (!pressed) return;
            if (IsTypingInField()) return;
            TogglePanel();
        }

        /// <summary>玩家正在输入框里打字时不要抢热键。</summary>
        private static bool IsTypingInField()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return false;
                GameObject go = es.currentSelectedGameObject;
                if (go == null) return false;
                return go.GetComponent<TMP_InputField>() != null
                    || go.GetComponent<UnityEngine.UI.InputField>() != null
                    || go.GetComponentInParent<TMP_InputField>() != null
                    || go.GetComponentInParent<UnityEngine.UI.InputField>() != null;
            }
            catch { return false; }
        }

        // =====================================================================
        // 把本 Mod 的按键行注入游戏“设置 - 按键设置”
        //   游戏原生结构：KeybindPanel(含 keybindFields[]) -> 每行一个 KeybindField
        //   （字段 keyName:TMP_Text / keyValue:KeyIcon / secondaryKeyValue:KeyIcon）
        //   原生 rebind 只能作用于 Keys 枚举里的固定几项，所以这里克隆一行、把原生组件拆掉，
        //   再用本 Mod 自己的“按任意键”流程，键位落在 battlehold_config.json。
        // =====================================================================
        private void PollKeybindInjection()
        {
            if (!cfgInjectKeybind) return;
            if (_kbInjected && _kbRowGo != null) return;
            _kbPoll -= Time.unscaledDeltaTime;
            if (_kbPoll > 0f) return;
            _kbPoll = 2.5f;
            if (_kbInjected && _kbRowGo == null) _kbInjected = false;   // 场景重建 -> 重新注入
            if (_kbInjected) return;
            _kbInjected = TryInjectKeybindRow();
        }

        private bool ResolveKeybindTypes()
        {
            if (_kbTypesResolved) return _kbPanelType != null && _kbFieldType != null;
            _kbTypesResolved = true;
            try
            {
                var asm = typeof(PlaneContainer).Assembly;
                _kbPanelType = asm.GetType("KeybindPanel");
                _kbFieldType = asm.GetType("KeybindField");
                _keyIconType = asm.GetType("KeyIcon");
            }
            catch (Exception e) { _api.Log("BattleHold: keybind type lookup failed " + e.Message); }
            if (_kbPanelType == null || _kbFieldType == null)
                _api.Log("BattleHold: KeybindPanel/KeybindField not found -> settings row disabled");
            return _kbPanelType != null && _kbFieldType != null;
        }

        private bool TryInjectKeybindRow()
        {
            if (!ResolveKeybindTypes()) return false;
            try
            {
                UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(_kbPanelType);
                if (found != null)
                {
                    for (int i = 0; i < found.Length; i++)
                    {
                        Component panel = found[i] as Component;
                        if (!IsSceneObject(panel)) continue;
                        if (!panel.gameObject.activeInHierarchy) continue;   // 只注入已经 Awake 过的面板
                        Component[] fields = panel.GetComponentsInChildren(_kbFieldType, true);
                        if (fields == null || fields.Length == 0) continue;
                        if (BuildKeybindRow(panel, fields[0])) return true;
                    }
                }
            }
            catch (Exception e) { _api.Log("BattleHold: keybind inject failed " + e.Message); }
            return false;
        }

        private bool BuildKeybindRow(Component panel, Component srcField)
        {
            FieldInfo fKeyName = _kbFieldType.GetField("keyName", BF);
            FieldInfo fKeyValue = _kbFieldType.GetField("keyValue", BF);
            FieldInfo fSecondary = _kbFieldType.GetField("secondaryKeyValue", BF);
            if (fKeyName == null || fKeyValue == null) return false;

            Component srcLabel = fKeyName.GetValue(srcField) as Component;
            Component srcPrimary = fKeyValue.GetValue(srcField) as Component;
            Component srcSecondary = fSecondary != null ? fSecondary.GetValue(srcField) as Component : null;
            if (srcLabel == null || srcPrimary == null) return false;

            // 决定“行根”：优先 KeybindField 自己；若标签/键图标挂在它的父级上且父级只有这一行，则用父级
            Transform srcT = srcField.transform;
            Transform rowRoot = srcT;
            if (!(srcLabel.transform.IsChildOf(srcT) && srcPrimary.transform.IsChildOf(srcT)))
            {
                Transform p = srcT.parent;
                if (p != null && p != panel.transform &&
                    p.GetComponentsInChildren(_kbFieldType, true).Length == 1)
                    rowRoot = p;
            }

            string labelPath = RelPath(srcLabel.transform, rowRoot);
            string primaryPath = RelPath(srcPrimary.transform, rowRoot);
            string secondaryPath = srcSecondary != null ? RelPath(srcSecondary.transform, rowRoot) : "";

            GameObject clone = UnityEngine.Object.Instantiate(rowRoot.gameObject, rowRoot.parent) as GameObject;
            if (clone == null) return false;
            clone.name = "BattleHoldKeybindRow";
            try { clone.transform.SetSiblingIndex(rowRoot.GetSiblingIndex() + 1); } catch { }

            StripCloneComponents(clone);

            // 拆掉克隆出来的原生 KeybindField：先 disable 再 Destroy，
            // 否则它的 Start() 可能在本帧末被调用，用错误的 Keys 去刷新显示。
            Component[] clonedFields = clone.GetComponentsInChildren(_kbFieldType, true);
            for (int i = 0; i < clonedFields.Length; i++)
            {
                Behaviour b = clonedFields[i] as Behaviour;
                if (b != null) b.enabled = false;
                UnityEngine.Object.Destroy(clonedFields[i]);
            }

            // 行名
            Transform lt = FindPath(clone.transform, labelPath);
            TMP_Text label = lt != null ? lt.GetComponent<TMP_Text>() : clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) { label.text = "Battle Hold"; label.raycastTarget = false; }

            // 主键图标
            Transform pt = FindPath(clone.transform, primaryPath);
            _kbIcon = (pt != null && _keyIconType != null) ? pt.GetComponent(_keyIconType) : null;
            if (_kbIcon != null) SetKeyIconValue(_kbIcon, cfgToggleKey);

            // 次键槽：本 Mod 只支持一个键，隐藏掉避免误导
            Transform st = FindPath(clone.transform, secondaryPath);
            if (st != null) st.gameObject.SetActive(false);

            // 点击 -> 进入“按任意键”状态
            Button btn = FindClickable(clone.transform, pt);
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(BeginRebind);
            }
            else if (pt != null)
            {
                Graphic[] gs = pt.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; i < gs.Length; i++) gs[i].raycastTarget = true;
                Button nb = pt.gameObject.AddComponent<Button>();
                nb.transition = Selectable.Transition.None;
                nb.onClick.AddListener(BeginRebind);
            }

            // 原生回调可能挂在 EventTrigger 上，一并接管
            EventTrigger[] ets = clone.GetComponentsInChildren<EventTrigger>(true);
            for (int i = 0; i < ets.Length; i++)
            {
                if (pt != null && !(ets[i].transform == pt || pt.IsChildOf(ets[i].transform))) continue;
                if (ets[i].triggers == null) ets[i].triggers = new List<EventTrigger.Entry>();
                ets[i].triggers.Clear();
                var entry = new EventTrigger.Entry();
                entry.eventID = EventTriggerType.PointerClick;
                entry.callback.AddListener(delegate (BaseEventData d) { BeginRebind(); });
                ets[i].triggers.Add(entry);
            }

            _kbRowGo = clone;
            _api.Log("BattleHold: keybind row injected into settings (" + panel.name
                     + ", row=" + rowRoot.name + ", labelPath='" + labelPath + "')");
            return true;
        }

        /// <summary>剥掉克隆行里带场景序列化回调的游戏组件（和 MenuInjector 同一套路）。</summary>
        private static void StripCloneComponents(GameObject clone)
        {
            Component[] all = clone.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n == "CustomButton" || n == "OrangeHoverButton" || n == "ButtonAudio" ||
                    n == "ButtonHighlight" || n == "ButtonPrompt" || n == "MenuAudio")
                {
                    Behaviour b = c as Behaviour;
                    if (b != null) b.enabled = false;
                    UnityEngine.Object.Destroy(c);
                }
            }
        }

        private static Button FindClickable(Transform root, Transform icon)
        {
            if (root == null) return null;
            Button[] bs = root.GetComponentsInChildren<Button>(true);
            if (bs == null || bs.Length == 0) return null;
            if (icon != null)
            {
                for (int i = 0; i < bs.Length; i++)
                    if (bs[i].transform == icon || icon.IsChildOf(bs[i].transform)) return bs[i];
            }
            return bs.Length == 1 ? bs[0] : null;
        }

        private static Transform FindPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            return root.Find(path);
        }

        /// <summary>
        /// 只认真正挂在已加载场景里的对象。Resources.FindObjectsOfTypeAll 还会返回
        /// 预制体资源（它的 activeInHierarchy 也可能为 true），往预制体上改是无效操作。
        /// </summary>
        private static bool IsSceneObject(Component c)
        {
            if (c == null) return false;
            GameObject go = c.gameObject;
            if (go == null) return false;
            try
            {
                UnityEngine.SceneManagement.Scene s = go.scene;
                return s.IsValid() && s.isLoaded;
            }
            catch { return false; }
        }

        /// <summary>child 相对 root 的层级路径（"A/B/C"）；不在 root 下返回空串。</summary>
        private static string RelPath(Transform child, Transform root)
        {
            if (child == null || root == null || child == root) return "";
            var stack = new List<string>();
            Transform t = child;
            while (t != null && t != root)
            {
                if (t.name.IndexOf('/') >= 0) return "";
                stack.Add(t.name);
                t = t.parent;
            }
            if (t != root) return "";
            stack.Reverse();
            return string.Join("/", stack.ToArray());
        }

        private void SetKeyIconValue(Component icon, string path)
        {
            if (icon == null || _keyIconType == null || string.IsNullOrEmpty(path)) return;
            try
            {
                MethodInfo m = _keyIconType.GetMethod("SetValue", BF, null, new Type[] { typeof(string) }, null);
                if (m != null) m.Invoke(icon, new object[] { path });
            }
            catch (Exception e) { _api.Log("BattleHold: key icon update failed " + e.Message); }
        }

        private void SetKeyIconWaiting(Component icon)
        {
            if (icon == null || _keyIconType == null) return;
            try
            {
                MethodInfo m = _keyIconType.GetMethod("Wait", BF, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(icon, null);
            }
            catch { }
        }

        // ---- 重新绑定流程 ----
        private void BeginRebind()
        {
            if (_rebinding) return;
            _rebinding = true;
            _rebindStart = Time.unscaledTime;
            SetKeyIconWaiting(_kbIcon);
            _api.Log("BattleHold: rebinding toggle key - press a key (Esc cancels)");
        }

        private void UpdateRebind()
        {
            if (!_rebinding) return;
            if (_kbRowGo != null && !_kbRowGo.activeInHierarchy) { CancelRebind("row hidden"); return; }
            if (Time.unscaledTime - _rebindStart > 10f) { CancelRebind("timeout"); return; }

            string path = CapturePressedKey();
            if (path == "CANCEL") { CancelRebind("escape"); return; }
            if (string.IsNullOrEmpty(path)) return;

            cfgToggleKey = path;
            ResolveToggleKey();
            SetKeyIconValue(_kbIcon, cfgToggleKey);
            SaveConfig();
            _rebinding = false;
            _api.Log("BattleHold: toggle key rebound -> " + cfgToggleKey + " (" + ToggleKeyName() + ")");
        }

        private void CancelRebind(string why)
        {
            _rebinding = false;
            SetKeyIconValue(_kbIcon, cfgToggleKey);
            _api.Log("BattleHold: rebind cancelled (" + why + ")");
        }

        /// <summary>返回按下的键的绑定路径；ESC 返回 "CANCEL"；没按返回空串。鼠标被排除（点击本身就是触发）。</summary>
        private static string CapturePressedKey()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    if (kb.escapeKey.wasPressedThisFrame) return "CANCEL";
                    var keys = kb.allKeys;
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var k = keys[i];
                        if (k != null && k.wasPressedThisFrame) return k.path;
                    }
                }
                var gp = UnityEngine.InputSystem.Gamepad.current;
                if (gp != null)
                {
                    var controls = gp.allControls;
                    for (int i = 0; i < controls.Count; i++)
                    {
                        var bc = controls[i] as UnityEngine.InputSystem.Controls.ButtonControl;
                        if (bc != null && bc.wasPressedThisFrame) return bc.path;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>自测用：打开设置界面里的按键页，验证按键行是否注入成功。</summary>
        private IEnumerator TestKeybindRow()
        {
            ResolveKeybindTypes();
            _api.Log("BattleHold: keybind-row test starting");
            Component sp = OpenSettingsKeybindTab();

            for (int i = 0; i < 16 && _kbRowGo == null; i++) yield return new WaitForSeconds(0.5f);
            _api.Log("BattleHold: UI state -> keybindRow=" + (_kbRowGo != null)
                     + " settingsPanel=" + (sp != null)
                     + " toggleKey=" + cfgToggleKey + " (" + ToggleKeyName() + ")"
                     + " panelAnchor=" + cfgAnchor + " pos=" + cfgPanelX + "," + cfgPanelY
                     + " panelVisible=" + cfgPanelVisible);
            yield return new WaitForSeconds(0.5f);
            Shot("kb_01_settings");
            yield return new WaitForSeconds(0.8f);

            // 面板开关测试：切换一次后恢复。
            // 注意 ScreenCapture.CaptureScreenshot 是异步落盘的（帧率低时可能滞后几百毫秒），
            // 等待时间必须足够长，否则会拍到切换前的状态。
            bool before = cfgPanelVisible;
            TogglePanel();
            yield return new WaitForSeconds(1.2f);
            Shot("kb_02_panel_hidden");
            bool hid = !cfgPanelVisible;
            TogglePanel();
            yield return new WaitForSeconds(1.2f);
            Shot("kb_03_panel_shown");
            _api.Log("BattleHold: toggle test -> before=" + before + " hidden=" + hid
                     + " after=" + cfgPanelVisible);
        }

        /// <summary>找到 SettingsPanel 并切到含 KeybindField 的那一页（无 yield，便于放在 try/catch 里）。</summary>
        private Component OpenSettingsKeybindTab()
        {
            try
            {
                var asm = typeof(PlaneContainer).Assembly;
                Type spType = asm.GetType("SettingsPanel");
                if (spType == null) { _api.Log("BattleHold: SettingsPanel type not found"); return null; }
                Component sp = null;
                UnityEngine.Object[] arr = Resources.FindObjectsOfTypeAll(spType);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Component c = arr[i] as Component;
                        if (IsSceneObject(c)) { sp = c; break; }
                    }
                }
                if (sp == null) { _api.Log("BattleHold: SettingsPanel not found (no keybind-row test)"); return null; }

                if (!sp.gameObject.activeInHierarchy) sp.gameObject.SetActive(true);

                FieldInfo fp = spType.GetField("panels", BF);
                GameObject[] panels = fp != null ? fp.GetValue(sp) as GameObject[] : null;
                if (panels == null) { _api.Log("BattleHold: SettingsPanel.panels is null"); return sp; }

                for (int i = 0; i < panels.Length; i++)
                {
                    if (panels[i] == null) continue;
                    if (_kbFieldType == null || panels[i].GetComponentsInChildren(_kbFieldType, true).Length == 0) continue;
                    MethodInfo mOpen = spType.GetMethod("OpenPanel", BF, null, new Type[] { typeof(int) }, null);
                    if (mOpen != null) mOpen.Invoke(sp, new object[] { i });
                    else panels[i].SetActive(true);
                    _api.Log("BattleHold: opened settings tab " + i + " (" + panels[i].name + ")");
                    return sp;
                }
                _api.Log("BattleHold: no settings tab contains a KeybindField (" + panels.Length + " tabs)");
                return sp;
            }
            catch (Exception e)
            {
                _api.Log("BattleHold: open settings failed " + e.Message);
                return null;
            }
        }

        // =====================================================================
        // 自测（cfgAutoTest=true 时启动后自动执行一次核心行为验证）
        // =====================================================================
        private IEnumerator SelfTest()
        {
            var log = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            Action<bool, string> check = delegate (bool ok, string what)
            {
                if (ok) pass++; else fail++;
                log.Append(ok ? "  [PASS] " : "  [FAIL] ").Append(what).Append("\n");
            };

            _api.Log("BattleHold: self-test starting");
            _selfTestRunning = true;
            _stateDirty = false;

            Shot("00_boot");
            yield return new WaitForSeconds(5f);
            Shot("01_menu");

            // 可选：验证“设置 - 按键”里的按键行注入 + 面板热键开关
            if (cfgTestKeybind) yield return TestKeybindRow();

            // 耐心等待进入飞机（最多 180s）：核心断言需要真实的货仓 + 飞机
            int waited = 0;
            while (waited < 180 && !(ResolveRefs() && _ci != null && _plane != null))
            {
                yield return new WaitForSeconds(2f);
                waited += 2;
                if (waited % 20 == 0)
                {
                    _api.Log("BattleHold: self-test waiting for plane... " + waited + "s");
                    Shot("wait" + waited);
                }
            }
            Shot("02_plane");
            yield return new WaitForSeconds(2f);
            ResolveRefs();

            CargoType combat = FindCargoByName(DemoCargoName);
            CargoType normal = FindFirstNormalCargo(combat);

            log.Append("resolved: ci=").Append(_ci != null)
               .Append(" plane=").Append(_plane != null)
               .Append(" combatType=").Append(combat != null)
               .Append(" normalType=").Append(normal != null).Append("\n");
            check(_ci != null, "CargoInventory instance resolved");
            check(_plane != null, "PlaneContainer instance resolved");
            check(combat != null, "demo combat cargo '" + DemoCargoName + "' registered");
            check(normal != null, "a normal cargo type available");

            if (_ci != null && _plane != null && combat != null && normal != null)
            {
                // 基线
                ClearCombat();
                ClearGameHold();
                yield return null; yield return null;

                // 放入 2 个普通货物 + 3 个战斗货物
                bool nOk = GameAddCargo(normal); yield return null;
                bool nOk2 = GameAddCargo(normal); yield return null;
                bool cOk = TryAdd(combat, 3);
                yield return null; yield return null;

                float expectCombat = combat.cargoSpace * 3f;
                float expectNormal = normal.cargoSpace * 2f;
                log.Append("after add: normal=").Append(_normalVol).Append(" combat=").Append(_combatVol)
                   .Append(" gameCur=").Append(GameCurrentVolume()).Append(" max=").Append(GameMaxVolume()).Append("\n");
                check(nOk && nOk2, "normal cargo accepted by game hold");
                check(cOk, "combat cargo accepted by battle hold");
                check(Mathf.Abs(_combatVol - expectCombat) < 0.001f, "combat volume == " + expectCombat);
                check(Mathf.Abs(GameCurrentVolume() - (expectNormal + expectCombat)) < 0.001f,
                      "shared volume fed back to game (normal+combat)");

                Shot("03_added");

                // 关键：清空原货仓，战斗货物必须存活，且清单收敛会经游戏原版 AddCargo 自动补回
                //（质量/容量由游戏重新记账——这正是"质量还给游戏"架构的核心验证点）
                ClearGameHold();
                yield return new WaitForSeconds(1.2f);   // 等 0.5s 宽限期 + 补回
                log.Append("after game ClearInventory (+resync): normal=").Append(_normalVol)
                   .Append(" combat=").Append(_combatVol)
                   .Append(" combatCount=").Append(Count(combat))
                   .Append(" gameCount=").Append(GameCargoCount(combat)).Append("\n");
                check(Mathf.Abs(_normalVol) < 0.001f, "normal hold cleared to 0");
                check(Count(combat) == 3, "combat cargo SURVIVED ClearInventory");
                check(GameCargoCount(combat) == 3, "resync re-added combat cargo via vanilla AddCargo");
                check(Mathf.Abs(GameCurrentVolume() - expectCombat) < 0.001f, "game volume re-accrued after clear");
                Shot("04_after_clear");

                // 容量共享：把总容量塞满后，战斗仓库拒绝超载
                if (normal.cargoSpace > 1)
                {
                    int room = Mathf.FloorToInt(Mathf.RoundToInt(GameMaxVolume()) - (_normalVol + _combatVol));
                    int n = room / normal.cargoSpace;
                    for (int i = 0; i < n; i++) { GameAddCargo(normal); }
                    yield return null; yield return null;
                    bool overflow = TryAdd(combat, 100000);
                    check(!overflow, "battle hold rejects overflow once shared hold is full");
                }
                else
                {
                    log.Append("  [SKIP] overflow test (normal cargoSpace too small)\n");
                }

                if (cfgCaptureShots) yield return new WaitForSeconds(1f);
            }
            else
            {
                log.Append("  [SKIP] behavioural checks (missing refs)\n");
            }

            log.Append("RESULT: pass=").Append(pass).Append(" fail=").Append(fail).Append("\n");
            _api.Log("BattleHold: self-test done\n" + log.ToString());

            // 自测完成后清理，避免污染玩家存档状态
            ClearCombat();
            ClearGameHold();
        }

        private CargoType FindCargoByName(string name)
        {
            try
            {
                var am = AirportManager.Instance;
                if (am == null || am.allCargoTypes == null) return null;
                for (int i = 0; i < am.allCargoTypes.Length; i++)
                {
                    var c = am.allCargoTypes[i];
                    if (c != null && c.cargoName == name) return c;
                }
            }
            catch { }
            return null;
        }

        private CargoType FindFirstNormalCargo(CargoType exclude)
        {
            try
            {
                var am = AirportManager.Instance;
                if (am == null || am.allCargoTypes == null) return null;
                for (int i = 0; i < am.allCargoTypes.Length; i++)
                {
                    var c = am.allCargoTypes[i];
                    if (c == null || c == exclude) continue;
                    if (IsCombat(c)) continue;
                    if (c.fragile || c.expires) continue;   // 避免自测中货物被破碎/过期逻辑清掉
                    return c;
                }
            }
            catch { }
            return null;
        }

        private bool GameAddCargo(CargoType type)
        {
            if (_ci == null || _ciType == null || type == null) return false;
            try
            {
                var m = _ciType.GetMethod("AddCargo", BF, null, new Type[] { typeof(CargoType) }, null);
                if (m == null) return false;
                m.Invoke(_ci, new object[] { type });
                return true;
            }
            catch { return false; }
        }

        private void ClearGameHold()
        {
            if (_ci == null || _mClearInventory == null) return;
            try { _mClearInventory.Invoke(_ci, null); } catch { }
        }

        private string ShotDir()
        {
            string dir = cfgShotDir;
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Machine", "logs", "battlehold_shots");
            return dir;
        }

        private void Shot(string tag)
        {
            if (!cfgCaptureShots) return;
            try
            {
                RefreshBody();   // 强制刷新，避免面板节流导致截图拍到旧值
                string dir = ShotDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _shotIdx++;
                string file = string.Format("{0:00}_{1}.png", _shotIdx, tag);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, file));
            }
            catch (Exception e) { _api.Log("BattleHold: shot failed " + e.Message); }
        }
    }

    /// <summary>
    /// 面板拖动：鼠标左键按住面板（含四周边框）拖动即可移动窗口。
    /// 挂在面板根上，因此除按钮自身外的任意位置都能起拖；位置会被夹在画布内，不会拖出屏幕。
    /// </summary>
    public class PanelDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Target;
        public Action<Vector2> OnMoved;
        public Action OnFinished;

        private RectTransform _parent;
        private Vector2 _startPointer;
        private Vector2 _startAnchored;
        private bool _active;

        public void OnBeginDrag(PointerEventData e)
        {
            _active = false;
            if (Target == null) return;
            _parent = Target.parent as RectTransform;
            if (_parent == null) return;
            Vector2 p;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_parent, e.position, e.pressEventCamera, out p)) return;
            _startPointer = p;
            _startAnchored = Target.anchoredPosition;
            _active = true;
        }

        public void OnDrag(PointerEventData e)
        {
            if (!_active || Target == null || _parent == null) return;
            Vector2 p;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_parent, e.position, e.pressEventCamera, out p)) return;
            Vector2 anchored = _startAnchored + (p - _startPointer);
            Target.anchoredPosition = Clamp(anchored);
            if (OnMoved != null) OnMoved(Target.anchoredPosition);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!_active) return;
            _active = false;
            if (OnFinished != null) OnFinished();
        }

        /// <summary>把锚点位置夹到父级矩形内，保证整块面板始终可见。</summary>
        private Vector2 Clamp(Vector2 anchored)
        {
            Rect pr = _parent.rect;
            Vector2 size = Target.rect.size;
            Vector2 pivot = Target.pivot;
            Vector2 amin = Target.anchorMin;
            Vector2 anchorRef = new Vector2(pr.xMin + amin.x * pr.width, pr.yMin + amin.y * pr.height);
            Vector2 pos = anchorRef + anchored;   // 面板 pivot 在父级局部空间的位置

            float minX = pr.xMin + pivot.x * size.x;
            float maxX = pr.xMax - (1f - pivot.x) * size.x;
            float minY = pr.yMin + pivot.y * size.y;
            float maxY = pr.yMax - (1f - pivot.y) * size.y;
            if (maxX < minX) { float m = (minX + maxX) * 0.5f; minX = m; maxX = m; }
            if (maxY < minY) { float m = (minY + maxY) * 0.5f; minY = m; maxY = m; }

            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
            return pos - anchorRef;
        }
    }
}
