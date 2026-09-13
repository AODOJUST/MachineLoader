<#
.SYNOPSIS
    Machine Mod 模板生成器。自动创建新 Mod 的目录结构和基础文件。

.DESCRIPTION
    使用方法：
        .\newmod.ps1 -Name "MyMod" -Author "MyName" -Id "author.mymod"
        .\newmod.ps1 -Name "RadarExtension" -Author "AODo" -Id "aodo.radarext" -OutputDir "D:\mods"

    生成的目录结构：
        MyMod/
        ├── mod.json          # Mod清单（v2格式）
        ├── MyMod.cs          # 主类源码（继承MachineModBase）
        ├── code/
        │   └── MyMod.dll     # 编译后的程序集（编译后生成）
        ├── build.ps1         # 编译脚本
        └── README.md         # 说明文档

.PARAMETER Name
    Mod 名称（用于类名和目录名）。必填。

.PARAMETER Author
    作者名称。默认 "Unknown"。

.PARAMETER Id
    Mod 唯一 ID。默认 "author.<name小写>"。

.PARAMETER Version
    初始版本号。默认 "1.0.0"。

.PARAMETER Description
    Mod 描述。默认空。

.PARAMETER OutputDir
    输出目录。默认当前目录。

.PARAMETER Dependencies
    必需依赖列表（逗号分隔）。如 "machine.battlecore,machine.radar"。

.PARAMETER OptionalDependencies
    可选依赖列表（逗号分隔）。

.EXAMPLE
    .\newmod.ps1 -Name "MyFirstMod" -Author "AODo"
    创建一个名为 MyFirstMod 的 Mod 模板。

.EXAMPLE
    .\newmod.ps1 -Name "RadarExt" -Author "AODo" -Id "aodo.radarext" -Dependencies "machine.radar>=2.0.0"
    创建一个依赖 Radar Mod 的扩展 Mod。
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$Name,

    [string]$Author = "Unknown",

    [string]$Id = "",

    [string]$Version = "1.0.0",

    [string]$Description = "",

    [string]$OutputDir = ".",

    [string]$Dependencies = "",

    [string]$OptionalDependencies = ""
)

$ErrorActionPreference = "Stop"

# 验证名称
if ($Name -match '[^a-zA-Z0-9_]') {
    Write-Error "Mod 名称只能包含字母、数字和下划线：$Name"
    exit 1
}

# 生成默认ID
if ([string]::IsNullOrEmpty($Id)) {
    $Id = ($Author.ToLower() -replace '[^a-z0-9]', '') + "." + ($Name.ToLower())
}

# 命名空间
$Namespace = "Machine.Mods.$Name"
$ClassName = $Name

# 创建目录
$modDir = Join-Path $OutputDir $Name
$codeDir = Join-Path $modDir "code"
New-Item -ItemType Directory -Path $modDir -Force | Out-Null
New-Item -ItemType Directory -Path $codeDir -Force | Out-Null

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Machine Mod 模板生成器" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Mod 名称 : $Name"
Write-Host "作者     : $Author"
Write-Host "Mod ID   : $Id"
Write-Host "版本     : $Version"
Write-Host "命名空间 : $Namespace"
Write-Host "输出目录 : $modDir"
Write-Host ""

# 解析依赖
$depArray = @()
if (-not [string]::IsNullOrEmpty($Dependencies)) {
    $depArray = $Dependencies -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}
$optDepArray = @()
if (-not [string]::IsNullOrEmpty($OptionalDependencies)) {
    $optDepArray = $OptionalDependencies -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}

# 生成 mod.json
$depJson = if ($depArray.Count -gt 0) { '["' + ($depArray -join '","') + '"]' } else { '[]' }
$optDepJson = if ($optDepArray.Count -gt 0) { '["' + ($optDepArray -join '","') + '"]' } else { '[]' }

$modJson = @"
{
  "id": "$Id",
  "name": "$Name",
  "version": "$Version",
  "author": "$Author",
  "description": "$Description",
  "type": "code",
  "apiVersion": "1.0",
  "loaderVersion": ">=1.0.0",
  "dependencies": $depJson,
  "optionalDependencies": $optDepJson,
  "loadAfter": [],
  "loadBefore": [],
  "entry": "$Namespace.$ClassName",
  "code": {
    "assemblies": ["code/$Name.dll"],
    "mainClass": "$Namespace.$ClassName"
  },
  "content": {
    "cargo": [],
    "parts": [],
    "decals": [],
    "textures": []
  }
}
"@

$modJsonPath = Join-Path $modDir "mod.json"
Set-Content -Path $modJsonPath -Value $modJson -Encoding UTF8
Write-Host "[OK] mod.json 已生成" -ForegroundColor Green

