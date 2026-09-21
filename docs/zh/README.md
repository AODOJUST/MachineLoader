# Machine Mod Loader - 中文文档

> Aviassembly 游戏的 Mod 加载器，类似 Minecraft 的 Forge/Fabric。

[English](../en/README.md) | [项目首页](../../README.md) | [开发指南](mod-development.md) | [API 参考](../api/README.md)

## 简介

Machine 是为 Aviassembly 游戏开发的 Mod 加载器，为玩家和开发者提供：

- **Mod 管理**：主菜单 Mod 按钮，启用/禁用 Mod，依赖与冲突检测
- **内容扩展**：添加新货物、部件、贴图、音效、飞机模型
- **玩法扩展**：新游戏机制、物理效果、联机模式、AI 阵营
- **开发者 API**：事件总线、生命周期、UI 注入、配置系统、对象池
- **性能保障**：异常隔离、帧预算、内存泄漏检测、性能分析器
- **联机支持**：局域网/网络房间、玩家同步、Mod 版本校验

## 快速开始

### 玩家：安装 Machine

1. 下载最新发布包 `MachineLoader-vX.X.X.zip`
2. 运行 `MachineInstaller.exe`
3. 安装程序会自动扫描 Aviassembly 游戏目录
4. 确认安装路径后点击安装
5. 启动游戏，主菜单左下角显示 `Machine vX.X.X` 即安装成功

> 安装前建议备份游戏存档。如遇问题，运行游戏目录下的 `UninstallMachine.exe` 可完全移除。

### 玩家：安装 Mod

1. 将 Mod 文件夹放入 `Aviassembly/Machine/mods/` 目录
2. 启动游戏，主菜单点击 `Mods` 按钮
3. 在 Mod 管理器中启用/禁用 Mod
4. 缺失前置 Mod 时会有明确提示

### 开发者：创建第一个 Mod

```bash
# 使用模板生成器（Windows PowerShell）
.\newmod.ps1 MyFirstMod

# 或手动参考 examples/ 目录下的示例
```

详细开发指南请阅读 [Mod 开发指南](mod-development.md)。

## 目录结构

```
Aviassembly/                    # 游戏根目录
├── Aviassembly.exe             # 游戏主程序
├── Aviassembly_Data/
│   └── Managed/
│       ├── Assembly-CSharp.dll      # 游戏代码（已注入 Machine）
│       ├── Assembly-CSharp.dll.machinebak  # 原版备份
│       ├── Machine.Core.dll         # Machine 核心
│       └── Mono.Cecil.dll          # 依赖库
├── Machine/                   # Machine 数据目录
│   ├── mods/                  # Mod 目录（每个 Mod 一个子文件夹）
│   ├── config.json            # Machine 全局配置
│   ├── update.json            # 更新配置
│   ├── profile.json           # 玩家资料
│   ├── saves/                 # Mod 版存档
│   ├── logs/                  # 日志目录
│   └── update/                # 更新缓存
├── MachineInstaller.exe       # 安装程序
├── UninstallMachine.exe       # 卸载程序
└── steam_appid.txt            # Steam App ID
```

## 内置 Mod

| Mod ID | 名称 | 说明 |
|--------|------|------|
| `machine.battlecore` | 战斗部 | 信息集散前置，汇总输出其他 Mod 信息 |
| `machine.battlehold` | 战斗仓库 | 战斗物资管理，质量重算 |
| `machine.faction` | 阵营系统 | 三大阵营 AI，KD 计分板 |
| `machine.flighttrails` | 航迹 | 地图航迹显示，速度配色 |
| `machine.gmeter` | G 计算 | 过载计算，过载语音警告 |
| `machine.gvision` | 辅助显示器 | 战斗辅助 HUD |
| `machine.killfeed` | 事件播报 | 战况事件记录与播报 |
| `machine.aam` | 空空导弹 | 导弹+机炮系统 |
| `machine.shop` | 独立装货 | Machine 独立装货界面 |
| `machine.opti` | 性能优化 | CPU 优化 Mod |
| `machine.radar` | 雷达 | 多级雷达+火控锁定 |
| `machine.voice` | 语音警报 | 中系/英文双语语音警报 |
| `machine.zoom` | 放大镜 | 画面放大功能 |

## 核心特性

### 加载器核心

- **事件总线**：12 种事件类型，订阅/发布/隔离机制
- **Mod 生命周期**：OnLoad → OnEnable → OnUpdate/OnFixedUpdate → OnDisable → OnUnload
- **依赖管理**：必需依赖、可选依赖、版本范围、循环依赖检测
- **冲突检测**：冲突 Mod 自动禁用，不崩溃
- **加载顺序**：拓扑排序，支持 loadBefore/loadAfter

### 性能与稳定性

- **异常隔离**：单个 Mod 异常不影响其他 Mod
- **自动禁用**：连续异常超过 10 次自动禁用该 Mod
- **帧预算**：每个 Mod 每帧最多 2ms，超过报警
- **性能分析器**：记录每个 Mod 的初始化时间、每帧耗时、峰值
- **内存泄漏检测**：定期检查事件订阅、对象池、托管内存
- **日志轮转**：5MB 切分，保留 5 个，崩溃报告自动生成

### 联机功能

- **局域网房间**：IP:端口 直连
- **网络房间**：房间号加入，大厅浏览
- **Mod 校验**：自动检测游戏版本和 Mod 列表一致性
- **玩家同步**：飞机位置、模型、状态实时同步
- **玩家系统**：UID/RID/OID 权限分级，加密用户名单

## 常见问题

### Q: 安装后游戏无法启动？
A: 运行 `UninstallMachine.exe` 卸载后重新安装。确保游戏目录路径正确，没有中文或特殊字符。

### Q: Mod 不生效？
A: 检查 `Machine/logs/Machine.log`，查看 Mod 加载是否有错误。常见原因：
- 缺失前置 Mod（日志会明确提示）
- Mod 版本与加载器版本不兼容
- mod.json 格式错误

### Q: 如何启用 DEBUG 日志？
A: 编辑 `Machine/config.json`，将 `logLevel` 改为 `"DEBUG"`：
```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

### Q: 游戏卡顿怎么办？
A:
1. 在游戏内输入 `/machine diag` 查看性能报告
2. 检查哪个 Mod 每帧耗时过高
3. 禁用不必要的 Mod
4. 确保 `machine.opti` 性能优化 Mod 已启用

### Q: 纯净版和 Mod 版如何切换？
A: 主菜单点击 `Play` 后：
- `Load` - 加载纯净版存档（不加载 Mod）
- `Mod Saves` - 加载/创建 Mod 版存档（加载 Mod）

## 开发者资源

- [Mod 开发指南](mod-development.md)
- [调试工具指南](debugging.md)
- [API 参考](../api/README.md)
- [示例 Mod](../../examples/)
- [模板生成器](../../newmod.ps1)
- [更新日志](../../CHANGELOG.md)

## 构建

```bash
# 编译核心
powershell -ExecutionPolicy Bypass -File build-core.ps1

# 编译所有 Mod
powershell -ExecutionPolicy Bypass -File build-mods.ps1

# 构建发布包
python build_release.py --version 2.3.0

# 运行测试
pytest tests/ -v
```

## 贡献

欢迎贡献代码、文档、翻译或示例 Mod。

## 许可证

MIT License
