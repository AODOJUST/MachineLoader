using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Machine.Mod;

namespace Machine.Radar
{
    /// <summary>
    /// 雷达系统（Radar System）：
    /// - 雷达部件是战斗货物（CargoType），只可装入战斗货仓（BattleHold 前置），不进普通货仓；
    /// - 扫描模型（Mk1 初始雷达）：探测距离 750m，扫描扇区 左右各 30° / 上下各 20°，
    ///   后方整个半球为盲区；扫描线以 120°/s 在扇区内往复扫描；
    /// - 锁定：可锁定目标进入扫描范围时按鼠标中键锁定；锁定目标在画面上以红点+距离标记，
    ///   红色方框框住；距离超出探测范围不显示标记但锁定保留；
    /// - 屏幕中央绿色小圈辅助瞄准；语音联动 VoiceAlerts（Lock / Fox 1）；
    /// - 信息通过 BattleCore 共享；导弹发射由其他 Mod 通过 RadarApi.NotifyMissileLaunch()
    ///   或 BattleCore.Post("Weapon","Launch",...) 触发 Fox 1 播报。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.radar"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("Radar loading...");
            var go = new GameObject("Machine.Radar");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<RadarSystem>().Init(api);
        }
    }

    /// <summary>雷达公开 API（其他 Mod 可反射调用）。</summary>
    public static class RadarApi
    {
        private static RadarSystem _sys;
        internal static void Bind(RadarSystem sys) { _sys = sys; }
        public static bool Available { get { return _sys != null; } }
        /// <summary>通知雷达：空空导弹已发射（触发 Fox 1 语音与信息上报）。</summary>
        public static void NotifyMissileLaunch()
        {
            if (_sys != null) _sys.OnMissileLaunch();
        }
        /// <summary>当前是否有锁定目标。</summary>
        public static bool HasLock { get { return _sys != null && _sys.LockedName != null; } }
        /// <summary>锁定目标名（无则 null）。</summary>
        public static string LockedName { get { return _sys != null ? _sys.LockedName : null; } }
        /// <summary>锁定目标当前世界坐标（无锁定返回零向量）。</summary>
        public static Vector3 LockedPos { get { return _sys != null ? _sys.LockedPos : Vector3.zero; } }
        /// <summary>查询锁定目标：成功返回 true 并给出名称与位置。</summary>
        public static bool TryGetLocked(out string name, out Vector3 pos)
        {
            name = LockedName;
            pos = LockedPos;
            return name != null;
        }
        /// <summary>锁定目标的动态实体引用（导弹制导用：攻击锁定目标而非就近目标）；无则 null。</summary>
        public static Transform TryGetLockedTransform()
        {
            if (_sys == null) return null;
            return _sys.LockedRef;
        }
    }

    /// <summary>可锁定目标（机场/基地或动态实体，如 AI 飞机、导弹）。</summary>
    public class LockTarget
    {
        public Vector3 Pos;
        public string Name;
        public Transform Ref;   // 动态实体引用（每帧跟随其位置）
        public bool IsDynamic;
        public bool IsAirport;  // 机场（雷达盘上画方块，不可锁定）
        public bool IsMissile;  // 导弹（雷达盘上画三角，红色）
        public bool Friendly;   // 友军/己方基地（雷达盘上画蓝色，不可锁定）
        public int Faction = -1; // 机场归属阵营（-1 = 中立大陆；只有 IsAirport 的有意义）
        public string ModelName; // 机型（AI 呼号/设计名）
        public float LastSpeed;  // 最近一次测得速度（节）
    }

    /// <summary>雷达等级配置。</summary>
    public class RadarTier
    {
        public string CargoName;
        public float Weight;
        public int Space;
        public float Price;
        public float Range;      // 探测距离（m）
        public float ScanH;      // 普通扫描水平半角（度）
        public float ScanV;      // 普通扫描垂直半角（度）
        public float LockH;      // 锁定窄角（度）：目标进入该角内才可锁定，辅助圈按此投影
        public float ScanRate;   // 扫描线速度（°/s）
        public bool IsAesa;      // 有源相控阵：水平无死角（360°），探测与锁定即时
    }

    public class RadarSystem : MonoBehaviour
    {
        private IMachineApi _api;

        // 配置（默认值，实际按战斗货仓中装载的雷达等级取参数）
        private float _range = 750f;         // 当前生效探测距离（m）
        private float _scanRate = 175f;      // 扫描速度（°/s）
        private float _scanWidthH = 30f;     // 当前生效水平半角（度）
        private float _scanWidthV = 20f;     // 当前生效垂直半角（度）
        private float _lockH = 30f;          // 当前生效锁定窄角（度）
        private float _sweepHalf = 6f;       // 扫描线捕捉宽度（±6°）
        private bool _aesa;                  // 当前是否为相控阵（水平无死角 + 即时探测锁定）

        // 雷达部件（多等级）
        private readonly List<RadarTier> _tiers = new List<RadarTier>();
        private readonly List<CargoType> _radarTypes = new List<CargoType>();
        private RadarTier _activeTier;
        private Texture2D _scopeBg;

        // 状态
        private bool _active;
        private float _sweepAngle;
        private float _sweepDir = 1f;
        private float _scanTimer;
        private string _lastReport = "";

        // 目标与锁定
        private readonly List<LockTarget> _targets = new List<LockTarget>();
        private readonly List<LockTarget> _preList = new List<LockTarget>();   // 预锁定目标（普通扫描区内敌机/敌方导弹，可显示但不可锁定）
        private readonly List<LockTarget> _lockableList = new List<LockTarget>();   // 可锁定目标（锁定窄角区内敌机/敌方导弹，可锁定）
        private LockTarget _current;
        private LockTarget _locked;
        private float _targetScanTimer;
        private float _preSpeedTimer;

        // 雷达盘面板（可拖动）
        private Rect _scopeRect = new Rect(20f, 20f, 220f, 220f);
        private bool _scopePosInitialized = false;  // 首次绘制时设置默认位置（右下角）
        private bool _dragging;
        private Vector2 _dragOffset;

        // 语音联动（VoiceAlerts）
        private bool _vaChecked;
        private bool _vaAvailable;
        private System.Reflection.MethodInfo _vaPlay;
        private float _foxCooldown;

        // BattleCore 反射
        private bool _bcChecked;
        private bool _bcAvailable;
        private System.Reflection.MethodInfo _bcPost;
        private System.Reflection.MethodInfo _bcGet;
        private string _lastLaunchInfo = "";
        private float _bcPollTimer;

        // BattleHold（战斗货仓前置）反射
        private bool _bhChecked;
        private bool _bhAvailable;
        private System.Reflection.MethodInfo _bhMarkCombat;
        private System.Reflection.MethodInfo _bhContains;

        private bool _airportsInjected;
        
        // 强制装货按钮（修复基地装货键消失的bug）
        private GameObject _forceCargoBtn;
        private UnityEngine.UI.Button _forceCargoBtnComp;
        private bool _forceCargoBtnCreated;
        private int _forceCargoDiagCounter;

        public string LockedName { get { return _locked != null ? _locked.Name : null; } }
        public Vector3 LockedPos { get { return _locked != null ? _locked.Pos : Vector3.zero; } }

        public void Init(IMachineApi api)
        {
            _api = api;
            RadarApi.Bind(this);
            DefineTiers();
            CreateRadarCargo();
            CheckBattleCore();
            CheckBattleHold();
            MarkRadarCombat();
            // 注册场景切换事件：每次加载新场景时重置商店状态（修复第二次进入存档装货键消失的bug）
            SceneManager.sceneLoaded += OnSceneLoaded;
            // 强制装货按钮已被MachineShop mod替代，暂时禁用
            // CreateForceCargoButton();
            _api.Log("Radar: ready (tiers=Mk1/Mk2, scene-reset registered, force-cargo button disabled)");
        }

        /// <summary>创建强制装货按钮（修复基地装货键消失的bug，就算没有货物也能打开装货界面）。</summary>
        private void CreateForceCargoButton()
        {
            if (_forceCargoBtnCreated) return;
            _api.Log("Radar: CreateForceCargoButton START");
            try
            {
                // 查找或创建Canvas
                var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                _api.Log("Radar: CreateForceCargoButton canvas found=" + (canvas != null));
                if (canvas == null)
                {
                    var canvasGo = new GameObject("MachineForceCargoCanvas");
                    canvas = canvasGo.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                    canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                    _api.Log("Radar: CreateForceCargoButton new canvas created");
                }

                // 创建按钮
                _forceCargoBtn = new GameObject("ForceCargoButton");
                _forceCargoBtn.transform.SetParent(canvas.transform, false);
                var rt = _forceCargoBtn.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.15f);
                rt.anchorMax = new Vector2(0.5f, 0.15f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(160f, 50f);
                rt.anchoredPosition = Vector2.zero;

                // 添加Image组件（按钮背景）
                var img = _forceCargoBtn.AddComponent<UnityEngine.UI.Image>();
                img.color = new Color(0.2f, 0.4f, 0.8f, 0.9f);

                // 添加Button组件
                _forceCargoBtnComp = _forceCargoBtn.AddComponent<UnityEngine.UI.Button>();
                var colors = _forceCargoBtnComp.colors;
                colors.normalColor = new Color(0.2f, 0.4f, 0.8f, 0.9f);
                colors.highlightedColor = new Color(0.3f, 0.5f, 0.9f, 1f);
                colors.pressedColor = new Color(0.1f, 0.3f, 0.7f, 1f);
                _forceCargoBtnComp.colors = colors;

                // 添加文本
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(_forceCargoBtn.transform, false);
                var textRt = textGo.AddComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = Vector2.zero;
                textRt.offsetMax = Vector2.zero;
                var text = textGo.AddComponent<TMPro.TextMeshProUGUI>();
                text.text = "装货";
                text.fontSize = 24;
                text.alignment = TMPro.TextAlignmentOptions.Center;
                text.color = Color.white;

                // 按钮点击事件：调用CargoInventoryUI.OpenAirportInventory(true)
                _forceCargoBtnComp.onClick.AddListener(() =>
                {
                    try
                    {
                        var all = UnityEngine.Object.FindObjectsOfType<CargoInventoryUI>(true);
                        if (all != null && all.Length > 0)
                        {
                            var cui = all[0];
                            var openMethod = typeof(CargoInventoryUI).GetMethod("OpenAirportInventory",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (openMethod != null)
                            {
                                openMethod.Invoke(cui, new object[] { true });
                                _api.Log("Radar: ForceCargoButton clicked, OpenAirportInventory(true) called");
                            }
                            else
                            {
                                _api.Log("Radar: ForceCargoButton error - OpenAirportInventory method not found");
                            }
                        }
                        else
                        {
                            _api.Log("Radar: ForceCargoButton error - CargoInventoryUI not found");
                        }
                    }
                    catch (Exception e) { _api.Log("Radar: ForceCargoButton click error " + e.Message); }
                });

                _forceCargoBtn.SetActive(false); // 默认隐藏，靠近机场时显示
                _forceCargoBtnCreated = true;
                _api.Log("Radar: ForceCargoButton created SUCCESS, btn=" + (_forceCargoBtn != null));
            }
            catch (Exception e) { _api.Log("Radar: ForceCargoButton create ERROR " + e.Message + "\n" + e.StackTrace); }
        }

        /// <summary>更新强制装货按钮的显示/隐藏状态。</summary>
        private void UpdateForceCargoButton()
        {
            // 诊断：每次进入都输出状态
            if (_forceCargoDiagCounter++ % 30 == 0)
            {
                _api.Log("Radar: ForceCargo diag created=" + _forceCargoBtnCreated + 
                    " btnNull=" + (_forceCargoBtn == null) + 
                    " btnActive=" + (_forceCargoBtn != null && _forceCargoBtn.activeSelf));
            }
            
            if (!_forceCargoBtnCreated || _forceCargoBtn == null) return;
            try
            {
                // 只在游戏场景中显示（简化逻辑：只要在游戏场景就显示，不做距离检测）
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                
                // 诊断：检查inGame状态
                if (_forceCargoDiagCounter % 60 == 0)
                {
                    _api.Log("Radar: ForceCargo inGame=" + inGame);
                }
                
                if (inGame && !_forceCargoBtn.activeSelf)
                {
                    _forceCargoBtn.SetActive(true);
                    _api.Log("Radar: ForceCargo button SHOWN");
                }
                else if (!inGame && _forceCargoBtn.activeSelf)
                {
                    _forceCargoBtn.SetActive(false);
                }
            }
            catch (Exception e) { _api.Log("Radar: UpdateForceCargoButton error " + e.Message); }
        }

        /// <summary>场景切换时重置商店相关状态（DontDestroyOnLoad导致旧状态残留）。</summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                _cui = null;              // 重置CargoInventoryUI引用（旧场景对象已销毁）
                _shopLayoutInit = false;  // 重置商店布局初始化标志
                _shopBaseX = 186f;        // 重置基准X位置
                _shopRowY[0] = 0f;        // 重置行基准Y位置
                _shopRowY[1] = 0f;
                _shopRowY[2] = 0f;
                _shopScroll = 0f;          // 重置滚动位置
                _shopScrollTarget = 0f;
                _shopMaxScroll = 0f;
                // 原来是 0f（立即触发）：一次转场会连着触发 3 次场景加载（Persistent/Game/Flying），
                // 于是刚加载完的那一帧就要全场景 FindObjectsOfType 三次，而商店 UI 此时根本还没创建
                // —— 三次都是白扫，还都砸在加载最忙的帧上。
                // 改成"至少等 0.5s"：给游戏时间把 UI 建出来，也避开加载帧。
                // 取 max 是为了不打断上面已有的退避（正在退避时保持更长的间隔）。
                _slotCheckT = Mathf.Max(_slotCheckT, 0.5f);
                _airportsInjected = false;  // 关键：重置机场货物注入标志，否则第二次进入场景时不会重新添加雷达/导弹货物
                
                // 重置强制装货按钮（场景切换时旧按钮对象被销毁，需要重新创建）
                try
                {
                    _api.Log("Radar: OnSceneLoaded resetting ForceCargoButton, old btn=" + (_forceCargoBtn != null) + ", created=" + _forceCargoBtnCreated);
                    if (_forceCargoBtn != null)
                    {
                        UnityEngine.Object.Destroy(_forceCargoBtn);
                        _forceCargoBtn = null;
                        _forceCargoBtnComp = null;
                    }
                    _forceCargoBtnCreated = false;
                }
                catch { }
                _api.Log("Radar: shop state reset on scene load (" + scene.name + ")");
                
                // 关键修复：场景加载后立即注入货物，而不是等待0.5秒节流
                // 游戏UI在场景加载时检查cargoType，如果为空就不创建装货按钮
                // 所以必须在游戏UI检查前就给基地添加货物
                try
                {
                    if (InjectAirports())
                    {
                        _airportsInjected = true;
                        _api.Log("Radar: airports injected immediately on scene load");
                    }
                    else
                    {
                        _scanTimer = 0f; // AirportManager未就绪，下一帧立即重试
                        _api.Log("Radar: airports inject deferred (AirportManager not ready)");
                    }
                }
                catch (Exception e) { _api.Log("Radar: immediate inject failed " + e.Message); }
            }
            catch (Exception e) { _api.Log("Radar: scene reset error " + e.Message); }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void DefineTiers()
        {
            _tiers.Add(new RadarTier
            {
                CargoName = "Radar Mk1",
                Weight = 0.1f,
                Space = 15,
                Price = 500f,
                Range = 750f,
                ScanH = 30f,
                ScanV = 20f,
                LockH = 30f,
                ScanRate = 175f
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "Radar Mk2",
                Weight = 0.1f,
                Space = 15,
                Price = 2600f,
                Range = 6800f,
                ScanH = 135f,
                ScanV = 62f,
                LockH = 30f,
                ScanRate = 78f
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "Radar Mk3",
                Weight = 0.1f,
                Space = 18,
                Price = 4800f,
                Range = 8100f,
                ScanH = 138f,
                ScanV = 64f,
                LockH = 31f,
                ScanRate = 88f
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "Radar Mk4",
                Weight = 0.1f,
                Space = 18,
                Price = 9000f,
                Range = 10000f,
                ScanH = 140f,
                ScanV = 65f,
                LockH = 31f,
                ScanRate = 90f
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "Radar Mk5",
                Weight = 0.1f,
                Space = 10,
                Price = 18000f,
                Range = 14000f,
                ScanH = 144f,
                ScanV = 66f,
                LockH = 31f,
                ScanRate = 105f
            });

            // ---- 有源相控阵（AESA）：水平无死角 360°，探测与锁定即时 ----
            _tiers.Add(new RadarTier
            {
                CargoName = "AESA Mk1",
                Weight = 2.0f,
                Space = 15,
                Price = 12000f,
                Range = 5200f,
                ScanH = 180f,
                ScanV = 62f,
                LockH = 30f,
                ScanRate = 9999f,
                IsAesa = true
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "AESA Mk2",
                Weight = 2.0f,
                Space = 10,
                Price = 25000f,
                Range = 7000f,
                ScanH = 180f,
                ScanV = 62f,
                LockH = 32f,
                ScanRate = 9999f,
                IsAesa = true
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "AESA Mk3",
                Weight = 2.0f,
                Space = 8,
                Price = 40000f,
                Range = 8600f,
                ScanH = 180f,
                ScanV = 68f,
                LockH = 33f,
                ScanRate = 9999f,
                IsAesa = true
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "AESA Mk4",
                Weight = 2.0f,
                Space = 8,
                Price = 60000f,
                Range = 10000f,
                ScanH = 180f,
                ScanV = 69f,
                LockH = 33f,
                ScanRate = 9999f,
                IsAesa = true
            });
            _tiers.Add(new RadarTier
            {
                CargoName = "AESA Mk5",
                Weight = 2.0f,
                Space = 200,
                Price = 120000f,
                Range = 21000f,
                ScanH = 180f,
                ScanV = 62f,
                LockH = 30f,
                ScanRate = 9999f,
                IsAesa = true
            });
        }

        // ---------------- 机场商店槽位扩展 ----------------
        private CargoInventoryUI _cui;
        private float _slotCheckT;
        /// <summary>商店 UI 未找到时的退避间隔（秒）。见 EnsureAirportUiSlots 里的说明。</summary>
        private float _cuiMissT;
        private float _shopScroll;      // 商店滚动偏移（<=0，当前平滑位置）
        private float _shopScrollTarget;   // 滚轮目标偏移
        private float _shopMaxScroll;
        private readonly float[] _shopRowY = new float[3];   // 每行基准 Y
        private float _shopBaseX = 186f;
        private bool _shopLayoutInit;
        private static bool ShopDiag;          // 商店槽位诊断日志总开关：false = 稳态零日志，只保留状态跳变
        private float _shopRelayoutT;          // 重排限流计时（性能）
        private static System.Reflection.FieldInfo _shopOfferingField;   // 反射缓存
        private static System.Reflection.FieldInfo _shopAirportField;    // 反射缓存

        // ---------------- 自计时探针：定位菜单里残余的每帧开销在哪个入口 ----------------
        private static readonly System.Diagnostics.Stopwatch _profSw = new System.Diagnostics.Stopwatch();
        private double _profUpdMs, _profLateMs, _profGuiMs;
        private int _profFrames;
        private float _profNextLog;

        private void TickProf()
        {
            _profFrames++;
            if (Time.unscaledTime < _profNextLog) return;
            _profNextLog = Time.unscaledTime + 20f;
            _api.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Radar: prof upd={0:F2}ms late={1:F2}ms gui={2:F2}ms frames={3}",
                _profUpdMs, _profLateMs, _profGuiMs, _profFrames));
            _profUpdMs = _profLateMs = _profGuiMs = 0;
            _profFrames = 0;
        }

        /// <summary>
        /// 游戏机场 UI 的售卖槽位是预置数组（循环上限 = cargoOfferings.Length）。
        /// 当 mod 注入的货物数超过预置槽数时，多出的货物永远没有槽显示（买不到）。
        /// 这里动态克隆槽位补齐，克隆进同一父容器（布局组件会自动重排），原版 Update 随即正常填充。
        /// </summary>
        private void EnsureAirportUiSlots()
        {
            try
            {
                // ★ 节流闸门必须放在最前面。
                //   原来"单例守卫"写在闸门之前，于是 PlaneContainer.Instance / AirportManager.Instance
                //   这两个属性访问变成了**每帧执行**。实测（OptiMod 消融）Radar 在主菜单吃掉 ~25ms/帧，
                //   而它另一个每帧入口（Update/LateUpdate/OnGUI）全是早退的 —— 开销就在这里。
                //   先做一次减法+比较，稳态下每帧只花几个纳秒；真正要做事的频率仍是 0.4s 一次，行为不变。
                _slotCheckT -= Time.deltaTime;
                if (_slotCheckT > 0f) return;
                _slotCheckT = 0.4f;   // 加快补齐：商店打开后新货物槽快速出现

                // 主菜单/非游戏场景守卫：FindObjectsOfType 很贵，只在游戏内扫描
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                if (!inGame) return;

                if (_cui == null)
                {
                    var all = UnityEngine.Object.FindObjectsOfType<CargoInventoryUI>(true);
                    if (all == null || all.Length == 0)
                    {
                        // 商店 UI 不存在 —— 这是最常见的情况（人在飞行，压根没开商店）。
                        // 这里必须退避：直接 return 的话闸门已经把 _slotCheckT 设成 0.4s，
                        // 于是变成"每 0.4 秒一次全场景 FindObjectsOfType 且永远扫不到"。
                        // 场景里 6k~18k 个对象时，等于每 0.4s 白白烧掉一次全场景遍历，
                        // 飞行全程都在付这个钱。指数退避到 5s；一旦真的进商店，
                        // 下一拍就能找到并把退避复位，补齐速度不受影响。
                        _cuiMissT = (_cuiMissT <= 0f) ? 1f : Mathf.Min(_cuiMissT * 2f, 5f);
                        _slotCheckT = _cuiMissT;
                        return;
                    }
                    _cui = all[0];
                    _cuiMissT = 0f;
                    if (ShopDiag) _api.Log("Radar: shop UI found, CargoInventoryUI x" + all.Length);
                }
                if (_cui == null) return;

                // 反射字段缓存（原来是每次调用都 GetField，NonPublic+Instance 的 GetField 并不便宜）
                if (_shopOfferingField == null)
                    _shopOfferingField = typeof(CargoInventoryUI).GetField("cargoOfferings",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (_shopOfferingField == null) return;
                var arr = _shopOfferingField.GetValue(_cui) as CargoOfferingUI[];
                if (arr == null || arr.Length == 0) return;

                // 先把"需要多少槽"算出来：已经够了就立刻返回。
                // 稳态下（绝大多数调用）这里是零日志、零字符串拼接、零多余反射 —— 这是砍掉日志洪泛的关键。
                int max = arr.Length;
                AirportManager am = AirportManager.Instance;
                if (am != null && am.airports != null)
                {
                    foreach (var ap in am.airports)
                    {
                        if (ap == null || ap.cargoType == null) continue;
                        if (ap.cargoType.Length > max) max = ap.cargoType.Length;
                    }
                }
                if (max <= arr.Length) return;

                // 只有真的需要扩展时才走下面的路径（罕见事件），诊断日志只在这一条路径上输出
                _api.Log("Radar: shop slots need extend " + arr.Length + " -> " + max);
                if (ShopDiag)
                {
                    string posInfo2 = "";
                    for (int i = 0; i < Math.Min(3, arr.Length); i++)
                    {
                        if (arr[i] == null) continue;
                        var rt2 = arr[i].GetComponent<RectTransform>();
                        if (rt2 != null)
                            posInfo2 += "(" + rt2.anchoredPosition.x.ToString("F0") + "," + rt2.anchoredPosition.y.ToString("F0") + ")";
                    }
                    _api.Log("Radar: shop diag shopOpen=" + IsAirportUiOpen()
                        + " cuiActive=" + _cui.gameObject.activeSelf
                        + " poses=" + posInfo2);
                }

                // 找带布局的父容器：克隆自动排进网格/列表
                var template = arr[0];
                if (template == null) return;
                var parent = template.transform.parent;
                if (parent == null) return;

                // 父容器布局 + 槽排布规律（无布局组件时需手动定位克隆槽）—— 仅诊断开关打开时输出
                if (ShopDiag)
                {
                    try
                    {
                        var lgs = parent.GetComponentsInChildren<UnityEngine.UI.GridLayoutGroup>(true);
                        var rt = template.GetComponent<RectTransform>();
                        _api.Log("Radar: slot parent=" + parent.name + " layout="
                            + (lgs.Length > 0 ? "Grid" : "none")
                            + " tmpl=" + (rt != null ? rt.anchoredPosition.ToString("F0") : "-")
                            + " size=" + (rt != null ? rt.sizeDelta.ToString("F0") : "-"));
                    }
                    catch { }
                }

                var list = new List<CargoOfferingUI>(arr);
                int need = max - arr.Length;
                int added = 0;
                // 分帧补齐：一次 Instantiate 19 个 UI 槽（每个都是带子物体的一整块 UI）会把
                // "进机场/落地"那一帧顶到 600~900ms。改成每拍只补 SLOT_BATCH 个、并把节流
                // 间隔压到 0.12s：19 个约 0.6s 补齐，观感是槽位逐个出现，但不再有单帧尖峰。
                const int SLOT_BATCH = 4;
                bool partial = need > SLOT_BATCH;
                int budget = partial ? SLOT_BATCH : need;
                if (partial) _slotCheckT = 0.12f;   // 还有缺口 —— 下一拍接着补
                // 无布局组件：手动排布。每列 3 个，第 4 个起横向新列（与第 1 列行位对齐）。
                // 行距复用第 1 列各槽的实际 Y；列距 = 槽宽 + 空隙。
                // 修复：如果模板槽位位置是 (0,0)（游戏重新创建UI导致），使用之前记录的基准位置
                float tmplX = arr[0] != null && arr[0].GetComponent<RectTransform>() != null
                    ? arr[0].GetComponent<RectTransform>().anchoredPosition.x : 0f;
                float baseX = (Mathf.Abs(tmplX) > 1f) ? tmplX : (_shopLayoutInit ? _shopBaseX : 186f);
                float colStep = 420f;
                int rowsPerCol = 3;
                for (int i = 0; i < budget; i++)
                {
                    var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
                    go.SetActive(true);
                    var off = go.GetComponent<CargoOfferingUI>();
                    if (off == null) { UnityEngine.Object.Destroy(go); continue; }
                    var crt = go.GetComponent<RectTransform>();
                    if (crt != null)
                    {
                        int g = arr.Length + i;
                        int row = g % rowsPerCol;
                        int col = g / rowsPerCol;
                        // 修复：如果原槽位Y位置是0（游戏重新创建UI导致），使用记录的行基准
                        float origY = arr[row] != null && arr[row].GetComponent<RectTransform>() != null
                            ? arr[row].GetComponent<RectTransform>().anchoredPosition.y : 0f;
                        float y = (Mathf.Abs(origY) > 1f) ? origY : (_shopLayoutInit ? _shopRowY[row] : -370f - row * 371f);
                        crt.anchoredPosition = new Vector2(baseX + col * colStep, y);
                    }
                    list.Add(off);
                    added++;
                }
                _shopOfferingField.SetValue(_cui, list.ToArray());
                _shopBaseX = baseX;
                _shopLayoutInit = false;   // 下次重排时刷新行基准
                _api.Log("Radar: airport shop slots extended " + arr.Length + " -> " + list.Count + " (+" + added + ")"
                    + " baseX=" + baseX.ToString("F0") + " tmplX=" + tmplX.ToString("F0")
                    + (partial ? " partial(分帧补齐中)" : " done"));
                // 扩展后立即重排所有槽位（包括原有的3个），确保位置正确
                _shopLayoutInit = false;
                RelayoutShopSlots(list.ToArray());
            }
            catch (Exception e) { _api.Log("Radar: slot extend failed " + e.Message); }
        }

        /// <summary>商店槽按"每列 3 个、横向多列"重排，并应用当前横向滚动偏移。</summary>
        private void RelayoutShopSlots(CargoOfferingUI[] arr)
        {
            if (arr == null || arr.Length == 0) return;

            // 第一步：不再禁用父对象布局组件（之前禁用导致第二次进入场景时装货按钮消失）
            // 游戏原有的VerticalLayoutGroup负责装货按钮的创建和布局，禁用会破坏游戏UI逻辑
            // Radar只手动控制槽位位置，不干扰游戏原有的布局组件
            try
            {
                var parent = arr[0].transform.parent;
                if (parent != null)
                {
                    // 确保布局组件是启用的，让游戏原有的UI逻辑正常工作
                    var lgs = parent.GetComponents<UnityEngine.UI.LayoutGroup>();
                    foreach (var lg in lgs) { if (lg != null) lg.enabled = true; }
                }
            }
            catch { }

            // 第二步：强制激活所有槽位（防止游戏只激活前几个槽位）
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null && !arr[i].gameObject.activeSelf)
                    arr[i].gameObject.SetActive(true);
            }

            // 第三步：计算行距基准
            if (!_shopLayoutInit)
            {
                float y0 = -370f, y1 = -741f, y2 = -1112f;
                if (arr.Length > 0 && arr[0] != null && arr[0].GetComponent<RectTransform>() != null)
                {
                    float ty = arr[0].GetComponent<RectTransform>().anchoredPosition.y;
                    if (Mathf.Abs(ty) > 1f) y0 = ty;
                }
                if (arr.Length > 1 && arr[1] != null && arr[1].GetComponent<RectTransform>() != null)
                {
                    float ty = arr[1].GetComponent<RectTransform>().anchoredPosition.y;
                    if (Mathf.Abs(ty) > 1f) y1 = ty;
                }
                if (arr.Length > 2 && arr[2] != null && arr[2].GetComponent<RectTransform>() != null)
                {
                    float ty = arr[2].GetComponent<RectTransform>().anchoredPosition.y;
                    if (Mathf.Abs(ty) > 1f) y2 = ty;
                }
                float step = Mathf.Max(80f, y0 - y1);
                if (y1 > y0) step = Mathf.Max(80f, y1 - y0);
                _shopRowY[0] = y0;
                _shopRowY[1] = y0 - step;
                _shopRowY[2] = y0 - step * 2f;
                float tx = arr[0] != null && arr[0].GetComponent<RectTransform>() != null
                    ? arr[0].GetComponent<RectTransform>().anchoredPosition.x : 0f;
                _shopBaseX = (Mathf.Abs(tx) > 1f) ? tx : 186f;
                _shopLayoutInit = true;
            }

            // 第四步：重排所有槽位位置
            int n = arr.Length;
            int cols = Mathf.Max(1, Mathf.CeilToInt(n / 3f));
            int visibleCols = 3;
            _shopMaxScroll = Mathf.Max(0f, (cols - visibleCols) * 420f);
            for (int i = 0; i < n; i++)
            {
                var off = arr[i];
                if (off == null) continue;
                var rt = off.GetComponent<RectTransform>();
                if (rt == null) continue;
                int row = i % 3;
                int col = i / 3;
                float nx = _shopBaseX + col * 420f + _shopScroll;
                float ny = _shopRowY[row];
                if (Mathf.Abs(rt.anchoredPosition.x - nx) > 0.01f || Mathf.Abs(rt.anchoredPosition.y - ny) > 0.01f)
                    rt.anchoredPosition = new Vector2(nx, ny);
            }

            // 第五步：诊断日志（显示操作后的状态）—— 每次重排都会跑，默认关闭（含 5 次 GetField 反射）
            if (!ShopDiag) return;
            try
            {
                var parent = arr[0].transform.parent;
                if (parent != null)
                {
                    var prt = parent.GetComponent<RectTransform>();
                    var layout = parent.GetComponent<UnityEngine.UI.LayoutGroup>();
                    _api.Log("Radar: SHOP DIAG parent=" + parent.name
                        + " size=" + (prt != null ? prt.rect.size.ToString("F0") : "-")
                        + " layout=" + (layout != null ? layout.GetType().Name + "/" + layout.enabled : "none"));
                }
                var ctf = typeof(CargoOfferingUI).GetField("currentCargoType",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                for (int i = 0; i < Mathf.Min(5, arr.Length); i++)
                {
                    var off = arr[i];
                    if (off == null) continue;
                    var rt = off.GetComponent<RectTransform>();
                    string cargoName = "?";
                    if (ctf != null)
                    {
                        var c = ctf.GetValue(off) as CargoType;
                        if (c != null) cargoName = c.cargoName;
                    }
                    _api.Log("Radar: SHOP slot[" + i + "] pos=" + (rt != null ? rt.anchoredPosition.ToString("F0") : "-")
                        + " active=" + off.gameObject.activeSelf
                        + " cargo=" + cargoName);
                }
            }
            catch (Exception e) { _api.Log("Radar: SHOP DIAG failed " + e.Message); }
        }

        /// <summary>机场商店是否打开（airportUI 为私有字段，用反射读取，FieldInfo 静态缓存避免每帧反射）。</summary>
        private bool IsAirportUiOpen()
        {
            try
            {
                if (_shopAirportField == null)
                    _shopAirportField = typeof(CargoInventoryUI).GetField("airportUI",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (_shopAirportField == null) return false;
                return (bool)_shopAirportField.GetValue(_cui);
            }
            catch { return false; }
        }

        /// <summary>机场商店打开时：强制 3 列网格排布 + 鼠标滚轮横向滚动货物列表（覆盖游戏每帧的 1 列纵排）。</summary>
        private void HandleShopScroll()
        {
            try
            {
                if (_cui == null) return;
                if (!IsAirportUiOpen()) return;
                if (_shopOfferingField == null)
                    _shopOfferingField = typeof(CargoInventoryUI).GetField("cargoOfferings",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (_shopOfferingField == null) return;
                var arr = _shopOfferingField.GetValue(_cui) as CargoOfferingUI[];
                if (arr == null || arr.Length == 0) return;

                // 每帧强制禁用布局组件和激活所有槽位（游戏每帧可能重置这些状态）
                try
                {
                    var parent = arr[0].transform.parent;
                    if (parent != null)
                    {
                        var lgs = parent.GetComponents<UnityEngine.UI.LayoutGroup>();
                        foreach (var lg in lgs) { if (lg != null) lg.enabled = false; }
                    }
                }
                catch { }
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] != null && !arr[i].gameObject.activeSelf)
                        arr[i].gameObject.SetActive(true);
                }

                if (!_shopLayoutInit) RelayoutShopSlots(arr);
                // 商店打开时立即触发槽位补齐检查（不必等下个 0.4s 周期）
                _slotCheckT = 0f;
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) >= 0.01f && arr.Length > 3)
                {
                    _shopScrollTarget = Mathf.Clamp(_shopScrollTarget - wheel * 140f, -_shopMaxScroll, 0f);
                }
                // 平滑滚动（插值，避免跳变卡顿）
                bool rolling = Mathf.Abs(_shopScroll - _shopScrollTarget) > 0.5f;
                if (rolling)
                {
                    _shopScroll = Mathf.Lerp(_shopScroll, _shopScrollTarget, Mathf.Min(1f, Time.unscaledDeltaTime * 12f));
                    if (Mathf.Abs(_shopScroll - _shopScrollTarget) < 0.5f) _shopScroll = _shopScrollTarget;
                }
                // 重排限流：滚动进行中或超过 0.15s 未压才重排（游戏 Update 会把它重置成 1 列纵排）
                _shopRelayoutT -= Time.unscaledDeltaTime;
                if (rolling || _shopRelayoutT <= 0f)
                {
                    _shopRelayoutT = 0.15f;
                    RelayoutShopSlots(arr);
                }
            }
            catch { }
        }

        private void LateUpdate()
        {
            long __t0 = _profSw.ElapsedTicks;
            try { LateUpdateImpl(); }
            finally { _profLateMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        private void LateUpdateImpl()
        {
            // 商店兜底：游戏 UI 的 Update 可能在 mod 之后把槽位重置回 1 列，LateUpdate 限流压回 3 列
            try
            {
                if (_cui == null) return;
                if (!IsAirportUiOpen()) return;
                if (_shopOfferingField == null) return;
                var arr = _shopOfferingField.GetValue(_cui) as CargoOfferingUI[];
                if (arr == null || arr.Length == 0) return;
                _shopRelayoutT -= Time.unscaledDeltaTime;
                if (_shopRelayoutT <= 0f)
                {
                    _shopRelayoutT = 0.15f;
                    RelayoutShopSlots(arr);
                }
            }
            catch { }
        }

        // ---------------- 雷达部件 ----------------

        private void CreateRadarCargo()
        {
            try
            {
                for (int i = 0; i < _tiers.Count; i++)
                {
                    var tier = _tiers[i];
                    var ct = ScriptableObject.CreateInstance<CargoType>();
                    ct.cargoName = tier.CargoName;
                    ct.basePrice = tier.Price;
                    ct.weight = tier.Weight;
                    ct.cargoSpace = tier.Space;
                    ct.fragile = false;
                    ct.expires = false;
                    ct.icon = MakeRadarIcon(i == 0);
                    UnityEngine.Object.DontDestroyOnLoad(ct);
                    _radarTypes.Add(ct);
                    _api.Log("Radar: cargo created " + tier.CargoName + " w=" + tier.Weight + " size=" + tier.Space);
                }
            }
            catch (Exception e) { _api.Log("Radar: create cargo failed " + e.Message); }
        }

        private Texture2D MakeRadarIcon(bool mk1)
        {
            // 优先从外部图片文件加载（mods/Radar/icons/）
            string iconName = mk1 ? "radar.png" : "aesa.png";
            Texture2D fileIcon = LoadIconFromFile(iconName);
            if (fileIcon != null)
            {
                _api.Log("Radar: icon loaded from file " + iconName + " (" + fileIcon.width + "x" + fileIcon.height + ")");
                return fileIcon;
            }
            // 兜底：代码生成图标
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            float c = (64f - 1f) / 2f;
            Color ring = mk1 ? new Color(0.25f, 0.85f, 0.5f, 1f) : new Color(0.3f, 0.65f, 1f, 1f);
            Color sweep = mk1 ? new Color(0.2f, 0.65f, 0.35f, 0.75f) : new Color(0.25f, 0.55f, 0.9f, 0.75f);
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color col = new Color(0.05f, 0.09f, 0.06f, 1f);
                    if (d < 3f) col = new Color(0.8f, 0.95f, 0.85f, 1f);
                    else if (Mathf.Abs(d - c * 0.82f) < 2.2f) col = ring;
                    else
                    {
                        float ang = Mathf.Atan2(dy, dx);
                        float rel = Mathf.Abs(Mathf.Repeat(ang, 2f * Mathf.PI) - 0.5f);
                        if (d < c * 0.72f && rel < 0.35f) col = sweep;
                    }
                    tex.SetPixel(x, y, col);
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>从 mods/Radar/icons/ 目录加载PNG图标文件，失败返回null。</summary>
        private Texture2D LoadIconFromFile(string fileName)
        {
            try
            {
                string iconDir = Path.Combine(_api.GetModsDirectory(), "Radar", "icons");
                string path = Path.Combine(iconDir, fileName);
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(data))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    return tex;
                }
                return null;
            }
            catch (Exception e)
            {
                _api.Log("Radar: load icon failed " + fileName + " - " + e.Message);
                return null;
            }
        }

        private void MakeScopeBg(int size)
        {
            if (_scopeBg != null) return;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1f) / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color col = new Color(0.02f, 0.05f, 0.03f, 0.82f);
                    if (d < 2.5f) col = new Color(0.4f, 0.9f, 0.55f, 1f);
                    else if (Mathf.Abs(d - c * 0.5f) < 1.2f || Mathf.Abs(d - c) < 1.2f) col = new Color(0.18f, 0.55f, 0.3f, 0.8f);
                    else if (Mathf.Abs(dx) < 1f || Mathf.Abs(dy) < 1f) col = new Color(0.12f, 0.4f, 0.22f, 0.7f);
                    tex.SetPixel(x, y, col);
                }
            }
            tex.Apply();
            _scopeBg = tex;
        }

        // ---------------- 机场货物注入 ----------------

        /// <summary>上次诊断打印的"机场货物"签名。内容没变就不重复打 17 行日志。</summary>
        private string _airportDiagSig;

        private bool InjectAirports()
        {
            if (_radarTypes.Count == 0) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var am = AirportManager.Instance;
                if (am == null || am.airports == null) return false;
                int injected = 0;
                foreach (var ap in am.airports)
                {
                    if (ap == null) continue;
                    CargoType[] arr = null;
                    try { arr = ap.cargoType; }
                    catch
                    {
                        var f = typeof(Airport).GetField("cargoType",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (f != null) arr = f.GetValue(ap) as CargoType[];
                    }
                    if (arr == null) continue;
                    bool changed = false;
                    var list = new List<CargoType>(arr);
                    // 移除测试品 Ammo Crate（用户要求删除，无实际作用）
                    for (int k = list.Count - 1; k >= 0; k--)
                    {
                        if (list[k] != null && list[k].cargoName == "Ammo Crate") { list.RemoveAt(k); changed = true; }
                    }
                    foreach (var rt in _radarTypes)
                    {
                        if (!list.Contains(rt)) { list.Add(rt); changed = true; }
                    }
                    if (!changed) continue;
                    var na = list.ToArray();
                    try { ap.cargoType = na; }
                    catch
                    {
                        var f = typeof(Airport).GetField("cargoType",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (f != null) f.SetValue(ap, na);
                    }
                    injected++;
                }
                // 诊断：机场货物现状，只打 1 行摘要。
                //
                // 转场时游戏会连着触发好几次场景加载（Persistent / Game / Flying），
                // 原来每次都给 30 个机场各写一行 10~13 项的长日志 —— 一次转场就是 90+ 行纯噪音，
                // 而且全都落在本来就最忙的那一两帧上（字符串用 += 循环拼，还是 O(n^2)）。
                // 现在：摘要 1 行（机场数 / 货物数区间 / 首个机场样例 / 耗时），
                // 明细只在 ShopDiag 打开时才打。
                int apCount = 0, minCargo = int.MaxValue, maxCargo = 0;
                string sample = "-";
                var sig = new System.Text.StringBuilder();
                foreach (var ap in am.airports)
                {
                    if (ap == null) continue;
                    CargoType[] arr2 = null;
                    try { arr2 = ap.cargoType; } catch { }
                    if (arr2 == null) continue;
                    apCount++;
                    if (arr2.Length < minCargo) minCargo = arr2.Length;
                    if (arr2.Length > maxCargo) maxCargo = arr2.Length;
                    sig.Append(ap.airportName).Append(':').Append(arr2.Length).Append(';');
                    if (apCount == 1)
                    {
                        var sbN = new System.Text.StringBuilder();
                        for (int k = 0; k < arr2.Length; k++)
                            sbN.Append(arr2[k] != null ? arr2[k].cargoName : "null").Append(',');
                        sample = ap.airportName + "[" + sbN + "]";
                    }
                }
                if (apCount == 0) { minCargo = 0; }
                string sigS = sig.ToString();
                bool sigChanged = (sigS != _airportDiagSig);
                _airportDiagSig = sigS;
                _api.Log("Radar: airports scanned=" + apCount + " cargo=" + minCargo + "~" + maxCargo
                         + " injected=" + injected + " changed=" + sigChanged
                         + " in " + sw.ElapsedMilliseconds + "ms sample=" + sample);

                if (sigChanged && ShopDiag)
                {
                    foreach (var ap in am.airports)
                    {
                        if (ap == null) continue;
                        CargoType[] arr2 = null;
                        try { arr2 = ap.cargoType; } catch { }
                        if (arr2 == null) continue;
                        var names = new System.Text.StringBuilder();
                        foreach (var ct in arr2) names.Append(ct != null ? ct.cargoName : "null").Append(',');
                        _api.Log("Radar: airport " + ap.airportName + " cargoType=" + arr2.Length + " [" + names + "]");
                    }
                }
                return true;
            }
            catch (Exception e) { _api.Log("Radar: inject airports failed " + e.Message); return false; }
        }

        // ---------------- 前置联动（反射） ----------------

        private void CheckBattleCore()
        {
            if (_bcChecked) return;
            _bcChecked = true;
            try
            {
                var t = Type.GetType("Machine.BattleCore.Core, BattleCore");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "BattleCore") { t = asm.GetType("Machine.BattleCore.Core"); break; }
                    }
                }
                if (t != null)
                {
                    _bcPost = t.GetMethod("Post", new Type[] { typeof(string), typeof(string), typeof(string), typeof(int) });
                    _bcGet = t.GetMethod("Get", new Type[] { typeof(string), typeof(string) });
                    _bcAvailable = _bcPost != null && _bcGet != null;
                }
            }
            catch { }
            _api.Log("Radar: BattleCore available=" + _bcAvailable);
        }

        private void CheckBattleHold()
        {
            if (_bhChecked) return;
            _bhChecked = true;
            try
            {
                var t = Type.GetType("Machine.BattleHold.BattleHoldApi, BattleHold");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "BattleHold") { t = asm.GetType("Machine.BattleHold.BattleHoldApi"); break; }
                    }
                }
                if (t != null)
                {
                    _bhMarkCombat = t.GetMethod("MarkCombat", new Type[] { typeof(CargoType) });
                    _bhContains = t.GetMethod("Contains", new Type[] { typeof(CargoType) });
                    _bhAvailable = _bhMarkCombat != null && _bhContains != null;
                }
            }
            catch { }
            _api.Log("Radar: BattleHold available=" + _bhAvailable);
        }

        private void MarkRadarCombat()
        {
            if (!_bhAvailable || _bhMarkCombat == null) return;
            try
            {
                foreach (var rt in _radarTypes)
                    _bhMarkCombat.Invoke(null, new object[] { rt });
                _api.Log("Radar: all tiers marked as combat cargo (BattleHold)");
            }
            catch (Exception e) { _api.Log("Radar: mark combat failed " + e.Message); }
        }

        private void CheckVoiceAlerts()
        {
            if (_vaChecked) return;
            _vaChecked = true;
            try
            {
                var t = Type.GetType("VoiceAlertsMod.VoiceAlertsApi, VoiceAlerts");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "VoiceAlerts") { t = asm.GetType("VoiceAlertsMod.VoiceAlertsApi"); break; }
                    }
                }
                if (t != null)
                {
                    _vaPlay = t.GetMethod("Play", new Type[] { typeof(string), typeof(float) });
                    _vaSetRwr = t.GetMethod("SetRwr", new Type[] { typeof(int) });
                    _vaAvailable = _vaPlay != null;
                }
            }
            catch { }
            _api.Log("Radar: VoiceAlerts available=" + _vaAvailable);
        }

        private void PostToBattleCore(string key, string value, int sev)
        {
            if (!_bcChecked) CheckBattleCore();
            if (!_bcAvailable || _bcPost == null) return;
            try { _bcPost.Invoke(null, new object[] { "Radar", key, value, sev }); } catch { }
        }

        private string QueryBattleCore(string source, string key)
        {
            if (!_bcChecked) CheckBattleCore();
            if (!_bcAvailable || _bcGet == null) return null;
            try { return (string)_bcGet.Invoke(null, new object[] { source, key }); }
            catch { return null; }
        }

        private bool RadarInstalled()
        {
            if (!_bhChecked) CheckBattleHold();
            if (!_bhAvailable || _bhContains == null || _radarTypes.Count == 0) return false;
            try
            {
                // 高等级优先：Mk2 > Mk1
                for (int t = _tiers.Count - 1; t >= 0; t--)
                {
                    if (t < _radarTypes.Count && (bool)_bhContains.Invoke(null, new object[] { _radarTypes[t] }))
                    {
                        if (_activeTier != _tiers[t])
                        {
                            _activeTier = _tiers[t];
                            ApplyTierParams();
                            _api.Log("Radar: tier active = " + _activeTier.CargoName);
                        }
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>锁定目标的动态实体引用（无则 null），供导弹制导使用。</summary>
        public Transform LockedRef
        {
            get { return _locked != null ? _locked.Ref : null; }
        }

        private void ApplyTierParams()
        {
            if (_activeTier == null) return;
            _range = _activeTier.Range;
            _scanWidthH = _activeTier.ScanH;
            _scanWidthV = _activeTier.ScanV;
            _lockH = _activeTier.LockH;
            _scanRate = _activeTier.ScanRate;
            _aesa = _activeTier.IsAesa;   // AESA：水平无死角 + 即时探测锁定
            // 扫描线捕捉宽度：大扇区用更宽的捕捉窗（±6° 固定也可，保持简单）
        }

        private void PlayVoice(string name)
        {
            if (!_vaChecked) CheckVoiceAlerts();
            if (!_vaAvailable || _vaPlay == null) return;
            try { _vaPlay.Invoke(null, new object[] { name, 3f }); }
            catch { }
        }

        private void PlayVoiceCd(string name, float cd)
        {
            if (!_vaChecked) CheckVoiceAlerts();
            if (!_vaAvailable || _vaPlay == null) return;
            try { _vaPlay.Invoke(null, new object[] { name, cd }); }
            catch { }
        }

        // ---------------- RWR 雷达告警（仅在被敌机导弹锁定后播报，强度由导弹距离决定） ----------------

        private float _rwrTimer;
        private float _lastMisLog = -99f;
        private float _rwrLastLow, _rwrLastMid, _rwrLastHigh;
        private int _rwrLowTick, _rwrMidTick, _rwrHighTick;
        private System.Reflection.MethodInfo _vaSetRwr;

        // MISSILE 一次性警报：记录已播报过的导弹实例，每颗新导弹只播报一次（可与 RWR 循环同时播放）
        private readonly HashSet<int> _missileSeen = new HashSet<int>();
        private float _missileCleanT = 8f;
        // BANDIT 敌机发现警报：记录已播报过的敌机实例；敌机离开探测范围后从集合移除，重新进入会再次播报
        private readonly HashSet<int> _banditSeen = new HashSet<int>();

        private void StepRwrAlerts()
        {
            if (!_active) return;
            _rwrTimer -= Time.deltaTime;
            if (_rwrTimer > 0f) return;
            _rwrTimer = 0.5f;

            // MISSILE 一次性警报：发现新导弹 → 播报一次（保持原样）
            StepMissileNew();

            // RWR 循环警报：仅在被导弹锁定时播报，强度由导弹距离决定
            //   距离 > 5000m → 低频（LOW）—— 一旦被锁定就触发
            //   距离 2000-5000m → 中频（MID）
            //   距离 < 2000m → 高频（HIGH）
            float lockDist = GetMissileLockDistance();
            int level = 0;
            if (lockDist > 0f)
            {
                if (lockDist > 5000f)
                {
                    _rwrLowTick++;
                    if (_rwrLowTick % 4 == 1) _api.Log("Radar: RWR LOW (missile lock, dist=" + lockDist.ToString("F0") + "m)");
                    level = 1;
                }
                else if (lockDist > 2000f)
                {
                    _rwrMidTick++;
                    if (_rwrMidTick % 4 == 1) _api.Log("Radar: RWR MID (missile lock, dist=" + lockDist.ToString("F0") + "m)");
                    level = 2;
                }
                else
                {
                    _rwrHighTick++;
                    if (_rwrHighTick % 4 == 1) _api.Log("Radar: RWR HIGH (missile lock, dist=" + lockDist.ToString("F0") + "m)");
                    level = 3;
                }
            }
            // 单循环通道：高等级覆盖低等级，0 = 停止；由 VoiceAlerts 管理循环
            if (!_vaChecked) CheckVoiceAlerts();
            if (_vaAvailable && _vaSetRwr != null)
            {
                try { _vaSetRwr.Invoke(null, new object[] { level }); } catch { }
            }
        }

        /// <summary>获取锁定玩家的最近导弹距离；未被锁定返回 0。检测范围 10000m。</summary>
        private float GetMissileLockDistance()
        {
            try
            {
                var mis = FindMissiles();
                Vector3 p = PlanePos();
                float minDist = float.MaxValue;
                for (int i = 0; i < mis.Count; i++)
                {
                    var mt = mis[i];
                    if (mt == null) continue;
                    if (IsFriendlyEntity(mt.gameObject)) continue;
                    Vector3 mpos = mt.position;
                    float dist = Vector3.Distance(p, mpos);
                    if (dist > 10000f) continue;
                    var rb = mt.GetComponent<Rigidbody>();
                    Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
                    if (vel.sqrMagnitude < 4f) continue;
                    Vector3 toPlayer = (p - mpos).normalized;
                    // 导弹朝向玩家（角度 < 30°）即视为锁定
                    if (Vector3.Angle(vel.normalized, toPlayer) < 30f)
                    {
                        if (dist < minDist) minDist = dist;
                    }
                }
                if (minDist < float.MaxValue) return minDist;
            }
            catch { }
            return 0f;
        }

        /// <summary>MISSILE 警报：场景出现一枚新导弹时播报一次（每颗导弹实例只报一次，不循环）。</summary>
        private void StepMissileNew()
        {
            try
            {
                var mis = FindMissiles();
                if (mis.Count == 0)
                {
                    // 场景无导弹：定时清空已播报集合（导弹寿命短，防泄漏）
                    _missileCleanT -= 0.5f;
                    if (_missileCleanT <= 0f) { _missileSeen.Clear(); _missileCleanT = 8f; }
                    return;
                }
                bool anyNew = false;
                for (int i = 0; i < mis.Count; i++)
                {
                    if (mis[i] == null) continue;
                    int id = mis[i].GetInstanceID();
                    if (_missileSeen.Add(id)) anyNew = true;
                }
                if (anyNew) PlayVoice("missile");   // 一次性播报，VoiceAlerts 侧 0.5s 冷却防同帧重复
            }
            catch { }
        }

        /// <summary>BANDIT 敌机发现警报：探测范围（预锁定列表）出现新敌机 → 播报一次；敌机离开探测范围后移除，重新进入再次播报。</summary>
        private void StepBanditNew(List<LockTarget> pre)
        {
            try
            {
                var cur = new HashSet<int>();
                for (int i = 0; i < pre.Count; i++)
                {
                    var t = pre[i];
                    if (t == null || t.IsMissile || t.Ref == null) continue;   // 只报敌机（飞机），导弹走 MISSILE 警报
                    int id = t.Ref.GetInstanceID();
                    cur.Add(id);
                    if (_banditSeen.Add(id))
                    {
                        PlayVoice("bandit");
                        _api.Log("Radar: BANDIT " + t.Name);
                    }
                }
                // 移除已不在探测范围的敌机（重新进入视野会再次播报）
                if (_banditSeen.Count > cur.Count && _banditSeen.Count > 0)
                {
                    _banditSeen.RemoveWhere(id => !cur.Contains(id));
                }
            }
            catch { }
        }

        /// <summary>低频：敌机（BanditAircraft，MachineAAM 程序集）当前正锁定玩家。</summary>
        private static Type _banditType;
        private static System.Reflection.PropertyInfo _banditLocked, _banditTarget;
        private static bool _banditChecked;
        private bool DetectEnemyRadarLock()
        {
            try
            {
                // 类型/属性一次性缓存（避免每 0.5s 遍历全部程序集）
                if (!_banditChecked)
                {
                    _banditChecked = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "MachineAAM") { _banditType = asm.GetType("Machine.AAM.BanditAircraft"); break; }
                    }
                    if (_banditType != null)
                    {
                        _banditLocked = _banditType.GetProperty("LockedNow");
                        _banditTarget = _banditType.GetProperty("TargetIsPlayer");
                    }
                }
                if (_banditType == null) return false;
                var arr = UnityEngine.Object.FindObjectsOfType(_banditType, true);
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i];
                    if (c == null) continue;
                    bool locked = _banditLocked != null ? (bool)_banditLocked.GetValue(c, null) : false;
                    if (!locked) continue;
                    if (_banditTarget != null && (bool)_banditTarget.GetValue(c, null)) return true;
                    // 无 TargetIsPlayer 时兜底：敌机在雷达范围内且朝向玩家即视为在锁定
                    var p = PlanePos();
                    var bc = c as Component;
                    if (bc != null && Vector3.Distance(p, bc.transform.position) < _range) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>反射读取 AI 飞机的模型名（机型，如 F-22）+ 呼号（阵营+编号），格式 "F-22 (Snow-3)"。</summary>
        private string ReadAiCallsign(GameObject go)
        {
            try
            {
                if (go == null) return "AIR";
                var comp = go.GetComponent("Machine.AAM.AiAircraft");
                if (comp == null) comp = go.transform.root.GetComponent("Machine.AAM.AiAircraft");
                if (comp == null) return "AIR";
                const System.Reflection.BindingFlags FL = System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
                string model = "";
                var mf = comp.GetType().GetField("ModelName", FL);
                if (mf != null) { try { var mv = mf.GetValue(comp); if (mv != null) model = mv.ToString(); } catch { } }
                var prop = comp.GetType().GetProperty("Callsign", FL);
                var field = comp.GetType().GetField("Callsign", FL);
                object v = null;
                if (prop != null) { try { v = prop.GetValue(comp, null); } catch { } }
                if (v == null && field != null) { try { v = field.GetValue(comp); } catch { } }
                if (v == null) return model.Length > 0 ? model : "AIR";
                string s = v.ToString();
                if (model.Length > 0) return model + " (" + s + ")";
                if (s.Length == 0) return "AIR";
                return s;
            }
            catch { return "AIR"; }
        }

        /// <summary>刷新锁定目标的速度（节）与机型名。</summary>
        private void RefreshLockedInfo()
        {
            if (_locked == null || _locked.Ref == null) return;
            try
            {
                var rb = _locked.Ref.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    _locked.LastSpeed = rb.linearVelocity.magnitude * 1.94384f;   // m/s -> knots
                    return;
                }
            }
            catch { }
            try
            {
                var pc = _locked.Ref.GetComponent<PlaneContainer>();
                if (pc != null)
                {
                    _locked.LastSpeed = pc.GetVelocityMagintude() * 1.94384f;
                    return;
                }
            }
            catch { }
        }

        /// <summary>反射获取场景中所有导弹 Transform（优先 MachineAAM.AamSystem.GetLiveMissiles，失败兜底 AiEntityMarker.Kind=="missile"）。
        private static System.Reflection.MethodInfo _aamLiveMissiles;
        private static bool _aamChecked;
        private List<Transform> FindMissiles()
        {
            var list = new List<Transform>();
            try
            {
                if (!_aamChecked)
                {
                    _aamChecked = true;
                    Type aamType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "MachineAAM") { aamType = asm.GetType("Machine.AAM.AamSystem"); break; }
                    }
                    if (aamType != null) _aamLiveMissiles = aamType.GetMethod("GetLiveMissiles", Type.EmptyTypes);
                }
                if (_aamLiveMissiles != null)
                {
                    var ret = _aamLiveMissiles.Invoke(null, null) as System.Collections.IEnumerable;
                    if (ret != null)
                    {
                        foreach (var o in ret)
                        {
                            var t = o as Transform;
                            if (t != null) list.Add(t);
                        }
                    }
                }
                if (list.Count > 0)
                {
                    // 验证日志（节流）：导弹探测成功
                    float now = Time.realtimeSinceStartup;
                    if (now - _lastMisLog > 2f)
                    {
                        _lastMisLog = now;
                        _api.Log("Radar: missiles detected=" + list.Count);
                    }
                    return list;
                }
            }
            catch { }

            // 兜底：AiEntityMarker.Kind=="missile"
            try
            {
                Type mkType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "MachineAAM") { mkType = asm.GetType("Machine.AAM.AiEntityMarker"); break; }
                }
                if (mkType == null) return list;
                var kindField = mkType.GetField("Kind");
                var kindProp = mkType.GetProperty("Kind");
                var arr = UnityEngine.Object.FindObjectsOfType(mkType, true);
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i];
                    if (c == null) continue;
                    string kind = null;
                    if (kindField != null) { var kv = kindField.GetValue(c); if (kv != null) kind = kv as string; }
                    else if (kindProp != null) { var kv = kindProp.GetValue(c, null); if (kv != null) kind = kv as string; }
                    if (kind != "missile") continue;
                    var cmp = c as Component;
                    if (cmp == null) continue;
                    list.Add(cmp.transform);
                }
            }
            catch { }
            return list;
        }

        /// <summary>反射读取导弹型号（MissileController.SpecName），失败回退 "MISS"。</summary>
        private static System.Reflection.FieldInfo _mcSpecNameField;
        private static bool _mcChecked;
        private string ReadMissileName(Transform mt)
        {
            try
            {
                if (mt == null) return "MISS";
                var mc = mt.GetComponent("MissileController");
                if (mc == null) return "MISS";
                var t = mc.GetType();
                if (!_mcChecked)
                {
                    _mcChecked = true;
                    _mcSpecNameField = t.GetField("SpecName");
                }
                if (_mcSpecNameField == null) return "MISS";
                var v = _mcSpecNameField.GetValue(mc);
                string s = v as string;
                return string.IsNullOrEmpty(s) ? "MISS" : s;
            }
            catch { return "MISS"; }
        }

        /// <summary>中频：场景中存在非友军导弹且距离玩家较近。</summary>
        private bool DetectMissileNearby()
        {
            try
            {
                var mis = FindMissiles();
                Vector3 p = PlanePos();
                for (int i = 0; i < mis.Count; i++)
                {
                    var mt = mis[i];
                    if (mt == null) continue;
                    if (IsFriendlyEntity(mt.gameObject)) continue;
                    if (Vector3.Distance(p, mt.position) < 5000f) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>高频：非友军导弹正朝玩家逼近（速度方向指向玩家且距离近）。</summary>
        private bool DetectMissileLockedOnPlayer()
        {
            try
            {
                var mis = FindMissiles();
                Vector3 p = PlanePos();
                for (int i = 0; i < mis.Count; i++)
                {
                    var mt = mis[i];
                    if (mt == null) continue;
                    if (IsFriendlyEntity(mt.gameObject)) continue;
                    Vector3 mpos = mt.position;
                    float dist = Vector3.Distance(p, mpos);
                    if (dist > 4000f) continue;
                    var rb = mt.GetComponent<Rigidbody>();
                    Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
                    if (vel.sqrMagnitude < 4f) continue;
                    Vector3 toPlayer = (p - mpos).normalized;
                    if (Vector3.Angle(vel.normalized, toPlayer) < 30f) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>其他 Mod（如空空导弹）发射导弹时调用：播报 Fox 1 并上报。</summary>
        public void OnMissileLaunch()
        {
            if (_foxCooldown > 0f) return;
            _foxCooldown = 3f;
            PlayVoice("fox1");
            _api.Log("Radar: FOX 1 (missile launch)");
            if (!_bcChecked) CheckBattleCore();
            try { if (_bcAvailable && _bcPost != null) _bcPost.Invoke(null, new object[] { "Weapon", "Launch", "Fox 1", 9 }); } catch { }
        }

        // ---------------- 目标收集 ----------------

        private void CollectTargets()
        {
            _targets.Clear();
            try
            {
                var am = AirportManager.Instance;
                if (am != null && am.airports != null)
                {
                    int myFac = PlayerFactionFromApi();
                    int nBlue = 0, nEnemy = 0, nNeutral = 0;
                    foreach (var ap in am.airports)
                    {
                        if (ap == null) continue;
                        // 机场归属 = 它所在的主岛大陆归谁：同一大陆上的普通机场和 Base 一样是友军，
                        // 否则雷达上整个己方大陆只有基地一个蓝点，看着像 Bug。
                        int owner = AirportFactionOf(ap);
                        bool friendly = (owner >= 0 && owner == myFac);
                        if (friendly) nBlue++;
                        else if (owner >= 0) nEnemy++;
                        else nNeutral++;
                        _targets.Add(new LockTarget
                        {
                            Pos = ap.position,
                            Name = string.IsNullOrEmpty(ap.airportName) ? "Airport" : ap.airportName,
                            Ref = null,
                            IsDynamic = false,
                            IsAirport = true,
                            Friendly = friendly,   // 己方阵营大陆上的机场蓝色，不可锁定
                            Faction = owner
                        });
                    }
                    LogAirportFriendlyOnce(nBlue, nEnemy, nNeutral, myFac);
                }
            }
            catch { }

            // 动态实体：场景中其他飞机（AI），友军保留显示（蓝色，不可锁定），敌机可锁定
            try
            {
                var player = PlaneContainer.Instance;
                var all = UnityEngine.Object.FindObjectsByType<PlaneContainer>(UnityEngine.FindObjectsSortMode.None);
                foreach (var p in all)
                {
                    if (p == null || p == player) continue;
                    if (p.transform.position.y < -5f) continue;   // 遁地/钻地目标不显示（修复雷达扫到地底 AI）
                    bool friendly = IsFriendlyEntity(p.gameObject);
                    _targets.Add(new LockTarget
                    {
                        Pos = p.transform.position,
                        Name = "AIR " + (_targets.Count + 1),
                        Ref = p.transform,
                        IsDynamic = true,
                        IsAirport = false,
                        Friendly = friendly,
                        ModelName = ReadAiCallsign(p.gameObject)
                    });
                }

                // 联机：远程玩家（Machine.Core 的 NetSync 远程实体）。
                // 它们**不是** PlaneContainer —— 核心刻意不给远程实体挂 PlaneContainer /
                // PlaneController（前者会抢 Singleton.m_Instance 把玩家飞机顶掉，
                // 后者 container 恒为 null、FixedUpdate 每帧 NRE），
                // 所以上面那句 FindObjectsByType<PlaneContainer> 永远扫不到它们，必须单独收进来。
                try
                {
                    var netSync = Machine.Core.Net.Sync;
                    if (netSync != null)
                    {
                        foreach (var kv in netSync.Remote)
                        {
                            var rp = kv.Value;
                            if (rp == null) continue;
                            Transform rt = rp.Entity != null ? rp.Entity.transform : null;
                            Vector3 rpos = rt != null ? rt.position : rp.Pos;
                            if (rpos.y < -5f) continue;   // 遁地/钻地目标不显示
                            string call = string.IsNullOrEmpty(rp.Call) ? "PLAYER" : rp.Call;
                            string rmodel = (string.IsNullOrEmpty(rp.Model) || rp.Model == "PLAYER")
                                ? call : rp.Model + " (" + call + ")";
                            _targets.Add(new LockTarget
                            {
                                Pos = rpos,
                                Name = "AIR " + (_targets.Count + 1),
                                Ref = rt,
                                IsDynamic = true,
                                IsAirport = false,
                                Friendly = IsRemoteFriendly(rp.Faction),
                                ModelName = rmodel
                            });
                        }
                    }
                }
                catch { }

                // 导弹：MachineAAM 的 AiEntityMarker.Kind=="missile" 飞行物，可探测可锁定
                var mis = FindMissiles();
                for (int i = 0; i < mis.Count; i++)
                {
                    var mt = mis[i];
                    if (mt == null) continue;
                    if (mt.position.y < -5f) continue;   // 遁地导弹不显示（修复雷达扫到地底导弹）
                    // 友军导弹保留显示（蓝色）但不可锁定
                    bool friendly = IsFriendlyEntity(mt.gameObject);
                    _targets.Add(new LockTarget
                    {
                        Pos = mt.position,
                        Name = "MIS " + (_targets.Count + 1),
                        Ref = mt,
                        IsDynamic = true,
                        IsMissile = true,
                        Friendly = friendly,
                        ModelName = ReadMissileName(mt)
                    });
                }
            }
            catch { }
        }

        /// <summary>
        /// FactionApi 类型（跨 mod 只走反射；加载器里的程序集名与 mod 名对不上，
        /// 所以失败时扫所有已加载程序集按类型全名找）。只在成功时缓存，避免阵营系统晚加载时永久拿不到。
        /// </summary>
        private static Type _factionApiType;
        private static Type FactionApiType()
        {
            if (_factionApiType != null) return _factionApiType;
            try
            {
                // 优先挑"带 AirportFaction 的那个 FactionApi"：同名程序集可能有多份
                // （mods/FactionSystem/FactionSystem.dll 是历史遗留的旧版），按名字解析有可能拿到旧的。
                var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (t != null && HasAirportFaction(t)) { _factionApiType = t; return t; }
                Type any = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type x = null;
                    try { x = asm.GetType("Machine.Faction.FactionApi"); } catch { }
                    if (x == null) continue;
                    if (HasAirportFaction(x)) { _factionApiType = x; return x; }
                    if (any == null) any = x;
                }
                // 只在成功时缓存，避免阵营系统晚加载时永久拿不到
                if (any != null) _factionApiType = any;
            }
            catch { }
            return _factionApiType;
        }

        private static bool HasAirportFaction(Type t)
        {
            try
            {
                return t.GetMethod("AirportFaction",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 机场归属阵营 id（-1 = 中立大陆；阵营系统缺失时也是 -1）。
        /// FactionApi.AirportFaction 按"机场落在哪个主岛大陆上"判定 —— 玩家阵营主岛上的机场
        /// （含 7 座普通机场）与基地一样是友军，不再只有基地一个蓝点。
        /// </summary>
        private bool _apFacDiag;
        private int AirportFactionOf(Airport ap)
        {
            try
            {
                if (ap == null) return -1;
                var t = FactionApiType();
                if (t == null)
                {
                    DiagOnce("type<null> (FactionApi not resolvable by reflection)");
                    return -1;
                }
                var m = t.GetMethod("AirportFaction", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null)
                {
                    DiagOnce("method<null> on " + t.FullName + " from " + AsmPath(t));
                    return -1;
                }
                return (int)m.Invoke(null, new object[] { ap });
            }
            catch (Exception e)
            {
                DiagOnce("EX " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        private void DiagOnce(string s)
        {
            if (_apFacDiag) return;
            _apFacDiag = true;
            _api.Log("Radar: AirportFaction diag " + s);
        }

        private static string AsmPath(Type t)
        {
            try { return t.Assembly.Location; } catch { return "?"; }
        }

        /// <summary>玩家阵营 id（阵营系统缺失时按 0）。</summary>
        private static int PlayerFactionFromApi()
        {
            try
            {
                var t = FactionApiType();
                if (t == null) return 0;
                var p = t.GetProperty("PlayerFaction", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p == null) return 0;
                return (int)p.GetValue(null, null);
            }
            catch { return 0; }
        }

        private string _apFriendlySig;
        private int _apFriendlyLogs;
        private float _apFriendlyLogT = -99f;
        /// <summary>
        /// 机场友军分类日志：分类结果变化时打一行（正常每场景一次）。
        /// 阵营系统晚于雷达就绪时（主菜单场景没有 ContinentManager，要进飞行场景才探到大陆），
        /// 第一批会全是 neutral —— 这种"没就绪"的样本每 6s 再采一行、最多 8 行，
        /// 好让日志里能看到它从全中立变成三色的那一刻。
        /// </summary>
        private void LogAirportFriendlyOnce(int blue, int enemy, int neutral, int myFac)
        {
            string sig = myFac + "/" + blue + "/" + enemy + "/" + neutral;
            if (sig == _apFriendlySig) return;
            if (blue == 0 && enemy == 0)
            {
                if (_apFriendlyLogs >= 8 || Time.unscaledTime - _apFriendlyLogT < 6f) return;
            }
            _apFriendlySig = sig;
            _apFriendlyLogs++;
            _apFriendlyLogT = Time.unscaledTime;
            _api.Log("Radar: airport friendly blue=" + blue + " enemy=" + enemy
                     + " neutral=" + neutral + " myFaction=" + myFac);
        }

        /// <summary>是否友军实体（FactionApi.IsFriendlyToPlayer；阵营系统缺失时返回 false）。</summary>
        private bool IsFriendlyEntity(GameObject go)
        {
            try
            {
                var t = FactionApiType();
                if (t == null) return false;
                var m = t.GetMethod("IsFriendlyToPlayer", new Type[] { typeof(GameObject) });
                if (m == null) return false;
                return (bool)m.Invoke(null, new object[] { go });
            }
            catch { return false; }
        }

        /// <summary>
        /// 联机远程玩家是否友军：拿 STATE 下发的阵营号与本机玩家阵营比对
        /// （阵营号由 Machine.Core.NetSync 反射 FactionApi.PlayerFaction 写入）。
        /// 阵营系统没装时按"合作"处理 —— 联机默认是队友，避免把队友当敌机
        /// 播 BANDIT 语音警报并允许锁定。
        /// </summary>
        private bool IsRemoteFriendly(int faction)
        {
            try
            {
                var t = FactionApiType();
                if (t == null) return true;
                var p = t.GetProperty("PlayerFaction",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p == null) return true;
                return faction == (int)p.GetValue(null, null);
            }
            catch { return true; }
        }

        private Vector3 PlanePos()
        {
            var plane = PlaneContainer.Instance;
            return plane != null ? plane.transform.position : Vector3.zero;
        }

        private Vector3 PlaneFwd()
        {
            var plane = PlaneContainer.Instance;
            return plane != null ? plane.transform.forward : Vector3.forward;
        }

        private Transform PlaneTransform()
        {
            var plane = PlaneContainer.Instance;
            return plane != null ? plane.transform : null;
        }

        /// <summary>目标是否在当前扫描锥内（AESA 水平无死角，只受垂直角与距离限制；普通雷达受水平/扫描线限制）。</summary>
        private bool InScanCone(Vector3 worldPos)
        {
            var plane = PlaneContainer.Instance;
            if (plane == null) return false;
            Vector3 to = worldPos - plane.transform.position;
            float dist = to.magnitude;
            if (dist > _range) return false;
            Vector3 local = plane.transform.InverseTransformDirection(to);
            if (local.z < 0f && !_aesa) return false;   // 普通雷达后方半球为盲区；AESA 全向
            float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            if (!_aesa && Mathf.Abs(az) > _scanWidthH) return false;
            float el = Mathf.Atan2(local.y, local.z) * Mathf.Rad2Deg;
            if (Mathf.Abs(el) > _scanWidthV) return false;
            if (!_aesa && Mathf.Abs(Mathf.DeltaAngle(az, _sweepAngle)) > _sweepHalf) return false;
            return true;
        }

        /// <summary>目标是否进入锁定窄角（用于中键锁定与辅助圈范围）。</summary>
        private bool InLockCone(Vector3 worldPos)
        {
            var plane = PlaneContainer.Instance;
            if (plane == null) return false;
            Vector3 to = worldPos - plane.transform.position;
            if (to.magnitude > _range) return false;
            Vector3 local = plane.transform.InverseTransformDirection(to);
            if (local.z < 0f) return false;
            float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float el = Mathf.Atan2(local.y, local.z) * Mathf.Rad2Deg;
            if (Mathf.Abs(az) > _lockH) return false;
            if (Mathf.Abs(el) > _scanWidthV) return false;
            return true;
        }

        // ---------------- 主循环 ----------------

        private void Update()
        {
            if (!_profSw.IsRunning) _profSw.Start();
            long __t0 = _profSw.ElapsedTicks;
            try { UpdateImpl(); }
            finally
            {
                _profUpdMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                TickProf();
            }
        }

        private void UpdateImpl()
        {
            // 纯净模式守卫：原版档不扫描/不处理商店 3 列/不渲染雷达
            if (Machine.Mod.MachineState.PureMode) return;
            // 机场商店槽位扩展（与飞行无关：商店界面 PlaneContainer 为空，任何场景保持 3 列布局）
            EnsureAirportUiSlots();
            HandleShopScroll();
            // 强制装货按钮已被MachineShop mod替代，暂时禁用
            // if (!_forceCargoBtnCreated) CreateForceCargoButton();
            // UpdateForceCargoButton();
            // 观战模式（与雷达无关，随时可用）
            UpdateSpectate();
            // 编辑器/停机坪/主菜单：玩家未进入飞行模式时不扫描、不渲染雷达
            if (!Machine.Mod.MachineState.InFlight()) return;

            // 扫描线：120°/s 在 ±扫描宽内往复
            _sweepAngle += _sweepDir * _scanRate * Time.deltaTime;
            if (_sweepAngle > _scanWidthH) { _sweepAngle = _scanWidthH; _sweepDir = -1f; }
            else if (_sweepAngle < -_scanWidthH) { _sweepAngle = -_scanWidthH; _sweepDir = 1f; }

            // 每帧：预锁定收集（普通扫描区内所有可锁定目标，仅显示不可锁定）+ 自动锁定 + 中键循环切换正式锁定（仅锁定窄角区内目标）
            if (_active)
            {
                _preList.Clear();
                _lockableList.Clear();
                Vector3 ppos = PlanePos();
                for (int i = 0; i < _targets.Count; i++)
                {
                    var t = _targets[i];
                    if (t == null) continue;
                    if (t.IsAirport || t.Friendly || t.IsMissile) continue;   // 机场/友军/导弹不预锁定（只锁敌机）
                    if (!InScanCone(t.Pos)) continue;
                    _preList.Add(t);   // 普通扫描区：可发现/显示，但不可锁定
                    if (InLockCone(t.Pos)) _lockableList.Add(t);   // 锁定窄角区：可锁定
                }

                // ==================== 自动锁定逻辑 ====================
                if (_locked == null)
                {
                    // 第一种情况：当前没有锁定目标，锁定窄角区内有敌机 → 自动锁定第一个进入的敌机
                    if (_lockableList.Count > 0)
                    {
                        _locked = _lockableList[0];
                        PlayVoice("lock");
                        float dd = Vector3.Distance(ppos, _locked.Pos);
                        _api.Log("Radar: AUTO LOCK " + _locked.Name + " dist=" + Mathf.RoundToInt(dd));
                        PostToBattleCore("Lock", _locked.Name + "|" + Mathf.RoundToInt(dd) + "m", 8);
                    }
                }
                else
                {
                    // 检查被锁定的敌机是否还在雷达扫描范围内（普通扫描区）
                    bool lockedInScan = false;
                    for (int i = 0; i < _preList.Count; i++)
                    {
                        if (_preList[i] == _locked) { lockedInScan = true; break; }
                    }

                    if (!lockedInScan)
                    {
                        // 第三种情况：被锁定的敌机逃离了雷达扫描范围
                        if (_lockableList.Count > 0)
                        {
                            // 锁定窄角区内有其他敌机 → 取最近的敌机进行锁定
                            LockTarget nearest = null;
                            float nearestDist = float.MaxValue;
                            for (int i = 0; i < _lockableList.Count; i++)
                            {
                                float d = Vector3.Distance(ppos, _lockableList[i].Pos);
                                if (d < nearestDist) { nearestDist = d; nearest = _lockableList[i]; }
                            }
                            if (nearest != null)
                            {
                                _locked = nearest;
                                PlayVoice("lock");
                                float dd = Vector3.Distance(ppos, _locked.Pos);
                                _api.Log("Radar: RELOCK (target escaped scan) " + _locked.Name + " dist=" + Mathf.RoundToInt(dd));
                                PostToBattleCore("Lock", _locked.Name + "|" + Mathf.RoundToInt(dd) + "m", 8);
                            }
                        }
                        else
                        {
                            // 第四种情况：锁定窄角区内没有其他敌机 → 清除锁定，进入第一种情况的循环（等待新敌机进入锁定窄角区后自动锁定）
                            _api.Log("Radar: LOCK LOST (all targets escaped scan, waiting for new target)");
                            _locked = null;
                        }
                    }
                    // 注意：被锁定的敌机只要还在雷达扫描范围内（_preList），即使离开了锁定窄角区，也不会脱离锁定
                }

                // ==================== 手动切换锁定（中键）====================
                // 第二种情况：有第二架或多架敌机进入雷达扫描范围时，敌机进入时不会触发自动锁定，需要玩家手动切换目标
                if (Input.GetMouseButtonDown(2))
                {
                    if (_lockableList.Count > 0)
                    {
                        int idx = _locked != null ? _lockableList.IndexOf(_locked) : -1;
                        LockTarget next = _lockableList[(idx + 1) % _lockableList.Count];
                        _locked = next;
                        PlayVoice("lock");
                        float dd = Vector3.Distance(ppos, next.Pos);
                        _api.Log("Radar: MANUAL LOCK " + next.Name + " dist=" + Mathf.RoundToInt(dd));
                        PostToBattleCore("Lock", next.Name + "|" + Mathf.RoundToInt(dd) + "m", 8);
                    }
                    else
                    {
                        _api.Log("Radar: no target in lock cone (LockH=" + _lockH + "°)");
                    }
                }

                // RWR 雷达告警（低频/中频/高频）
                StepRwrAlerts();

                // BANDIT 敌机发现警报：探测范围出现新敌机或敌机重新进入范围 → 播报一次
                StepBanditNew(_preList);
            }

            // 0.2s 节流：重活（安装检测、注入、目标收集、上报、Fox1 轮询）
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = 0.5f;

            // 机场货物注入（启动时可能未就绪）
            if (!_airportsInjected)
            {
                if (InjectAirports()) _airportsInjected = true;
                else { _scanTimer = 3f; return; }
            }

            // 目标列表刷新（机场 + 动态实体）
                // 注意：这段代码每 ~0.5s 才执行一次（上面 _scanTimer 节流），所以这里必须按"每次
                // 0.5s"扣时间。原来写的是 -= Time.deltaTime（帧间隔 ~0.033s），实际要 ~30s 才收一次
                // 目标 —— 注释写的 2s 从来没生效过，新敌机/机场颜色要等几十秒才刷新。
                _targetScanTimer -= 0.5f;
                if (_targetScanTimer <= 0f)
                {
                    _targetScanTimer = 2f;   // 2s 一次（原 1s：全场景 FindObjectsOfType 开销大，是卡顿源之一）
                    CollectTargets();
                }

            // 动态锁定目标位置跟随
            if (_locked != null && _locked.IsDynamic && _locked.Ref != null)
                _locked.Pos = _locked.Ref.position;

            // 预锁定目标速度刷新（0.5s 节流，供绿框右侧实时数据）
            _preSpeedTimer -= Time.deltaTime;
            if (_preSpeedTimer <= 0f)
            {
                _preSpeedTimer = 0.5f;
                for (int i = 0; i < _preList.Count; i++)
                {
                    var pt = _preList[i];
                    if (pt == null || pt.Ref == null) continue;
                    try
                    {
                        var rb = pt.Ref.GetComponent<Rigidbody>();
                        if (rb != null) pt.LastSpeed = rb.linearVelocity.magnitude * 1.94384f;
                        else
                        {
                            var pc = pt.Ref.GetComponent<PlaneContainer>();
                            if (pc != null) pt.LastSpeed = pc.GetVelocityMagintude() * 1.94384f;
                        }
                    }
                    catch { }
                }
            }

            bool has = RadarInstalled();
            if (has != _active)
            {
                _active = has;
                _api.Log("Radar: " + (_active ? "ACTIVE" : "offline (no radar in combat hold)"));
                PostToBattleCore("Status", _active ? ("Active " + (_activeTier != null ? _activeTier.CargoName : "?")) : "Offline", _active ? 3 : 2);
                if (!_active) _locked = null;
            }

            // 机场商店槽位扩展已上移到 Update 开头（任何场景保持 3 列布局）

            if (!_active)
            {
                PostToBattleCore("Status", "Offline", 2);
                return;
            }

            // 锁定信息上报（变化时）
            if (_locked != null)
            {
                float dist = Vector3.Distance(PlanePos(), _locked.Pos);
                string report = _locked.Name + "|" + Mathf.RoundToInt(dist) + "m";
                if (report != _lastReport)
                {
                    _lastReport = report;
                    PostToBattleCore("Target", report, 5);
                    PostToBattleCore("Status", "Active " + (_activeTier != null ? _activeTier.CargoName : "?"), 3);
                }
            }

            // Fox 1：轮询 BattleCore 的 Weapon|Launch（workbuddy 导弹 Mod 上报），变化即播报
            _bcPollTimer -= Time.deltaTime;
            if (_bcPollTimer <= 0f)
            {
                _bcPollTimer = 0.5f;
                string info = QueryBattleCore("Weapon", "Launch");
                if (!string.IsNullOrEmpty(info) && info != _lastLaunchInfo)
                {
                    _lastLaunchInfo = info;
                    OnMissileLaunch();
                }
            }
        }

        /// <summary>雷达盘 Cycle：切换下一个可锁定目标（用于锁定切换）。</summary>
        private void CycleTarget()
        {
            // 只循环可锁定目标（机场/友军不可锁定——只锁敌机）
            if (_targets.Count == 0) return;
            if (_current == null)
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    if (_targets[i] == null || _targets[i].IsAirport || _targets[i].Friendly) continue;
                    _current = _targets[i];
                    return;
                }
                return;
            }
            int idx = _targets.IndexOf(_current);
            for (int step = 1; step <= _targets.Count; step++)
            {
                int j = (idx + step) % _targets.Count;
                if (_targets[j] == null || _targets[j].IsAirport || _targets[j].Friendly) continue;
                _current = _targets[j];
                return;
            }
            _current = null;
        }

        // ---------------- HUD 绘制（OnGUI） ----------------

        private void OnGUI()
        {
            long __t0 = _profSw.ElapsedTicks;
            try { OnGuiImpl(); }
            finally { _profGuiMs += (_profSw.ElapsedTicks - __t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        private void OnGuiImpl()
        {
            // 纯净模式守卫：原版档不绘制雷达/商店/导弹标记等
            if (Machine.Mod.MachineState.PureMode) return;
            // 编辑器/停机坪：不绘制雷达 HUD
            if (!Machine.Mod.MachineState.InFlight()) return;
            // 观战 HUD（与雷达激活无关）
            DrawSpectateHud();

            if (!_active) return;

            // 首次绘制时设置默认位置为右下角
            if (!_scopePosInitialized)
            {
                _scopeRect.x = Screen.width - _scopeRect.width - 20f;
                _scopeRect.y = Screen.height - _scopeRect.height - 20f;
                _scopePosInitialized = true;
            }

            HandleScopeDrag();
            MakeScopeBg(256);

            Rect scope = _scopeRect;
            float size = scope.width;

            GUI.DrawTexture(scope, _scopeBg);

            // 扇区目标点（前向朝上，左右 ±30°、上下 ±20°）
            var plane = PlaneContainer.Instance;
            if (plane != null)
            {
                Vector3 pos = plane.transform.position;
                for (int i = 0; i < _targets.Count; i++)
                {
                    var t = _targets[i];
                    if (t == null) continue;
                    Vector3 to = t.Pos - pos;
                    float dist = to.magnitude;
                    if (dist > _range) continue;
                    Vector3 local = plane.transform.InverseTransformDirection(to);
                    if (local.z <= 0f) continue;
                    float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                    float el = Mathf.Atan2(local.y, local.z) * Mathf.Rad2Deg;
                    if (Mathf.Abs(az) > _scanWidthH || Mathf.Abs(el) > _scanWidthV) continue;
                    float r = (dist / _range) * size * 0.5f;
                    float px = scope.x + size * 0.5f + (az / _scanWidthH) * size * 0.42f;
                    float py = scope.y + size * 0.5f - (r) + (el / _scanWidthV) * size * 0.1f;
                    bool isLocked = _locked == t;
                    bool isCur = _current == t;
                    // 形状与颜色：机场=方块，飞机/导弹=三角；友军/己方基地=蓝色（不可锁定），敌机=绿/锁定红
                    if (t.IsAirport)
                    {
                        float d = isLocked ? 7f : (isCur ? 5f : 3.5f);
                        // 机场三种：己方阵营大陆=蓝、中立大陆（Seaport/Hermit/Prison）=灰、敌方阵营大陆=绿
                        if (t.Friendly) GUI.color = new Color(0.3f, 0.55f, 1f, 1f);
                        else if (t.Faction < 0) GUI.color = new Color(0.58f, 0.6f, 0.64f, 0.9f);
                        else GUI.color = isLocked ? new Color(1f, 0.3f, 0.2f, 1f) : (isCur ? new Color(0.4f, 1f, 0.5f, 1f) : new Color(0.3f, 0.85f, 0.45f, 0.85f));
                        GUI.DrawTexture(new Rect(px - d, py - d, d * 2f, d * 2f), UiCoreWhite());
                    }
                    else
                    {
                        float d = isLocked ? 9f : (isCur ? 7f : 5.5f);
                        if (t.Friendly)
                        {
                            // 友军：蓝色方框（实时显示，不可锁定）
                            GUI.color = new Color(0.3f, 0.55f, 1f, 1f);
                            GUI.DrawTexture(new Rect(px - d, py - d, d * 2f, 2f), UiCoreWhite());
                            GUI.DrawTexture(new Rect(px - d, py + d - 2f, d * 2f, 2f), UiCoreWhite());
                            GUI.DrawTexture(new Rect(px - d, py - d, 2f, d * 2f), UiCoreWhite());
                            GUI.DrawTexture(new Rect(px + d - 2f, py - d, 2f, d * 2f), UiCoreWhite());
                        }
                        else if (t.IsMissile) GUI.color = isLocked ? new Color(1f, 0.9f, 0.2f, 1f) : new Color(1f, 0.45f, 0.25f, 0.95f);
                        else GUI.color = isLocked ? new Color(1f, 0.3f, 0.2f, 1f) : (isCur ? new Color(0.4f, 1f, 0.5f, 1f) : new Color(0.3f, 0.85f, 0.45f, 0.85f));
                        if (!t.Friendly)
                            GUI.DrawTexture(new Rect(px - d * 0.62f, py - d * 0.55f, d * 1.24f, d * 1.1f), UiTriangle());
                    }
                }
            }

            // 扫描线（当前方位，前向朝上）；AESA 全向即时探测，无扫描点指示
            if (!_aesa)
            {
                float sx = scope.x + size * 0.5f + (Mathf.Sin(_sweepAngle * Mathf.Deg2Rad) * size * 0.5f);
                float sy = scope.y + size * 0.5f - (Mathf.Cos(_sweepAngle * Mathf.Deg2Rad) * size * 0.5f);
                GUI.color = new Color(0.5f, 1f, 0.6f, 0.9f);
                GUI.DrawTexture(new Rect(sx - 2f, sy - 2f, 4f, 4f), UiCoreWhite());
                GUI.color = Color.white;
            }

            string tierName = _activeTier != null ? _activeTier.CargoName : "RADAR";
            GUI.Label(new Rect(scope.x + 6f, scope.y + 4f, 200f, 18f), UiCoreSafe(tierName) + "  " + Mathf.RoundToInt(_sweepAngle) + "d  R" + Mathf.RoundToInt(_range), LabelStyle());
            // 锁定行：机型 + 速度（节）
            string lockTxt = "NO LOCK";
            if (_locked != null)
            {
                RefreshLockedInfo();
                string model = string.IsNullOrEmpty(_locked.ModelName) ? _locked.Name : _locked.ModelName;
                lockTxt = "LOCK " + UiCoreSafe(model) + "  " + Mathf.RoundToInt(_locked.LastSpeed) + "kt";
            }
            GUI.Label(new Rect(scope.x + 6f, scope.y + size - 44f, 240f, 18f), lockTxt, LabelStyle());

            if (GUI.Button(new Rect(scope.x + size - 84f, scope.y + size - 26f, 78f, 20f), "Cycle"))
                CycleTarget();

            // 屏幕中央绿色辅助圈 + 苏式空战 HUD（速度/航向/高度/仰角/姿态/导弹余量）
            DrawCombatHud();

            // 预锁定目标：绿色方框 + 右侧实时数据（速度/距离/机型）
            DrawPreLockMarkers();

            // 锁定目标标记（红点+距离，红色方框）
            DrawLockMarker();

            // 导弹自动扫描红框（屏幕中标记所有敌方导弹）
            DrawMissileBoxes();

            // 锁定窄角雷达画面（军事测绘绿风格）
            DrawLockScope();
        }

        private Texture2D _lockScopeBg;
        private Texture2D LockScopeBg()
        {
            if (_lockScopeBg != null) return _lockScopeBg;
            int W = 160, H = 300;   // 竖矩形
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            Color bg = new Color(0.03f, 0.09f, 0.05f, 0.93f);
            Color grid = new Color(0.14f, 0.42f, 0.24f, 0.5f);
            Color axis = new Color(0.25f, 0.68f, 0.35f, 0.85f);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color c = bg;
                    float nx = (x - W / 2f) / (W / 2f);
                    float ny = (y - H / 2f) / (H / 2f);
                    float gx = Mathf.Abs(nx % 0.2f), gy = Mathf.Abs(ny % 0.2f);
                    if (gx < 0.01f || gy < 0.01f) c = grid;
                    if (Mathf.Abs(nx) < 0.012f || Mathf.Abs(ny) < 0.012f) c = axis;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            _lockScopeBg = tex;
            return tex;
        }

        /// <summary>锁定窄角雷达画面（竖矩形，军事测绘绿风格）：宽度对应锁定窄角 ±LockH，高度对应垂直扫描 ±ScanV。</summary>
        private void DrawLockScope()
        {
            if (!_active) return;
            float w = 170f, h = 300f;
            Rect r = new Rect(_scopeRect.x + _scopeRect.width + 14f, _scopeRect.y, w, h);
            GUI.DrawTexture(r, LockScopeBg());
            var plane = PlaneContainer.Instance;
            if (plane == null) return;
            Vector3 ppos = plane.transform.position;
            float cxp = r.x + w * 0.5f, cyp = r.y + h * 0.5f;
            GUI.color = new Color(0.4f, 0.95f, 0.55f, 1f);
            GUI.Label(new Rect(r.x + 4f, r.y + 2f, 140f, 14f), "LOCK " + Mathf.RoundToInt(_lockH) + "d", LabelStyle());
            GUI.color = Color.white;

            // 目标：预锁定列表 + 正式锁定（若不在预锁定内）
            for (int i = 0; i < _preList.Count; i++)
            {
                var t = _preList[i];
                if (t == null) continue;
                DrawLockScopeTargetRect(t, ppos, plane.transform, cxp, cyp, w, h);
            }
            if (_locked != null && !_preList.Contains(_locked))
                DrawLockScopeTargetRect(_locked, ppos, plane.transform, cxp, cyp, w, h);
        }

        private void DrawLockScopeTargetRect(LockTarget t, Vector3 ppos, Transform planeT, float cxp, float cyp, float w, float h)
        {
            Vector3 to = t.Pos - ppos;
            float dist = to.magnitude;
            if (dist > _range) return;
            // 用水平坐标系计算方位角（忽略飞机滚转影响）
            Vector3 toH = new Vector3(to.x, 0f, to.z);
            Vector3 fwdH = new Vector3(planeT.forward.x, 0f, planeT.forward.z);
            if (toH.magnitude < 0.1f) toH = planeT.forward;
            if (fwdH.magnitude < 0.1f) fwdH = Vector3.forward;
            float az = Vector3.SignedAngle(fwdH, toH, Vector3.up);
            float el = Mathf.Atan2(to.y, toH.magnitude) * Mathf.Rad2Deg;
            // 非AESA雷达：后方目标不显示
            if (!_aesa && Mathf.Abs(az) > 90f) return;
            if (Mathf.Abs(az) > _lockH + 2f) return;   // 锁定窄角外不显示
            // 竖矩形投影：水平角映射宽度，垂直角映射高度
            float px = cxp + (az / Mathf.Max(1f, _lockH)) * (w * 0.44f);
            float py = cyp - (el / Mathf.Max(1f, _scanWidthV)) * (h * 0.44f);
            if (t == _locked) GUI.color = new Color(1f, 0.3f, 0.2f, 1f);
            else if (t.Friendly) GUI.color = new Color(0.3f, 0.6f, 1f, 1f);
            else if (t.IsMissile) GUI.color = new Color(1f, 0.6f, 0.25f, 1f);
            else GUI.color = new Color(0.4f, 1f, 0.5f, 1f);
            GUI.DrawTexture(new Rect(px - 2.5f, py - 2.5f, 5f, 5f), UiCoreWhite());
            GUI.color = Color.white;
        }

        /// <summary>预锁定目标（敌机/敌方导弹）绿色方框 + 友军蓝色方框（实时，雷达位置共享）+ 右侧实时速度/距离/机型。</summary>
        private void DrawPreLockMarkers()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var plane = PlaneContainer.Instance;
            if (plane == null) return;
            Vector3 ppos = plane.transform.position;

            // 友军：游戏画面蓝色方框（实时显示，位置共享）
            for (int i = 0; i < _targets.Count; i++)
            {
                var ft = _targets[i];
                if (ft == null || !ft.Friendly || !ft.IsDynamic || ft.Ref == null) continue;
                Vector3 fsp = cam.WorldToScreenPoint(ft.Pos);
                if (fsp.z <= 0f) continue;
                float fgx = fsp.x;
                float fgy = Screen.height - fsp.y;
                float fb = 24f;
                GUI.color = new Color(0.3f, 0.6f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(fgx - fb / 2f, fgy - fb / 2f, fb, 1.5f), UiCoreWhite());
                GUI.DrawTexture(new Rect(fgx - fb / 2f, fgy + fb / 2f - 1.5f, fb, 1.5f), UiCoreWhite());
                GUI.DrawTexture(new Rect(fgx - fb / 2f, fgy - fb / 2f, 1.5f, fb), UiCoreWhite());
                GUI.DrawTexture(new Rect(fgx + fb / 2f - 1.5f, fgy - fb / 2f, 1.5f, fb), UiCoreWhite());
                GUI.color = Color.white;
            }

            // 预锁定敌机：绿色方框 + 右侧实时数据
            if (_preList.Count == 0) return;
            for (int i = 0; i < _preList.Count; i++)
            {
                var t = _preList[i];
                if (t == null) continue;
                if (t == _locked) continue;   // 正式锁定目标用红框表示
                Vector3 sp = cam.WorldToScreenPoint(t.Pos);
                if (sp.z <= 0f) continue;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                float dist = Vector3.Distance(ppos, t.Pos);
                // 绿色方框（细边框）
                float bs = 30f;
                GUI.color = new Color(0.3f, 1f, 0.4f, 0.92f);
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, bs, 1.5f), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy + bs / 2f - 1.5f, bs, 1.5f), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, 1.5f, bs), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx + bs / 2f - 1.5f, gy - bs / 2f, 1.5f, bs), UiCoreWhite());
                // 右侧数据：机型  速度kt  距离m
                GUI.color = new Color(0.75f, 1f, 0.82f, 1f);
                GUI.Label(new Rect(gx + bs / 2f + 8f, gy - 9f, 260f, 18f),
                          t.ModelName + "  " + Mathf.RoundToInt(t.LastSpeed) + "kt  " + Mathf.RoundToInt(dist) + "m");
                GUI.color = Color.white;
            }
        }

        private void HandleScopeDrag()
        {
            var ev = Event.current;
            if (ev == null) return;
            if (ev.type == EventType.MouseDown && ev.button == 0 && _scopeRect.Contains(ev.mousePosition))
            {
                // 避开 Cycle 按钮区域
                var btn = new Rect(_scopeRect.x + _scopeRect.width - 84f, _scopeRect.y + _scopeRect.height - 26f, 78f, 20f);
                if (!btn.Contains(ev.mousePosition))
                {
                    _dragging = true;
                    _dragOffset = ev.mousePosition - new Vector2(_scopeRect.x, _scopeRect.y);
                }
            }
            else if (ev.type == EventType.MouseUp)
            {
                _dragging = false;
            }
            if (_dragging && ev.type == EventType.MouseDrag)
            {
                _scopeRect.position = ev.mousePosition - _dragOffset;
                ev.Use();
            }
        }

        private void DrawAimCircle()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float radius = AimCircleRadiusPx();
            if (_aimRing == null || _aimRingTexSize != 512)
            {
                _aimRingTexSize = 512;
                _aimRing = MakeRingTexture(512, 0.012f);   // 细辅助圈（用户：太粗了）
            }
            GUI.color = new Color(0.2f, 1f, 0.35f, 0.8f);
            GUI.DrawTexture(new Rect(cx - radius, cy - radius, radius * 2f, radius * 2f), _aimRing);
            GUI.color = Color.white;
        }

        // =====================================================================
        // 苏式空战 HUD（辅助战斗部）：速度/航向标/高度尺/仰角尺/基准线/瞄准十字/姿态/会动的辅助圆/导弹余量
        // =====================================================================

        private GUIStyle _hudBig, _hudMed, _hudSm;
        private GUIStyle HudBig()
        {
            if (_hudBig == null)
            {
                _hudBig = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
                _hudBig.normal.textColor = new Color(0.25f, 1f, 0.45f, 1f);
            }
            return _hudBig;
        }
        private GUIStyle HudMed()
        {
            if (_hudMed == null)
            {
                _hudMed = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                _hudMed.normal.textColor = new Color(0.25f, 1f, 0.45f, 1f);
            }
            return _hudMed;
        }
        private GUIStyle HudSm()
        {
            if (_hudSm == null)
            {
                _hudSm = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                _hudSm.normal.textColor = new Color(0.25f, 1f, 0.45f, 1f);
            }
            return _hudSm;
        }

        /// <summary>苏式空战 HUD 总绘制（数据实时更新，整体集中于屏幕中央区域）。</summary>
        private void DrawCombatHud()
        {
            try
            {
                var plane = PlaneContainer.Instance;
                if (plane == null) return;
                // 飞行中才显示
                var pc = plane.GetComponent<PlaneController>();
                if (pc != null)
                {
                    try
                    {
                        var pr = pc.GetType().GetProperty("FlightModeInitialized");
                        if (pr != null && !(bool)pr.GetValue(pc, null)) return;
                    }
                    catch { }
                }
                if (_aimRing == null || _aimRingTexSize != 512)
                {
                    _aimRingTexSize = 512;
                    _aimRing = MakeRingTexture(512, 0.012f);
                }

                float W = Screen.width, H = Screen.height;
                float cx = W * 0.5f, cy = H * 0.5f;
                Vector3 pos = plane.transform.position;
                float yaw = plane.transform.eulerAngles.y;
                float pitch = plane.transform.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f;

                // 1. 速度（节）：高度尺上方（紧贴中心，不与其他标尺重叠）
                float spd = 0f;
                try { spd = plane.GetVelocity().magnitude * 1.94384f; } catch { }   // m/s -> 节
                GUI.Label(new Rect(cx - W * 0.155f, cy - H * 0.20f, 96f, 26f), Mathf.RoundToInt(spd).ToString(), HudBig());

                // 2. 水平航向标：基准线上方（紧凑）
                float topY = cy - H * 0.11f;
                for (int deg = -30; deg <= 30; deg += 5)
                {
                    float a = yaw + deg;
                    float x = cx + deg * 2.6f;
                    bool major = (Mathf.Abs(deg) % 10 == 0);
                    GUI.DrawTexture(new Rect(x, topY + 10f, 2f, major ? 9f : 5f), UiCoreWhite());
                    if (major)
                    {
                        int n = ((Mathf.RoundToInt(a) % 360) + 360) % 360;
                        GUI.Label(new Rect(x - 13f, topY - 5f, 26f, 15f), (n / 10).ToString("D2"), HudSm());
                    }
                }
                // 中心航向指针
                GUI.DrawTexture(new Rect(cx - 1.5f, topY + 6f, 3f, 7f), UiCoreWhite());
                // 底部一条长横线
                GUI.DrawTexture(new Rect(cx - 78f, topY + 20f, 156f, 2f), UiCoreWhite());

                // 3. 与被锁定目标的高度差 + 距离：基准线右上方（紧凑）
                if (_locked != null)
                {
                    float dh = _locked.Pos.y - pos.y;
                    float dist = Vector3.Distance(_locked.Pos, pos);
                    GUI.Label(new Rect(cx + W * 0.12f, cy - H * 0.12f, 160f, 32f), Mathf.RoundToInt(dh).ToString(), HudBig());
                    GUI.Label(new Rect(cx + W * 0.10f, cy - H * 0.08f, 180f, 20f), Mathf.RoundToInt(dist) + "p", HudMed());
                }

                // 4. 左侧高度尺（短标尺，随高度滚动）
                float alt = pos.y;
                float lx = cx - W * 0.115f;
                float ly = cy - H * 0.16f, lh = H * 0.32f;
                GUI.DrawTexture(new Rect(lx, ly, 2f, lh), UiCoreWhite());
                float scroll = (alt % 100f) / 100f * 24f;
                for (int i = -6; i <= 6; i++)
                {
                    float y = ly + lh * 0.5f - i * 24f + scroll;
                    if (y < ly || y > ly + lh) continue;
                    GUI.DrawTexture(new Rect(lx - 5f, y, 5f, 2f), UiCoreWhite());
                    if (i % 2 == 0)
                    {
                        int hv = (Mathf.RoundToInt(alt / 100f) * 100 + i * 100);
                        GUI.Label(new Rect(lx - 44f, y - 8f, 40f, 15f), hv.ToString(), HudSm());
                    }
                }
                GUI.Label(new Rect(lx - 50f, ly + lh * 0.5f - 10f, 46f, 20f),
                          Mathf.RoundToInt(alt).ToString(), HudMed());

                // 5. 右侧仰角尺（短标尺，随俯仰滚动）
                float rx = cx + W * 0.115f;
                float ry = cy - H * 0.16f, rh = H * 0.32f;
                GUI.DrawTexture(new Rect(rx, ry, 2f, rh), UiCoreWhite());
                float pscroll = (pitch % 10f) / 10f * 24f;
                for (int i = -6; i <= 6; i++)
                {
                    float y = ry + rh * 0.5f - i * 24f + pscroll;
                    if (y < ry || y > ry + rh) continue;
                    GUI.DrawTexture(new Rect(rx + 3f, y, 5f, 2f), UiCoreWhite());
                    if (i % 2 == 0)
                    {
                        int pv = (Mathf.RoundToInt(pitch / 10f) * 10 + i * 10);
                        GUI.Label(new Rect(rx + 12f, y - 8f, 42f, 15f), pv.ToString("D2"), HudSm());
                    }
                }

                // 6. 中心基准线（短横线，与地面平行）
                float baseY = cy;
                GUI.DrawTexture(new Rect(cx - W * 0.09f, baseY, W * 0.18f, 2f), UiCoreWhite());

                // 7. 小十字（瞄准辅助）+ 反丁字（机身姿态）
                float ccx = cx;
                GUI.DrawTexture(new Rect(ccx - 9f, baseY - 1f, 18f, 2f), UiCoreWhite());
                GUI.DrawTexture(new Rect(ccx - 1f, baseY - 9f, 2f, 18f), UiCoreWhite());
                GUI.DrawTexture(new Rect(ccx - 6f, baseY + 13f, 12f, 2f), UiCoreWhite());
                GUI.DrawTexture(new Rect(ccx - 1f, baseY + 15f, 2f, 6f), UiCoreWhite());

                // 8. 辅助圆（会动：机头方向投影到屏幕，指示飞机将飞向哪里）
                DrawFpmCircle(plane);

                // 9. 正下方：剩余每种导弹可发射数，选中者下方画三角形
                DrawMissileRack();

                // 10. 左下 ATK 攻击模式装饰（紧贴中心左）
                GUI.Label(new Rect(cx - W * 0.15f, cy + H * 0.16f, 70f, 22f), "ATK", HudMed());

                // 11. 距海平面高度（紧贴中心右）
                GUI.Label(new Rect(cx + W * 0.11f, cy + H * 0.16f, 100f, 26f), Mathf.RoundToInt(alt).ToString(), HudBig());
            }
            catch { }
        }

        /// <summary>会动的辅助圆：机头方向（速度矢量方向）投影到屏幕，帮观察者判断飞机将要飞向哪里。</summary>
        private void DrawFpmCircle(PlaneContainer plane)
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return;
                Vector3 dir = plane.transform.forward;
                Vector3 sp = cam.WorldToScreenPoint(plane.transform.position + dir * 80f);
                if (sp.z < 0f) return;   // 机头朝镜头外（背对）时不画
                sp.y = Screen.height - sp.y;
                float r = 24f;
                GUI.color = new Color(0.25f, 1f, 0.45f, 0.9f);
                GUI.DrawTexture(new Rect(sp.x - r, sp.y - r, r * 2f, r * 2f), _aimRing);
                GUI.DrawTexture(new Rect(sp.x - 1.5f, sp.y - 1.5f, 3f, 3f), UiCoreWhite());
                GUI.color = Color.white;
            }
            catch { }
        }

        /// <summary>正下方导弹余量：每种导弹剩余可发射数，当前选中者下方画三角。</summary>
        private void DrawMissileRack()
        {
            try
            {
                string lines = "";
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
                if (lines.Length == 0) return;
                var rows = lines.Split(new char[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                float W = Screen.width;
                float slot = 48f;
                float x0 = W * 0.5f - rows.Length * slot * 0.5f;
                float y = Screen.height * 0.665f;   // 基准线正下方（紧凑）
                for (int i = 0; i < rows.Length; i++)
                {
                    var p = rows[i].Split('|');
                    if (p.Length < 4) continue;
                    bool sel = p[2] == "1";
                    int cnt = 0; int.TryParse(p[3], out cnt);
                    GUI.color = sel ? new Color(1f, 0.82f, 0.15f, 1f) : new Color(0.25f, 1f, 0.45f, 0.85f);
                    GUI.Label(new Rect(x0 + i * slot, y, slot, 22f), cnt.ToString(), HudMed());
                    if (sel)
                    {
                        // 选中者下方小三角
                        GUI.DrawTexture(new Rect(x0 + i * slot + slot * 0.5f - 5f, y + 22f, 10f, 6f), UiTriangle());
                    }
                }
                GUI.color = Color.white;
            }
            catch { }
        }

        /// <summary>
        /// 辅助圈半径：按当前雷达锁定窄角投影到屏幕，与锁定扫描范围视觉一致；
        /// 但限制最大尺寸，避免占满屏幕影响视线。
        /// </summary>
        private float AimCircleRadiusPx()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return 60f;
                float fovV = cam.fieldOfView;
                if (fovV < 10f || fovV > 120f) fovV = 60f;
                float f = (Screen.height * 0.5f) / Mathf.Tan(fovV * 0.5f * Mathf.Deg2Rad);
                float proj = f * Mathf.Tan(_lockH * Mathf.Deg2Rad);
                return Mathf.Min(proj, Screen.height * 0.16f);
            }
            catch { return 60f; }
        }

        private int _aimRingTexSize;
        private Texture2D _aimRing;
        private Texture2D MakeRingTexture(int size, float thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1f) / 2f;
            float r = c - 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float dd = Mathf.Abs(d - r);
                    bool on = dd < thickness * r && d <= r + thickness;
                    tex.SetPixel(x, y, on ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            return tex;
        }

        private void DrawLockMarker()
        {
            if (_locked == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            var plane = PlaneContainer.Instance;
            if (plane == null) return;
            Vector3 sp = cam.WorldToScreenPoint(_locked.Pos);
            if (sp.z <= 0f) return;   // 相机后方不画
            float gx = sp.x;
            float gy = Screen.height - sp.y;

            float dist = Vector3.Distance(plane.transform.position, _locked.Pos);

            // 红色正方形框
            float bs = 20f;
            GUI.color = new Color(1f, 0.18f, 0.12f, 0.95f);
            GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, bs, 2f), UiCoreWhite());
            GUI.DrawTexture(new Rect(gx - bs / 2f, gy + bs / 2f - 2f, bs, 2f), UiCoreWhite());
            GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, 2f, bs), UiCoreWhite());
            GUI.DrawTexture(new Rect(gx + bs / 2f - 2f, gy - bs / 2f, 2f, bs), UiCoreWhite());
            GUI.color = Color.white;

            // 红点 + 距离（纯数字，无单位）；超出探测范围不显示（锁定保留）
            if (dist <= _range)
            {
                GUI.color = new Color(1f, 0.2f, 0.15f, 1f);
                GUI.DrawTexture(new Rect(gx - 3f, gy - 3f, 6f, 6f), UiCoreWhite());
                GUI.color = Color.white;
                GUI.Label(new Rect(gx - 40f, gy - bs - 20f, 80f, 16f), Mathf.RoundToInt(dist).ToString(), DistStyle());
            }
        }

        // ---------------- 观战模式 ----------------
        // F5 进入/退出观战；[ / ] 切换目标（所有在场景中的飞机，含玩家与 AI）；
        // 观战时只跟随相机、只读数据，不夺取 AI 控制权。

        private bool _spectating;
        private int _specIndex = -1;
        private Transform _specTarget;
        private readonly List<Transform> _specPool = new List<Transform>();
        private Transform _specSavedParent;
        private Vector3 _specSavedPos;
        private Quaternion _specSavedRot;

        private void UpdateSpectate()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F5))
                {
                    _spectating = !_spectating;
                    if (_spectating)
                    {
                        var cam = Camera.main;
                        if (cam != null)
                        {
                            _specSavedParent = cam.transform.parent;
                            _specSavedPos = cam.transform.localPosition;
                            _specSavedRot = cam.transform.localRotation;
                            cam.transform.SetParent(null, true);   // 脱离玩家飞机，否则父级约束会让视角仍跟随玩家
                        }
                        BuildSpecPool();
                        _specIndex = _specPool.Count > 0 ? 0 : -1;
                        _specTarget = _specIndex >= 0 ? _specPool[_specIndex] : null;
                        _api.Log("Spectate: ON targets=" + _specPool.Count);
                    }
                    else
                    {
                        _specTarget = null;
                        var cam = Camera.main;
                        if (cam != null && _specSavedParent != null)
                        {
                            cam.transform.SetParent(_specSavedParent, false);
                            cam.transform.localPosition = _specSavedPos;
                            cam.transform.localRotation = _specSavedRot;
                        }
                        _api.Log("Spectate: OFF");
                    }
                }
                if (!_spectating) return;

                bool next = Input.GetKeyDown(KeyCode.RightBracket);
                bool prev = Input.GetKeyDown(KeyCode.LeftBracket);
                if ((next || prev) && _specPool.Count > 0)
                {
                    int dir = next ? 1 : -1;
                    _specIndex = (_specIndex + dir + _specPool.Count) % _specPool.Count;
                    _specTarget = _specPool[_specIndex];
                    _api.Log("Spectate: -> " + ReadAiCallsign(_specTarget != null ? _specTarget.gameObject : null));
                }

                if (_specTarget == null) return;
                var cam2 = Camera.main;
                if (cam2 == null) return;
                Vector3 tpos = _specTarget.position;
                Vector3 behind = tpos - _specTarget.forward * 30f + Vector3.up * 10f;
                float dt = Time.deltaTime;
                cam2.transform.position = Vector3.Lerp(cam2.transform.position, behind, Mathf.Min(1f, 4f * dt));
                Vector3 look = tpos - cam2.transform.position;
                if (look.sqrMagnitude > 0.01f)
                    cam2.transform.rotation = Quaternion.Lerp(cam2.transform.rotation,
                        Quaternion.LookRotation(look), Mathf.Min(1f, 4f * dt));
            }
            catch { }
        }

        private void BuildSpecPool()
        {
            _specPool.Clear();
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<PlaneContainer>(UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    _specPool.Add(all[i].transform);
                }
            }
            catch { }
        }

        private void DrawSpectateHud()
        {
            if (!_spectating || _specTarget == null) return;
            string model = ReadAiCallsign(_specTarget.gameObject);
            float spd = 0f;
            try
            {
                var rb = _specTarget.GetComponent<Rigidbody>();
                if (rb != null) spd = rb.linearVelocity.magnitude * 1.94384f;
            }
            catch { }
            float alt = _specTarget.position.y;
            GUI.color = new Color(0.4f, 1f, 0.9f, 1f);
            var st = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(Screen.width / 2f - 360f, 10f, 720f, 24f),
                      "观战: " + model + "   " + Mathf.RoundToInt(spd) + "kt   高度 " + Mathf.RoundToInt(alt) + "m   [ / ]切换目标   F5退出", st);
            GUI.color = Color.white;
        }

        // ---------------- 导弹自动扫描红框（屏幕中标记所有敌方导弹） ----------------

        private readonly List<Transform> _missileBoxCache = new List<Transform>();
        private float _missileBoxT;

        private void DrawMissileBoxes()
        {
            if (!_active) return;
            // 节流刷新导弹列表（0.4s），每帧绘制
            _missileBoxT -= Time.deltaTime;
            if (_missileBoxT <= 0f)
            {
                _missileBoxT = 0.4f;
                _missileBoxCache.Clear();
                var mis = FindMissiles();
                for (int i = 0; i < mis.Count; i++) _missileBoxCache.Add(mis[i]);
            }
            if (_missileBoxCache.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            float bs = 16f;
            for (int i = 0; i < _missileBoxCache.Count; i++)
            {
                var mt = _missileBoxCache[i];
                if (mt == null) continue;
                if (IsFriendlyEntity(mt.gameObject)) continue;
                Vector3 sp = cam.WorldToScreenPoint(mt.position);
                if (sp.z <= 0f) continue;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                GUI.color = new Color(1f, 0.15f, 0.1f, 0.95f);
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, bs, 2f), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy + bs / 2f - 2f, bs, 2f), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx - bs / 2f, gy - bs / 2f, 2f, bs), UiCoreWhite());
                GUI.DrawTexture(new Rect(gx + bs / 2f - 2f, gy - bs / 2f, 2f, bs), UiCoreWhite());
            }
            GUI.color = Color.white;
        }

        // ---------------- 样式 / 工具 ----------------

        private GUIStyle _labelStyle;
        private GUIStyle LabelStyle()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.fontSize = 12;
                _labelStyle.normal.textColor = new Color(0.35f, 0.95f, 0.55f, 1f);
            }
            return _labelStyle;
        }

        private GUIStyle _distStyle;
        private GUIStyle DistStyle()
        {
            if (_distStyle == null)
            {
                _distStyle = new GUIStyle(GUI.skin.label);
                _distStyle.fontSize = 14;
                _distStyle.fontStyle = FontStyle.Bold;
                _distStyle.alignment = TextAnchor.MiddleCenter;
                _distStyle.normal.textColor = new Color(1f, 0.25f, 0.2f, 1f);
            }
            return _distStyle;
        }

        private Texture2D _white;
        private Texture2D _tri;
        private Texture2D UiCoreWhite()
        {
            if (_white == null)
            {
                _white = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                _white.SetPixels(new Color[] { Color.white, Color.white, Color.white, Color.white });
                _white.Apply();
            }
            return _white;
        }

        /// <summary>白色三角形纹理（朝上），用于雷达盘上飞机/导弹目标。</summary>
        private Texture2D UiTriangle()
        {
            if (_tri == null)
            {
                int s = 32;
                _tri = new Texture2D(s, s, TextureFormat.RGBA32, false);
                var px = new Color[s * s];
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        // 三角形顶点 (16,2) 底边 (3,29)-(29,29)
                        float halfW = (y - 2f) / (29f - 2f) * 13f;
                        bool inside = y >= 2 && x >= 16f - halfW && x <= 16f + halfW;
                        px[y * s + x] = inside ? Color.white : new Color(0f, 0f, 0f, 0f);
                    }
                }
                _tri.SetPixels(px);
                _tri.Apply();
            }
            return _tri;
        }

        private string UiCoreSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char[] ch = s.ToCharArray();
            for (int i = 0; i < ch.Length; i++)
                if (ch[i] < 0x20 || ch[i] > 0x7E) ch[i] = '?';
            return new string(ch);
        }
    }
}
