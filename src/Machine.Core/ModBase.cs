using System;
using UnityEngine;
using Machine.Core;

namespace Machine.Mod
{
    /// <summary>
    /// Machine Mod 生命周期状态。
    /// </summary>
    public enum ModLifecycleState
    {
        Created,
        Loaded,
        Enabled,
        Disabled,
        Unloaded,
        Error
    }

    /// <summary>
    /// Machine Mod 抽象基类。
    /// 新 Mod 推荐继承此类，只需重写需要的生命周期方法。
    /// 旧 Mod 直接实现 IMachineMod 接口仍然兼容（只有 OnLoad 会被调用）。
    ///
    /// 生命周期顺序：
    ///   OnLoad → OnEnable → (OnUpdate 每帧) → OnDisable → OnUnload
    /// </summary>
    public abstract class MachineModBase : IMachineMod
    {
        /// <summary>Mod 唯一 ID（由子类实现）。</summary>
        public abstract string Id { get; }

        /// <summary>Mod 显示名称（可选，默认用 Id）。</summary>
        public virtual string Name { get { return Id; } }

        /// <summary>Mod 版本（可选）。</summary>
        public virtual string Version { get { return "1.0.0"; } }

        /// <summary>当前生命周期状态。</summary>
        public ModLifecycleState State { get; private set; }

        /// <summary>Machine API 引用（OnLoad 后可用）。</summary>
        protected IMachineApi Api { get; private set; }

        /// <summary>Mod 是否处于启用状态。</summary>
        public bool IsEnabled { get { return State == ModLifecycleState.Enabled; } }

        /// <summary>
        /// 加载回调：注册内容、挂载事件、创建对象都在这里。
        /// 调用后 State = Loaded。
        /// </summary>
        public virtual void OnLoad(IMachineApi api)
        {
            Api = api;
            State = ModLifecycleState.Loaded;
        }

        /// <summary>
        /// 启用回调：Mod 被启用时调用（OnLoad 之后自动调用）。
        /// 可在此订阅事件总线事件。
        /// </summary>
        public virtual void OnEnable()
        {
            State = ModLifecycleState.Enabled;
        }

        /// <summary>
        /// 禁用回调：Mod 被禁用时调用。
        /// 应在此取消订阅事件总线事件。
        /// </summary>
        public virtual void OnDisable()
        {
            State = ModLifecycleState.Disabled;
        }

        /// <summary>
        /// 卸载回调：Mod 被卸载时调用（游戏退出或热重载）。
        /// 应在此清理资源。
        /// </summary>
        public virtual void OnUnload()
        {
            State = ModLifecycleState.Unloaded;
        }

        /// <summary>
        /// 每帧更新回调（由 ModManager 统一调度）。
        /// 注意：只有继承 MachineModBase 的 Mod 才会收到此回调。
        /// </summary>
        public virtual void OnUpdate()
        {
        }

        /// <summary>
        /// 固定帧率更新回调（由 ModManager 统一调度）。
        /// </summary>
        public virtual void OnFixedUpdate()
        {
        }

        // ------------------------------------------------------------------
        // 事件总线便捷方法
        // ------------------------------------------------------------------

        /// <summary>订阅事件总线事件。</summary>
        protected void SubscribeEvent(MachineEventBus.EventType type, MachineEventBus.MachineEventHandler handler)
        {
            MachineEventBus.Subscribe(type, handler);
        }

        /// <summary>取消订阅事件总线事件。</summary>
        protected void UnsubscribeEvent(MachineEventBus.EventType type, MachineEventBus.MachineEventHandler handler)
        {
            MachineEventBus.Unsubscribe(type, handler);
        }

        /// <summary>记录日志。</summary>
        protected void Log(string message)
        {
            if (Api != null) Api.Log("[" + Id + "] " + message);
            else MachineLog.Info("[" + Id + "] " + message);
        }

        /// <summary>记录警告。</summary>
        protected void LogWarn(string message)
        {
            MachineLog.Warn("[" + Id + "] " + message);
        }

        /// <summary>记录错误。</summary>
        protected void LogError(string message)
        {
            MachineLog.Error("[" + Id + "] " + message);
        }

        // ------------------------------------------------------------------
        // 内部方法（由 ModManager 调用，不要重写）
        // ------------------------------------------------------------------

        /// <summary>内部：标记错误状态。</summary>
        internal void SetError()
        {
            State = ModLifecycleState.Error;
        }
    }
}
