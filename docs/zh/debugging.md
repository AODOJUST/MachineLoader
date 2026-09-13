# 调试工具指南

> Machine 提供的调试和诊断工具。

[返回首页](../../README.md) | [开发指南](mod-development.md)

## 目录

1. [日志系统](#1-日志系统)
2. [诊断命令](#2-诊断命令)
3. [性能分析器](#3-性能分析器)
4. [崩溃报告](#4-崩溃报告)
5. [Mod 管理器](#5-mod-管理器)
6. [调试技巧](#6-调试技巧)

---

## 1. 日志系统

### 日志级别

Machine 提供 5 个日志级别：

| 级别 | 说明 | Unity 控制台 | 默认输出 |
|------|------|-------------|---------|
| `ERROR` | 错误 | ✅ Error | ✅ |
| `WARN` | 警告 | ✅ Warning | ✅ |
| `INFO` | 信息 | ✅ Log | ✅ |
| `DEBUG` | 调试 | ❌ | ❌ |
| `TRACE` | 追踪 | ❌ | ❌ |

### 配置日志级别

编辑 `Machine/config.json`：

```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

可选值：`ERROR`、`WARN`、`INFO`、`DEBUG`、`TRACE`

### 日志输出位置

- **文件**：`Machine/logs/Machine.log`
- **Unity 控制台**：Editor 中可查看
- **轮转**：单文件 5MB 自动切分，保留最近 5 个

### 在 Mod 中使用日志

```csharp
using Machine.Core;

// 推荐：带模块名
Log.Info("MyMod", "Initializing...");
Log.Debug("MyMod", "Variable value: " + x);
Log.Warn("MyMod", "Something unusual happened");
Log.Error("MyMod", "Failed to load: " + e.Message);

// 简单方式（在 MachineModBase 中）
Log("Info message");
LogWarn("Warning message");
LogError("Error message");
```

### 日志格式

```
2026-09-13 14:30:25.123 [INFO ] [MyMod] Initializing...
2026-09-13 14:30:25.456 [ERROR] [MyMod] Failed to load config: ...
```

格式：`时间(毫秒) [级别(5字符对齐)] [模块] 消息`

---

## 2. 诊断命令

### /machine diag

游戏内聊天框输入 `/machine diag`，输出完整诊断信息：

```
[DIAG] === Machine Loader Diagnostic ===
[DIAG] Loader Version: 2.3.0
[DIAG] API Version: 1.0
[DIAG] Log Level: INFO
[DIAG] Update Channel: stable
[DIAG]
[DIAG] === Loaded Mods ===
[DIAG]   [OK] machine.battlecore v1.2.0
[DIAG]   [OK] machine.radar v2.1.0
[DIAG]   [DISABLED] machine.example v1.0.0
[DIAG]
[DIAG] === Log Statistics ===
[DIAG]   ERROR: 0
[DIAG]   WARN: 3
[DIAG]   INFO: 156
[DIAG]
[PERF] === Mod Performance Report ===
[PERF]   machine.battlecore     init:  12.3ms  avg:  0.12ms  peak:  0.45ms
[PERF]   machine.radar          init:  45.6ms  avg:  1.23ms  peak:  3.45ms
[PERF]   TOTAL: avg=1.35ms/frame  peak=3.90ms
[POOL] === Object Pool Stats ===
[POOL]   missiles                available:  20  created:  50
```

### 诊断信息包含

1. **加载器信息**：版本、API 版本、日志级别、更新通道
2. **Mod 列表**：每个 Mod 的状态（OK/DISABLED/ERRORS）
3. **日志统计**：各级别日志计数
4. **最近错误**：最近 10 条错误日志
5. **性能报告**：每个 Mod 的初始化时间、平均/峰值帧耗时
6. **对象池统计**：每个池的可用/已创建数量

---

## 3. 性能分析器

### 概述

ModProfiler 自动记录每个 Mod 的性能数据：

- 初始化耗时
- 每帧 Update/FixedUpdate 耗时
- 平均/峰值耗时
- 帧预算报警次数
- 异常次数

### 帧预算

默认每个 Mod 每帧最多消耗 **2ms**，超过会记录警告（节流输出，避免刷屏）。

可在代码中调整：

```csharp
ModProfiler.FrameBudgetMs = 3.0;  // 改为 3ms
```

### 查看性能数据

1. 游戏内输入 `/machine diag`
2. 查看 `[PERF]` 部分的输出

### 性能字段说明

| 字段 | 说明 |
|------|------|
| `init` | 初始化耗时（OnLoad+OnEnable） |
| `avg` | 平均每帧 Update 耗时 |
| `peak` | 峰值 Update 耗时 |
| `[WARN:N]` | 帧预算报警次数 |
| `[TIMEOUT]` | 初始化超时（>5秒） |
| `[ERR:N]` | 异常次数 |

### 在 Mod 中手动计时

```csharp
public override void OnUpdate()
{
    ModProfiler.BeginUpdate("mymod");
    try
    {
        // 你的逻辑
        HeavyOperation();
    }
    finally
    {
        ModProfiler.EndUpdate("mymod");
    }
}
```

> 注意：通常 ModManager 会自动为每个 Mod 的 OnUpdate 计时，不需要手动调用。

---

## 4. 崩溃报告

### 自动生成

游戏崩溃时，Machine 自动生成崩溃报告：

```
Machine/logs/crash_report_20260913_143025.txt
```

### 报告内容

1. **基本信息**：时间、加载器版本、日志级别、上下文
2. **异常信息**：类型、消息、堆栈跟踪、内部异常
3. **Mod 列表**：所有已加载 Mod 的状态
4. **日志统计**：各级别日志计数
5. **最近错误**：最近 50 条错误
6. **最后 200 行日志**：崩溃前的日志

### 手动生成

```csharp
using Machine.Core;

try
{
    // 可能崩溃的代码
}
catch (Exception ex)
{
    string reportPath = Log.GenerateCrashReport(ex, "MyMod operation");
    Log.Error("Crash report saved to: " + reportPath);
}
```

### 提交 Bug 时

请附上：
1. 崩溃报告文件（`crash_report_*.txt`）
2. 最新日志文件（`Machine.log`）
3. 复现步骤
4. Mod 列表和版本

---

## 5. Mod 管理器

### 打开方式

主菜单点击 `Mods` 按钮。

### 功能

- 查看所有已安装 Mod
- 启用/禁用 Mod
- 查看 Mod 详情（版本、作者、描述、依赖）
- 查看禁用原因（缺失依赖、版本不兼容、冲突等）

### 依赖提示

当 Mod 缺失前置时，会显示明确的错误信息：

```
========== MOD DISABLED: machine.radar ==========
  Reason: Missing required dependency
  Missing: machine.battlecore (>=1.0.0)
  Fix: Download and install machine.battlecore
====================================================
```

---

## 6. 调试技巧

### 6.1 检查 Mod 是否加载

1. 查看日志 `Machine/logs/Machine.log`
2. 搜索 Mod ID，如 `machine.mymod`
3. 应该看到：
   ```
   mod [machine.mymod] manifest ok: My Mod v1.0.0
   mod [machine.mymod] My Mod v1.0.0 enabled=True
   code mod loaded: Machine.MyMod.MyModClass
   ```

### 6.2 常见加载失败原因

| 现象 | 原因 | 解决 |
|------|------|------|
| 日志中没有 Mod | mod.json 格式错误 | 检查 JSON 语法 |
| "assembly missing" | DLL 路径错误 | 检查 mod.json 中 code.assemblies 路径 |
| "no IMachineMod found" | 主类名错误 | 检查 code.mainClass 是否与实际类名一致 |
| "OnLoad failed" | 代码异常 | 查看异常堆栈，修复代码 |
| Mod 被禁用 | 缺失依赖/版本不兼容/冲突 | 查看禁用原因，安装依赖或调整版本 |

### 6.3 调试 OnUpdate 性能问题

1. 游戏内输入 `/machine diag`
2. 查看 `[PERF]` 部分
3. 找到 avg 或 peak 过高的 Mod
4. 检查该 Mod 的 OnUpdate 中是否有：
   - 每帧创建对象（使用对象池）
   - 每帧反射调用（缓存结果）
   - 每帧文件读写（改为事件驱动或节流）
   - 复杂循环（优化算法或降低频率）

### 6.4 调试内存泄漏

1. 设置 `logLevel: "DEBUG"`
2. 查看日志中的内存检测输出：
   ```
   [Memory] EventBus subscriber count is high: 600
   [Memory] Managed memory is high: 620MB
   ```
3. 检查 Mod 的 OnDisable 是否：
   - 取消了所有事件订阅
   - 销毁了所有创建的 UI/GameObject
   - 清空了静态引用

### 6.5 调试 UI 问题

1. 检查 Canvas 的 `sortingOrder` 是否足够大
2. 检查 RectTransform 的锚点和位置
3. 使用 `Log.Debug` 输出 UI 元素的位置和状态
4. 确保在 OnDisable 中销毁 UI，避免残留

### 6.6 调试联机问题

1. 查看房主和成员的日志
2. 检查 NetSync 相关日志
3. 确认双方 Mod 列表一致
4. 检查防火墙和端口设置

### 6.7 热重载测试

目前 Machine 不支持运行时热重载。测试 Mod 时需要：
1. 编译 DLL
2. 复制到 Mod 目录
3. 重启游戏

建议使用批处理脚本自动化这个流程。

---

## 更多资源

- [Mod 开发指南](mod-development.md)
- [API 参考](../api/README.md)
- [示例 Mod](../../examples/)
- [常见问题](../../README.md#常见问题)
