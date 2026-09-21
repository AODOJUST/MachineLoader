# -*- coding: utf-8 -*-
"""把本轮改动的 mod 运行文件打成一个"同版本内容补丁"zip（不碰 core、不碰其它 mod）。

产物：outputs/<name>.zip（默认 Machine_<版本>_<日期>_patch.zip）
  mods/MachineAAM/code/MachineAAM.dll
  mods/MachineAAM/aam_config.json
  mods/MachineAAM/R-37.planedesign
  mods/MachineAAM/AIM-424.planedesign
  mods/MachineShop/code/MachineShop.dll
  SHA256SUMS.txt
  <说明文件>                      （存在则一并打入）

设计要点：
- 用 'x' 独占模式建包，绝不覆盖已存在的 zip。
- 打完立刻重新读包、逐文件核对哈希与开发部署目录一致，不一致直接失败退出 1。
- 只读开发目录；不写 dist / dist_upload / 游戏目录。

用法: python tools/make_aam_patch.py [--version 2.4.1] [--date YYYYMMDD] [--name X.zip] [--notes 路径]
"""
import argparse
import hashlib
import os
import sys
import zipfile
from datetime import date

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(BASE)
MODS_ROOT = os.path.join(REPO, "Aviassembly_DEV", "mods")
OUT_DIR = os.path.join(REPO, "outputs")
DEFAULT_NOTES = os.path.join(OUT_DIR, "Machine_2.4.1_补丁说明.txt")

# mod 名 -> 需要打进补丁的相对文件（相对 mods/<Mod>/）
MODS = {
    "MachineAAM": (
        "code/MachineAAM.dll",
        "aam_config.json",
        "R-37.planedesign",
        "AIM-424.planedesign",
        "decoy flare.planedesign",
    ),
    "MachineShop": (
        "code/MachineShop.dll",
    ),
}


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def collect():
    """返回 [(arc_name, abs_src, sha256)]，缺文件则抛 FileNotFoundError。"""
    entries = []
    for mod, names in MODS.items():
        for name in names:
            src = os.path.join(MODS_ROOT, mod, name)
            if not os.path.isfile(src):
                raise FileNotFoundError(src)
            entries.append(("mods/%s/%s" % (mod, name), src, sha256_file(src)))
    return entries


def build(archive, notes):
    os.makedirs(OUT_DIR, exist_ok=True)

    entries = collect()
    sums = dict((arc, digest) for arc, _src, digest in entries)
    manifest = "\n".join("%s  %s" % (digest, arc) for arc, _src, digest in entries) + "\n"

    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED) as bundle:
        for arc, src, _digest in entries:
            bundle.write(src, arc)
        bundle.writestr("SHA256SUMS.txt", manifest)
        if notes and os.path.isfile(notes):
            bundle.write(notes, os.path.basename(notes))
        else:
            print("note: 未找到补丁说明，包内不含说明文件")

    print("built ->", archive, "(%d bytes)" % os.path.getsize(archive))

    # 重新读包校验：包内每个文件必须与开发部署目录逐字节一致
    ok = True
    with zipfile.ZipFile(archive) as bundle:
        names = bundle.namelist()
        print("zip entries:", names)
        for arc, _src, expected in entries:
            if arc not in names:
                print("  FAIL 缺失条目", arc)
                ok = False
                continue
            got = hashlib.sha256(bundle.read(arc)).hexdigest()
            if got != expected:
                print("  FAIL 哈希不符", arc, got[:12], "!=", expected[:12])
                ok = False
            else:
                print("  OK   %-46s %s" % (arc, got[:12]))
        listed = dict(
            line.split("  ", 1)[::-1]
            for line in bundle.read("SHA256SUMS.txt").decode().splitlines()
            if line.strip()
        )
        for arc, _src, expected in entries:
            if listed.get(arc) != expected:
                print("  FAIL 清单不一致", arc)
                ok = False

    print("RESULT:", "OK" if ok else "FAILED")
    return 0 if ok else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="2.4.1")
    parser.add_argument("--date", default=date.today().strftime("%Y%m%d"))
    parser.add_argument("--name", default=None, help="输出 zip 文件名（默认 Machine_<版本>_<日期>_patch.zip）")
    parser.add_argument("--notes", default=DEFAULT_NOTES, help="要一并打入的补丁说明文件路径")
    args = parser.parse_args()

    name = args.name or "Machine_%s_%s_patch.zip" % (args.version, args.date)
    archive = os.path.join(OUT_DIR, name)
    if os.path.exists(archive):
        print("FAIL: 目标已存在，拒绝覆盖 ->", archive)
        print("      如需重建，请先手工删除或换 --name。")
        return 1
    try:
        return build(archive, args.notes)
    except FileNotFoundError as exc:
        print("FAIL: 开发目录缺少文件 ->", exc)
        return 1


if __name__ == "__main__":
    sys.exit(main())
