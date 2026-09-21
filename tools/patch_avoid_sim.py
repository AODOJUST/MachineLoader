import io

P = r'D:\豆包的下载\Machine_Dev\tools\avoid_sim.cs'
s = io.open(P, encoding='utf-8').read()
orig = s
report = []

def rep(old, new, tag):
    global s
    n = s.count(old)
    if n != 1:
        report.append("FAIL  " + tag + "  count=" + str(n)); return False
    s = s.replace(old, new, 1); report.append("OK    " + tag); return True

# 1) Run 重载：加 targetPriority
old = '''    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
                      bool avoid, bool impactDetonate)
    {
        return Run(name, f, pos0, vel0, tgt, tVel, avoid, impactDetonate, false);
    }

    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
                      bool avoid, bool impactDetonate, bool trace)
    {'''
new = '''    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
                      bool avoid, bool impactDetonate)
    {
        return Run(name, f, pos0, vel0, tgt, tVel, avoid, impactDetonate, false, false);
    }

    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
                      bool avoid, bool impactDetonate, bool trace)
    {
        return Run(name, f, pos0, vel0, tgt, tVel, avoid, impactDetonate, trace, false);
    }

    /// <summary>
    /// targetPriority = true 时启用生产代码的新行为（2026-09-15）：
    ///   把"到目标的距离"交给 TerrainAvoid.Compute —— 目标优先（视距截断 + LOS 外推 + 终端让步）。
    ///   false = 旧行为（纯速度外推），用来做"修前 / 修后"对照。
    /// </summary>
    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
                      bool avoid, bool impactDetonate, bool trace, bool targetPriority)
    {'''
rep(old, new, "S1 Run 重载")

# 2) 目标距离 + Compute 调用
old = '''            float range = Vector3.Distance(pos, tp);
            if (range < r.MinRange) r.MinRange = range;
            if (range < Proximity) { r.Outcome = "HIT"; break; }'''
new = '''            float range = Vector3.Distance(pos, tp);
            if (range < r.MinRange) r.MinRange = range;
            if (range < Proximity) { r.Outcome = "HIT"; break; }
            // 与 MissileController.FixedUpdate 一致：把目标距离（目标优先）传给避障
            float tgtRange = targetPriority ? range : float.MaxValue;'''
rep(old, new, "S2 tgtRange")

old = '''                    avoidDir = TerrainAvoid.Compute(probe, pos, vel, MaxG * 9.81f, p, threat, out newThreat, out agl);'''
new = '''                    avoidDir = TerrainAvoid.Compute(probe, pos, vel, MaxG * 9.81f, p, threat, tp, tgtRange,
                                                    out newThreat, out agl);'''
rep(old, new, "S3 Compute 调用")

old = '''                    if (probe.Blocked(pos, vel.normalized, vel.magnitude * Dt + p.Nose))
                    {
                        r.Outcome = "TERRAIN";
                        r.ImpactAlt = pos.y;
                        break;
                    }
                }
            }
            else'''
new = '''                    float step = vel.magnitude * Dt + p.Nose;
                    if (tgtRange < float.MaxValue) step = Math.Min(step, Math.Max(2f, tgtRange - 1f));
                    if (probe.Blocked(pos, vel.normalized, step))
                    {
                        r.Outcome = "TERRAIN";
                        r.ImpactAlt = pos.y;
                        break;
                    }
                }
            }
            else'''
rep(old, new, "S4 撞地射线")

# 3) 新增场景 7 / 8
old = '''        Console.WriteLine("done");
    }
}'''
new = '''        // =====================================================================
        // 场景 7（2026-09-15 用户反馈回归）：尾追**贴地目标**不许被避障顶开
        //   用户原话："导弹AI在攻击贴地目标时会触发规避，导致无法攻击到目标"。
        //   根因：旧代码把"当前速度直线"外推到 0.6×视距（≈1240m）处算自身高度 ——
        //   打贴地目标时这条外推线在目标之前就扎到地面以下，于是明明马上命中，却被判成
        //   "马上撞地" -> 满舵抬升 + PN 让位 85% -> 脱靶。
        //   修复：视距截到目标距离 + 山脊剖面按"我->目标"直线外推 + 终端段威胁打折。
        // =====================================================================
        var f7 = new HeightField { Name = "groundhug", RollAmp = 30f };
        Vector3 m7p = new Vector3(0, 900, 0), m7v = new Vector3(0, 0, 900);
        Vector3 t7p = new Vector3(0, 60, 5000), t7v = new Vector3(0, 0, 230);
        var s7old = Run("贴地目标尾追（旧行为）", f7, m7p, m7v, t7p, t7v, true, true, false, false);
        var s7new = Run("贴地目标尾追（目标优先）", f7, m7p, m7v, t7p, t7v, true, true, false, true);
        Console.WriteLine("== 贴地目标尾追（靶 60m，弹 900m 平飞起步）==");
        Console.WriteLine("   旧行为  : " + Pad(s7old.Outcome) + " minRange=" + M(s7old.MinRange)
                          + "m maxThreat=" + s7old.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s7old.AvoidEvents + " maxAlt=" + M(s7old.MaxAlt) + "m");
        Console.WriteLine("   目标优先: " + Pad(s7new.Outcome) + " minRange=" + M(s7new.MinRange)
                          + "m maxThreat=" + s7new.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s7new.AvoidEvents + " maxAlt=" + M(s7new.MaxAlt) + "m");
        bool s7ok = (s7new.Outcome == "HIT");
        Console.WriteLine("   结论: " + (s7ok ? "PASS（目标优先后能命中贴地目标）"
                                                : "FAIL（还是打不到贴地目标）"));
        Console.WriteLine();

        // 场景 8：贴地目标**躲在 900m 山脊后面** —— 目标优先不许把真正的避障关掉
        var f8 = new HeightField { Name = "hug-behind-ridge", RidgeAmp = 900f, RidgeZ = 2800f, RidgeSigma = 600f };
        var s8 = Run("山脊后贴地目标（不许撞山）", f8, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                     new Vector3(0, 60, 6000), new Vector3(0, 0, 230), true, true, false, true);
        Console.WriteLine("== 山脊后贴地目标（目标优先必须仍然翻山）==");
        Console.WriteLine("   目标优先: " + Pad(s8.Outcome) + " minRange=" + M(s8.MinRange)
                          + "m maxThreat=" + s8.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s8.AvoidEvents + " minAgl=" + M(s8.MinAgl) + "m");
        bool s8ok = (s8.Outcome != "TERRAIN");
        Console.WriteLine("   结论: " + (s8ok ? "PASS（没有因为目标优先而撞山）" : "FAIL（目标优先把避障关过头了）"));
        Console.WriteLine();

        Console.WriteLine("done");
    }
}'''
rep(old, new, "S5 场景 7/8")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig)
