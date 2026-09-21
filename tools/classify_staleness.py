# -*- coding: utf-8 -*-
"""读 _release_staleness.txt，按 mod × 类别 × 状态（STALE / MISSING）归类汇总。
用于区分：真正需要同步的发布内容 vs 本就不该进包的垃圾（备份、运行时状态、调试截图）。
只读。
"""
import os, re, collections

REPO = r"D:/豆包的下载"
REPORT = os.path.join(REPO, "Machine_Dev", "_release_staleness.txt")

JUNK_SUFFIX = (".github_backup",)
JUNK_NAME = ("installed.json", "battlehold_state.json")

def category(rel):
    base = os.path.basename(rel)
    ext = os.path.splitext(base)[1].lower()
    if rel.endswith(JUNK_SUFFIX) or ".bak" in base:
        return "备份(垃圾)"
    if base in JUNK_NAME or base.endswith("_state.json"):
        return "运行时状态(垃圾)"
    if ext == ".dll":
        # 根级重复 DLL（扁平旧布局）也是垃圾
        if "/" not in rel:
            return "DLL(根级重复)"
        return "DLL(代码)"
    if ext in (".json",):
        return "配置/元数据"
    if ext in (".planedesign",):
        return "载具设计"
    if ext in (".png",):
        return "PNG"
    if ext in (".wav", ".mp3", ".ogg"):
        return "音频"
    if ext in (".txt", ".md"):
        return "文档"
    return "其它" + (ext or "")

mods = collections.OrderedDict()
cur_mod = None
cur_file = None
state = None  # None | "src" | copy-label

lines = open(REPORT, encoding="utf-8").read().splitlines()
i = 0
for ln in lines:
    m = re.match(r"^### (\S+) ", ln)
    if m:
        cur_mod = m.group(1)
        mods.setdefault(cur_mod, {})
        cur_file = None
        continue
    if cur_mod is None:
        continue
    if re.match(r"^  \S", ln) and not ln.startswith("      "):
        cur_file = ln.strip()
        mods[cur_mod].setdefault(cur_file, {"stale": set(), "missing": set()})
        continue
    m = re.match(r"^      (\S+)\s+(OK|STALE|MISSING)", ln)
    if m and cur_file:
        label, st = m.group(1), m.group(2)
        if st == "STALE":
            mods[cur_mod][cur_file]["stale"].add(label)
        elif st == "MISSING":
            mods[cur_mod][cur_file]["missing"].add(label)

print("=" * 100)
print("陈旧文件归类（STALE = 目标有但内容不同；MISSING = 目标缺该文件）")
print("=" * 100)
for mod, files in mods.items():
    agg = collections.defaultdict(lambda: {"n": 0, "stale": 0, "missing": 0})
    for rel, st in files.items():
        c = category(rel)
        agg[c]["n"] += 1
        if st["stale"]:
            agg[c]["stale"] += 1
        else:
            agg[c]["missing"] += 1
    print("\n%-14s 共 %d 个文件有差异" % (mod, len(files)))
    for c, v in sorted(agg.items(), key=lambda kv: -kv[1]["n"]):
        print("    %-16s %3d 个   (内容不同 %d / 目标缺失 %d)" % (c, v["n"], v["stale"], v["missing"]))

# 关键：列出所有"内容不同"的发布关键文件（DLL / 配置 / 载具设计）
print()
print("=" * 100)
print("★ 真正影响发布行为的文件（内容不同，必须同步）")
print("=" * 100)
for mod, files in mods.items():
    rows = []
    for rel, st in sorted(files.items()):
        c = category(rel)
        if c in ("DLL(代码)", "配置/元数据", "载具设计") and st["stale"]:
            rows.append((rel, c, sorted(st["stale"])))
    if rows:
        print("\n%s:" % mod)
        for rel, c, lbl in rows:
            print("    %-42s %-14s 陈旧副本: %s" % (rel, c, ", ".join(lbl)))
