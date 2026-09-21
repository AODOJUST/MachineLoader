using System;
using System.Collections.Generic;

namespace Machine.Core
{
    /// <summary>
    /// Machine 事件总线。
    /// 加载器核心在关键节点发布事件，Mod 订阅后可在不侵入游戏代码的情况下响应。
    /// 所有事件回调都在 try/catch 中执行，单个 Mod 抛异常不影响其他订阅者。
    /// </summary>
    public static class MachineEventBus
    {
        /// <summary>事件类型。</summary>
        public enum EventType
        {
            /// <summary>游戏启动完成（主菜单就绪）。</summary>
            GameStart,
            /// <summary>场景加载完成（参数：场景名）。</summary>
            SceneLoaded,
            /// <summary>场景即将卸载（参数：场景名）。</summary>
            SceneUnloading,
            /// <summary>玩家飞机生成（参数：PlaneContainer）。</summary>
            PlayerSpawn,
            /// <summary>每帧更新（参数：deltaTime）。</summary>
            Tick,
            /// <summary>固定帧率更新（参数：fixedDeltaTime）。</summary>
            FixedTick,
            /// <summary>进入飞行模式（起飞后）。</summary>
            FlightStart,
            /// <summary>退出飞行模式。</summary>
            FlightEnd,
            /// <summary>游戏即将退出。</summary>
            GameEnd,
            /// <summary>Mod 加载完成（所有 Mod OnLoad 执行完毕）。</summary>
            ModsLoaded,
            /// <summary>存档加载完成。</summary>
            SaveLoaded,
        }

        /// <summary>事件参数基类。</summary>
        public class MachineEventArgs : EventArgs
        {
            public EventType Type;
            public object Data;
            public string SceneName;
            public float DeltaTime;

            public MachineEventArgs(EventType type) { Type = type; }
        }

        /// <summary>事件回调委托。</summary>
        public delegate void MachineEventHandler(MachineEventArgs e);

        private static readonly Dictionary<EventType, List<MachineEventHandler>> _subscribers =
            new Dictionary<EventType, List<MachineEventHandler>>();

        private static readonly object _lock = new object();

        /// <summary>订阅事件。</summary>
        public static void Subscribe(EventType type, MachineEventHandler handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(type))
                    _subscribers[type] = new List<MachineEventHandler>();
                if (!_subscribers[type].Contains(handler))
                    _subscribers[type].Add(handler);
            }
        }

        /// <summary>取消订阅事件。</summary>
        public static void Unsubscribe(EventType type, MachineEventHandler handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                if (_subscribers.ContainsKey(type))
                    _subscribers[type].Remove(handler);
            }
        }

        /// <summary>发布事件。所有回调在 try/catch 中执行，单个 Mod 异常不影响其他订阅者。</summary>
        public static void Publish(MachineEventArgs e)
        {
            if (e == null) return;
            List<MachineEventHandler> handlers;
            lock (_lock)
            {
                if (!_subscribers.TryGetValue(e.Type, out handlers) || handlers.Count == 0) return;
                handlers = new List<MachineEventHandler>(handlers); // 复制一份，防止回调中修改集合
            }
            foreach (var handler in handlers)
            {
                try { handler(e); }
                catch (Exception ex) { MachineLog.Error("EventBus handler error for " + e.Type + ": " + ex.Message); }
            }
        }

        /// <summary>便捷发布：仅指定事件类型。</summary>
        public static void Publish(EventType type)
        {
            Publish(new MachineEventArgs(type));
        }

        /// <summary>便捷发布：事件类型 + 场景名。</summary>
        public static void PublishScene(EventType type, string sceneName)
        {
            var e = new MachineEventArgs(type);
            e.SceneName = sceneName;
            Publish(e);
        }

        /// <summary>便捷发布：事件类型 + deltaTime。</summary>
        public static void PublishTick(EventType type, float deltaTime)
        {
            var e = new MachineEventArgs(type);
            e.DeltaTime = deltaTime;
            Publish(e);
        }

        /// <summary>便捷发布：事件类型 + 任意数据。</summary>
        public static void PublishData(EventType type, object data)
        {
            var e = new MachineEventArgs(type);
            e.Data = data;
            Publish(e);
        }

        /// <summary>清除所有订阅（游戏退出时调用）。</summary>
        public static void ClearAll()
        {
            lock (_lock) { _subscribers.Clear(); }
        }

        /// <summary>获取某事件的订阅者数量（诊断用）。</summary>
        public static int GetSubscriberCount(EventType type)
        {
            lock (_lock)
            {
                List<MachineEventHandler> list;
                if (_subscribers.TryGetValue(type, out list)) return list.Count;
                return 0;
            }
        }

        /// <summary>获取所有事件的订阅者总数（内存泄漏检测用）。</summary>
        public static int GetTotalSubscriberCount()
        {
            lock (_lock)
            {
                int total = 0;
                foreach (var kvp in _subscribers)
                    total += kvp.Value.Count;
                return total;
            }
        }
    }
}
