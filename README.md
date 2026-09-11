# Machine Loader — Aviassembly Mod Loader

Machine 是 Steam 游戏 Aviassembly 的 Mod 加载器（类似 Minecraft 的 Forge / Fabric）。
安装后主菜单会出现 **Mods** 按钮管理 Mod，游戏目录下自动创建 `mods/` 文件夹。

## 功能特性
- 主菜单 Mod 管理窗口（游戏风格 UI）
- Mod 化架构：`mods/<Mod>/mod.json` + `code/<Mod>.dll`
- **多人联机**（Online）：网络房间 / 局域网房间，官方服务器由 Radmin LAN 支持，房间号 7 位数字字母
- 游客模式：输入昵称（锁定）后方可联机，昵称仅限中英文/数字
- 记忆系统：`Machine/profile.json` 持久化玩家信息与设置（键位、布局）
- **联网检查更新**：启动时检查本仓库 `version.json`，主菜单弹窗提示，下载后运行更新器即可（无需重装）
- 一键安装 / 卸载 / 更新程序（傻瓜式，自动定位游戏目录）

## 安装
1. 下载本仓库 `dist/` 发布包
2. 双击 **install_machine.bat**，自动扫盘定位 Aviassembly 游戏目录并安装
3. 若游戏无法启动或异常：运行 **uninstall_machine.bat** 一键恢复纯净版，确认正常后再重装

## 更新
- 游戏启动时自动检查 GitHub 仓库版本（`Machine/update.json` 配置仓库地址）
- 有新版本 → 主菜单弹窗 → 点下载（存入 `Machine/update/`）
- 退出游戏运行 `Machine/machine_update.bat` 应用更新，**无需重新安装**

## 内置 Mod 列表
| Mod | 功能 |
| --- | --- |
| BattleCore | 战斗部核心（信息集散） |
| BattleHold | 战斗部面板（油量/油门/导弹信息） |
| FlightTrails | 飞行航迹（虚线、速度颜色） |
| VoiceAlerts | 语音告警（多语言） |
| Radar | 雷达（探测/锁定/火控） |
| MachineAAM | 空空导弹系统 |
| GMeter | G 值计算与告警 |
| GVision | 空战辅助瞄准 HUD |
| KillFeed | 事件播报（击落/坠毁） |
| FactionSystem | 阵营系统（三阵营/计分板） |
| OptiMod | CPU 性能优化 |
| ZoomMod | 放大镜 |

## 版本
- Loader: **v2.2.0**
- 支持游戏: Aviassembly (Steam)

## 许可
仅供个人学习使用，禁止商用。
