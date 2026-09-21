# Mod 开发指南

> 从零到发布，30 分钟跑通第一个 Mod。

[English](../en/mod-development.md) | [返回首页](../../README.md)

## 目录

1. [环境准备](#1-环境准备)
2. [创建第一个 Mod](#2-创建第一个-mod)
3. [目录结构](#3-目录结构)
4. [mod.json 字段说明](#4-modjson-字段说明)
5. [生命周期](#5-生命周期)
6. [事件系统](#6-事件系统)
7. [UI 开发](#7-ui-开发)
8. [配置系统](#8-配置系统)
9. [内容注册（货物/部件/贴图）](#9-内容注册)
10. [调试技巧](#10-调试技巧)
11. [打包发布](#11-打包发布)
12. [最佳实践](#12-最佳实践)

---

## 1. 环境准备

### 必需工具

- **Windows 10/11**
- **Aviassembly 游戏**（Steam 购买）
- **.NET Framework 4.x**（Windows 自带）
- **C# 编译器**（`csc.exe`，随 .NET Framework 安装）
- **文本编辑器**（推荐 VS Code 或 Visual Studio）

### 验证环境

打开 PowerShell，运行：

```powershell
# 检查 csc 编译器
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /?

# 检查游戏目录
Test-Path "D:\steam\steamapps\common\Aviassembly"
```

### 开发目录约定

建议将开发文件放在 `D:\豆包的下载\Machine_Dev\`：

```
Machine_Dev/
├── src/                    # 源代码
│   ├── Machine.Core/       # 加载器核心
│   └── Mods/               # 官方 Mod 源码
├── templates/              # Mod 模板
├── examples/               # 示例 Mod
├── docs/                   # 文档
├── bin/                    # 编译输出
├── build-core.ps1          # 核心编译脚本
├── build-mods.ps1          # Mod 编译脚本
└── newmod.ps1              # Mod 模板生成器
```

---

## 2. 创建第一个 Mod

### 方法一：使用模板生成器（推荐）

```powershell
cd D:\豆包的下载\Machine_Dev
.\newmod.ps1 HelloWorld
```

这会在 `src/Mods/HelloWorld/` 创建完整的 Mod 目录结构。

### 方法二：手动创建

#### 步骤 1：创建目录

```
Machine/mods/machine.helloworld/
├── mod.json
├── config.json
└── code/
    └── HelloWorld.dll  (编译后生成)
```

#### 步骤 2：创建 mod.json

```json
{
  "id": "machine.helloworld",
  "name": "Hello World",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "我的第一个 Machine Mod",
  "type": "code",
  "apiVersion": "1.0",
  "loaderVersion": ">=2.0.0",
  "dependencies": [],
  "entry": "Machine.HelloWorld.HelloWorldMod",
  "code": {
    "assemblies": ["code/HelloWorld.dll"],
    "mainClass": "Machine.HelloWorld.HelloWorldMod"
  },
  "content": {
    "cargo": [],
    "parts": [],
    "decals": [],
    "textures": []
  }
}
```

#### 步骤 3：创建主类 HelloWorld.cs

```csharp
using System;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Machine.HelloWorld
{
    public class HelloWorldMod : MachineModBase
    {
        public override string Id { get { return "machine.helloworld"; } }
        public override string Name { get { return "Hello World"; } }
        public override string Version { get { return "1.0.0"; } }

        private int _frameCount;

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("Hello World! Mod loaded.");
        }

        public override void OnEnable()
        {
            base.OnEnable();
            Log("Hello World! Mod enabled.");

            // 订阅场景加载事件
            SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
        }

        public override void OnUpdate()
        {
            _frameCount++;
            if (_frameCount % 300 == 0)  // 每5秒（60fps）
            {
                Log("Hello World! Frame count: " + _frameCount);
            }
        }

        public override void OnDisable()
        {
            Log("Hello World! Mod disabled.");
            UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            base.OnDisable();
        }

        private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
        {
            Log("Scene loaded: " + e.SceneName);
        }
    }
}
```

#### 步骤 4：编译

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$game = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed"

& $csc /target:library /out:Machine/mods/machine.helloworld/code/HelloWorld.dll `
    /reference:"$game\Assembly-CSharp.dll" `
    /reference:"$game\Machine.Core.dll" `
    /reference:"$game\UnityEngine.dll" `
    /reference:"$game\UnityEngine.CoreModule.dll" `
    HelloWorld.cs
```

#### 步骤 5：测试

1. 启动游戏
2. 主菜单点击 `Mods` 按钮
3. 确认 `Hello World` Mod 已启用
4. 进入游戏，查看日志 `Machine/logs/Machine.log`

---

## 3. 目录结构

### Mod 目录结构

```
mods/machine.mymod/
├── mod.json              # Mod 清单（必需）
├── config.json           # Mod 配置（可选）
├── README.md             # Mod 说明文档（推荐）
├── changelog.md          # 更新日志（推荐）
├── code/                 # 编译后的 DLL
│   └── MyMod.dll
├── content/              # 内容文件
│   ├── cargo/            # 货物定义 JSON
│   ├── parts/            # 部件定义 JSON
│   ├── decals/           # 贴花定义 JSON
│   └── textures/         # 贴图文件 PNG
├── assets/               # 资源文件
│   ├── audio/            # 音频文件 MP3/WAV
│   ├── models/           # 模型文件
│   └── ui/               # UI 资源
└── localization/         # 多语言文件
    ├── en.json
    └── zh.json
```

### 内容定义文件格式

#### 货物定义 (content/cargo/mycargo.json)

```json
{
  "id": "mymod.mycargo",
  "name": "My Cargo",
  "price": 100,
  "weight": 5.0,
  "cargoSpace": 2,
  "description": "这是一个示例货物"
}
```

---

## 4. mod.json 字段说明

### 必需字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | string | Mod 唯一标识，建议反向域名格式，如 `machine.radar` |
| `name` | string | Mod 显示名称 |
| `version` | string | 语义化版本，如 `1.2.0` |
| `type` | string | Mod 类型，目前支持 `code`（代码 Mod） |
| `code.assemblies` | array | DLL 文件路径列表（相对 Mod 目录） |
| `code.mainClass` | string | 主类完整名称，如 `Machine.Radar.RadarMod` |

### 推荐字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `author` | string | 作者名称 |
| `description` | string | Mod 描述 |
| `apiVersion` | string | API 版本，目前为 `1.0` |
| `loaderVersion` | string | 加载器版本要求，如 `>=2.0.0` |
| `entry` | string | 入口类（同 code.mainClass，用于快速识别） |

### 依赖与加载顺序

| 字段 | 类型 | 说明 |
|------|------|------|
| `dependencies` | array | 必需依赖，缺失则禁用此 Mod。格式：`{"id": "machine.battlecore", "version": ">=1.0.0"}` |
| `optionalDependencies` | array | 可选依赖，缺失不影响加载 |
| `conflicts` | array | 冲突 Mod，同时存在时自动禁用其中一个 |
| `loadBefore` | array | 在此 Mod 之前加载的 Mod ID 列表 |
| `loadAfter` | array | 在此 Mod 之后加载的 Mod ID 列表 |

### 依赖格式示例

```json
{
  "dependencies": [
    {"id": "machine.battlecore", "version": ">=1.0.0"},
    {"id": "machine.radar", "version": ">=2.1.0"}
  ],
  "optionalDependencies": [
    {"id": "machine.voice", "version": ">=1.0.0"}
  ],
  "conflicts": ["machine.oldradar"]
}
```

### 版本范围语法

| 格式 | 说明 | 示例 |
|------|------|------|
| `>=1.0.0` | 大于等于 | `>=2.0.0` |
| `>1.0.0` | 大于 | `>1.5.0` |
| `<=1.0.0` | 小于等于 | `<=3.0.0` |
| `<1.0.0` | 小于 | `<2.0.0` |
| `=1.0.0` | 等于 | `=1.2.0` |
| `!=1.0.0` | 不等于 | `!=1.0.0` |
| `>=1.0.0 <2.0.0` | 组合条件 | `>=1.0.0 <2.0.0` |

### 内容字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `content.cargo` | array | 货物定义文件路径列表 |
| `content.parts` | array | 部件定义文件路径列表 |
| `content.decals` | array | 贴花定义文件路径列表 |
| `content.textures` | array | 贴图文件路径列表 |

---

## 5. 生命周期

MachineModBase 提供完整的生命周期回调：

```
OnLoad(api)       ← Mod 加载时，注册内容、读取配置
    ↓
OnEnable()        ← Mod 启用时，订阅事件、创建 UI
    ↓
OnUpdate()        ← 每帧调用（60fps）
OnFixedUpdate()   ← 固定帧率调用（物理更新，50fps）
    ↓
OnDisable()       ← Mod 禁用时，取消订阅、销毁 UI
    ↓
OnUnload()        ← Mod 卸载时，清理资源
```

### 生命周期方法说明

#### OnLoad(IMachineApi api)

- **调用时机**：Mod 加载时（游戏启动后）
- **用途**：
  - 注册货物、部件、贴图等内容
  - 读取配置文件
  - 初始化数据结构
- **注意**：此时游戏场景可能尚未加载，不要访问场景对象

#### OnEnable()

- **调用时机**：OnLoad 之后自动调用
- **用途**：
  - 订阅事件总线事件
  - 创建 UI 元素
  - 注册命令
- **注意**：必须与 OnDisable 对称，取消所有订阅

#### OnUpdate()

- **调用时机**：每帧调用（由 ModManager 统一调度）
- **用途**：
  - 每帧逻辑更新
  - UI 更新
- **注意**：
  - 性能敏感，避免 heavy 操作
  - 帧预算 2ms，超过会报警
  - 只有继承 MachineModBase 的 Mod 会收到此回调

#### OnFixedUpdate()

- **调用时机**：固定帧率调用（物理更新）
- **用途**：
  - 物理相关逻辑
  - 需要稳定时间步的计算

#### OnDisable()

- **调用时机**：Mod 禁用时
- **用途**：
  - 取消事件订阅
  - 销毁 UI 元素
  - 保存状态
- **注意**：必须清理所有资源，避免内存泄漏

#### OnUnload()

- **调用时机**：Mod 卸载时（游戏退出或热重载）
- **用途**：
  - 最终清理
  - 保存持久化数据

### 生命周期示例

```csharp
public class MyMod : MachineModBase
{
    private GameObject _myWindow;

    public override void OnLoad(IMachineApi api)
    {
        base.OnLoad(api);
        LoadConfig();
        RegisterContent();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
        CreateUI();
    }

    public override void OnUpdate()
    {
        UpdateUI();
    }

    public override void OnDisable()
    {
        UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
        DestroyUI();
        base.OnDisable();
    }

    public override void OnUnload()
    {
        SaveData();
        base.OnUnload();
    }
}
```

---

## 6. 事件系统

### 事件类型

Machine 事件总线提供 12 种事件类型：

| 事件类型 | 触发时机 | 事件参数 |
|----------|----------|----------|
| `GameStart` | 游戏启动 | 无 |
| `SceneLoaded` | 场景加载完成 | `SceneName` |
| `SceneUnloading` | 场景卸载前 | `SceneName` |
| `PlayerSpawn` | 玩家飞机生成 | 无 |
| `Tick` | 每帧 | `DeltaTime` |
| `FixedTick` | 固定帧率 | `DeltaTime` |
| `FlightStart` | 开始飞行 | 无 |
| `FlightEnd` | 结束飞行 | 无 |
| `GameEnd` | 游戏退出 | 无 |
| `ModsLoaded` | 所有 Mod 加载完成 | 无 |
| `SaveLoaded` | 存档加载完成 | 无 |

### 订阅事件

```csharp
public override void OnEnable()
{
    base.OnEnable();

    // 订阅事件
    SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
    SubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
    SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
}

private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
{
    Log("Scene loaded: " + e.SceneName);
}

private void OnPlayerSpawn(MachineEventBus.MachineEventArgs e)
{
    Log("Player spawned!");
}

private void OnTick(MachineEventBus.MachineEventArgs e)
{
    // 每帧事件，DeltaTime 可用
    float dt = e.DeltaTime;
}
```

### 取消订阅

```csharp
public override void OnDisable()
{
    UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
    UnsubscribeEvent(MachineEventBus.EventType.PlayerSpawn, OnPlayerSpawn);
    UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
    base.OnDisable();
}
```

### 发布自定义事件

Mod 可以发布自定义事件，供其他 Mod 订阅：

```csharp
// 发布事件
var args = new MachineEventBus.MachineEventArgs();
args.Data = myCustomData;
MachineEventBus.Publish(MachineEventBus.EventType.Tick, args);

// 注意：建议使用自定义事件类型（未来版本支持）
```

### 事件隔离

- 每个事件处理器都在 try/catch 中执行
- 单个 Mod 的事件处理异常不会影响其他 Mod
- 异常会被记录到日志

---

## 7. UI 开发

### 添加主菜单按钮

```csharp
public override void OnEnable()
{
    base.OnEnable();

    // 在主菜单添加按钮
    if (Api != null)
    {
        Api.AddMainMenuButton("My Mod", OnMyButtonClicked);
    }
}

private void OnMyButtonClicked()
{
    Log("My Mod button clicked!");
    OpenMyWindow();
}
```

### 创建自定义窗口

```csharp
private GameObject _window;

private void CreateWindow()
{
    // 创建 Canvas
    var canvasObj = new GameObject("MyModWindow");
    var canvas = canvasObj.AddComponent<Canvas>();
    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
    canvas.sortingOrder = 100;
    canvasObj.AddComponent<CanvasScaler>();
    canvasObj.AddComponent<GraphicRaycaster>();

    // 创建背景面板
    var panel = new GameObject("Panel");
    panel.transform.SetParent(canvasObj.transform, false);
    var image = panel.AddComponent<Image>();
    image.color = new Color(0, 0, 0, 0.8f);
    var rect = panel.GetComponent<RectTransform>();
    rect.sizeDelta = new Vector2(400, 300);
    rect.anchoredPosition = Vector2.zero;

    // 创建文本
    var textObj = new GameObject("Text");
    textObj.transform.SetParent(panel.transform, false);
    var text = textObj.AddComponent<Text>();
    text.text = "Hello World!";
    text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    text.fontSize = 24;
    text.alignment = TextAnchor.MiddleCenter;
    var textRect = text.GetComponent<RectTransform>();
    textRect.sizeDelta = new Vector2(380, 280);

    _window = canvasObj;
}

private void DestroyWindow()
{
    if (_window != null)
    {
        UnityEngine.Object.Destroy(_window);
        _window = null;
    }
}
```

### UI 最佳实践

1. **使用 CanvasScaler**：适配不同分辨率
2. **合理的 sortingOrder**：避免被其他 UI 遮挡
3. **及时销毁**：OnDisable 中销毁所有创建的 UI
4. **复用对象**：频繁创建/销毁的 UI 使用对象池
5. **避免每帧创建**：UI 元素在 OnEnable 中创建，OnUpdate 中只更新内容

---

## 8. 配置系统

### Mod 配置文件

每个 Mod 可以有自己的 `config.json`，放在 Mod 目录下：

```
mods/machine.mymod/
├── mod.json
└── config.json      ← Mod 配置
```

### 配置文件格式

```json
{
  "enabled": true,
  "mySetting": "value",
  "myNumber": 42,
  "myFloat": 3.14,
  "myBool": true,
  "myArray": [1, 2, 3],
  "myObject": {
    "nested": "value"
  }
}
```

### 读取配置

```csharp
[Serializable]
private class MyModConfig
{
    public bool enabled = true;
    public string mySetting = "default";
    public int myNumber = 42;
    public float myFloat = 3.14f;
}

private MyModConfig _config;

private void LoadConfig()
{
    try
    {
        if (Api == null) return;
        string modDir = Api.GetModsDirectory();
        string configPath = Path.Combine(modDir, "machine.mymod", "config.json");

        if (!File.Exists(configPath))
        {
            // 创建默认配置
            _config = new MyModConfig();
            File.WriteAllText(configPath, JsonUtility.ToJson(_config, true));
            Log("Default config created at " + configPath);
            return;
        }

        string json = File.ReadAllText(configPath);
        _config = JsonUtility.FromJson<MyModConfig>(json);
        Log("Config loaded: " + _config.mySetting);
    }
    catch (Exception ex)
    {
        LogWarn("Config load failed: " + ex.Message);
        _config = new MyModConfig();  // 使用默认值
    }
}
```

### 保存配置

```csharp
private void SaveConfig()
{
    try
    {
        if (Api == null || _config == null) return;
        string modDir = Api.GetModsDirectory();
        string configPath = Path.Combine(modDir, "machine.mymod", "config.json");
        File.WriteAllText(configPath, JsonUtility.ToJson(_config, true));
        Log("Config saved");
    }
    catch (Exception ex)
    {
        LogError("Config save failed: " + ex.Message);
    }
}
```

---

## 9. 内容注册

### 注册货物

```csharp
public override void OnLoad(IMachineApi api)
{
    base.OnLoad(api);

    // 方法一：通过 JSON 文件注册（在 mod.json 的 content.cargo 中声明）
    // 方法二：通过代码注册
    var cargo = new CargoDefinition();
    cargo.Id = "mymod.specialcargo";
    cargo.Name = "Special Cargo";
    cargo.Price = 500f;
    cargo.Weight = 10f;
    cargo.CargoSpace = 3;
    api.RegisterCargo(cargo);

    Log("Cargo registered: " + cargo.Name);
}
```

### 注册部件

```csharp
var part = new PartDefinition();
part.Id = "mymod.specialpart";
part.Name = "Special Part";
part.Weight = 5f;
api.RegisterPart(part);
```

### 注册贴图

```csharp
// 加载 PNG 贴图
byte[] pngBytes = File.ReadAllBytes(Path.Combine(modDir, "assets", "mytexture.png"));
Texture2D tex = new Texture2D(2, 2);
tex.LoadImage(pngBytes);
tex.name = "mymod_mytexture";
api.RegisterTexture(tex);
```

---

## 10. 调试技巧

### 日志输出

```csharp
// 基础日志
Log("This is an info message");
LogWarn("This is a warning");
LogError("This is an error");

// 带模块名的日志（推荐）
Machine.Core.Log.Info("MyMod", "Initializing...");
Machine.Core.Log.Debug("MyMod", "Debug info: " + someVariable);
```

### 日志级别

编辑 `Machine/config.json`：

```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

级别：`ERROR` < `WARN` < `INFO` < `DEBUG` < `TRACE`

### 诊断命令

游戏内（聊天框）输入：

```
/machine diag
```

输出：
- 加载器版本、API 版本、日志级别
- 更新通道
- 已加载 Mod 列表（含状态）
- 性能报告（每个 Mod 的 init/avg/peak 耗时）
- 对象池统计

### 性能分析

```csharp
// 在 Mod 中使用性能分析器
ModProfiler.BeginUpdate("mymod");
// ... 你的代码 ...
ModProfiler.EndUpdate("mymod");
```

查看性能报告：`/machine diag`

### 崩溃报告

游戏崩溃时自动生成 `Machine/logs/crash_report_YYYYMMDD_HHmmss.txt`，包含：
- 加载器版本、Mod 列表
- 异常信息和堆栈
- 最后 200 行日志
- 最近错误

### 常见调试问题

**Q: Mod 没有加载？**
A: 检查日志：
1. `mod.json` 格式是否正确（JSON 语法）
2. DLL 路径是否正确（相对 Mod 目录）
3. 主类名称是否正确（命名空间.类名）
4. 是否缺失依赖（日志会明确提示）

**Q: Mod 加载了但没有效果？**
A: 
1. 确认 Mod 已启用（主菜单 Mods 按钮）
2. 检查 OnLoad/OnEnable 是否有异常
3. 确认事件订阅正确
4. 用 `Log()` 输出调试信息

**Q: 游戏卡顿？**
A:
1. `/machine diag` 查看哪个 Mod 耗时高
2. 检查 OnUpdate 中是否有 heavy 操作
3. 避免每帧创建对象（使用对象池）
4. 避免每帧反射调用（缓存结果）

---

## 11. 打包发布

### 发布前检查清单

- [ ] `mod.json` 字段完整正确
- [ ] 版本号已更新（语义化版本）
- [ ] 配置文件有合理的默认值
- [ ] README.md 包含功能说明和使用方法
- [ ] CHANGELOG.md 记录本次更新
- [ ] 依赖声明正确（必需/可选）
- [ ] 冲突声明正确
- [ ] 在纯净环境中测试通过
- [ ] 无明显性能问题（`/machine diag` 检查）

### 目录打包

将 Mod 目录打包为 ZIP：

```
mymod-v1.0.0.zip
└── machine.mymod/
    ├── mod.json
    ├── config.json
    ├── README.md
    ├── CHANGELOG.md
    ├── code/
    │   └── MyMod.dll
    └── content/
        └── ...
```

### 安装方式

玩家将 ZIP 解压到 `Aviassembly/Machine/mods/` 目录即可。

### 版本号规范

使用语义化版本 `MAJOR.MINOR.PATCH`：

- **MAJOR**：不兼容的 API 变更
- **MINOR**：向下兼容的功能新增
- **PATCH**：向下兼容的问题修复

示例：
- `1.0.0` → `1.0.1`（修复 bug）
- `1.0.1` → `1.1.0`（新增功能）
- `1.1.0` → `2.0.0`（不兼容变更）

---

## 12. 最佳实践

### 性能

1. **OnUpdate 轻量化**：避免 heavy 操作，帧预算 2ms
2. **使用对象池**：频繁创建/销毁的对象使用 `ObjectPool<T>`
3. **缓存反射结果**：不要每帧 `GetField`/`GetMethod`
4. **节流日志**：高频日志使用 `Log.Debug()` 或计数器节流
5. **延迟初始化**：非关键内容在场景加载后再初始化

### 稳定性

1. **异常隔离**：所有外部调用都在 try/catch 中
2. **对称清理**：OnEnable 中创建的，OnDisable 中销毁
3. **空值检查**：访问游戏对象前检查 null
4. **配置容错**：配置读取失败时使用默认值
5. **版本兼容**：使用 `loaderVersion` 声明最低版本要求

### 可维护性

1. **模块化设计**：单一职责，避免上帝类
2. **配置驱动**：可调参数放在 config.json
3. **日志充分**：关键节点输出日志，方便调试
4. **文档完善**：README、CHANGELOG、代码注释
5. **示例丰富**：提供最小可运行示例

### 兼容性

1. **不修改游戏文件**：通过 API 和事件扩展
2. **纯净模式友好**：Mod 版存档与纯净版隔离
3. **依赖声明**：明确声明前置 Mod 和版本要求
4. **冲突声明**：与不兼容的 Mod 声明冲突
5. **可选依赖**：非必需功能使用可选依赖

---

## 下一步

- 阅读 [API 参考](../api/) 了解完整 API
- 查看 [示例 Mod](../../examples/) 学习实际用法
- 加入社区，分享你的 Mod

---

**遇到问题？** 查看 [常见问题](../../README.md#常见问题) 或提交 GitHub Issue。
