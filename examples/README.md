# Machine Mod 示例

> 学习 Machine Mod 开发的最佳起点。

[返回首页](../README.md) | [开发指南](../docs/zh/mod-development.md) | [API 参考](../docs/api/README.md)

## 示例列表

| 示例 | 说明 | 难度 |
|------|------|------|
| [HelloWorld](HelloWorld/) | 最小示例：基本结构和日志输出 | ⭐ |
| [AddButton](AddButton/) | 在主菜单添加自定义按钮和窗口 | ⭐⭐ |
| [AddCargo](AddCargo/) | 注册自定义货物（普通/易碎/过期） | ⭐⭐ |
| [ListenEvents](ListenEvents/) | 订阅事件总线的各种事件 | ⭐⭐ |
| [ReadConfig](ReadConfig/) | 读取和保存自定义配置文件 | ⭐⭐ |
| [综合模板](../templates/ExampleMod/) | 完整示例：事件+UI+配置+命令 | ⭐⭐⭐ |

## 快速开始

### 1. 选择一个示例

从最简单的 `HelloWorld` 开始，逐步学习更复杂的示例。

### 2. 编译示例

每个示例目录下都有 `.cs` 源文件，使用 `csc` 编译：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$game = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed"

# 编译 HelloWorld
& $csc /target:library /out:HelloWorld/code/HelloWorld.dll `
    /reference:"$game\Assembly-CSharp.dll" `
    /reference:"$game\Machine.Core.dll" `
    /reference:"$game\UnityEngine.dll" `
    /reference:"$game\UnityEngine.CoreModule.dll" `
    HelloWorld/HelloWorld.cs
```

### 3. 安装示例

将示例文件夹复制到游戏的 Mod 目录：

```
Aviassembly/Machine/mods/
├── example.helloworld/     ← 复制到这里
│   ├── mod.json
│   └── code/
│       └── HelloWorld.dll
```

### 4. 测试

1. 启动游戏
2. 主菜单点击 `Mods` 按钮
3. 确认示例 Mod 已启用
4. 查看日志 `Machine/logs/Machine.log`

## 学习路径

### 初学者

1. **HelloWorld** - 了解 Mod 的基本结构
2. **ListenEvents** - 学习事件订阅机制
3. **ReadConfig** - 掌握配置文件读写

### 进阶

4. **AddButton** - 学习 UI 创建
5. **AddCargo** - 学习内容注册
6. **综合模板** - 学习完整的 Mod 开发

### 高级

7. 参考官方 Mod 源码（`src/Mods/` 目录）
8. 阅读 [API 参考](../docs/api/README.md)
9. 开发自己的 Mod！

## 常见问题

### Q: 编译报错 "找不到类型或命名空间"
A: 确保引用了正确的 DLL：
- `Assembly-CSharp.dll`（游戏代码）
- `Machine.Core.dll`（Machine 核心）
- `UnityEngine.dll`、`UnityEngine.CoreModule.dll`（Unity 引擎）

### Q: Mod 加载了但没有效果
A: 
1. 检查日志是否有异常
2. 确认 `mod.json` 中的 `mainClass` 与实际类名一致（包括命名空间）
3. 确认 DLL 路径正确（相对 Mod 目录）

### Q: 如何调试 Mod
A: 
1. 使用 `Log()` 输出调试信息
2. 设置 `logLevel: "DEBUG"` 查看更多日志
3. 游戏内输入 `/machine diag` 查看诊断信息
4. 查看崩溃报告 `Machine/logs/crash_report_*.txt`

## 贡献示例

欢迎提交新的示例 Mod！请确保：
- 代码有充分注释
- 包含 README 说明
- 遵循 [开发指南](../docs/zh/mod-development.md) 的最佳实践
