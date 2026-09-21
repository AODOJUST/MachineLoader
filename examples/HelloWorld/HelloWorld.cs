using System;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Example.HelloWorld
{
    /// <summary>
    /// Hello World 示例：最小的 Machine Mod。
    ///
    /// 功能：
    /// 1. Mod 加载时输出 "Hello World!"
    /// 2. 每 300 帧（约5秒）输出一次帧计数
    /// 3. 演示基本的生命周期方法
    ///
    /// 编译：
    ///   csc /target:library /out:code/HelloWorld.dll
    ///       /reference:Managed/Assembly-CSharp.dll
    ///       /reference:Managed/Machine.Core.dll
    ///       /reference:Managed/UnityEngine.dll
    ///       HelloWorld.cs
    /// </summary>
    public class HelloWorldMod : MachineModBase
    {
        public override string Id { get { return "example.helloworld"; } }
        public override string Name { get { return "Hello World Example"; } }
        public override string Version { get { return "1.0.0"; } }

        private int _frameCount;

        /// <summary>
        /// OnLoad：Mod 加载时调用。
        /// 在此注册内容、读取配置、初始化对象。
        /// </summary>
        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("Hello World! Mod loaded.");
        }

        /// <summary>
        /// OnEnable：Mod 启用时调用。
        /// 在此订阅事件、创建 UI。
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            Log("Hello World! Mod enabled.");
        }

        /// <summary>
        /// OnUpdate：每帧调用（由 ModManager 统一调度）。
        /// 注意性能：避免 heavy 操作，帧预算 2ms。
        /// </summary>
        public override void OnUpdate()
        {
            _frameCount++;

            // 每 300 帧输出一次（约5秒，60fps）
            if (_frameCount % 300 == 0)
            {
                Log("Hello World! Frame count: " + _frameCount);
            }
        }

        /// <summary>
        /// OnDisable：Mod 禁用时调用。
        /// 在此取消订阅事件、销毁 UI。
        /// </summary>
        public override void OnDisable()
        {
            Log("Hello World! Mod disabled. Total frames: " + _frameCount);
            base.OnDisable();
        }
    }
}
