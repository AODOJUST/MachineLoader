using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// Machine 加载器入口。由注入到 Assembly-CSharp 的 [RuntimeInitializeOnLoadMethod]
    /// 钩子在游戏启动后调用。
    /// </summary>
    public static class Bootstrap
    {
        public const string Version = "1.0.0";
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                string gameDir = Path.GetDirectoryName(Application.dataPath);
                if (gameDir == null) gameDir = Directory.GetCurrentDirectory();
                string machineDir = Path.Combine(gameDir, "Machine");
                string modsDir = Path.Combine(gameDir, "mods");

                Log.Init(Path.Combine(machineDir, "logs"));
                Log.Info("==== Machine v" + Version + " boot ====");
                Log.Info("Game dir : " + gameDir);
                Log.Info("Unity ver : " + Application.unityVersion);

                var go = new GameObject("Machine.Runtime");
                GameObject.DontDestroyOnLoad(go);
                var rt = go.AddComponent<MachineRuntime>();
                rt.Init(gameDir, machineDir, modsDir);
            }
            catch (Exception e)
            {
                Log.Error("Bootstrap failed: " + e);
            }
        }
    }

    /// <summary>加载器运行时宿主（常驻 Update 驱动）。</summary>
    public class MachineRuntime : MonoBehaviour
    {
        private string _gameDir;
        private string _machineDir;
        private string _modsDir;
        private ModManager _mods;
        private MenuInjector _menu;
        private ModManagerUI _managerUI;
        private Registrar _registrar;
        private MachineApi _api;
        private bool _modsLoaded;
        private bool _menuStarted;
        private float _wsFeedbackCooldown;
        private bool _wsDiagLogged;
        private bool _wasInGame;
        private PlayerCard _playerCard;
        private UserRegistry _userRegistry;

        public string GameDir { get { return _gameDir; } }
        public string MachineDir { get { return _machineDir; } }
        public string ModsDir { get { return _modsDir; } }
        public ModManager Mods { get { return _mods; } }
        public Registrar Registrar { get { return _registrar; } }
        public MachineApi Api { get { return _api; } }
        public MachineMemory Memory { get { return _memory; } }
        private MachineMemory _memory;

        public void Init(string gameDir, string machineDir, string modsDir)
        {
            _gameDir = gameDir;
            _machineDir = machineDir;
            _modsDir = modsDir;
            // 初始化多语言系统（加载中文字体，解决中文乱码）
            try { MachineLang.Init(); } catch (Exception e) { Log.Error("MachineLang init: " + e.Message); }
            _memory = new MachineMemory();
            _memory.Load(machineDir);
            // 原始开发者种子：AODo 自动获得 OID=001（仅当尚未设置时）
            if (_memory.PlayerName == "AODo" && string.IsNullOrEmpty(_memory.OID))
            {
                _memory.OID = "001";
                Log.Info("Original Developer OID=001 granted to AODo");
            }
            _registrar = new Registrar();
            _api = new MachineApi(this);
            _mods = new ModManager();
            // 设置日志系统的委托（避免循环引用）
            // 加载器版本号的唯一来源是 UI.cs 里的 MachineLoader.Version
            // （build-core.ps1 / tools/sign_release.py 也是从这里读的）。
            Log.GetLoaderVersion = delegate () { return MachineLoader.Version; };
            Log.GetModList = delegate ()
            {
                var list = new List<string>();
                try
                {
                    foreach (var mod in _mods.Mods)
                    {
                        string status = mod.Enabled ? "OK" : "DISABLED";
                        string errors = mod.HasErrors ? " [ERRORS:" + mod.Errors.Count + "]" : "";
                        list.Add("[" + status + "] " + mod.Info.id + " v" + mod.Info.version + errors);
                    }
                }
                catch { }
                return list;
            };
            _menu = new MenuInjector(this);
            _managerUI = new ModManagerUI(this);
            _online = new OnlineUI(this);
            _nameUI = new PlayerNameUI(this);
            _updater = new MachineUpdate(this);
            Net.SetPlayerName(_memory.PlayerName);
            _menu.AddExtraButton("Online", delegate () { OpenOnlineFlow(); });
            Net.InitRuntime(this);
            _updater.StartCheck();
            // 玩家明信片 + 用户名单
            _playerCard = gameObject.AddComponent<PlayerCard>();
            _playerCard.Init(this);
            _userRegistry = new UserRegistry(machineDir);
            // 窗口位置注册表
            try { WindowRegistry.Init(machineDir); } catch (Exception e) { Log.Error("WindowRegistry init: " + e.Message); }
            // 按存档的玩家数据记录
            try { RoomPlayers.Init(machineDir); } catch (Exception e) { Log.Error("RoomPlayers init: " + e.Message); }
            // 将自己写入用户名单（如果有权限）
            try { _userRegistry.Upsert(_memory, _memory.PlayerName, _memory.UID, "127.0.0.1"); } catch { }
            Log.Info("Machine runtime created (name=" + (_memory.PlayerName.Length > 0 ? "locked" : "guest") + ")");
        }

        /// <summary>Online 入口：无锁定昵称先走游客名字设置，否则直接打开联机窗口。</summary>
        public void OpenOnlineFlow()
        {
            if (string.IsNullOrEmpty(_memory.PlayerName)) _nameUI.Show();
            else _online.Show();
        }

        public void OpenOnline() { _online.Show(); }

        public void OpenModManager() { _managerUI.Show(); }
        public void CloseModManager() { _managerUI.Hide(); }
        public MenuInjector Menu { get { return _menu; } }
        private OnlineUI _online;
        private PlayerNameUI _nameUI;
        private MachineUpdate _updater;

        private int _perfFrameCounter;
        private double _loaderFrameTimeMs;
        private System.Diagnostics.Stopwatch _loaderFrameSw = new System.Diagnostics.Stopwatch();

        private void Update()
        {
            _loaderFrameSw.Restart();

            if (!_modsLoaded)
            {
                _modsLoaded = true;
                try { _mods.LoadAll(_modsDir, _machineDir, _api); }
                catch (Exception e) { Log.Error("Mod load error: " + e); }
                Log.Info("Mod scan finished, loaded " + _mods.LoadedCount + " mod(s)");
                // 发布 ModsLoaded 事件
                try { MachineEventBus.Publish(MachineEventBus.EventType.ModsLoaded); } catch { }
                // 收集已启用 mod 列表，用于联机房间检测
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var m in _mods.Mods)
                    {
                        if (!m.Enabled) continue;
                        string id = "";
                        try { id = m.Info.id; } catch { }
                        if (id.Length == 0) try { id = m.Info.name; } catch { }
                        if (id.Length > 0)
                        {
                            if (sb.Length > 0) sb.Append(',');
                            sb.Append(id);
                        }
                    }
                    Net.ClientMods = sb.ToString();
                    Log.Info("Client mods for online: " + Net.ClientMods);
                }
                catch (Exception e) { Log.Error("ClientMods collect error: " + e); }
            }
            try { _registrar.Update(); }
            catch (Exception e) { Log.Error("Registrar error: " + e); }
            if (!_menuStarted)
            {
                _menuStarted = true;
                _menu.Start();
            }
            _menu.TickEnable();
            try { _menu.Poll(); }
            catch (Exception e) { Log.Error("Menu poll error: " + e); }
            try { _online.TickPublic(); }
            catch (Exception e) { MachineLog.Error("OnlineUI tick error: " + e.Message); }
            try { Net.Tick(); }
            catch (Exception e) { Log.Error("Net tick error: " + e.Message); }
            try { _updater.Tick(); }
            catch (Exception e) { Log.Error("Update tick error: " + e.Message); }
            UpdatePureMode();
            // 游玩时间累加（仅 mod 模式）
            try
            {
                if (!Machine.Mod.MachineState.PureMode && _memory != null)
                    _memory.TickPlayTime(Time.unscaledDeltaTime);
            }
            catch { }
            TryCloseWorkshopFeedback();
            // 生命周期：调用所有Mod的OnUpdate，并发布Tick事件
            if (_modsLoaded)
            {
                try { _mods.UpdateAll(); } catch (Exception e) { Log.Error("Mod UpdateAll error: " + e.Message); }
                try { MachineEventBus.PublishTick(MachineEventBus.EventType.Tick, Time.unscaledDeltaTime); } catch { }
            }

            // 性能管理：每60帧计算一次平均值
            _perfFrameCounter++;
            if (_perfFrameCounter >= 60)
            {
                _perfFrameCounter = 0;
                try { ModProfiler.ComputeAverages(); } catch { }

                // 内存泄漏检测：每60帧检查一次
                try { CheckMemoryLeaks(); } catch { }
            }

            _loaderFrameSw.Stop();
            _loaderFrameTimeMs = _loaderFrameSw.Elapsed.TotalMilliseconds;

            // 加载器自身开销报警（超过1ms/帧）
            if (_loaderFrameTimeMs > 1.0 && _perfFrameCounter == 30)
            {
                Log.Debug("Perf", "Loader frame time: " + _loaderFrameTimeMs.ToString("F2") +
                    "ms (target: <1ms)");
            }
        }

        /// <summary>固定帧率更新：调用Mod的OnFixedUpdate并发布FixedTick事件。</summary>
        private void FixedUpdate()
        {
            if (!_modsLoaded) return;
            try { _mods.FixedUpdateAll(); } catch (Exception e) { Log.Error("Mod FixedUpdateAll error: " + e.Message); }
            try { MachineEventBus.PublishTick(MachineEventBus.EventType.FixedTick, Time.fixedDeltaTime); } catch { }
        }

        /// <summary>
        /// 内存泄漏检测：定期检查事件订阅数量、静态引用、对象池状态。
        /// 发现异常时记录警告。
        /// </summary>
        private void CheckMemoryLeaks()
        {
            try
            {
                // 1. 检查事件总线订阅数量（异常增长可能表示泄漏）
                int eventSubCount = MachineEventBus.GetTotalSubscriberCount();
                if (eventSubCount > 500)
                {
                    Log.Warn("Memory", "EventBus subscriber count is high: " + eventSubCount +
                        " (possible memory leak - mods should unsubscribe on disable)");
                }

                // 2. 检查对象池状态
                string poolStats = PoolManager.GetStats();
                Log.Debug("Memory", poolStats);

                // 3. 检查已禁用但仍有订阅的Mod
                foreach (var mod in _mods.Mods)
                {
                    if (!mod.Enabled && mod.HasFullLifecycle)
                    {
                        // 已禁用的Mod不应该有活跃的事件订阅
                        // （这里只是提示，具体检查需要EventBus支持按Mod统计）
                    }
                }

                // 4. 检查Unity内存（如果可用）
                long managedMem = System.GC.GetTotalMemory(false);
                if (managedMem > 500 * 1024 * 1024) // 超过500MB
                {
                    Log.Warn("Memory", "Managed memory is high: " +
                        (managedMem / 1024 / 1024) + "MB (possible memory leak)");
                }
            }
            catch (Exception e)
            {
                Log.Debug("Memory", "Memory leak check error: " + e.Message);
            }
        }

        /// <summary>游戏退出：发布GameEnd事件，卸载所有Mod，清除事件订阅。</summary>
        private void OnDestroy()
        {
            try { MachineEventBus.Publish(MachineEventBus.EventType.GameEnd); } catch { }
            try { if (_mods != null) _mods.UnloadAll(); } catch (Exception e) { Log.Error("Mod UnloadAll error: " + e.Message); }
            try { MachineEventBus.ClearAll(); } catch { }
            Log.Info("Machine runtime destroyed");
            Log.Flush();
        }

        /// <summary>
        /// 纯净模式检测：Mod Saves 入口 → mod 模式；其他入口（原生 Load / New Game / Sandbox /
        /// 自动继续）→ 纯净模式。以"玩家飞机出现"作为进入游戏场景的边缘触发。
        /// </summary>
        ///
        /// <remarks>
        /// ★ 节流：PlaneContainer.Instance 在 null 时走全场景 FindFirstObjectByType（游戏 Singleton
        ///   实现），主菜单里每帧调用 = 每帧一次全场景扫描（单次 2~4ms）。边缘检测 0.25s 轮询足够。
        /// </remarks>
        private float _pmPollT;

        private void UpdatePureMode()
        {
            _pmPollT -= Time.unscaledDeltaTime;
            if (_pmPollT > 0f) return;
            _pmPollT = 0.25f;
            try
            {
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null; } catch { }
                if (inGame && !_wasInGame)
                {
                    bool viaMod = Machine.Mod.MachineState.ViaModSaves;
                    Machine.Mod.MachineState.PureMode = !viaMod;
                    Machine.Mod.MachineState.ViaModSaves = false;
                    string scn = "";
                    try { scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                    Log.Info("scene entered | viaModSaves=" + viaMod + " PureMode=" + Machine.Mod.MachineState.PureMode + " scene=" + scn);
                    // 发布场景加载和玩家生成事件
                    try { MachineEventBus.PublishScene(MachineEventBus.EventType.SceneLoaded, scn); } catch { }
                    try { MachineEventBus.Publish(MachineEventBus.EventType.PlayerSpawn); } catch { }
                }
                else if (!inGame && _wasInGame)
                {
                    string scn = "";
                    try { scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                    Log.Info("scene exited to menu | scene=" + scn);
                    // 发布场景卸载事件
                    try { MachineEventBus.PublishScene(MachineEventBus.EventType.SceneUnloading, scn); } catch { }
                    // 退出游戏回到主菜单时，重置单人模式聊天框状态，确保下次进入能重新创建
                    try { if (_online != null) _online.ResetSinglePlayerChatState(); } catch { }
                }
                _wasInGame = inGame;
            }
            catch { }
        }

        /// <summary>
        /// 规避：无 Steam 环境下 Workshop 上传失败提示会持续弹出，使游戏 UI 判定鼠标始终
        /// 悬在 UI 上，导致编辑器 Local/Workshop 面板无法点空白处关闭。检测到该提示即自动关闭。
        /// 注意：不能直接 typeof(WorkshopUploadFeedbackPanel) 或 FindFirstObjectByType(该类型)——其
        /// fileID 字段引用 Facepunch.Steamworks，无 Steam DLL 时类型加载/对象查找都会抛异常；
        /// 提示框文本是 TMPro.TMP_Text（非 UnityEngine.UI.Text），按文本扫描 TMP_Text 定位再隐藏。
        /// </summary>
        private void TryCloseWorkshopFeedback()
        {
            _wsFeedbackCooldown -= Time.deltaTime;
            if (_wsFeedbackCooldown > 0f) return;
            try
            {
                // 字符串反射找类型（避免直接 typeof 触发 TypeLoadException）
                Type panelType = null;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    try
                    {
                        panelType = assemblies[i].GetType("WorkshopUploadFeedbackPanel");
                        if (panelType != null) break;
                    }
                    catch { }
                }
                if (panelType == null)
                {
                    _wsFeedbackCooldown = 3f;
                    return;
                }
                TMPro.TMP_Text target = null;
                var texts = UnityEngine.Object.FindObjectsByType<TMPro.TMP_Text>(UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < texts.Length; i++)
                {
                    TMPro.TMP_Text t = texts[i];
                    if (t == null || !t.isActiveAndEnabled || t.text == null) continue;
                    // 两类提示都会让游戏 UI 判定鼠标悬停，导致编辑器面板无法点空白关闭：
                    // 1) 上传失败；2) Steam 订阅协议未接受（物品隐藏提示）
                    if (t.text.Contains("Workshop upload failed") ||
                        t.text.Contains("still need to accept the Steam subscriber agreement"))
                    {
                        target = t;
                        break;
                    }
                }
                if (target == null)
                {
                    _wsFeedbackCooldown = 3f;
                    return;
                }
                // 只隐藏提示框自身：从文本向上找 WorkshopUploadFeedbackPanel 组件所在 GameObject。
                // 绝不使用 transform.root（会把整个编辑器 UI 一并禁用，导致无法编辑/起飞）。
                UnityEngine.GameObject hideGo = null;
                Transform cur = target.transform;
                while (cur != null)
                {
                    try
                    {
                        if (cur.GetComponent(panelType) != null) { hideGo = cur.gameObject; break; }
                    }
                    catch { }
                    cur = cur.parent;
                }
                if (hideGo == null) hideGo = target.gameObject; // 兜底：只隐藏文本自身
                try { hideGo.SetActive(false); } catch { }
                Log.Info("workshop upload feedback auto-closed (by text)");
            }
            catch (Exception e) { Log.Warn("workshop feedback close failed: " + e.Message); }
            _wsFeedbackCooldown = 3f;
        }
    }
}
