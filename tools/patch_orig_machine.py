# -*- coding: utf-8 -*-
"""补原版 Machine/ 目录（更新器 + 配置模板）。"""
import os
import shutil

orig = r"D:\steam\steamapps\common\Aviassembly"
mdir = os.path.join(orig, "Machine")
os.makedirs(mdir, exist_ok=True)
shutil.copy2(r"D:\豆包的下载\Machine_Dev\dist\machine_update.bat", os.path.join(mdir, "machine_update.bat"))
shutil.copy2(r"D:\豆包的下载\Machine_Dev\dist\MachineInstaller.exe", os.path.join(mdir, "MachineInstaller.exe"))
net = os.path.join(mdir, "net.json")
if not os.path.exists(net):
    with open(net, "w", encoding="utf-8") as f:
        f.write('{"server":"","port":26460,"playerName":"Pilot"}')
upd = os.path.join(mdir, "update.json")
if not os.path.exists(upd):
    with open(upd, "w", encoding="utf-8") as f:
        f.write('{"repo":"","enabled":true}\n')
print("original Machine/ ok")
print(os.listdir(mdir))
