# -*- coding: utf-8 -*-
import io, re, sys

log = u"D:\豆包的下载\Aviassembly_DEV\Machine\logs\Machine.log"
try:
    data = io.open(log, "r", encoding="utf-8", errors="ignore").read()
except Exception as e:
    print("ERR", e)
    sys.exit(1)

lines = data.splitlines()
def cnt(pat):
    return sum(1 for l in lines if pat in l)

print("underground:", cnt("underground"))
print("spawned_total:", cnt("Faction: spawned"))
print("kills:", cnt("destroyed by missile"))
print("crashed:", cnt("crashed, retired"))
print("firing:", cnt("AAM: firing"))
print("--- spawned (first 6):")
for l in lines:
    if "Faction: spawned" in l:
        print(l)
        if sum(1 for x in lines if "Faction: spawned" in x and lines.index(x) <= lines.index(l)) >= 6:
            break
print("--- kills (first 4):")
n = 0
for l in lines:
    if "destroyed by missile" in l:
        print(l)
        n += 1
        if n >= 4: break
