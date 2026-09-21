# -*- coding: utf-8 -*-
import re, os

def scan(path, label, patterns):
    t = open(path, encoding="utf-8", errors="ignore").read()
    print("==== " + label + " ====")
    for pat in patterns:
        try:
            for m in re.finditer(pat, t):
                print(" ", m.group(0)[:130].replace("\n", " "))
        except Exception as e:
            print("  pat err", e)

# VoiceAlerts 播放调用与触发上下文
scan(r"D:\豆包的下载\Machine_Dev\src\Mods\VoiceAlerts.cs", "VoiceAlerts",
     [r'Play\("[a-z0-9_]+"', r'// [A-Z][A-Z /]+：', r'Play\("[a-z0-9_]+", [\d.]+f\); //[^\n]*'])

print()
# 其他 mod 对 VoiceAlerts 的调用（反射/直调）
for f in ["Radar.cs", "MachineAAM.cs", "GMeter.cs", "BattleHold.cs", "FactionSystem.cs", "KillFeed.cs", "GVision.cs"]:
    p = os.path.join(r"D:\豆包的下载\Machine_Dev\src\Mods", f)
    if not os.path.exists(p):
        continue
    t = open(p, encoding="utf-8", errors="ignore").read()
    hits = []
    for pat in [r'VoiceAlertsApi\.\w+\([^)]*\)', r'"fox1"', r'"lock"', r'"missile"', r'"rwr_low"', r'"rwr_mid"', r'"rwr_high"', r'"overspeed"', r'"overg"', r'"bingo"', r'"fuellow"', r'PlayLaunchSfx\(\)']:
        for m in re.finditer(pat, t):
            s = m.group(0)
            if s not in hits:
                hits.append(s)
    if hits:
        print("==== " + f + " ====")
        for h in hits:
            print(" ", h[:120])
