# Machine Mod 加载器

> Aviassembly 游戏的 Mod 加载器，类似 Minecraft 的 Forge / Fabric。

[![Version](https://img.shields.io/badge/version-2.4.1-blue.svg)]()
[![Game](https://img.shields.io/badge/game-Aviassembly-green.svg)]()
[![License](https://img.shields.io/badge/license-MIT-yellow.svg)]()

[English](docs/en/README.md) | [中文](docs/zh/README.md) | [日本語](docs/ja/README.md) | [한국어](docs/ko/README.md) | [Русский](docs/ru/README.md) | [Deutsch](docs/de/README.md) | [Français](docs/fr/README.md) | [Nederlands](docs/nl/README.md)

## 简介

Machine 是为 Aviassembly 开发的 Mod 加载器，为玩家和开发者提供：

- **Mod 管理**：主菜单 Mod 按钮，启用/禁用 Mod，依赖与冲突检测
- **内容扩展**：添加新货物、部件、贴图、音效、飞机模型
- **玩法扩展**：新游戏机制、物理效果、联机模式、AI 阵营
- **开发者 API**：事件总线、生命周期、UI 注入、配置系统、对象池
- **性能保障**：异常隔离、帧预算、内存泄漏检测、性能分析器
- **联机支持**：局域网/网络房间、玩家同步、Mod 版本校验

## 快速开始

### 玩家：安装 Machine

1. 从 [Releases 页面](https://github.com/AODOJUST/MachineLoader/releases) 下载
   `MachineLoader-2.4.1.zip`
2. **把整个压缩包解压成一个文件夹**（不要直接在压缩包预览窗口里运行）
3. 双击 `install_machine.bat`，按控制台提示确认游戏目录
4. 安装完成后启动游戏，主菜单左下角显示 `Machine Loader v2.4.1` 即成功

> 完整说明（含故障排查、退出码表）见 [INSTALL.md](INSTALL.md)。
> 安装器是自带的 `MachineInstaller.exe`，**不需要系统里装过 Python**。

### 玩家：卸载 Machine

1. 在解压出来的文件夹里双击 `uninstall_machine.bat`
2. 控制台会先列出「将移除什么 / 将保留什么」，确认之后才会动手
3. `mods/` 目录默认**保留**；想连它一起删就加 `--purge-mods`

### 玩家：安装第三方 Mod

1. 把 Mod 文件夹放进**游戏根目录**的 `mods/` 下，即 `<游戏目录>\mods\<ModName>\`
2. 启动游戏，主菜单点击 `Mods` 按钮
3. 在 Mod 管理器中启用/禁用 Mod
4. 缺失前置 Mod 时会有明确提示

### 开发者：创建第一个 Mod

```powershell
# 使用模板生成器（Windows PowerShell），会在 mods/ 下建好骨架
.\newmod.ps1 MyFirstMod
```

详细开发指南请阅读 [Mod 开发指南](docs/zh/mod-development.md)。

## 目录结构

```
Aviassembly/                       # 游戏根目录
├── Aviassembly.exe                # 游戏主程序
├── Aviassembly_Data/
│   └── Managed/
│       ├── Assembly-CSharp.dll              # 游戏代码（已注入 Machine 启动钩子）
│       ├── Assembly-CSharp.dll.machinebak   # 原版备份（卸载时用它还原）
│       ├── Machine.Core.dll                 # Machine 加载器本体
│       └── Mono.Cecil.dll                   # 注入依赖库
├── mods/                          # Mod 目录（在游戏根目录，不在 Machine/ 里面）
│   └── <ModName>/
│       ├── mod.json               # Mod 清单（id / name / version / description）
│       ├── xxx_config.json        # Mod 自己的配置（可选）
│       └── code/
│           └── <ModName>.dll      # 编译后的 DLL
└── Machine/                       # Machine 数据目录（安装器也在这里）
    ├── MachineInstaller.exe       # 安装 / 卸载 / 更新程序
    ├── machine_update.bat         # 应用游戏内下载好的更新
    ├── uninstall_machine.bat      # 一键卸载
    ├── machine_common.py          # 版本号、公钥、清单校验（命令行工具共用）
    ├── machine_update.py          # 命令行更新器
    ├── README.txt                 # 安装时生成，只写相对路径
    ├── net.json                   # 联机 / 局域网配置
    ├── update.json                # 更新仓库与通道
    ├── profile.json               # 玩家资料
    ├── installed.json             # 安装记录（各文件哈希）
    ├── backup/                    # 旧版 Machine.Core.dll 备份（保留最近 5 份）
    ├── lang/                      # 界面语言文件（en/zh/ru/ja/ko/de/fr/nl.json）
    ├── logs/
    │   ├── Machine.log            # 加载器日志
    │   └── installer.log          # 安装器 / 更新器日志
    └── update/                    # 游戏内下载的更新缓存
```

## 内置 Mod

Machine 自带以下官方 Mod（都打包在发布包的 `mods/` 里）：

| Mod ID | 名称 | 版本 | 说明 |
| --- | --- | --- | --- |
| `machine.battlecore` | BattleCore | 2.4.0 | 信息集散前置，汇总输出其他 Mod 的信息 |
| `machine.battlehold` | BattleHold | 2.3.0 | 战斗物资管理，质量重算 |
| `machine.faction` | Faction System | 1.9.0 | 三大阵营 AI，KD 计分板 |
| `machine.flighttrails` | Flight Trails | 2.5.0 | 地图航迹显示，速度配色 |
| `machine.gmeter` | G Meter | 1.7.0 | 过载计算，过载语音警告 |
| `machine.gvision` | G Vision | 1.3.0 | 战斗辅助 HUD |
| `machine.killfeed` | KillFeed | 1.1.0 | 战况事件记录与播报 |
| `machine.aam` | Air-to-Air Missile | 2.8.0 | 导弹 + 机炮系统 |
| `machine.shop` | MachineShop | 1.0.0 | Machine 独立装货界面 |
| `machine.opti` | OptiMod | 2.1.0 | CPU 性能优化 |
| `machine.radar` | Radar System | 3.3.1 | 多级雷达 + 火控锁定 |
| `machine.voicealerts` | Voice Alert System | 2.13.0 | 中/英双语语音警报 |
| `machine.zoom` | Zoom Mod | 1.1.0 | 画面放大功能 |

## 核心特性

### 加载器核心

- **事件总线**：多种事件类型，订阅 / 发布 / 异常隔离
- **Mod 生命周期**：OnLoad → OnEnable → OnUpdate/OnFixedUpdate → OnDisable → OnUnload
- **依赖管理**：必需依赖、可选依赖、版本范围、循环依赖检测
- **冲突检测**：冲突 Mod 自动禁用，不崩溃
- **加载顺序**：拓扑排序，支持 loadBefore / loadAfter

### 性能与稳定性

- **异常隔离**：单个 Mod 异常不影响其他 Mod
- **自动禁用**：连续异常超过 10 次自动禁用该 Mod
- **帧预算**：每个 Mod 每帧最多 2ms，超过报警
- **性能分析器**：记录每个 Mod 的初始化时间、每帧耗时、峰值
- **内存泄漏检测**：定期检查事件订阅、对象池、托管内存
- **日志轮转**：5MB 切分，保留 5 个，崩溃报告自动生成

### 更新与安全

- **双重校验**：下载内容同时校验 SHA-256 与 RSA-3072 签名，两者都通过才写入
- **原子替换**：先写同目录的临时文件再替换，不会出现「覆盖到一半」的半截 DLL
- **写入后复核**：不一致就自动回滚到备份
- **地址白名单**：只接受 `https` 的 GitHub 域名下载地址

> 信任模型，以及明确**不**防护的场景，见 [SECURITY.md](SECURITY.md)。

### 联机功能

- **局域网房间**：IP : 端口 直连
- **网络房间**：房间号加入，大厅浏览
- **Mod 校验**：自动检测游戏版本和 Mod 列表一致性
- **玩家同步**：飞机位置、模型、状态实时同步

## 常见问题

### Q: 双击安装器，窗口一闪就没了？
A: 这是旧版本的 bug，最新版已经修复（不再依赖系统里的 `python` 命令）。请确认你用的
是最新的压缩包 —— 控制台第一行会打印版本号。详见 [INSTALL.md](INSTALL.md)。

### Q: 安装后游戏无法启动？
A: 运行 `uninstall_machine.bat` 卸载后重新安装，并确认游戏目录路径正确。

### Q: Mod 不生效？
A: 检查 `Machine/logs/Machine.log`，常见原因：
- `mods/` 放错了位置（必须在**游戏根目录**）
- 缺失前置 Mod（日志会明确提示）
- Mod 版本与加载器版本不兼容
- `mod.json` 格式错误

### Q: 如何启用 DEBUG 日志？
A: 编辑 `Machine/` 下的配置，把 `logLevel` 改为 `"DEBUG"`：

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
- `Load` —— 加载纯净版存档（不加载 Mod）
- `Mod Saves` —— 加载/创建 Mod 版存档（加载 Mod）

### Q: 如何报告 Bug？
A: 
1. 复现问题
2. 找到 `Machine/logs/` 目录下的最新日志和崩溃报告
3. 在 GitHub Issues 中提交，附上日志与复现步骤

## 开发者资源

- [Mod 开发指南（中文）](docs/zh/mod-development.md)
- [Mod Development Guide (English)](docs/en/mod-development.md)
- [调试指南](docs/zh/debugging.md)
- [API 参考](docs/api/)
- [模板生成器 newmod.ps1](newmod.ps1)
- [更新日志](CHANGELOG.md)

## 构建

```powershell
# 编译核心（src/Machine.Core -> bin/Machine.Core.dll）
powershell -ExecutionPolicy Bypass -File build-core.ps1

# 编译所有 Mod（src/Mods -> bin/mods/*）
powershell -ExecutionPolicy Bypass -File build-mods.ps1

# 编译安装器（src/Installer -> bin/MachineInstaller.exe）
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

发布打包（顺序固定，签名必须排在编译之后）：

```bash
python tools/pack_release.py      # 组装 dist/ + 发布包目录 + zip，内部会调用签名
python tools/sync_release.py      # 同步到发布仓库副本
python tools/selftest_release.py  # 发布链路自测（请在前台运行）
```

## 贡献

欢迎贡献代码、文档、翻译或示例 Mod。请阅读 [贡献指南](CONTRIBUTING.md)。

## 许可证

MIT License
