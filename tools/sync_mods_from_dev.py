# -*- coding: utf-8 -*-
"""把 Aviassembly_DEV/mods 的"发布相关文件"同步到三处发布副本。

为什么需要它：
  pack_dist.py 用 copytree 整体镜像 DEV/mods，会把调试截图、_prev/ 备份、
  .github_backup、根级重复 DLL 一起带进发布件（09-14 刚清理过一轮）。
  而且 pack_dist.py 会覆盖 dist/Machine.Core.dll，让已签名的 version.json 失效。
  所以这里只做**白名单式**的文件级同步，不碰任何二进制/签名文件。

白名单（相对 mod 目录）：
  code/<Mod>.dll            程序集
  mod.json / language.json / *_config.json
  *.planedesign             载具/武器设计
  audio/ icons/ assets/ textures/ sounds/ lang/ 下的所有文件

排除（即使匹配白名单）：
  路径中出现 _ 开头的目录（_prev / _archive）
  *.github_backup、*.bak*
  *_state.json              运行时状态
  根级 <Mod>.dll            旧扁平布局的重复文件
  根级 *.png/*.wav/*.mp3    调试截图与工作音频

用法：
  python tools/sync_mods_from_dev.py            # 干跑，只报告
  python tools/sync_mods_from_dev.py --apply    # 实际写入
"""
import argparse
import hashlib
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEV_ROOT = os.path.dirname(HERE)                       # Machine_Dev
BASE = os.path.dirname(DEV_ROOT)                       # 豆包的下载
SRC = os.path.join(BASE, "Aviassembly_DEV", "mods")

TARGETS = [
    ("dist", os.path.join(DEV_ROOT, "dist", "mods")),
    ("dist_upload", os.path.join(DEV_ROOT, "dist_upload", "mods")),
    ("Loader_github", os.path.join(BASE, "MachineLoader_github", "MachineLoader-main", "mods")),
]

ASSET_DIRS = {"audio", "icons", "assets", "textures", "sounds", "lang", "languages", "fonts"}
ROOT_DOCS = {"mod.json", "language.json"}


def is_junk_dir(name):
    """中间目录名是否属于备份/临时目录。"""
    low = name.lower()
    return (name.startswith("_") or low.startswith("backup") or low.startswith("bak")
            or low.startswith("old") or low.startswith("prev")
            or low.endswith("_bak") or low.endswith("_backup"))


def wanted(rel):
    """rel 形如 'MachineAAM/code/MachineAAM.dll'（正斜杠）。"""
    parts = rel.split("/")
    if len(parts) < 2:
        return False
    # 中间目录不允许是备份/临时目录（_prev / _archive / backup_20260914_103846）
    if any(is_junk_dir(p) for p in parts[1:-1]):
        return False
    name = parts[-1]
    low = name.lower()
    if low.endswith(".github_backup") or ".bak" in low:
        return False
    if low.endswith("_state.json"):
        return False

    depth = len(parts) - 1          # 文件相对 mod 根的层级：1=根级
    if depth == 1:
        if low.endswith(".dll"):
            return False            # 根级重复 DLL
        if low.endswith((".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg", ".log")):
            return False            # 根级调试截图 / 工作音频 / 日志
        if name in ROOT_DOCS or low.endswith("_config.json"):
            return True
        if low.endswith(".planedesign"):
            return True
        return False
    # 子目录
    if parts[1] == "code":
        return depth == 2 and low.endswith(".dll")
    if parts[1] in ASSET_DIRS:
        return True
    return False


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="实际写入（默认只报告）")
    args = ap.parse_args(argv)

    if not os.path.isdir(SRC):
        print("找不到源目录:", SRC)
        return 1

    plan = []          # (mod, rel, src, [缺少/不同的目标])
    skipped_junk = 0
    for mod in sorted(os.listdir(SRC)):
        mroot = os.path.join(SRC, mod)
        if not os.path.isdir(mroot):
            continue
        for dirpath, _dirs, names in os.walk(mroot):
            for n in names:
                full = os.path.join(dirpath, n)
                rel_mod = os.path.relpath(full, mroot).replace("\\", "/")
                rel = mod + "/" + rel_mod
                if not wanted(rel):
                    skipped_junk += 1
                    continue
                sh = sha256(full)
                need = []
                for label, troot in TARGETS:
                    tgt = os.path.join(troot, mod, rel_mod)
                    if not os.path.isfile(tgt) or sha256(tgt) != sh:
                        need.append(label)
                if need:
                    plan.append((mod, rel, full, sh, need))

    print("源: %s" % SRC)
    print("白名单命中但目标缺失/不同 —— 需同步 %d 个文件（跳过非发布文件 %d 个）"
          % (len(plan), skipped_junk))
    print()
    cur = None
    for mod, rel, full, sh, need in plan:
        if mod != cur:
            cur = mod
            print("### %s" % mod)
        print("   %-46s -> %s" % (rel, ", ".join(need)))

    if not args.apply:
        print()
        print("（干跑，未写入。加 --apply 执行）")
        return 0

    print()
    print("=== 开始写入 ===")
    done = 0
    for mod, rel, full, sh, need in plan:
        rel_mod = rel.split("/", 1)[1]
        for label, troot in TARGETS:
            if label not in need:
                continue
            tgt = os.path.join(troot, mod, rel_mod)
            os.makedirs(os.path.dirname(tgt), exist_ok=True)
            shutil.copy2(full, tgt)
            if sha256(tgt) != sh:
                print("   !! 复制后哈希不符: %s" % tgt)
                return 1
            done += 1
    print("已写入 %d 个文件 × 目标" % done)

    # 复核
    print()
    print("=== 复核 ===")
    bad = 0
    for mod, rel, full, sh, need in plan:
        rel_mod = rel.split("/", 1)[1]
        for label, troot in TARGETS:
            tgt = os.path.join(troot, mod, rel_mod)
            if sha256(tgt) != sh:
                print("   !! 仍不一致: %s" % tgt)
                bad += 1
    print("复核结果: %s" % ("全部一致" if bad == 0 else "%d 个不一致" % bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
