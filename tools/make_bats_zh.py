# -*- coding: utf-8 -*-
"""
把三个 .bat 启动器写成中文版。

编码规则（重要，别再改回 UTF-8）：
  * 文件本身保存为 GBK / CP936 —— 系统 OEMCP=936，双击时 cmd 用的就是这个代码页；
    实测 cmd.exe 解析含 UTF-8 多字节字符的批处理会出现字节位移错乱（整行被当命令执行）。
  * 开头加 `chcp 936 >nul` —— 这样在 UTF-8(65001) 控制台下也能正常显示中文。
    实测：936 控制台 5/5 行正确、65001 控制台 5/5 行正确、无解析错误。
  * rem 行里不写 < > | & ^ %（cmd 会对 rem 行做重定向解析）；
    echo 行里的 < > 用 ^ 转义。
"""
import os

DIST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "dist")

HEAD = (
    "@echo off\r\n"
    "rem 本文件用 GBK(936) 编码保存，请勿改成 UTF-8：cmd.exe 解析含 UTF-8 多字节\r\n"
    "rem 字符的批处理会错位，导致整行被当成命令执行。\r\n"
    "setlocal enableextensions\r\n"
    "chcp 936 >nul\r\n"
    "cd /d \"%~dp0\"\r\n"
    "\r\n"
)

MISSING_TITLE = (
    ":missing\r\n"
    "echo.\r\n"
    "echo [错误] 本文件夹里找不到 MachineInstaller.exe\r\n"
    "echo.\r\n"
)

INSTALL = (
    "@echo off\r\n"
    "rem Machine 加载器 - 一键安装\r\n"
    "rem\r\n"
    "rem 本文件只是个启动器：真正干活的是同目录下的 MachineInstaller.exe，\r\n"
    "rem 所以不需要你的系统里装过 Python。\r\n"
    "rem 旧版本是用 python 命令去调 install_machine.py 的，而 Windows 上的 python\r\n"
    "rem 往往是微软商店的应用执行别名占位程序，只会打印 Python was not found 然后\r\n"
    "rem 以 9009 退出 —— 这就是以前黑窗一闪而过、后面什么提示都没有的原因。\r\n"
    "rem\r\n"
    "rem 加上 --yes 之外的开关请直接跟在命令行后面，例如：\r\n"
    "rem    install_machine.bat --quiet --no-pause\r\n"
) + HEAD + (
    "if not exist \"%~dp0MachineInstaller.exe\" goto :missing\r\n"
    "\r\n"
    "\"%~dp0MachineInstaller.exe\" --auto --install %*\r\n"
    "set \"RC=%ERRORLEVEL%\"\r\n"
    "exit /b %RC%\r\n"
    "\r\n"
) + MISSING_TITLE + (
    "echo   压缩包里不止这一个文件：请把整个压缩包解压到同一个文件夹，\r\n"
    "echo   再从解压出来的文件夹里运行 install_machine.bat。\r\n"
    "echo.\r\n"
    "pause\r\n"
    "exit /b 9009\r\n"
)

UNINSTALL = (
    "@echo off\r\n"
    "rem Machine 加载器 - 一键卸载（把游戏还原成纯净版）\r\n"
    "rem\r\n"
    "rem 本文件只是个启动器：真正干活的是同目录下的 MachineInstaller.exe，\r\n"
    "rem 所以不需要你的系统里装过 Python。\r\n"
    "rem 旧版本是用 python 命令去调 uninstall_machine.py 的，Windows 上的 python\r\n"
    "rem 常被微软商店的占位程序接管，于是报出 exit code 9009。\r\n"
    "rem\r\n"
    "rem 卸载前会先把玩家数据备份一份，并且保留 mods 目录。\r\n"
    "rem 如果连 mods 目录也要一起删掉，请加参数 --purge-mods。\r\n"
    "rem\r\n"
    "rem 加上别的开关请直接跟在命令行后面，例如：\r\n"
    "rem    uninstall_machine.bat --purge-mods\r\n"
) + HEAD + (
    "if not exist \"%~dp0MachineInstaller.exe\" goto :missing\r\n"
    "\r\n"
    "\"%~dp0MachineInstaller.exe\" --auto --uninstall --backup %*\r\n"
    "set \"RC=%ERRORLEVEL%\"\r\n"
    "exit /b %RC%\r\n"
    "\r\n"
) + MISSING_TITLE + (
    "echo   请把整个压缩包解压到同一个文件夹后再运行本文件；\r\n"
    "echo   也可以直接用游戏目录下 Machine 文件夹里的 MachineInstaller.exe，\r\n"
    "echo   并加上 --uninstall 参数。\r\n"
    "echo.\r\n"
    "pause\r\n"
    "exit /b 9009\r\n"
)

UPDATE = (
    "@echo off\r\n"
    "rem Machine 加载器 - 应用游戏内已经下载好的更新\r\n"
    "rem\r\n"
    "rem 请在关闭游戏之后运行：游戏内的更新器会把新的 Machine.Core.dll，连同它的\r\n"
    "rem .sig 签名与 apply.json 一起下载到 游戏目录 下的 Machine 文件夹的 update 子目录，\r\n"
    "rem 本脚本会先校验哈希与 RSA 签名，通过之后才写入。\r\n"
    "rem\r\n"
    "rem 本文件只是个启动器：真正干活的是同目录下的 MachineInstaller.exe，\r\n"
    "rem 所以不需要你的系统里装过 Python。\r\n"
) + HEAD + (
    "if not exist \"%~dp0MachineInstaller.exe\" goto :missing\r\n"
    "\r\n"
    "\"%~dp0MachineInstaller.exe\" --auto --apply-update %*\r\n"
    "set \"RC=%ERRORLEVEL%\"\r\n"
    "exit /b %RC%\r\n"
    "\r\n"
) + MISSING_TITLE + (
    "echo   正常情况下它就在本文件旁边，也就是游戏目录下 Machine 文件夹里的\r\n"
    "echo   MachineInstaller.exe。\r\n"
    "echo.\r\n"
    "pause\r\n"
    "exit /b 9009\r\n"
)

FILES = {
    "install_machine.bat": INSTALL,
    "uninstall_machine.bat": UNINSTALL,
    "machine_update.bat": UPDATE,
}

for name, text in FILES.items():
    p = os.path.join(DIST, name)
    with open(p, "w", encoding="gbk", newline="") as f:
        f.write(text)
    raw = text.encode("gbk")
    print("%-24s %6d bytes (GBK)" % (name, len(raw)))
print("ok")
