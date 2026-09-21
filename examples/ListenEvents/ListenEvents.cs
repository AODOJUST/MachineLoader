using System;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Example.ListenEvents
{
    /// <summary>
    /// Listen Events 示例：订阅 Machine 事件总线的各种事件。
    ///
    /// 功能：
    /// 1. 订阅 SceneLoaded 事件（场景加载）
    /// 2. 订阅 PlayerSpawn 事件（玩家生成）
    /// 3. 订阅 FlightStart/FlightEnd 事件（飞行开始/结束）
    /// 4. 订阅 GameEnd 事件（游戏退出）
    /// 5. 演示事件参数的使用
    /// </summary>
    public class ListenEventsMod : MachineModBase
    {
        public override string Id { get { return "example.listenevents"; } }
        public override string Name { get { return "Listen Events Example"; } }
        public override string Version { get { return "1.0.0"; } }

        private int _sceneLoadCount;
        private int _flightStartCount;
        private int _flightEndCount;

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("ListenEvents Mod loaded.");
        }

        public override void OnEnable()
        {
            base.OnEnable();

            // 订阅各种事件
            SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            SubscribeEvent(MachineEventBus.EventType.SceneUnloading, OnSceneUnloading);
            SubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
            SubscribeEvent(MachineEventBus.EventType.FlightStart, OnFlightStart);
            SubscribeEvent(MachineEventBus.EventType.FlightEnd, OnFlightEnd);
            SubscribeEvent(MachineEventBus.EventType.GameEnd, OnGameEnd);
            SubscribeEvent(MachineEventBus.EventType.SaveLoaded, OnSaveLoaded);

            Log("Events subscribed.");
        }

        // 场景加载完成
        private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
        {
            _sceneLoadCount++;
            Log("Scene loaded: " + e.SceneName + " (count: " + _sceneLoadCount + ")");
        }

        // 场景卸载前
        private void OnSceneUnloading(MachineEventBus.MachineEventArgs e)
        {
            Log("Scene unloading: " + e.SceneName);
        }

        // 玩家飞机生成
        private void OnPlayerSpawn(MachineEventBus.MachineEventArgs e)
        {
            Log("Player spawned!");
        }

        // 开始飞行
        private void OnFlightStart(MachineEventBus.MachineEventArgs e)
        {
            _flightStartCount++;
            Log("Flight started! (count: " + _flightStartCount + ")");
        }

        // 结束飞行
        private void OnFlightEnd(MachineEventBus.MachineEventArgs e)
        {
            _flightEndCount++;
            Log("Flight ended! (count: " + _flightEndCount + ")");
        }

        // 游戏退出
        private void OnGameEnd(MachineEventBus.MachineEventArgs e)
        {
            Log("Game ending... Stats: scenes=" + _sceneLoadCount +
                " flights=" + _flightStartCount + "/" + _flightEndCount);
        }

        // 存档加载完成
        private void OnSaveLoaded(MachineEventBus.MachineEventArgs e)
        {
            Log("Save loaded!");
        }

        public override void OnDisable()
        {
            // 取消所有事件订阅（必须与 OnEnable 对称）
            UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            UnsubscribeEvent(MachineEventBus.EventType.SceneUnloading, OnSceneUnloading);
            UnsubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
            UnsubscribeEvent(MachineEventBus.EventType.FlightStart, OnFlightStart);
            UnsubscribeEvent(MachineEventBus.EventType.FlightEnd, OnFlightEnd);
            UnsubscribeEvent(MachineEventBus.EventType.GameEnd, OnGameEnd);
            UnsubscribeEvent(MachineEventBus.EventType.SaveLoaded, OnSaveLoaded);

            Log("Events unsubscribed.");
            base.OnDisable();
        }
    }
}
