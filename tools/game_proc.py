# -*- coding: utf-8 -*-
"""工具：找/停 Aviassembly 进程，启动游戏，轮询日志。用法:
  python game_proc.py list
  python game_proc.py stop dev|vanilla
  python game_proc.py start dev|vanilla
"""
import subprocess, sys, time, os, glob

DEV = u"D:\\豆包的下载\\Aviassembly_DEV"
VAN = u"D:\\steam\\steamapps\\common\\Aviassembly"

def exe_of(root):
    cands = glob.glob(os.path.join(root, "*.exe"))
    return cands[0] if cands else None

def list_procs():
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*Aviassembly*' } | Select-Object Id,ProcessName,Path | Format-Table -AutoSize"],
        capture_output=True, text=True)
    print(out.stdout or out.stderr)
    return out.stdout

def stop(root):
    exe = os.path.basename(exe_of(root)) if exe_of(root) else "Aviassembly*"
    subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*" + root.replace("\\", "\\\\") + "*' } | Stop-Process -Force"],
        capture_output=True, text=True)
    time.sleep(3)
    print("stopped", root)

def start(root):
    exe = exe_of(root)
    if not exe:
        print("no exe in", root); return
    subprocess.Popen([exe], cwd=root)
    print("started", exe)

if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "list":
        list_procs()
    elif cmd == "stop":
        stop(DEV if sys.argv[2] == "dev" else VAN)
    elif cmd == "start":
        start(DEV if sys.argv[2] == "dev" else VAN)
