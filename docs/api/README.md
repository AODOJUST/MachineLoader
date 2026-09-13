# Machine API 参考

> Machine Mod Loader 核心 API 文档。

[返回首页](../../README.md) | [开发指南](../zh/mod-development.md)

## 目录

- [核心接口](#核心接口)
  - [IMachineMod](#imachinemod)
  - [IMachineApi](#imachineapi)
  - [MachineModBase](#machinemodbase)
- [事件系统](#事件系统)
  - [MachineEventBus](#machineeventbus)
  - [EventType](#eventtype)
  - [MachineEventArgs](#machineeventargs)
- [内容定义](#内容定义)
  - [CargoDefinition](#cargodefinition)
  - [PartDefinition](#partdefinition)
- [运行时状态](#运行时状态)
  - [MachineState](#machinestate)
- [日志系统](#日志系统)
  - [Log](#log)
- [性能分析](#性能分析)
  - [ModProfiler](#modprofiler)
- [对象池](#对象池)
  - [ObjectPool\<T\>](#objectpoolt)
  - [GameObjectPool](#gameobjectpool)
  - [PoolManager](#poolmanager)
- [Mod 管理](#mod-管理)
  - [ModManager](#modmanager)
- [版本工具](#版本工具)
  - [SemVer / VersionRange](#semver--versionrange)

---

## 核心接口

### IMachineMod

代码 Mod 的基础接口。实现此接口的类会被 Machine 加载。

**命名空间**：`Machine.Mod`

```csharp
public interface IMachineMod
{
    /// <summary>Mod 唯一 ID（建议形如 author.modname）。</summary>
    string Id { get; }

    /// <summary>加载回调：注册内容、挂接事件、创建对象都在这里。</summary>
    void OnLoad(IMachineApi api);
}
```

**示例**：

```csharp
public class MyMod : IMachineMod
{
    public string Id { get { return "mymod.example"; } }

    public void OnLoad(IMachineApi api)
    {
        api.Log("MyMod loaded!");
    }
}
```

> **注意**：建议继承 `MachineModBase` 而不是直接实现 `IMachineMod`，以获得完整生命周期支持。

---

### IMachineApi

Machine 提供给代码 Mod 的 API 面。通过 `OnLoad(IMachineApi api)` 参数获取。

**命名空间**：`Machine.Mod`

```csharp
public interface IMachineApi
{
    /// <summary>写入 Machine 日志。</summary>
    void Log(string message);

    /// <summary>注册一种新货物（会出现在机场与任务里）。</summary>
    void RegisterCargo(CargoDefinition definition);

    /// <summary>注册一种新部件（会出现在建造部件栏）。</summary>
    void RegisterPart(PartDefinition definition);

    /// <summary>注册一个贴花材质（进飞机涂装面板）。</summary>
    void RegisterDecal(string decalName, byte[] pngData);

    /// <summary>把 PNG 字节解码成 Texture2D（供 Mod 自行使用）。</summary>
    Texture2D LoadTexture(string name, byte[] pngData);

    /// <summary>获取 mods 文件夹路径。</summary>
    string GetModsDirectory();

    /// <summary>在主菜单追加一个按钮。</summary>
    void AddMainMenuButton(string text, Action onClick);

    /// <summary>打开 Machine 的 Mod 管理器界面。</summary>
    void OpenModManager();

    /// <summary>按定义构建部件预制体（不自动注册，便于高级 Mod 二次定制）。</summary>
    GameObject CreatePartPrefab(PartDefinition definition);
}
```

**方法说明**：

| 方法 | 参数 | 返回值 | 说明 |
|------|------|--------|------|
| `Log` | `string message` | void | 输出 INFO 级别日志 |
| `RegisterCargo` | `CargoDefinition definition` | void | 注册新货物 |
| `RegisterPart` | `PartDefinition definition` | void | 注册新部件 |
| `RegisterDecal` | `string decalName, byte[] pngData` | void | 注册贴花 |
| `LoadTexture` | `string name, byte[] pngData` | `Texture2D` | PNG 字节转纹理 |
| `GetModsDirectory` | 无 | `string` | mods 文件夹绝对路径 |
| `AddMainMenuButton` | `string text, Action onClick` | void | 主菜单添加按钮 |
| `OpenModManager` | 无 | void | 打开 Mod 管理器 |
| `CreatePartPrefab` | `PartDefinition definition` | `GameObject` | 创建部件预制体 |

---

### MachineModBase

Mod 基类，提供完整生命周期、事件订阅、日志等便利功能。

**命名空间**：`Machine.Mod`

```csharp
public abstract class MachineModBase : IMachineMod
{
    // 属性
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Version { get; }

    // 运行时属性
    protected IMachineApi Api { get; }
    protected bool HasError { get; }

    // 生命周期（可重写）
    public virtual void OnLoad(IMachineApi api);
    public virtual void OnEnable();
    public virtual void OnUpdate();
    public virtual void OnFixedUpdate();
    public virtual void OnDisable();
    public virtual void OnUnload();

    // 事件订阅
    protected void SubscribeEvent(EventType type, MachineEventHandler handler);
    protected void UnsubscribeEvent(EventType type, MachineEventHandler handler);

    // 日志便利方法
    protected void Log(string message);
    protected void LogWarn(string message);
    protected void LogError(string message);

    // 错误标记
    protected void SetError();
}
```

**生命周期顺序**：

```
OnLoad(api) → OnEnable() → OnUpdate()/OnFixedUpdate() → OnDisable() → OnUnload()
```

**示例**：

```csharp
public class MyMod : MachineModBase
{
    public override string Id { get { return "mymod.example"; } }
    public override string Name { get { return "Example Mod"; } }
    public override string Version { get { return "1.0.0"; } }

    public override void OnLoad(IMachineApi api)
    {
        base.OnLoad(api);
        Log("Mod loaded");
    }

    public override void OnEnable()
    {
        base.OnEnable();
        SubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
    }

    public override void OnDisable()
    {
        UnsubscribeEvent(MachineEventBus.EventType.Tick, OnTick);
        base.OnDisable();
    }
}
```

---

## 事件系统

### MachineEventBus

全局事件总线，支持订阅/发布/隔离机制。

**命名空间**：`Machine.Core`

```csharp
public static class MachineEventBus
{
    // 订阅/取消订阅
    public static void Subscribe(EventType type, MachineEventHandler handler);
    public static void Unsubscribe(EventType type, MachineEventHandler handler);

    // 发布事件
    public static void Publish(MachineEventArgs e);
    public static void Publish(EventType type);
    public static void PublishScene(EventType type, string sceneName);
    public static void PublishTick(EventType type, float deltaTime);
    public static void PublishData(EventType type, object data);

    // 诊断
    public static int GetSubscriberCount(EventType type);
    public static int GetTotalSubscriberCount();

    // 清理
    public static void ClearAll();
}
```

**事件处理器委托**：

```csharp
public delegate void MachineEventHandler(MachineEventArgs e);
```

**示例**：

```csharp
// 订阅
MachineEventBus.Subscribe(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);

// 发布
var args = new MachineEventBus.MachineEventArgs();
args.SceneName = "Airport";
MachineEventBus.Publish(MachineEventBus.EventType.SceneLoaded, args);

// 取消订阅
MachineEventBus.Unsubscribe(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
```

> **注意**：在 MachineModBase 中建议使用 `SubscribeEvent`/`UnsubscribeEvent` 方法，会自动记录订阅关系。

---

### EventType

事件类型枚举。

**命名空间**：`Machine.Core`

```csharp
public enum EventType
{
    GameStart,        // 游戏启动
    SceneLoaded,      // 场景加载完成（参数：SceneName）
    SceneUnloading,   // 场景卸载前（参数：SceneName）
    PlayerSpawn,      // 玩家飞机生成
    Tick,             // 每帧（参数：DeltaTime）
    FixedTick,        // 固定帧率（参数：DeltaTime）
    FlightStart,      // 开始飞行
    FlightEnd,        // 结束飞行
    GameEnd,          // 游戏退出
    ModsLoaded,       // 所有 Mod 加载完成
    SaveLoaded        // 存档加载完成
}
```

---

### MachineEventArgs

事件参数。

**命名空间**：`Machine.Core`

```csharp
public class MachineEventArgs
{
    public EventType Type;        // 事件类型
    public string SceneName;      // 场景名称（SceneLoaded/SceneUnloading）
    public float DeltaTime;       // 帧间隔（Tick/FixedTick）
    public object Data;           // 自定义数据
}
```

---

## 内容定义

### CargoDefinition

新货物定义。

**命名空间**：`Machine.Mod`

```csharp
[Serializable]
public class CargoDefinition
{
    public string Id = "";              // 货物唯一 ID
    public string Name = "";            // 显示名称
    public float Price = 100f;          // 价格
    public float Weight = 1f;            // 重量
    public int CargoSpace = 1;           // 占用货仓空间
    public bool Fragile = false;         // 是否易碎
    public bool Expires = false;         // 是否过期
    public float ExpirationTime = 60f;   // 过期时间（秒）
    public byte[] IconPng = null;        // 图标 PNG 字节（可选）
}
```

**示例**：

```csharp
var cargo = new CargoDefinition();
cargo.Id = "mymod.specialcargo";
cargo.Name = "Special Cargo";
cargo.Price = 500f;
cargo.Weight = 10f;
cargo.CargoSpace = 3;
api.RegisterCargo(cargo);
```

---

### PartDefinition

新部件定义（模型 + 数据）。

**命名空间**：`Machine.Mod`

```csharp
[Serializable]
public class PartDefinition
{
    public string Id = "";                    // 部件唯一 ID
    public string Name = "";                  // 显示名称
    public float Price = 50f;                 // 价格
    public float Weight = 1f;                  // 重量
    public float Scale = 1f;                   // 缩放
    public byte[] ModelObj = null;             // Wavefront OBJ 模型文本（UTF-8 字节）
    public byte[] TexturePng = null;           // 部件贴图 PNG 字节
    public string PartClass = "Machine.Core.MachinePart";  // 部件行为类
}
```

---

## 运行时状态

### MachineState

加载器运行时状态，供所有代码 Mod 查询。

**命名空间**：`Machine.Mod`

```csharp
public static class MachineState
{
    /// <summary>纯净模式：true = 只玩原版内容（mod 休眠）；false = mod 全功能。</summary>
    public static bool PureMode;

    /// <summary>本次进入游戏场景是否来自 Mod Saves 入口。</summary>
    public static bool ViaModSaves;

    /// <summary>玩家飞机是否处于"飞行模式"（起飞后）。</summary>
    public static bool InFlight();
}
```

**使用建议**：

```csharp
public override void OnUpdate()
{
    // 纯净模式下休眠
    if (MachineState.PureMode) return;

    // 只在飞行中显示 HUD
    if (!MachineState.InFlight()) return;

    // 你的飞行中逻辑
    UpdateHUD();
}
```

---

## 日志系统

### Log

分级日志系统。

**命名空间**：`Machine.Core`

```csharp
public static class Log
{
    // 日志级别
    public enum Level { ERROR, WARN, INFO, DEBUG, TRACE }

    // 当前级别
    public static Level CurrentLevel { get; set; }
    public static void SetLevel(string level);
    public static bool IsLevelEnabled(Level level);

    // 日志方法（简单）
    public static void Error(string msg);
    public static void Warn(string msg);
    public static void Info(string msg);
    public static void Debug(string msg);
    public static void Trace(string msg);

    // 日志方法（带模块名，推荐）
    public static void Error(string module, string msg);
    public static void Warn(string module, string msg);
    public static void Info(string module, string msg);
    public static void Debug(string module, string msg);
    public static void Trace(string module, string msg);

    // 崩溃报告
    public static string GenerateCrashReport(Exception ex, string context = "");

    // 诊断信息
    public static string GetDiagnosticInfo();

    // 刷新缓冲
    public static void Flush();
}
```

**日志级别**：

| 级别 | 说明 | Unity 控制台 | 默认输出 |
|------|------|-------------|---------|
| `ERROR` | 错误 | ✅ Error | ✅ |
| `WARN` | 警告 | ✅ Warning | ✅ |
| `INFO` | 信息 | ✅ Log | ✅ |
| `DEBUG` | 调试 | ❌ | ❌（需配置） |
| `TRACE` | 追踪 | ❌ | ❌（需配置） |

**示例**：

```csharp
// 推荐：带模块名
Log.Info("MyMod", "Initializing...");
Log.Debug("MyMod", "Variable value: " + x);
Log.Error("MyMod", "Failed to load config: " + e.Message);

// 简单
Log.Info("Hello World");
```

**配置日志级别**：编辑 `Machine/config.json`

```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

---

## 性能分析

### ModProfiler

Mod 性能分析器，记录每个 Mod 的初始化时间、每帧耗时、帧预算报警。

**命名空间**：`Machine.Core`

```csharp
public static class ModProfiler
{
    // 配置
    public static double FrameBudgetMs = 2.0;      // 帧预算（ms）
    public static double InitTimeoutMs = 5000.0;    // 初始化超时（ms）
    public static bool Enabled { get; set; }

    // 初始化计时
    public static void BeginInit(string modId);
    public static void EndInit(string modId);

    // Update 计时
    public static void BeginUpdate(string modId);
    public static void EndUpdate(string modId);

    // FixedUpdate 计时
    public static void BeginFixedUpdate(string modId);
    public static void EndFixedUpdate(string modId);

    // 错误记录
    public static void RecordUpdateError(string modId);
    public static void RecordFixedUpdateError(string modId);

    // 查询
    public static ModStats GetStats(string modId);
    public static Dictionary<string, ModStats> GetAllStats();

    // 报告
    public static string GetReport();

    // 计算平均值（每60帧自动调用）
    public static void ComputeAverages();

    // 重置
    public static void Reset();
}
```

**ModStats**：

```csharp
public class ModStats
{
    public string ModId;
    public double InitTimeMs;           // 初始化耗时
    public double LastUpdateMs;         // 最近一帧 Update 耗时
    public double LastFixedUpdateMs;    // 最近一帧 FixedUpdate 耗时
    public double AvgUpdateMs;          // 平均 Update 耗时
    public double PeakUpdateMs;         // 峰值 Update 耗时
    public double AvgFixedUpdateMs;     // 平均 FixedUpdate 耗时
    public double PeakFixedUpdateMs;    // 峰值 FixedUpdate 耗时
    public int FrameBudgetWarnings;     // 帧预算报警次数
    public int UpdateErrorCount;        // Update 异常次数
    public int FixedUpdateErrorCount;   // FixedUpdate 异常次数
    public bool TimedOut;                // 是否启动超时
}
```

**示例**：

```csharp
// 在 Mod 的 OnUpdate 中手动计时（通常 ModManager 自动处理）
public override void OnUpdate()
{
    ModProfiler.BeginUpdate("mymod");
    try
    {
        // 你的逻辑
    }
    finally
    {
        ModProfiler.EndUpdate("mymod");
    }
}
```

**查看性能报告**：游戏内输入 `/machine diag`

---

## 对象池

### ObjectPool\<T\>

泛型对象池，用于频繁创建/销毁的对象。

**命名空间**：`Machine.Core`

```csharp
public class ObjectPool<T> where T : Component
{
    // 构造函数
    public ObjectPool(T prefab, Transform parent = null,
                      Action<T> onGet = null, Action<T> onRelease = null,
                      int initialSize = 0, int maxSize = 100);

    // 获取对象
    public T Get();
    public T Get(Vector3 position, Quaternion rotation);

    // 归还对象
    public void Release(T obj);

    // 清空池
    public void Clear();

    // 统计
    public int AvailableCount { get; }
    public int CreatedCount { get; }
}
```

**示例**：

```csharp
private ObjectPool<ExplosionEffect> _explosionPool;

public override void OnLoad(IMachineApi api)
{
    base.OnLoad(api);
    // 创建对象池（预创建10个，最多50个）
    _explosionPool = new ObjectPool<ExplosionEffect>(
        explosionPrefab, poolContainer,
        onGet: (obj) => obj.Play(),
        onRelease: (obj) => obj.Stop(),
        initialSize: 10, maxSize: 50);
}

public void SpawnExplosion(Vector3 pos)
{
    var explosion = _explosionPool.Get(pos, Quaternion.identity);
    // 爆炸结束后归还
    explosion.OnFinished += () => _explosionPool.Release(explosion);
}
```

---

### GameObjectPool

GameObject 对象池（不需要特定 Component 类型时使用）。

**命名空间**：`Machine.Core`

```csharp
public class GameObjectPool
{
    public GameObjectPool(GameObject prefab, Transform parent = null,
                          Action<GameObject> onGet = null, Action<GameObject> onRelease = null,
                          int initialSize = 0, int maxSize = 100);

    public GameObject Get();
    public GameObject Get(Vector3 position, Quaternion rotation);
    public void Release(GameObject obj);
    public void Clear();
    public int AvailableCount { get; }
    public int CreatedCount { get; }
}
```

---

### PoolManager

全局对象池管理器，按名称注册和获取池。

**命名空间**：`Machine.Core`

```csharp
public static class PoolManager
{
    public static void Init();
    public static GameObjectPool RegisterPool(string name, GameObject prefab,
                                                int initialSize = 0, int maxSize = 100);
    public static GameObjectPool GetPool(string name);
    public static GameObject Get(string poolName);
    public static void Release(string poolName, GameObject obj);
    public static void ClearAll();
    public static string GetStats();
}
```

**示例**：

```csharp
// 注册池
PoolManager.RegisterPool("missiles", missilePrefab, initialSize: 20, maxSize: 100);

// 获取对象
var missile = PoolManager.Get("missiles");
missile.transform.position = launchPos;

// 归还对象
PoolManager.Release("missiles", missile);
```

---

## Mod 管理

### ModManager

Mod 发现、排序、加载与启停管理。

**命名空间**：`Machine.Core`

```csharp
public class ModManager
{
    // 属性
    public int LoadedCount { get; }
    public List<LoadedMod> Mods { get; }

    // 加载
    public void LoadAll(string modsDir, string machineDir, IMachineApi api);

    // 更新
    public void UpdateAll();
    public void FixedUpdateAll();

    // 启停
    public void EnableMod(string id);
    public void DisableMod(string id);

    // 卸载
    public void UnloadAll();

    // 查询
    public List<string> GetDisabledReasons();
    public bool IsModLoaded(string id);
    public string GetModVersion(string id);
}
```

> **注意**：ModManager 由 Bootstrap 内部管理，Mod 通常不需要直接访问。

---

## 版本工具

### SemVer / VersionRange

语义化版本解析和比较工具。

**命名空间**：`Machine.Core`

```csharp
public static class SemVer
{
    public static Version Parse(string version);
    public static int Compare(string a, string b);
    public static bool Satisfies(string version, string range);
}

public static class VersionRange
{
    public static bool Satisfies(string version, string range);
}
```

**版本范围语法**：

| 格式 | 说明 | 示例 |
|------|------|------|
| `>=1.0.0` | 大于等于 | `>=2.0.0` |
| `>1.0.0` | 大于 | `>1.5.0` |
| `<=1.0.0` | 小于等于 | `<=3.0.0` |
| `<1.0.0` | 小于 | `<2.0.0` |
| `=1.0.0` | 等于 | `=1.2.0` |
| `!=1.0.0` | 不等于 | `!=1.0.0` |
| `>=1.0.0 <2.0.0` | 组合条件 | `>=1.0.0 <2.0.0` |

**示例**：

```csharp
// 比较版本
int result = SemVer.Compare("1.2.0", "1.10.0");  // -1（1.2.0 < 1.10.0）

// 检查版本范围
bool ok = VersionRange.Satisfies("2.1.0", ">=2.0.0 <3.0.0");  // true
```

---

## 诊断命令

游戏内聊天框输入以下命令：

| 命令 | 说明 |
|------|------|
| `/machine diag` | 输出完整诊断信息（版本、Mod列表、性能报告、对象池统计） |

---

## 更多资源

- [Mod 开发指南](../zh/mod-development.md)
- [示例 Mod](../../examples/)
- [GitHub 仓库](https://github.com/AODOJUST/MachineLoader)
