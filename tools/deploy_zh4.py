# -*- coding: utf-8 -*-
import urllib.request, os, shutil

jobs = [
    (u"拉起.wav", u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/50a210e2-6504-4248-8286-9c7f75e31029.wav?preview=1&auth_key=1789212764-r0-u0-413e926b48991063651a584d4fc7f511"),
    (u"发射.wav", u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/76abdba6-ef0a-4a3d-afae-143df882ac63.wav?preview=1&auth_key=1789212765-r0-u0-c145a9bc5f2d584166988568ca413976"),
    (u"燃油告警.wav", u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/62d27656-b34b-43ed-8a7c-516b72d5071b.wav?preview=1&auth_key=1789212777-r0-u0-45c0d0714b9091b5da30c62e42e8a14a"),
    (u"敌导弹.wav", u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/a4b5d5d8-b2bd-4810-80a8-476a33498ec1.wav?preview=1&auth_key=1789212774-r0-u0-65c3d84643b1b009b5a2d946c4e3d777"),
]

# 下载到 tools/zh_cn/
dl_dir = u"D:\\豆包的下载\\Machine_Dev\\tools\\zh_cn"
os.makedirs(dl_dir, exist_ok=True)
for name, url in jobs:
    p = os.path.join(dl_dir, name)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        data = r.read()
    with open(p, "wb") as f:
        f.write(data)
    print("dl", name, len(data))

# 部署到 dev + 原版 audio/zh/：新 4 词（中文命名）
for game in [u"D:\\豆包的下载\\Aviassembly_DEV", u"D:\\steam\\steamapps\\common\\Aviassembly"]:
    zhd = os.path.join(game, "mods", "VoiceAlerts", "audio", "zh")
    os.makedirs(zhd, exist_ok=True)
    for name, _ in jobs:
        shutil.copy2(os.path.join(dl_dir, name), os.path.join(zhd, name))
    # 重命名旧英文名 -> 中文名（在 zh 目录内）
    rename_map = {
        "stall.wav": u"最小速度.wav",
        "angle.wav": u"最大迎角.wav",
        "overspeed.wav": u"过载超限.wav",
        "bingo.wav": u"剩余燃油.wav",
    }
    for old, new in rename_map.items():
        op = os.path.join(zhd, old)
        if os.path.exists(op):
            np_ = os.path.join(zhd, new)
            # 已有同名新文件则直接删旧（新纯净版优先）
            if os.path.exists(np_):
                os.remove(op)
                print(game.split(chr(92))[-1], "removed old", old)
            else:
                os.rename(op, np_)
                print(game.split(chr(92))[-1], "renamed", old, "->", new)
    print(game.split(chr(92))[-1], "zh:", sorted(os.listdir(zhd)))
