# -*- coding: utf-8 -*-
"""
Machine 更新应用器（游戏内下载完成后，退出游戏运行本程序）
位置：<游戏目录>/Machine/machine_update.bat（安装时自动复制）
原理：把 Machine/update/Machine.Core.dll 覆盖到 Managed，无需重新安装加载器。
"""
import os
import sys
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))   # <游戏目录>/Machine
GAME_DIR = os.path.dirname(HERE)                     # 游戏根目录

INSTALLER = os.path.join(HERE, "MachineInstaller.exe")
if not os.path.exists(INSTALLER):
    # 兜底：dist 目录
    INSTALLER = os.path.join(os.path.dirname(HERE), "MachineInstaller.exe")
if not os.path.exists(INSTALLER):
    print("错误: 找不到 MachineInstaller.exe（更新器）。")
    input("按回车退出...")
    sys.exit(1)

up_core = os.path.join(HERE, "update", "Machine.Core.dll")
if not os.path.exists(up_core):
    print("未找到待应用的更新 (Machine/update/Machine.Core.dll)。")
    print("请先在游戏内完成下载，或本更新器仅用于应用已下载的更新。")
    input("按回车退出...")
    sys.exit(0)

print("正在应用 Machine 更新...")
r = subprocess.run([INSTALLER, GAME_DIR, "--update"], capture_output=True,
                   text=True, encoding="utf-8", errors="ignore")
if r.stdout:
    print(r.stdout.strip())
if r.stderr:
    print(r.stderr.strip())
print("完成。现在可以重新启动游戏。")
input("按回车退出...")
