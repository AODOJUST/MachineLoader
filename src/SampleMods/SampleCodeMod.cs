using System;
using Machine.Mod;
using UnityEngine;

namespace SampleCodeMod
{
    /// <summary>示例代码 Mod 入口：演示 API 与主菜单按钮扩展。</summary>
    public class SampleModMain : IMachineMod
    {
        public string Id { get { return "sample.partmod"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("Sample code mod loaded: API, physics part and menu button demo.");
            api.AddMainMenuButton("Machine Demo", delegate ()
            {
                api.Log("Machine Demo button clicked (a code mod can open its own panel here).");
            });
        }
    }

    /// <summary>
    /// 自定义部件行为类：继承 Machine.Core.MachinePart（游戏 PlanePart 的子类）。
    /// FixedUpdate 中持续施加额外升力 —— 这就是“物理效果”Mod 的典型写法。
    /// </summary>
    public class TurboWing : Machine.Core.MachinePart
    {
        public float boost = 0.35f;

        private void FixedUpdate()
        {
            if (rb != null && gameObject.activeInHierarchy)
            {
                rb.AddRelativeForce(new Vector3(0f, boost * rb.mass, 0f));
            }
        }
    }
}
