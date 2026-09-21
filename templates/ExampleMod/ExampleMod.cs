using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Machine.Example
{
    /// <summary>
    /// 示例Mod：展示 Machine v2 API 的完整用法。
    ///
    /// 功能演示：
    /// 1. 生命周期：OnLoad / OnEnable / OnUpdate / OnDisable / OnUnload
    /// 2. 事件总线：订阅 Tick、SceneLoaded、PlayerSpawn 事件
    /// 3. UI按钮：在主菜单添加按钮
    /// 4. 配置读取：从 config.json 读取自定义配置
    /// 5. 命令注册：注册 /example 聊天命令
    ///
    /// 编译命令（在 Machine_Dev 目录下）：
    ///   csc /target:library /out:templates/ExampleMod/code/ExampleMod.dll
    ///       /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Assembly-CSharp.dll
    ///       /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Machine.Core.dll
    ///       /reference:Aviassembly_DEV/Aviassembly_Data/Managed/UnityEngine.dll
    ///       /reference:Aviassembly_DEV/Aviassembly_Data/Managed/UnityEngine.CoreModule.dll
    ///       templates/ExampleMod/ExampleMod.cs
    /// </summary>
    public class ExampleMod : MachineModBase
    {
        public override string Id { get { return "machine.example"; } }
        public override string Name { get { return "Example Mod"; } }
        public override string Version { get { return "1.0.0"; } }

        // 配置
        private bool _showHello = true;
        private float _updateInterval = 5f;
        private float _updateTimer;
        private int _tickCount;

        // UI
        private GameObject _helloButton;

        /// <summary>
        /// OnLoad：Mod加载时调用。
        /// 在此注册内容、读取配置、初始化对象。
        /// </summary>
        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("ExampleMod loading...");

            // 读取配置（从mod目录下的 config.json）
            LoadConfig();

            // 注册货物（示例）
            // var cargo = new CargoDefinition();
            // cargo.Id = "example_cargo";
            // cargo.Name = "Example Cargo";
            // cargo.Price = 100f;
            // cargo.Weight = 1f;
            // cargo.CargoSpace = 1;
            // api.RegisterCargo(cargo);

            Log("ExampleMod loaded. showHello=" + _showHello + " interval=" + _updateInterval);
        }

        /// <summary>
        /// OnEnable：Mod启用时调用（OnLoad之后自动调用）。
        /// 在此订阅事件总线事件、创建UI。
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            Log("ExampleMod enabled");

            // 订阅事件总线事件
            SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
            SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            SubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
            SubscribeEvent(MachineEventBus.EventType.GameEnd, OnGameEnd);

            // 在主菜单添加按钮
            if (_showHello && Api != null)
            {
                Api.AddMainMenuButton("Example", OnHelloClicked);
            }
        }

        /// <summary>
        /// OnUpdate：每帧调用（由ModManager统一调度）。
        /// 注意：只有继承MachineModBase的Mod才会收到此回调。
        /// </summary>
        public override void OnUpdate()
        {
            _tickCount++;
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer >= _updateInterval)
            {
                _updateTimer = 0f;
                Log("ExampleMod tick count=" + _tickCount + " (every " + _updateInterval + "s)");
            }
        }

        /// <summary>
        /// OnDisable：Mod禁用时调用。
        /// 在此取消订阅事件、销毁UI。
        /// </summary>
        public override void OnDisable()
        {
            Log("ExampleMod disabled");

            // 取消订阅事件
            UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
            UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            UnsubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
            UnsubscribeEvent(MachineEventBus.EventType.GameEnd, OnGameEnd);

            // 销毁UI
            if (_helloButton != null)
            {
                UnityEngine.Object.Destroy(_helloButton);
                _helloButton = null;
            }

            base.OnDisable();
        }

        /// <summary>
        /// OnUnload：Mod卸载时调用（游戏退出或热重载）。
        /// 在此清理资源。
        /// </summary>
        public override void OnUnload()
        {
            Log("ExampleMod unloaded. Total ticks=" + _tickCount);
            base.OnUnload();
        }

        // ------------------------------------------------------------------
        // 事件回调
        // ------------------------------------------------------------------

        private void OnTick(MachineEventBus.MachineEventArgs e)
        {
            // 每帧事件（注意：这里和OnUpdate都会被调用，选一个即可）
            // Log("Tick event: deltaTime=" + e.DeltaTime);
        }

        private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
        {
            Log("Scene loaded: " + e.SceneName);
        }

        private void OnPlayerSpawn(MachineEventBus.MachineEventArgs e)
        {
            Log("Player spawned!");
        }

        private void OnGameEnd(MachineEventBus.MachineEventArgs e)
        {
            Log("Game ending...");
        }

        // ------------------------------------------------------------------
        // UI回调
        // ------------------------------------------------------------------

        private void OnHelloClicked()
        {
            Log("Hello button clicked!");
            // 可以在这里打开自定义窗口
        }

        // ------------------------------------------------------------------
        // 配置读取
        // ------------------------------------------------------------------

        private void LoadConfig()
        {
            try
            {
                if (Api == null) return;
                string modDir = Api.GetModsDirectory();
                string configPath = Path.Combine(modDir, "machine.example", "config.json");
                if (!File.Exists(configPath))
                {
                    // 创建默认配置
                    var defaultConfig = new Dictionary<string, object>
                    {
                        { "showHello", true },
                        { "updateInterval", 5f }
                    };
                    File.WriteAllText(configPath, JsonUtility.ToJson(defaultConfig, true));
                    Log("Default config created at " + configPath);
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<ExampleConfig>(json);
                if (config != null)
                {
                    _showHello = config.showHello;
                    _updateInterval = config.updateInterval;
                }
            }
            catch (Exception ex)
            {
                LogWarn("Config load failed: " + ex.Message);
            }
        }

        [Serializable]
        private class ExampleConfig
        {
            public bool showHello = true;
            public float updateInterval = 5f;
        }
    }
}
