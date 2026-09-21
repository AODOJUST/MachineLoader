# -*- coding: utf-8 -*-
"""发布件全量陈旧度扫描（只读）。

以 Aviassembly_DEV/mods 为基准（verify_release.py 认定的"部署源"），
逐个 mod、逐个文件比对 dist / dist_upload / Loader_github / Loader_work / steam。

状态：
  OK       哈希一致
  STALE    哈希不同（体积一并给出，用于判断是否真改代码）
  MISSING  目标缺该文件
"""
import hashlib, os, sys, time

REPO = r"D:/豆包的下载"
BASE = os.path.join(REPO, "Aviassembly_DEV", "mods")

COPIES = [
    ("dist", os.path.join(REPO, "Machine_Dev", "dist", "mods")),
    ("dist_upload", os.path.join(REPO, "Machine_Dev", "dist_upload", "mods")),
    ("Loader_github", os.path.join(REPO, "MachineLoader_github", "MachineLoader-main", "mods")),
    ("Loader_work", os.path.join(REPO, "MachineLoader_work", "mods")),
    ("steam", r"D:/steam/steamapps/common/Aviassembly/mods"),
]

_cache = {}


def sha(p):
    if p in _cache:
        return _cache[p]
    h = hashlib.sha256()
    try:
        with open(p, "rb") as f:
            for c in iter(lambda: f.read(1 << 16), b""):
                h.update(c)
    except OSError:
        _cache[p] = None
        return None
    _cache[p] = h.hexdigest()
    return _cache[p]


def meta(p):
    try:
        st = os.stat(p)
    except OSError:
        return None
    return (st.st_size, time.strftime("%m-%d %H:%M", time.localtime(st.st_mtime)), sha(p))


out = []
out.append("=" * 96)
out.append("发布件全量陈旧度扫描   基准 = Aviassembly_DEV/mods   生成 %s"
           % time.strftime("%Y-%m-%d %H:%M:%S"))
out.append("=" * 96)

stale_rows = []
mods = sorted(d for d in os.listdir(BASE) if os.path.isdir(os.path.join(BASE, d)))

for mod in mods:
    mroot = os.path.join(BASE, mod)
    files = []
    for dirpath, _dirs, names in os.walk(mroot):
        for n in names:
            full = os.path.join(dirpath, n)
            files.append(os.path.relpath(full, mroot).replace("\\", "/"))
    files.sort()
    lines = []
    for rel in files:
        src = os.path.join(mroot, rel)
        sm = meta(src)
        if not sm:
            continue
        row = {"mod": mod, "rel": rel, "src": sm, "copies": {}}
        bad = False
        for label, root in COPIES:
            tgt = os.path.join(root, mod, rel)
            tm = meta(tgt)
            if tm is None:
                row["copies"][label] = ("MISSING", None, None)
                bad = True
            elif tm[2] == sm[2]:
                row["copies"][label] = ("OK", tm[0], tm[1])
            else:
                row["copies"][label] = ("STALE", tm[0], tm[1])
                bad = True
        if bad:
            stale_rows.append(row)
            lines.append(row)
    if lines:
        out.append("")
        out.append("### %s  —— 有 %d 个文件在部分副本中陈旧" % (mod, len(lines)))
        for row in lines:
            sm = row["src"]
            out.append("  %s" % row["rel"])
            out.append("      源      %8d  %s  %s" % (sm[0], sm[1], sm[2][:16]))
            for label, _root in COPIES:
                st, sz, mt = row["copies"][label]
                if st == "OK":
                    out.append("      %-13s OK" % label)
                else:
                    out.append("      %-13s %-7s %s  %s" % (
                        label, st,
                        ("%8d  %s" % (sz, mt)) if sz else "-",
                        "" if st == "MISSING" else ""))
        out.append("")

if not stale_rows:
    out.append("")
    out.append("全部 mod 的全部文件在所有副本中一致 —— 无需同步。")

out.append("")
out.append("=" * 96)
out.append("汇总：陈旧文件 %d 个，涉及 mod：%s"
           % (len(stale_rows), ", ".join(sorted({r["mod"] for r in stale_rows})) or "无"))
out.append("=" * 96)

text = "\n".join(out)
print(text)
with open(os.path.join(REPO, "Machine_Dev", "_release_staleness.txt"), "w", encoding="utf-8") as f:
    f.write(text + "\n")
