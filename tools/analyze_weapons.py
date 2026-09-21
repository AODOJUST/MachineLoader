#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析 Aviassembly DEV 日志，抽取每种武器的可观测交战数据，用于校准 Battle Rating。

日志里 MISSILE HIT 不带武器名，因此无法逐武器归因命中率。
可归因的部分：
  - "X launched <WEAPON> at target, dist=N"  -> 带武器名的发射距离分布
  - "X launched at player, dist=N"          -> AI 发射（当前 aiMissile=R-37），按机型可推断武器
  - "MISSILE HIT target at Xm after Ys speed=Z avoid=N" -> 总体命中表现
"""
import re, sys, statistics as st

LOG = r"D:\豆包的下载\Aviassembly_DEV\Machine\logs\Machine.log"
OUT = r"D:\豆包的下载\Machine_Dev\tools\weapon_stats.txt"

named_launch = {}      # weapon -> [dist,...]
ai_launch = []         # 无武器名的 AI 发射距离
hit_dist = []          # 命中距离
hit_time = []          # 命中用时
hit_speed = []         # 命中速度
hit_avoid = 0          # 命中时触发过避障的次数
lock_dist = []         # 锁定距离（雷达能力，非武器）

re_named = re.compile(r"launched\s+(\S+)\s+at target,\s*dist=(\d+)m")
re_ai    = re.compile(r"launched at player,\s*dist=(\d+)m")
re_hit   = re.compile(r"MISSILE HIT target at (\d+)m after ([\d.]+)s.*?speed=(\d+).*?avoid=(\d+)")
re_lock  = re.compile(r"LOCKED target at (\d+)m")

try:
    f = open(LOG, encoding="utf-8", errors="replace")
except Exception as e:
    print("cannot open log:", e); sys.exit(1)

with f:
    for line in f:
        m = re_named.search(line)
        if m:
            named_launch.setdefault(m.group(1), []).append(int(m.group(2))); continue
        m = re_ai.search(line)
        if m:
            ai_launch.append(int(m.group(1))); continue
        m = re_hit.search(line)
        if m:
            hit_dist.append(int(m.group(1))); hit_time.append(float(m.group(2)))
            hit_speed.append(int(m.group(3)))
            if int(m.group(4)) > 0: hit_avoid += 1
            continue
        m = re_lock.search(line)
        if m:
            lock_dist.append(int(m.group(1)))

def summ(a):
    if not a: return "n=0"
    return "n=%d min=%d med=%d mean=%d max=%d" % (
        len(a), min(a), int(st.median(a)), int(sum(a)/len(a)), max(a))

L = []
L.append("=== Aviassembly 武器交战日志分析 (DEV Machine.log) ===\n")
L.append("-- 带武器名的发射距离 (玩家/自测发射) --")
for w, d in sorted(named_launch.items(), key=lambda x:-len(x[1])):
    L.append("  %-22s %s" % (w, summ(d)))
L.append("")
L.append("-- 无武器名的 AI 发射距离 (默认 aiMissile=R-37) --")
L.append("  %s" % summ(ai_launch))
L.append("")
L.append("-- 总体 MISSILE HIT 表现 (日志不带武器名, 不能逐武器归因) --")
L.append("  命中距离 %s" % summ(hit_dist))
L.append("  命中用时 %s" % summ([int(x*10) for x in hit_time]))
L.append("  命中速度 %s" % summ(hit_speed))
L.append("  命中时触发过避障: %d / %d (%.0f%%)" % (hit_avoid, len(hit_dist), 100*hit_avoid/max(1,len(hit_dist))))
L.append("")
L.append("-- 雷达锁定距离 (FactionSystem 雷达能力, 非武器) --")
L.append("  %s" % summ(lock_dist))
L.append("")
L.append("注: 逐武器命中率无法从本日志归因 (MISSILE HIT 无武器名)；")
L.append("    BR 主值来自武器固有参数(速度×寿命=有效射程)+速度档, 日志用于校准/验证。")

txt = "\n".join(L)
open(OUT, "w", encoding="utf-8").write(txt)
print(txt)
