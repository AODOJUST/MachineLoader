# -*- coding: utf-8 -*-
import urllib.request, os, shutil

url = u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/9f2232be-1b9c-4e0d-b5d5-adf7e00ecfb7.wav?preview=1&auth_key=1789211547-r0-u0-bbb7503548a990923232e1de1a4fd6f5"
dst = u"D:\\豆包的下载\\Machine_Dev\\tools\\launch_sfx.wav"
req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
with urllib.request.urlopen(req, timeout=120) as r:
    data = r.read()
with open(dst, "wb") as f:
    f.write(data)
print("wav", dst, len(data))

# 部署到 dev + 原版 MachineAAM/audio/
for game in [u"D:\\豆包的下载\\Aviassembly_DEV", u"D:\\steam\\steamapps\\common\\Aviassembly"]:
    d = os.path.join(game, "mods", "MachineAAM", "audio")
    os.makedirs(d, exist_ok=True)
    t = os.path.join(d, "launch.wav")
    shutil.copy2(dst, t)
    print("deployed", game.split(chr(92))[-1], t, os.path.getsize(t))
