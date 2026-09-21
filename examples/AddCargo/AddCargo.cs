using System;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Example.AddCargo
{
    /// <summary>
    /// Add Cargo 示例：注册自定义货物。
    ///
    /// 功能：
    /// 1. 注册三种示例货物（普通货物、易碎货物、过期货物）
    /// 2. 货物会出现在机场装货界面
    /// 3. 演示 CargoDefinition 的各种字段
    /// </summary>
    public class AddCargoMod : MachineModBase
    {
        public override string Id { get { return "example.addcargo"; } }
        public override string Name { get { return "Add Cargo Example"; } }
        public override string Version { get { return "1.0.0"; } }

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);

            // 注册普通货物
            var normalCargo = new CargoDefinition();
            normalCargo.Id = "example.normalcargo";
            normalCargo.Name = "Example Normal Cargo";
            normalCargo.Price = 100f;
            normalCargo.Weight = 10f;
            normalCargo.CargoSpace = 2;
            api.RegisterCargo(normalCargo);
            Log("Registered: " + normalCargo.Name);

            // 注册易碎货物
            var fragileCargo = new CargoDefinition();
            fragileCargo.Id = "example.fragilecargo";
            fragileCargo.Name = "Example Fragile Cargo";
            fragileCargo.Price = 300f;
            fragileCargo.Weight = 5f;
            fragileCargo.CargoSpace = 1;
            fragileCargo.Fragile = true;  // 易碎
            api.RegisterCargo(fragileCargo);
            Log("Registered: " + fragileCargo.Name);

            // 注册过期货物
            var expiringCargo = new CargoDefinition();
            expiringCargo.Id = "example.expiringcargo";
            expiringCargo.Name = "Example Expiring Cargo";
            expiringCargo.Price = 50f;
            expiringCargo.Weight = 20f;
            expiringCargo.CargoSpace = 3;
            expiringCargo.Expires = true;           // 会过期
            expiringCargo.ExpirationTime = 120f;    // 120秒后过期
            api.RegisterCargo(expiringCargo);
            Log("Registered: " + expiringCargo.Name);

            Log("AddCargo Mod loaded. 3 cargo types registered.");
        }
    }
}
