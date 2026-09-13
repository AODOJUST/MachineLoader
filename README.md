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

> 安装前请确认**游戏已完全退出**、并**关闭杀毒软件对游戏目录的实时拦截**（否则注入会被拦截）。
> 若游戏装在 `C:\Program Files` 下，请右键 bat 选择「以管理员身份运行」。

## 更新
- 游戏启动时自动检查仓库版本（`Machine/update.json` 配置仓库 / 分支 / 通道）
- 有新版本 → 主菜单弹窗 → 点下载（存入 `Machine/update/`）
- 退出游戏后运行 `Machine/machine_update.bat` 应用更新，**无需重新安装**

### 更新是经过校验的
从 v2.4.0 起，更新链路带完整性与来源校验：

1. 下载完成后比对 **SHA-256**
2. 用**编译进客户端的 RSA-3072 公钥**验证 **PKCS#1 v1.5 / SHA-256 签名**
3. 只有两项都通过，DLL 才会被写入 `Managed/`；否则直接拒绝并留下日志
4. 写入前自动**备份**旧 DLL，采用**临时文件 + 原子替换**，写入后**复核哈希**，失败自动**回滚**

也就是说：即使仓库或 `update.json` 被篡改，客户端也不会安装非官方签名的 DLL。
细节与剩余风险见 [SECURITY.md](SECURITY.md)。

## 日志与排错
| 文件 | 内容 |
| --- | --- |
| `Machine/logs/Machine.log` | 游戏内加载器日志 |
| `Machine/logs/machine_update.log` | 安装 / 卸载 / 更新脚本日志 |
| `Machine/logs/installer.log` | 注入与更新执行器的日志 |
| `Machine/backup/` | 更新前的 `Machine.Core.dll` 备份（保留最近 5 份） |

脚本退出码语义：`0` 成功 · `1` 参数/环境 · `2` 哈希不符 · `3` 签名被拒 ·
`4` 安装器失败 · `5` 已回滚 · `6` 用户取消 · `7` 游戏在运行 · `8` 权限不足。

## 前置要求
- Windows + Python 3.6 或更高版本（安装时勾选 *Add python.exe to PATH*）
- 启动脚本会自动寻找 Python：优先 `py -3`，其次 `python` / `python3`

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
| MachineShop | Mod Cargo 装货窗 |

## 配置说明
`Machine/update.json`

```json
{
  "repo": "AODOJUST/MachineLoader",
  "branch": "main",
  "channel": "stable",
  "enabled": true,
  "requireSignature": true
}
```

- `repo` 留空 = 关闭更新检查；`channel` 只接受 `stable` / `beta`
- `requireSignature` 建议保持 `true`。改成 `false` 会允许未签名更新，
  等于放弃"防篡改"，只在本地自测时使用
- 下载地址只允许 `https` + GitHub 相关域名（`raw.githubusercontent.com` 等）

`Machine/net.json`

```json
{ "server": "", "port": 26460, "playerName": "Pilot" }
```

字段全部经过校验：`server` 必须是合法 IPv4 / 主机名（空 = 未配置）、
`port` 必须在 1..65535、`playerName` 限 1-16 位中英文/数字/下划线。

## 版本
- Loader: **v2.4.1**
- 支持游戏: Aviassembly (Steam)

### v2.4.1 变更（导弹地形避障）
- 新增：空对空导弹（MachineAAM）内置射线地形避障 —— 3×3 探针扇形（命中面法线投影 → 斜坡抬升 / 崖壁侧绕）+ 前方山脊剖面 + 离地高度兜底 + 撞地兜底引爆，并与比例导引按威胁度融合；导弹不再一头撞进山体导致目标丢失
- 新增：避障参数在 `mods/MachineAAM/aam_config.json` 的 `avoid*` 字段逐项可调（视距、撞地余量、规避强度、规避时的过载与油门系数等）
- 修复：低空平飞发射时把"没飞够安全高度"误判成"会撞上"，导致导弹被一路顶到高空、等它掉回来目标已飞远（改为下沉率门控 + 高度外推判撞 + 视距不小于一个转弯半径）
- 同步：全部 13 个 Mod 重新构建

### v2.4.0 变更（健壮性与安全性）
- 更新包强制 **SHA-256 + RSA 签名** 校验（游戏内下载器与应用器各校验一次）
- 修复：更新器只复制了 `.bat`，缺失 `.py` 与安装器，导致"退出游戏后应用更新"根本跑不通
- 修复：更新器找不到 `MachineInstaller.exe` 时的兜底路径算错（等于没有兜底）
- 修复：脚本不检查子进程返回码，失败也打印"完成"
- 修复：卸载会递归删除 `mods/`，直接毁掉玩家自己的 Mod（现改为保留 + 退出码语义化）
- 新增：备份 / 原子替换 / 写入后复核 / 失败自动回滚（安装器与应用器双层）
- 新增：全链路日志（`machine_update.log` / `installer.log`）
- 新增：`net.json` / `update.json` / `profile.json` 的 schema 校验
- 新增：游戏进程检测、文件占用检测、权限检测与明确提示
- 修复：`Machine/README.txt` 写入 BOM 且含开发机绝对路径

## 许可
仅供个人学习使用，禁止商用。
