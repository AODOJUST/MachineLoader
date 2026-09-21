# -*- coding: utf-8 -*-
"""清理 Aviassembly_DEV/mods 源目录里的非发布文件。

为什么需要：
  pack_dist.py 用 copytree 整体镜像 Aviassembly_DEV/mods -> dist/mods，
  所以源目录里的调试截图、_prev/ 备份、.github_backup、根级重复 DLL 会被
  一起打进发布件（09-14 清理过一次，但只要有人再跑 pack_dist.py 就会回来）。

判定复用 sync_mods_from_dev.wanted() —— 与发布同步**同一套白名单**，
保证"进发布件的东西"和"留在源目录的东西"永远一致。

安全设计：
  - 默认只报告（干跑），--apply 才动手
  - **移动**到备份目录，不删除；保持原目录结构，可整体还原
  - 备份目录放在工作区根（不在 game 目录、不在 mods/ 里），不会被 pack_dist 看到
  - 每个移动都复核"目标存在且哈希一致"后才算成功

用法：
  python tools/clean_mods_source.py            # 干跑，只报告
  python tools/clean_mods_source.py --apply    # 移动到备份目录
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
BACKUP = os.path.join(BASE, "_mods_junk_removed_%s" % time.strftime("%Y%m%d"))

sys.path.insert(0, HERE)
from sync_mods_from_dev import wanted, sha256  # noqa: E402

# 这些不是"发布内容"（白名单会挡在发布件之外），但**是玩家/运行时数据**，
# 必须留在源目录里，不能当垃圾移走。实测 battlehold_state.json 存的是
# 玩家货舱库存（capacity + items），删掉等于清空存货。
KEEP_IN_SOURCE = ("battlehold_state.json",)


def is_runtime_data(rel_mod):
    return os.path.basename(rel_mod) in KEEP_IN_SOURCE or rel_mod.endswith("_state.json")


def category(rel_mod):
    base = os.path.basename(rel_mod)
    low = base.lower()
    parts = rel_mod.split("/")
    if is_runtime_data(rel_mod):
        return "运行时数据(保留)"
    if len(parts) > 1 and any(p.lower().startswith(("_", "backup", "bak", "old", "prev"))
                              for p in parts[:-1]):
        return "备份目录"
    if low.endswith(".github_backup") or ".bak" in low:
        return "备份文件"
    if low.endswith("_state.json"):
        return "运行时状态"
    if len(parts) == 1 and low.endswith(".dll"):
        return "根级重复 DLL"
    if len(parts) == 1 and low.endswith((".png", ".jpg", ".jpeg")):
        return "根级调试截图"
    if len(parts) == 1 and low.endswith((".wav", ".mp3", ".ogg")):
        return "根级工作音频"
    if len(parts) == 1 and low.endswith(".log"):
        return "日志"
    return "其它"


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="实际移动（默认只报告）")
    args = ap.parse_args(argv)

    if not os.path.isdir(SRC):
        print("找不到源目录:", SRC)
        return 1

    plan = []          # (mod, rel_mod, abs, size, cat)
    keep = 0
    runtime = []
    for mod in sorted(os.listdir(SRC)):
        mroot = os.path.join(SRC, mod)
        if not os.path.isdir(mroot):
            continue
        for dirpath, _dirs, names in os.walk(mroot):
            for n in names:
                full = os.path.join(dirpath, n)
                rel_mod = os.path.relpath(full, mroot).replace("\\", "/")
                if wanted(mod + "/" + rel_mod):
                    keep += 1
                    continue
                if is_runtime_data(rel_mod):
                    runtime.append((mod, rel_mod))
                    continue
                plan.append((mod, rel_mod, full, os.path.getsize(full), category(rel_mod)))

    print("源目录: %s" % SRC)
    print("保留（白名单，发布需要）: %d 个文件" % keep)
    print("保留（运行时/玩家数据） : %d 个文件  %s"
          % (len(runtime), ", ".join("%s/%s" % r for r in runtime) or "-"))
    print("待移走（非发布文件）    : %d 个文件，合计 %.1f MB"
          % (len(plan), sum(p[3] for p in plan) / 1048576.0))
    print()

    cur = None
    tally = {}
    for mod, rel_mod, full, size, cat in plan:
        if mod != cur:
            cur = mod
            print("### %s" % mod)
        tally[(mod, cat)] = tally.get((mod, cat), 0) + 1
        if size > 200 * 1024 or cat not in ("根级调试截图", "根级工作音频"):
            print("   %-10s %9d  %s" % (cat, size, rel_mod))

    print()
    print("=== 按 mod × 类别汇总 ===")
    for (mod, cat), n in sorted(tally.items()):
        print("   %-16s %-12s %4d" % (mod, cat, n))

    if not args.apply:
        print()
        print("（干跑，未移动。加 --apply 执行，文件会移到 %s）" % BACKUP)
        return 0

    print()
    print("=== 开始移动 -> %s ===" % BACKUP)
    moved = 0
    for mod, rel_mod, full, _size, _cat in plan:
        dst = os.path.join(BACKUP, mod, rel_mod)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        before = sha256(full)
        shutil.move(full, dst)
        if not os.path.isfile(dst) or sha256(dst) != before:
            print("   !! 移动后校验失败: %s" % rel_mod)
            return 1
        moved += 1
    print("已移动 %d 个文件" % moved)

    # 复核：源目录已无非发布文件（运行时数据除外），且白名单文件数量未变
    left = 0
    kept_now = 0
    for mod in sorted(os.listdir(SRC)):
        mroot = os.path.join(SRC, mod)
        if not os.path.isdir(mroot):
            continue
        for dirpath, _dirs, names in os.walk(mroot):
            for n in names:
                rel_mod = os.path.relpath(os.path.join(dirpath, n), mroot).replace("\\", "/")
                if wanted(mod + "/" + rel_mod):
                    kept_now += 1
                elif not is_runtime_data(rel_mod):
                    left += 1
    print("复核: 源目录剩余非发布文件 = %d（应为 0）；白名单文件 = %d（应仍为 %d）"
          % (left, kept_now, keep))
    return 1 if (left or kept_now != keep) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
