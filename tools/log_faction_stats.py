# -*- coding: utf-8 -*-
"""从日志统计阵营 battle rating 相关数据。"""
import re, os, collections

LOGDIR = r'D:\豆包的下载\Aviassembly_DEV\Machine\logs'
files = ['Machine.log.1', 'Machine.log']

re_spawn = re.compile(r'Faction: spawned ([A-Za-z]+)-(\d+) -> ([A-Za-z]+) \(hostile=(\w+)\)')
re_lost = re.compile(r'Faction/points: LOST ([\w\-]+) \[([\w ]+)\] (\w+) (-?\d+) => (-?\d+)')
re_defeat = re.compile(r'Faction/points: (\w+) DEFEATED \((-?\d+)\)')
re_round = re.compile(r'Faction/points: ROUND OVER -> winner=(\w+) \| all factions reset to (\d+) \| next round = (\d+)')
re_kill = re.compile(r'Faction: kill -> (\w+|-) / death -> (\w+|\?) \(K (\d+)\)')
re_ts = re.compile(r'^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)')

spawns = collections.Counter()
spawn_events = []      # (ts, callsign, faction)
losts = []             # (ts, model, reason, faction, before, after)
defeats = []
rounds = []
kills = collections.Counter()

for fn in files:
    p = os.path.join(LOGDIR, fn)
    if not os.path.exists(p):
        continue
    with open(p, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            m = re_ts.match(line)
            ts = m.group(1) if m else '?'
            for mo in re_spawn.finditer(line):
                faction = mo.group(3)
                spawns[faction] += 1
                spawn_events.append((ts, mo.group(1) + '-' + mo.group(2), faction))
            mo = re_lost.search(line)
            if mo:
                losts.append((ts, mo.group(1), mo.group(2), mo.group(3),
                              int(mo.group(4)), int(mo.group(5))))
            mo = re_defeat.search(line)
            if mo:
                defeats.append((ts, mo.group(1), int(mo.group(2))))
            mo = re_round.search(line)
            if mo:
                rounds.append((ts, mo.group(1), int(mo.group(2)), int(mo.group(3))))
            mo = re_kill.search(line)
            if mo:
                kills[mo.group(1)] += 1

print('=' * 70)
print('一、各阵营 AI 刷出次数')
for k, v in spawns.most_common():
    print('   %-8s %d' % (k, v))
print('   合计 %d 次' % sum(spawns.values()))

print()
print('=' * 70)
print('二、各阵营损失次数 / 原因分布')
byfac = collections.Counter()
byreason = collections.defaultdict(collections.Counter)
for ts, model, reason, fac, b, a in losts:
    byfac[fac] += 1
    byreason[fac][reason] += 1
for fac, n in byfac.most_common():
    rs = ', '.join('%s=%d' % (r, c) for r, c in byreason[fac].most_common())
    print('   %-8s 共 %3d 次损失   (%s)' % (fac, n, rs))
print('   合计 %d 次' % sum(byfac.values()))

print()
print('=' * 70)
print('三、DEFEATED / ROUND OVER 时间线')
for ts, name, pts in defeats:
    print('   %s  %-8s DEFEATED (%d)' % (ts, name, pts))
print('   ----')
for ts, winner, base, rnd in rounds:
    print('   %s  ROUND OVER winner=%-8s base=%d round=%d' % (ts, winner, base, rnd))

print()
print('=' * 70)
print('四、Spawn（玩家方）AI 的完整生命周期')
sp = [e for e in spawn_events if e[2] == 'Spawn']
print('   刷出记录 %d 条:' % len(sp))
for ts, cs, fac in sp:
    print('      %s  %s' % (ts, cs))
splost = [l for l in losts if l[3] == 'Spawn']
print('   损失记录 %d 条:' % len(splost))
for ts, model, reason, fac, b, a in splost:
    print('      %s  %-10s [%s]  %d => %d' % (ts, model, reason, b, a))

print()
print('=' * 70)
print('五、击杀数（按击杀方阵营）')
for k, v in kills.most_common():
    print('   %-8s %d' % (k, v))
