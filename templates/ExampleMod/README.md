# Example Mod - Machine v2 API 示例

这是一个最小可运行的 Mod 模板，展示 Machine 加载器 v2 API 的完整用法。

## 功能演示

1. **生命周期**：`OnLoad` / `OnEnable` / `OnUpdate` / `OnDisable` / `OnUnload`
2. **事件总线**：订阅 `Tick`、`SceneLoaded`、`PlayerSpawn`、`GameEnd` 事件
3. **UI按钮**：在主菜单添加自定义按钮
4. **配置读取**：从 `config.json` 读取自定义配置
5. **命令注册**：可扩展注册聊天命令

## 目录结构

```
ExampleMod/
├── mod.json              # Mod 清单（v2格式，含版本化和依赖声明）
├── ExampleMod.cs         # 主类源码（继承 MachineModBase）
├── code/
│   └── ExampleMod.dll    # 编译后的程序集
└── config.json           # 运行时配置（首次运行自动生成）
```

## mod.json v2 格式说明

```json
{
  "id": "machine.example",           // Mod唯一ID（必填）
  "name": "Example Mod",             // 显示名称
  "version": "1.0.0",                // 语义化版本 MAJOR.MINOR.PATCH
  "author": "Machine Team",          // 作者
  "description": "...",              // 描述
  "type": "code",                    // code（代码Mod）或 content（纯内容Mod）

  // === v2 新增字段 ===
  "apiVersion": "1.0",               // 使用的 Machine API 版本
  "loaderVersion": ">=1.0.0",        // 要求的加载器版本范围
  "dependencies": [],                 // 必需依赖（缺少则拒绝加载）
  "optionalDependencies": [],         // 可选依赖（不存在也能运行）
  "loadAfter": [],                    // 在指定Mod之后加载
  "loadBefore": [],                   // 在指定Mod之前加载
  "entry": "Machine.Example.ExampleMod",  // 入口类名（兼容字段）

  "code": {
    "assemblies": ["code/ExampleMod.dll"],
    "mainClass": "Machine.Example.ExampleMod"
  },
  "content": {
    "cargo": [],
    "parts": [],
    "decals": [],
    "textures": []
  }
}
```

### 版本范围语法

- `>=2.4.0` - 大于等于 2.4.0
- `>2.3.0` - 大于 2.3.0
- `<=3.0.0` - 小于等于 3.0.0
- `<3.0.0` - 小于 3.0.0
- `=2.4.0` - 等于 2.4.0
- `>=2.3.0 <3.0.0` - 多条件组合（AND关系）

### 依赖声明格式

- `"machine.battlecore"` - 只要求存在，不限制版本
- `"machine.battlecore>=1.0.0"` - 要求版本大于等于 1.0.0
- `"machine.battlecore>=1.0.0 <2.0.0"` - 要求版本在 1.0.0 到 2.0.0 之间

## 编译方法

在 `Machine_Dev` 目录下执行：

```bash
csc /target:library /out:templates/ExampleMod/code/ExampleMod.dll ^
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Assembly-CSharp.dll ^
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Machine.Core.dll ^
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/UnityEngine.dll ^
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/UnityEngine.CoreModule.dll ^
    templates/ExampleMod/ExampleMod.cs
```

或使用项目提供的 `build-mod.ps1` 脚本。

## 安装方法

1. 将 `ExampleMod` 文件夹复制到游戏目录的 `mods/` 文件夹下
2. 确保 `code/ExampleMod.dll` 已编译
3. 启动游戏，Mod 会自动加载

## 生命周期详解

```
游戏启动
    ↓
OnLoad(api)     ← 注册内容、读取配置、初始化
    ↓
OnEnable()      ← 订阅事件、创建UI
    ↓
OnUpdate()      ← 每帧调用（游戏运行中）
    ↓
OnDisable()     ← Mod被禁用或游戏退出
    ↓
OnUnload()      ← 清理资源
```

## 事件总线

可用事件类型（`MachineEventBus.EventType`）：

| 事件 | 触发时机 | 参数 |
|------|----------|------|
| `GameStart` | 游戏启动完成 | - |
| `SceneLoaded` | 场景加载完成 | SceneName |
| `SceneUnloading` | 场景即将卸载 | SceneName |
| `PlayerSpawn` | 玩家飞机生成 | - |
| `Tick` | 每帧更新 | DeltaTime |
| `FixedTick` | 固定帧率更新 | DeltaTime |
| `FlightStart` | 进入飞行模式 | - |
| `FlightEnd` | 退出飞行模式 | - |
| `GameEnd` | 游戏即将退出 | - |
| `ModsLoaded` | 所有Mod加载完成 | - |
| `SaveLoaded` | 存档加载完成 | - |

## API 参考

### MachineModBase 基类

```csharp
public abstract class MachineModBase : IMachineMod
{
    public abstract string Id { get; }
    public virtual string Name { get; }
    public virtual string Version { get; }
    public ModLifecycleState State { get; }
    protected IMachineApi Api { get; }

    public virtual void OnLoad(IMachineApi api);
    public virtual void OnEnable();
    public virtual void OnDisable();
    public virtual void OnUnload();
    public virtual void OnUpdate();
    public virtual void OnFixedUpdate();

    // 便捷方法
    protected void SubscribeEvent(EventType type, MachineEventHandler handler);
    protected void UnsubscribeEvent(EventType type, MachineEventHandler handler);
    protected void Log(string message);
    protected void LogWarn(string message);
    protected void LogError(string message);
}
```

### IMachineApi 接口

```csharp
public interface IMachineApi
{
    void Log(string message);
    void RegisterCargo(CargoDefinition definition);
    void RegisterPart(PartDefinition definition);
    void RegisterDecal(string decalName, byte[] pngData);
    Texture2D LoadTexture(string name, byte[] pngData);
    string GetModsDirectory();
    void AddMainMenuButton(string text, Action onClick);
    void OpenModManager();
    GameObject CreatePartPrefab(PartDefinition definition);
}
```

## 向后兼容

- 旧 Mod 直接实现 `IMachineMod` 接口（只有 `Id` 和 `OnLoad`）仍然可以正常工作
- 新 Mod 推荐继承 `MachineModBase` 以获得完整生命周期支持
- `mod.json` 中不包含 v2 新字段时，使用默认值

## 常见问题

**Q: 我的 Mod 没有收到 OnUpdate 调用？**
A: 确保你的 Mod 继承了 `MachineModBase`，而不是直接实现 `IMachineMod` 接口。

**Q: 如何在 Mod 之间共享数据？**
A: 使用 `MachineEventBus` 发布和订阅事件，或使用静态类共享数据。

**Q: 如何声明可选依赖？**
A: 在 `mod.json` 的 `optionalDependencies` 字段中声明。可选依赖不存在时 Mod 仍可加载，但相关功能会被禁用。
