import io, os, sys

DEV = r"D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\aam_config.json"
CANON = r"D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\aam_config.json"

with io.open(DEV, "r", encoding="utf-8-sig") as f:
    txt = f.read()

orig = txt

# 1) 自测专用开关：关掉，并把 aiCombatTest 这一行整行删除（其余 5 份配置都没有这个键）
lines = txt.split("\n")
out = []
removed = []
for ln in lines:
    s = ln.strip()
    if s.startswith('"aiCombatTest"'):
        removed.append(ln)
        continue
    if s.startswith('"aiDamagePlayer"'):
        ln = ln.replace('"aiDamagePlayer": false', '"aiDamagePlayer": true')
    if s.startswith('"autoEnterFlyMode"'):
        ln = ln.replace('"autoEnterFlyMode": true', '"autoEnterFlyMode": false')
    out.append(ln)
txt = "\n".join(out)

with io.open(DEV, "w", encoding="utf-8-sig", newline="") as f:
    f.write(txt)

with io.open(CANON, "r", encoding="utf-8-sig") as f:
    canon = f.read()

same = (txt == canon)
print("removed lines:", removed)
print("changed:", txt != orig)
print("len DEV=%d CANON=%d" % (len(txt), len(canon)))
print("DEV == canonical(steam) ?", same)
if not same:
    import difflib
    d = list(difflib.unified_diff(canon.split("\n"), txt.split("\n"), "canon", "dev", lineterm=""))
    print("\n".join(d[:60]))
