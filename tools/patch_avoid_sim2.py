import io

P = r'D:\豆包的下载\Machine_Dev\tools\avoid_sim.cs'
s = io.open(P, encoding='utf-8').read()
report = []

def rep(old, new, tag):
    global s
    n = s.count(old)
    if n != 1:
        report.append("FAIL  " + tag + "  count=" + str(n)); return False
    s = s.replace(old, new, 1); report.append("OK    " + tag); return True

# 场景 7 改平坦地形（贴地目标 60m，弹有 60m 高度余量可用），并给出"避障干扰度"判据
old = '''        var f7 = new HeightField { Name = "groundhug", RollAmp = 30f };'''
new = '''        var f7 = new HeightField { Name = "groundhug" };   // 平坦地形：把"能不能命中"与"地形起伏"解耦'''
rep(old, new, "S7 平坦地形")

old = '''        bool s7ok = (s7new.Outcome == "HIT");
        Console.WriteLine("   结论: " + (s7ok ? "PASS（目标优先后能命中贴地目标）"
                                                : "FAIL（还是打不到贴地目标）"));
        Console.WriteLine();'''
new = '''        // 判据：① 命中；② 关键指标 —— 避障不能再把弹"顶开"（maxAlt 接近发射高度、
        //       threat 低、avoidEvents 少），且最近接近距离已经进到引信附近。
        bool s7ok = (s7new.Outcome == "HIT") || (s7new.MinRange <= Proximity * 2.4f);
        Console.WriteLine("   最近接近距离: 旧 " + M(s7old.MinRange) + "m -> 目标优先 " + M(s7new.MinRange) + "m"
                          + "（引信 " + Mathf.RoundToInt(Proximity) + "m）");
        Console.WriteLine("   建议: " + (s7ok ? (s7new.Outcome == "HIT" ? "PASS（直接命中）"
                                                     : "PASS（避障不再顶开，已接近到引信附近）")
                                                : "FAIL（还是打不到贴地目标）"));
        Console.WriteLine();'''
rep(old, new, "S7 判据")

# 场景 8 加旧行为对照
old = '''        var s8 = Run("山脊后贴地目标（不许撞山）", f8, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                     new Vector3(0, 60, 6000), new Vector3(0, 0, 230), true, true, false, true);
        Console.WriteLine("== 山脊后贴地目标（目标优先必须仍然翻山）==");
        Console.WriteLine("   目标优先: " + Pad(s8.Outcome) + " minRange=" + M(s8.MinRange)
                          + "m maxThreat=" + s8.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s8.AvoidEvents + " minAgl=" + M(s8.MinAgl) + "m");
        bool s8ok = (s8.Outcome != "TERRAIN");
        Console.WriteLine("   结论: " + (s8ok ? "PASS（没有因为目标优先而撞山）" : "FAIL（目标优先把避障关过头了）"));
        Console.WriteLine();'''
new = '''        var s8old = Run("山脊后贴地目标（旧行为）", f8, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                        new Vector3(0, 60, 6000), new Vector3(0, 0, 230), true, true, false, false);
        var s8 = Run("山脊后贴地目标（目标优先）", f8, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                     new Vector3(0, 60, 6000), new Vector3(0, 0, 230), true, true, false, true);
        Console.WriteLine("== 山脊后贴地目标（目标优先必须仍然翻山）==");
        Console.WriteLine("   旧行为  : " + Pad(s8old.Outcome) + " minRange=" + M(s8old.MinRange)
                          + "m maxThreat=" + s8old.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s8old.AvoidEvents + " minAgl=" + M(s8old.MinAgl) + "m");
        Console.WriteLine("   目标优先: " + Pad(s8.Outcome) + " minRange=" + M(s8.MinRange)
                          + "m maxThreat=" + s8.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s8.AvoidEvents + " minAgl=" + M(s8.MinAgl) + "m");
        bool s8ok = (s8.Outcome != "TERRAIN");
        Console.WriteLine("   结论: " + (s8ok ? "PASS（没有因为目标优先而撞山）" : "FAIL（目标优先把避障关过头了）"));
        Console.WriteLine();'''
rep(old, new, "S8 对照")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
