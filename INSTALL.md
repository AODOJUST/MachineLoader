# Machine 加载器 —— 安装说明

Machine 是 **Aviassembly** 的 Mod 加载器：它向游戏程序集注入一小段启动钩子，
然后加载 `<游戏目录>\mods\` 里的各个 Mod。

**不需要安装 Python。** 安装、更新、卸载全部由压缩包内的
`MachineInstaller.exe` 完成 —— 一个自带的 .NET Framework 4.0 程序。

---

## 你需要准备

* Windows（64 位）
* .NET Framework 4.0 或更高版本（Windows 8 及以后自带）
* 已安装 Aviassembly（Steam 版或独立版都可以）

---

## ⚠️ Windows SmartScreen 提示

第一次运行 `MachineInstaller.exe` 时，Windows 可能会弹出
**“Windows 已保护你的电脑”**，因为这个程序还没有做代码签名。

1. 点击 **更多信息**
2. 点击 **仍要运行**

那几个 `.bat` 文件也一样（它们只是去启动这个 .exe）。

---

## 方式一 —— 双击 .bat（推荐）

1. 从 [Releases 页面](https://github.com/AODOJUST/MachineLoader/releases)
   下载 `MachineLoader-2.4.1.zip`
2. **把整个压缩包解压成一个文件夹** —— 不要在压缩包的预览窗口里直接运行，
   安装器需要和它放在一起的那些文件。
3. 双击运行 `install_machine.bat`（如果游戏装在 `C:\Program Files` 下面，
   请右键 → **以管理员身份运行**）。
4. 会弹出一个控制台窗口，依次走 10 个步骤，并画一条进度条：

   ```
   ============================================================
     Machine 加载器 - 安装程序  v2.4.1
   ============================================================

   [####--------------------]  20%  校验发布包完整性与签名...
     [ OK ] 核心 v2.4.1
   [##############----------]  60%  注入启动钩子到 Assembly-CSharp.dll...
     [ OK ] 启动钩子已注入
   [########################] 100%  收尾...
     [ OK ] 安装完成
   ```

   游戏目录是自动定位的（Steam 库文件夹、注册表、常见安装路径，再加一次浅层盘符扫描）。
   写入任何文件之前，都一定会先让你**确认路径**：

   ```
   确认安装到 D:\steam\steamapps\common\Aviassembly ? (Y/n):
   ```

   直接回车表示同意，输入 `n` 表示放弃。
5. 看到 **“安装完成！现在可以启动游戏。”** 就装好了。

---

## 方式二 —— 直接运行安装器

`install_machine.bat` 只是个启动器。你也可以自己运行这个程序，
适合写脚本，或者想显式指定路径的情况：

```
MachineInstaller.exe --auto --install "D:\steam\steamapps\common\Aviassembly"
MachineInstaller.exe --help
```

常用开关：

| 开关 | 作用 |
| --- | --- |
| `--install` / `--uninstall` / `--apply-update` | 要做什么（默认 `--install`） |
| `--auto` | 不提问，全部用默认值 |
| `--yes` | 所有确认一律回答“是” |
| `--no-pause` | 结束时不等按键 |
| `--allow-unsigned` | 放行没有签名的发布包（不推荐） |
| `--purge-mods` | 仅卸载时：连 `mods\` 目录一起删 |
| `--backup` | 仅卸载时：删除前先备份玩家数据 |

**不带任何参数**运行 `MachineInstaller.exe`（也就是双击它）等价于
`--auto --install`：自动定位游戏、显示路径让你确认、然后安装。

---

## 方式三 —— 手动安装（高级）

只有在安装器都失败、而且你清楚自己在做什么的情况下才这么做。

1. 解压压缩包。
2. 把 `Machine.Core.dll` 复制进 `<游戏目录>\Aviassembly_Data\Managed\`。
3. 把 `mods\` 和 `Machine\` 两个文件夹复制进游戏根目录。
4. 注入启动钩子，二选一：
   * `MachineInstaller.exe --install --yes "<游戏目录>"` —— 还是用安装器省事；
   * `python install_machine.py "<游戏目录>" -y` —— 如果你**确实**装了 Python 3，
     并且更习惯用脚本。Python 是可选的，只有走这条路才需要。
5. 启动游戏。

> 单纯“把文件复制过去”**不够**：只有把启动钩子注入 `Assembly-CSharp.dll` 之后，
> 游戏才会去加载 Mod。第 4 步一定要执行。

---

## 确认是否装好

1. 启动 Aviassembly。
2. 主菜单**左下角**会显示 `Machine Loader v2.4.1`。
3. 点击主菜单里的 **Mods** 按钮，可以看到已加载的 Mod。
4. 日志在 `<游戏目录>\Machine\logs\`（`Machine.log`、`installer.log`）。

---

## 卸载

1. 在你解压出来的文件夹里运行 `uninstall_machine.bat`（需要的话右键 →
   以管理员身份运行），或者执行 `MachineInstaller.exe --uninstall "<游戏目录>"`。
2. 在提示处确认。**动手之前**，控制台会先把你将失去什么、将保留什么逐条列出来。
3. 然后 `Assembly-CSharp.dll` 会从备份还原，`Machine.Core.dll` 和 `Machine\`
   目录会被移除，而你的 `mods\` 目录会**保留**。
4. `Machine\` 里的玩家数据（用户名、个人资料）会先备份到
   `<游戏目录>\Machine_uninstalled_<时间戳>\`。

并没有 `uninstall_machine.exe` 这个文件 —— 卸载靠的是一个 .bat 加上同一个
`MachineInstaller.exe`。

---

## 常见问题

### 窗口一闪就没，什么提示都没有
这是旧版本的 bug（启动器去调 `python`，而 `python` 常常是微软商店的占位程序）。
已经修好了：现在 `.bat` 直接启动 `MachineInstaller.exe`，每一步都会打印出来。

*如果还是这样，说明你运行的还是旧的压缩包。* 看控制台第一行打印的版本号。

### `Python was not found ... App execution aliases` / 退出码 9009
旧版本是通过 `python` 命令去跑 `install_machine.py` / `uninstall_machine.py` 的。
在大多数 Windows 上，`python` 会指向微软商店的**应用执行别名**
（`%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe`），那个占位程序就会打印这句话
并以 9009 退出。

现在的版本完全不需要 Python。如果你确实要走 Python 那条路，请从 python.org
装 Python 3（勾上 *Add python.exe to PATH*），或者显式用 `py -3` 启动。

### “应用程序无法正常启动”
请安装 **.NET Framework 4.0** 或更高版本，然后以管理员身份重试。

### `未找到有效的 Aviassembly 游戏目录`
自动定位会覆盖 Steam 库文件夹、注册表、常见安装路径和一次浅层盘符扫描。
如果仍然失败，请自己把路径传进去：

```
MachineInstaller.exe --install "C:\路径\Aviassembly"
```

这个文件夹里必须有 `Aviassembly.exe` 和
`Aviassembly_Data\Managed\Assembly-CSharp.dll`。

### 游戏里看不到 Mod
* 确认是**装完之后**才启动的游戏。
* `mods\` 必须放在**游戏根目录**，不能放进 `Aviassembly_Data\Managed\`。
* 每个 Mod 需要 `mods\<Mod>\mod.json` 和 `mods\<Mod>\code\<Mod>.dll`。
* 查看 `Machine\logs\Machine.log`。

### 启动游戏就崩溃
运行 `uninstall_machine.bat` 回到纯净版，确认纯净版正常之后再重新安装。
反馈问题时请附上 `Machine\logs\Machine.log`。

### 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 参数或环境不正确 |
| 2 | 哈希校验失败（发布包损坏） |
| 3 | 签名被拒 |
| 4 | 安装器的某一步失败 |
| 5 | 安装失败并已回滚 |
| 6 | 用户取消 |
| 7 | 游戏正在运行 —— 请先退出游戏 |
| 8 | 权限不足 —— 请以管理员身份运行 |

---

## 获取帮助

* **GitHub Issues**：https://github.com/AODOJUST/MachineLoader/issues
* **日志文件**：`<游戏目录>\Machine\logs\Machine.log`

---

## 关于代码签名

1. **现在**：靠这份说明加 .bat 安装器。安装前会用 RSA-3072 / SHA-256 签名校验
   `Machine.Core.dll`（公钥指纹 `93c4ab85…0065a`）；哈希或签名对不上就拒绝安装。
2. **以后**：通过 [SignPath.io](https://signpath.io/) 做免费的开源代码签名。
3. **长期**：如果项目做大，再上商业证书。

---

**Machine Team** | https://github.com/AODOJUST/MachineLoader | 许可证：MIT