# 生成主类源码
$csCode = @"
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace $Namespace
{
    /// <summary>
    /// $Name - 由 Machine Mod 模板生成器创建。
    /// 作者: $Author
    /// 版本: $Version
    /// </summary>
    public class $ClassName : MachineModBase
    {
        public override string Id { get { return "$Id"; } }
        public override string Name { get { return "$Name"; } }
        public override string Version { get { return "$Version"; } }

        // === 在这里添加你的字段 ===
        // private bool _initialized;

        /// <summary>
        /// OnLoad：Mod加载时调用。
        /// 在此注册内容、读取配置、初始化对象。
        /// </summary>
        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("$Name loading...");

            // === 在这里添加你的初始化代码 ===
            // 示例：注册货物
            // var cargo = new CargoDefinition();
            // cargo.Id = "$Id.cargo";
            // cargo.Name = "My Cargo";
            // cargo.Price = 100f;
            // cargo.Weight = 1f;
            // api.RegisterCargo(cargo);

            Log("$Name loaded.");
        }

        /// <summary>
        /// OnEnable：Mod启用时调用（OnLoad之后自动调用）。
        /// 在此订阅事件总线事件、创建UI。
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            Log("$Name enabled");

            // === 在这里订阅事件 ===
            // SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
            // SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            // SubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);

            // === 在这里添加主菜单按钮 ===
            // Api.AddMainMenuButton("$Name", OnButtonClicked);
        }

        /// <summary>
        /// OnUpdate：每帧调用（由ModManager统一调度）。
        /// </summary>
        public override void OnUpdate()
        {
            // === 在这里添加每帧逻辑 ===
        }

        /// <summary>
        /// OnDisable：Mod禁用时调用。
        /// 在此取消订阅事件、销毁UI。
        /// </summary>
        public override void OnDisable()
        {
            Log("$Name disabled");

            // === 在这里取消订阅事件 ===
            // UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
            // UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            // UnsubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);

            base.OnDisable();
        }

        /// <summary>
        /// OnUnload：Mod卸载时调用（游戏退出或热重载）。
        /// 在此清理资源。
        /// </summary>
        public override void OnUnload()
        {
            Log("$Name unloaded.");
            base.OnUnload();
        }

        // === 在这里添加你的事件回调和辅助方法 ===
        // private void OnTick(MachineEventBus.MachineEventArgs e) { }
        // private void OnSceneLoaded(MachineEventBus.MachineEventArgs e) { }
        // private void OnPlayerSpawn(MachineEventBus.MachineEventArgs e) { }
        // private void OnButtonClicked() { }
    }
}
"@

$csPath = Join-Path $modDir "$Name.cs"
Set-Content -Path $csPath -Value $csCode -Encoding UTF8
Write-Host "[OK] $Name.cs 已生成" -ForegroundColor Green

# 生成编译脚本
$buildScript = @"
<#
    $Name 编译脚本
    使用方法: .\build.ps1
#>
`$ErrorActionPreference = "Stop"

`$gameDir = "D:\steam\steamapps\common\Aviassembly"
`$managedDir = Join-Path `$gameDir "Aviassembly_Data\Managed"
`$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

`$references = @(
    Join-Path `$managedDir "Assembly-CSharp.dll",
    Join-Path `$managedDir "Machine.Core.dll",
    Join-Path `$managedDir "UnityEngine.dll",
    Join-Path `$managedDir "UnityEngine.CoreModule.dll",
    Join-Path `$managedDir "UnityEngine.UI.dll",
    Join-Path `$managedDir "UnityEngine.IMGUIModule.dll"
)

`$refArgs = `$references | ForEach-Object { "/reference:`$_" }

Write-Host "Compiling $Name..." -ForegroundColor Cyan
& `$csc /target:library /out:"code\$Name.dll" `$refArgs "$Name.cs"

if (`$LASTEXITCODE -eq 0) {
    Write-Host "[OK] Compilation successful: code\$Name.dll" -ForegroundColor Green
} else {
    Write-Error "Compilation failed with exit code `$LASTEXITCODE"
}
"@

$buildPath = Join-Path $modDir "build.ps1"
Set-Content -Path $buildPath -Value $buildScript -Encoding UTF8
Write-Host "[OK] build.ps1 已生成" -ForegroundColor Green

# 生成 README
$readme = @"
# $Name

$Description

**作者**: $Author
**版本**: $Version
**Mod ID**: `$Id`

## 安装

1. 编译：运行 `.\build.ps1`
2. 将本文件夹复制到游戏目录的 `mods/` 文件夹下
3. 启动游戏

## 开发

- 主类：`$Namespace.$ClassName`（继承 `MachineModBase`）
- 源码：`$Name.cs`
- 清单：`mod.json`

## 生命周期

```
OnLoad → OnEnable → OnUpdate(每帧) → OnDisable → OnUnload
```

## 事件订阅

```csharp
// 在 OnEnable 中订阅
SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);

// 在 OnDisable 中取消订阅
UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
```

---
*由 Machine Mod 模板生成器创建*
"@

$readmePath = Join-Path $modDir "README.md"
Set-Content -Path $readmePath -Value $readme -Encoding UTF8
Write-Host "[OK] README.md 已生成" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Mod 模板创建完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "目录: $modDir"
Write-Host ""
Write-Host "下一步:"
Write-Host "  1. 编辑 $Name.cs 实现你的Mod功能"
Write-Host "  2. 运行 .\build.ps1 编译"
Write-Host "  3. 复制到游戏 mods/ 目录测试"
Write-Host ""
Write-Host "文件列表:"
Get-ChildItem $modDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($modDir.Length + 1)
    Write-Host "  $rel ($([math]::Round($_.Length/1KB,2)) KB)"
}
