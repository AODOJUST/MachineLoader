# -*- coding: utf-8 -*-
"""把 MachineAAM 的 mod.json 版本从 2.8.0 升到 2.9.0（6 份副本同步，保留 BOM）。

版本号只影响日志里那行 "[machine.aam] vX.Y.Z"，不进签名清单，不需要重新编译。
"""
import io, os

ROOT = r"D:\豆包的下载"
OLD = '"version": "2.8.0"'
NEW = '"version": "2.9.0"'

TARGETS = [
    r"Aviassembly_DEV\mods\MachineAAM\mod.json",
    r"D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\mod.json",
    r"Machine_Dev\dist\mods\MachineAAM\mod.json",
    r"Machine_Dev\dist_upload\mods\MachineAAM\mod.json",
    r"MachineLoader_work\mods\MachineAAM\mod.json",
    r"MachineLoader_github\MachineLoader-main\mods\MachineAAM\mod.json",
]

for rel in TARGETS:
    p = rel if os.path.isabs(rel) else os.path.join(ROOT, rel)
    if not os.path.exists(p):
        print("MISSING", p); continue
    raw = open(p, "rb").read()
    bom = raw[:3] == b"\xef\xbb\xbf"
    txt = raw.decode("utf-8-sig")
    if OLD not in txt:
        print("SKIP (no 2.8.0)", p); continue
    txt = txt.replace(OLD, NEW)
    with io.open(p, "w", encoding="utf-8-sig", newline="") as f:
        f.write(txt)
    back = open(p, "rb").read()
    print("OK  bom=%s  %s" % (back[:3] == b"\xef\xbb\xbf", p))

import hashlib
h = {}
for rel in TARGETS:
    p = rel if os.path.isabs(rel) else os.path.join(ROOT, rel)
    if os.path.exists(p):
        h.setdefault(hashlib.md5(open(p, "rb").read()).hexdigest()[:12], []).append(p)
print("\ndistinct md5 groups:", {k: len(v) for k, v in h.items()})
