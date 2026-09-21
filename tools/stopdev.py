# -*- coding: utf-8 -*-
import subprocess, time, os
# 停 dev
for pat in [r'Get-Process Aviassembly -ErrorAction SilentlyContinue | Stop-Process -Force',
            r"Get-Process | Where-Object { $_.Path -like '*Aviassembly_DEV*' } | Stop-Process -Force"]:
    subprocess.run(['powershell', '-NoProfile', '-Command', pat], capture_output=True, text=True)
    time.sleep(2)
log = u'D:\\豆包的下载\\Aviassembly_DEV\\Machine\\logs\\Machine.log'
for _ in range(8):
    try:
        if os.path.exists(log):
            os.remove(log)
            print('log removed')
        else:
            print('no log')
        break
    except PermissionError:
        time.sleep(2)
