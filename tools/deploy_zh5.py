# -*- coding: utf-8 -*-
import subprocess, json, os, urllib.request

jobs = [
    (u"剩余油量.wav", "amk-tool-extract-audio-1073725629442"),
    (u"迎角过大.wav", "amk-tool-extract-audio-1109363769602"),
    (u"锁定.wav", "amk-tool-extract-audio-1073432311298"),
    (u"燃油检测.wav", "amk-tool-extract-audio-979556590082"),
    (u"姿态仪失效.wav", "amk-tool-extract-audio-979556590594"),
]

def query(task_id):
    r = subprocess.run(["mediakit-cli", "shared", "query-task", "--task-id", task_id, "--poll-complete"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    try:
        d = json.loads(r.stdout)
        return d.get("audio_url", "")
    except Exception:
        return ""

dl_dir = u"D:\\豆包的下载\\Machine_Dev\\tools\\zh_cn"
os.makedirs(dl_dir, exist_ok=True)
ok = []
for name, tid in jobs:
    url = query(tid)
    if not url:
        print("NO URL for", name)
        continue
    p = os.path.join(dl_dir, name)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=90) as r:
        data = r.read()
    with open(p, "wb") as f:
        f.write(data)
    ok.append((name, data))
    print("dl", name, len(data))

if len(ok) != len(jobs):
    print("PARTIAL DL")
    raise SystemExit(1)

import shutil
for game in [u"D:\\豆包的下载\\Aviassembly_DEV", u"D:\\steam\\steamapps\\common\\Aviassembly"]:
    zhd = os.path.join(game, "mods", "VoiceAlerts", "audio", "zh")
    os.makedirs(zhd, exist_ok=True)
    for name, data in ok:
        with open(os.path.join(zhd, name), "wb") as f:
            f.write(data)
    old = os.path.join(zhd, u"剩余燃油.wav")
    if os.path.exists(old):
        os.remove(old)
        print(game.split(chr(92))[-1], "removed old 剩余燃油.wav")
    aud = os.path.join(game, "mods", "VoiceAlerts", "audio")
    fe = os.path.join(aud, "fuellow.wav")
    if os.path.exists(fe):
        shutil.copy2(fe, os.path.join(aud, "fuel45.wav"))
        print(game.split(chr(92))[-1], "en fuel45.wav <- fuellow.wav")
    print(game.split(chr(92))[-1], "zh:", sorted(os.listdir(zhd)))
