#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""统一升级所有 FactionSystem mod.json 的 patch 版本号。"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(r"D:\豆包的下载")
DIRS = [
    ROOT / "Aviassembly_DEV" / "mods" / "FactionSystem",
    Path(r"D:\steam\steamapps\common\Aviassembly") / "mods" / "FactionSystem",
    ROOT / "Machine_Dev" / "dist" / "mods" / "FactionSystem",
    ROOT / "Machine_Dev" / "dist_upload" / "mods" / "FactionSystem",
    ROOT / "MachineLoader_work" / "mods" / "FactionSystem",
    ROOT / "MachineLoader_github" / "MachineLoader-main" / "mods" / "FactionSystem",
]

def bump(old: str) -> str:
    m = re.match(r"^(\d+\.\d+)\.(\d+)$", old)
    if not m:
        raise ValueError(f"unsupported version format: {old}")
    return f"{m.group(1)}.{int(m.group(2)) + 1}"

new_ver = None
for d in DIRS:
    p = d / "mod.json"
    if not p.exists():
        print(f"MISSING {p}")
        continue
    txt = p.read_text(encoding="utf-8-sig")
    data = json.loads(txt)
    old = data.get("version", "")
    if new_ver is None:
        new_ver = bump(old)
    data["version"] = new_ver
    out = json.dumps(data, indent=4, ensure_ascii=False) + "\n"
    p.write_text(out, encoding="utf-8")
    print(f"{old} -> {new_ver}  {p}")

print(f"done -> {new_ver}")
