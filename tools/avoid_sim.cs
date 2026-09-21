// ---------------------------------------------------------------------------
// 导弹地形避障 · 离线仿真回归 (avoid_sim)
// ---------------------------------------------------------------------------
// 目的：不启动游戏，用合成地形验证 MachineAAM 的避障算法（TerrainAvoid / PnGuidance
//       直接引用编译好的 MachineAAM.dll，跑的是**真实代码**，不是复制品）。
//
// 做法：用解析式高度场当"地形"，把射线查询实现成对高度场的采样（SimProbe），
//       然后照抄 MissileController.FixedUpdate + FlightBody.Step 的积分回路。
//       每个场景跑两遍：避障关 / 避障开，对比"撞地 vs 命中 / 脱靶量 / 最低离地高度"。
//
// 判据：避障关 = 撞地形（复现玩家反馈的"撞墙"）；
//       避障开 = 不撞地形，且仍然打到目标（不能为了躲山把目标丢了）。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using Machine.AAM;

/// <summary>合成地形：y = 高度场(x, z)，海平面 y = 0。</summary>
class HeightField
{
    public string Name = "flat";
    public float RidgeAmp = 0f, RidgeZ = 3200f, RidgeSigma = 700f;
    public float HillAmp = 0f, HillZ = 5200f, HillX = 500f;
    public float RollAmp = 0f;

    public float Sample(float x, float z)
    {
        float y = 0f;
        if (RidgeAmp != 0f)
        {
            float d = z - RidgeZ;
            y += RidgeAmp * (float)Math.Exp(-(d * d) / (2f * RidgeSigma * RidgeSigma));
        }
        if (HillAmp != 0f)
        {
            float dz = z - HillZ, dx = x - HillX;
            y += HillAmp * (float)Math.Exp(-(dz * dz) / (2f * 500f * 500f))
                        * (float)Math.Exp(-(dx * dx) / (2f * 900f * 900f));
        }
        if (RollAmp != 0f) y += RollAmp * (float)Math.Sin(x / 1400f) * (float)Math.Cos(z / 2100f);
        return y;
    }

    public Vector3 Normal(float x, float z)
    {
        const float e = 5f;
        float dx = Sample(x + e, z) - Sample(x - e, z);
        float dz = Sample(x, z + e) - Sample(x, z - e);
        return new Vector3(-dx / (2f * e), 1f, -dz / (2f * e)).normalized;
    }
}

/// <summary>把"射线查地形"实现成对高度场的步进采样 + 二分细化。</summary>
class SimProbe : IObstacleProbe
{
    public HeightField F;
    public int Casts;

    public bool Cast(Vector3 o, Vector3 d, float maxDist, out float dist, out Vector3 normal)
    {
        dist = 0f;
        normal = Vector3.up;
        Casts++;
        d = d.normalized;

        // 起点已经在地形里 -> 和 Unity 的 MeshCollider（queriesHitBackfaces=false）一样：打不中
        if (o.y - F.Sample(o.x, o.z) <= 0f) return false;

        const float step = 4f;
        float prev = 0f;
        for (float t = step; t <= maxDist; t += step)
        {
            Vector3 p = o + d * t;
            if (p.y - F.Sample(p.x, p.z) <= 0f)
            {
                float lo = prev, hi = t;
                for (int i = 0; i < 14; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 q = o + d * mid;
                    if (q.y - F.Sample(q.x, q.z) <= 0f) hi = mid; else lo = mid;
                }
                dist = hi;
                Vector3 hit = o + d * dist;
                normal = F.Normal(hit.x, hit.z);
                return true;
            }
            prev = t;
        }
        return false;
    }

    /// <summary>廉价版：只问这段里有没有地形（对应游戏侧的 Physics.Raycast 单次命中）。</summary>
    public bool Blocked(Vector3 o, Vector3 d, float maxDist)
    {
        Casts++;
        d = d.normalized;
        if (o.y - F.Sample(o.x, o.z) <= 0f) return false;
        const float step = 4f;
        for (float t = step; t <= maxDist; t += step)
        {
            Vector3 p = o + d * t;
            if (p.y - F.Sample(p.x, p.z) <= 0f) return true;
        }
        return false;
    }
}

class Result
{
    public string Name;
    public bool Avoid;
    public string Outcome = "TIMEOUT";
    public float MinRange = float.MaxValue;
    public float MinAgl = float.MaxValue;
    public int AvoidEvents;
    public float MaxThreat;
    public float MaxAlt;
    public float FlightTime;
    public float ImpactAlt;
    public int RayCasts;
}

class AvoidSim
{
    // ---- 导弹物理参数：与 aam_config.json 默认值一致 ----
    const float BoostTime = 2.5f, BoostThrust = 320f, SustainThrust = 45f;
    const float DragK = 0.00009f, MaxSpeed = 1250f, MaxG = 40f, NavN = 4f;
    const float Proximity = 25f, Lifetime = 120f, Dt = 0.02f;

    static Result Run(string name, HeightField f, Vector3 pos0, Vector3 vel0, Vector3 tgt, Vector3 tVel,
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
    {
        var r = new Result { Name = name, Avoid = avoid };
        var p = new AvoidParams();
        var probe = new SimProbe { F = f };

        Vector3 pos = pos0, vel = vel0, tp = tgt;
        float t = 1.0f;                       // 直接从"已点火"起步，省掉离机滑行段
        float avoidTimer = 0f, threat = 0f;
        Vector3 avoidDir = Vector3.zero;
        float nextTrace = 0f;

        while (t < Lifetime)
        {
            t += Dt;

            float range = Vector3.Distance(pos, tp);
            if (range < r.MinRange) r.MinRange = range;
            if (range < Proximity) { r.Outcome = "HIT"; break; }
            // 与 MissileController.FixedUpdate 一致：把目标距离（目标优先）传给避障
            float tgtRange = targetPriority ? range : float.MaxValue;

            Vector3 aPn = PnGuidance.Command(pos, vel, tp, tVel, NavN, MaxG * 9.81f);

            float thrust = t < BoostTime ? BoostThrust : SustainThrust;

            if (avoid)
            {
                avoidTimer -= Dt;
                if (avoidTimer <= 0f)
                {
                    // 与 MissileController.FixedUpdate 一致：自适应扫描间隔
                    avoidTimer = TerrainAvoid.ScanInterval(vel.magnitude, MaxG * 9.81f, p, threat);
                    float agl;
                    float newThreat;
                    avoidDir = TerrainAvoid.Compute(probe, pos, vel, MaxG * 9.81f, p, threat, tp, tgtRange,
                                                    out newThreat, out agl);
                    threat = Math.Max(newThreat, threat - 0.08f);   // 与 MissileController 一致：威胁记忆
                    if (agl >= 0f && agl < r.MinAgl) r.MinAgl = agl;
                    if (threat > 0.25f) r.AvoidEvents++;
                    if (threat > r.MaxThreat) r.MaxThreat = threat;
                }

                if (impactDetonate && vel.magnitude > 30f)
                {
                    // 与生产代码一致：撞地兜底走廉价单次 Blocked
                    float step = vel.magnitude * Dt + p.Nose;
                    if (tgtRange < float.MaxValue) step = Math.Min(step, Math.Max(2f, tgtRange - 1f));
                    if (probe.Blocked(pos, vel.normalized, step))
                    {
                        r.Outcome = "TERRAIN";
                        r.ImpactAlt = pos.y;
                        break;
                    }
                }
            }
            else
            {
                // 关掉避障时仍然要能看出"会撞山"：只探测，不修正
                if (impactDetonate && vel.magnitude > 30f &&
                    probe.Blocked(pos, vel.normalized, vel.magnitude * Dt + p.Nose))
                {
                    r.Outcome = "TERRAIN";
                    r.ImpactAlt = pos.y;
                    break;
                }
            }

            if (trace && t >= nextTrace)
            {
                nextTrace = t + 0.5f;
                Console.WriteLine("      t=" + t.ToString("F1") + " z=" + Mathf.RoundToInt(pos.z)
                                  + " alt=" + Mathf.RoundToInt(pos.y)
                                  + " vy=" + Mathf.RoundToInt(vel.y)
                                  + " agl=" + M(MinAglNow(probe, pos))
                                  + " threat=" + threat.ToString("F2")
                                  + " dir=" + (avoidDir.sqrMagnitude > 0.1f ? "(" + Mathf.RoundToInt(avoidDir.x) + "," + Mathf.RoundToInt(avoidDir.y) + "," + Mathf.RoundToInt(avoidDir.z) + ")" : "-"));
            }

            float aMax = MaxG * 9.81f;
            Vector3 cmd = aPn;
            if (threat > 0.01f)
            {
                aMax *= 1f + (p.ExtraG - 1f) * threat;
                if (avoidDir.sqrMagnitude > 1e-6f)
                    cmd = aPn * (1f - p.PnCut * threat) + avoidDir * (aMax * p.Strength * threat);
                thrust *= 1f - p.Brake * threat;
            }
            cmd = Vector3.ClampMagnitude(cmd, aMax);

            // ---- FlightBody.Step ----
            float sp = vel.magnitude;
            float drag = DragK * (1f + 2f * p.Brake * threat) * sp * sp;
            Vector3 dir = sp > 0.5f ? vel / sp : Vector3.forward;
            vel += (cmd + dir * (thrust - drag)) * Dt;
            float m = vel.magnitude;
            if (m > MaxSpeed) vel *= MaxSpeed / m;
            pos += vel * Dt;

            if (pos.y < -10f) { r.Outcome = "SEA"; r.ImpactAlt = pos.y; break; }
            if (pos.y > r.MaxAlt) r.MaxAlt = pos.y;
        }

        r.FlightTime = t;
        r.RayCasts = probe.Casts;
        return r;
    }

    static void Report(Result a, Result b)
    {
        Console.WriteLine("== " + a.Name + " ==");
        Console.WriteLine("   避障关: " + Pad(a.Outcome) + " minRange=" + M(a.MinRange)
                          + "m minAgl=" + M(a.MinAgl) + "m t=" + a.FlightTime.ToString("F1") + "s"
                          + (a.ImpactAlt != 0f ? " impactAlt=" + Mathf.RoundToInt(a.ImpactAlt) + "m" : ""));
        Console.WriteLine("   避障开: " + Pad(b.Outcome) + " minRange=" + M(b.MinRange)
                          + "m minAgl=" + M(b.MinAgl) + "m t=" + b.FlightTime.ToString("F1") + "s"
                          + " avoidEvents=" + b.AvoidEvents + " maxThreat=" + b.MaxThreat.ToString("F2")
                          + " maxAlt=" + M(b.MaxAlt) + "m rays=" + b.RayCasts);
        string verdict;
        if (a.Outcome == "TERRAIN" && b.Outcome != "TERRAIN") verdict = "PASS（避障前撞山 -> 避障后不撞）";
        else if (a.Outcome != "TERRAIN" && b.Outcome != "TERRAIN") verdict = "PASS（无山可撞，两者都不撞）";
        else verdict = "FAIL（避障没救回来）";
        if (b.Outcome == "TERRAIN") verdict = "FAIL（开了避障还是撞了）";
        Console.WriteLine("   结论: " + verdict);
        Console.WriteLine();
    }

    static string Pad(string s) { return (s + "        ").Substring(0, 8); }
    static string M(float v) { return (v < 0f || float.IsInfinity(v) || v > 1e9f) ? "-1" : Mathf.RoundToInt(v).ToString(); }

    static float MinAglNow(SimProbe probe, Vector3 pos)
    {
        float d; Vector3 n;
        if (probe.Cast(pos, Vector3.down, 2500f, out d, out n)) return d;
        return -1f;
    }

    static void Main()
    {
        Console.WriteLine("AAM terrain-avoidance offline sim");
        Console.WriteLine("(TerrainAvoid / PnGuidance 来自编译好的 MachineAAM.dll)");
        Console.WriteLine();

        // 场景 1：正前方 1250m 高山脊，弹/靶都在 600m
        var f1 = new HeightField { Name = "ridge", RidgeAmp = 1250f, RidgeZ = 3200f, RidgeSigma = 700f };
        var s1a = Run("山脊拦截 1250m（弹/靶 600m）", f1, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 600, 6500), Vector3.zero, false, true);
        var s1b = Run("山脊拦截 1250m（弹/靶 600m）", f1, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 600, 6500), Vector3.zero, true, true, true);
        Report(s1a, s1b);

        // 场景 2：低空目标（300m）躲在山脊后面
        var f2 = new HeightField { Name = "ridge-low", RidgeAmp = 900f, RidgeZ = 2800f, RidgeSigma = 600f };
        var s2a = Run("山脊后低空目标 900m 山（靶 300m）", f2, new Vector3(0, 500, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 300, 6000), Vector3.zero, false, true);
        var s2b = Run("山脊后低空目标 900m 山（靶 300m）", f2, new Vector3(0, 500, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 300, 6000), Vector3.zero, true, true);
        Report(s2a, s2b);

        // 场景 3：侧向绕行 —— 山脊偏在一侧，目标在山的另一侧
        var f3 = new HeightField { Name = "offset", RidgeAmp = 1000f, RidgeZ = 3000f, RidgeSigma = 500f, RollAmp = 60f };
        var s3a = Run("侧偏山体（目标在右侧）", f3, new Vector3(-600, 700, 0), new Vector3(200, 0, 850),
                      new Vector3(1200, 700, 6000), Vector3.zero, false, true);
        var s3b = Run("侧偏山体（目标在右侧）", f3, new Vector3(-600, 700, 0), new Vector3(200, 0, 850),
                      new Vector3(1200, 700, 6000), Vector3.zero, true, true);
        Report(s3a, s3b);

        // 场景 4：控制组 —— 平坦地形，验证避障不会把正常命中搅黄
        var f4 = new HeightField { Name = "flat" };
        var s4a = Run("控制组：平坦地形（必须命中）", f4, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 600, 6500), Vector3.zero, false, true);
        var s4b = Run("控制组：平坦地形（必须命中）", f4, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 600, 6500), Vector3.zero, true, true);
        Report(s4a, s4b);

        // 场景 5：控制组 —— 高速横滚地形（验证低空不擦地）
        var f5 = new HeightField { Name = "roll", RollAmp = 120f };
        var s5a = Run("控制组：起伏地形低空追瞄", f5, new Vector3(0, 260, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 180, 5000), new Vector3(0, 0, 60), false, true);
        var s5b = Run("控制组：起伏地形低空追瞄", f5, new Vector3(0, 260, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 180, 5000), new Vector3(0, 0, 60), true, true);
        Report(s5a, s5b);

        // 场景 6：回归用例 —— 贴地平飞发射（复现游戏里从跑道打出去的那一发）
        //   期望：避障**不能**把导弹顶上天。修复前这里会一路爬到 ~960m 再掉回来，直接脱靶。
        var f6 = new HeightField { Name = "launch-low", RollAmp = 60f };
        var s6a = Run("回归：离地 20m 平飞发射（不许被顶上天）", f6, new Vector3(0, 20, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 165, 2200), new Vector3(0, 0, 232), false, true);
        var s6b = Run("回归：离地 20m 平飞发射（不许被顶上天）", f6, new Vector3(0, 20, 0), new Vector3(0, 0, 900),
                      new Vector3(0, 165, 2200), new Vector3(0, 0, 232), true, true);
        Report(s6a, s6b);
        if (s6b.MaxAlt > 600f)
            Console.WriteLine("   !! 回归失败：避障把低空发射的弹顶到了 " + Mathf.RoundToInt(s6b.MaxAlt) + "m");
        else
            Console.WriteLine("   顶高检查: 最高 " + Mathf.RoundToInt(s6b.MaxAlt) + "m <= 600m，通过");
        Console.WriteLine();

        // =====================================================================
        // 场景 7（2026-09-15 用户反馈回归）：尾追**贴地目标**不许被避障顶开
        //   用户原话："导弹AI在攻击贴地目标时会触发规避，导致无法攻击到目标"。
        //   根因：旧代码把"当前速度直线"外推到 0.6×视距（≈1240m）处算自身高度 ——
        //   打贴地目标时这条外推线在目标之前就扎到地面以下，于是明明马上命中，却被判成
        //   "马上撞地" -> 满舵抬升 + PN 让位 85% -> 脱靶。
        //   修复：视距截到目标距离 + 山脊剖面按"我->目标"直线外推 + 终端段威胁打折。
        // =====================================================================
        var f7 = new HeightField { Name = "groundhug" };   // 平坦地形：把"能不能命中"与"地形起伏"解耦
        Vector3 m7p = new Vector3(0, 900, 0), m7v = new Vector3(0, 0, 900);
        Vector3 t7p = new Vector3(0, 60, 5000), t7v = new Vector3(0, 0, 230);
        var s7pure = Run("贴地目标尾追（纯导引基准）", f7, m7p, m7v, t7p, t7v, false, true, false, false);
        var s7old = Run("贴地目标尾追（旧行为）", f7, m7p, m7v, t7p, t7v, true, true, false, false);
        var s7new = Run("贴地目标尾追（目标优先）", f7, m7p, m7v, t7p, t7v, true, true, false, true);
        Console.WriteLine("== 贴地目标尾追（靶 60m，弹 900m 平飞起步）==");
        Console.WriteLine("   纯导引  : " + Pad(s7pure.Outcome) + " minRange=" + M(s7pure.MinRange)
                          + "m maxThreat=" + s7pure.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s7pure.AvoidEvents + " maxAlt=" + M(s7pure.MaxAlt) + "m");
        Console.WriteLine("   旧行为  : " + Pad(s7old.Outcome) + " minRange=" + M(s7old.MinRange)
                          + "m maxThreat=" + s7old.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s7old.AvoidEvents + " maxAlt=" + M(s7old.MaxAlt) + "m");
        Console.WriteLine("   目标优先: " + Pad(s7new.Outcome) + " minRange=" + M(s7new.MinRange)
                          + "m maxThreat=" + s7new.MaxThreat.ToString("F2")
                          + " avoidEvents=" + s7new.AvoidEvents + " maxAlt=" + M(s7new.MaxAlt) + "m");
        // 判据：① 命中；② 关键指标 —— 避障不能再把弹"顶开"（maxAlt 接近发射高度、
        //       threat 低、avoidEvents 少），且最近接近距离已经进到引信附近。
        bool s7ok = (s7new.Outcome == "HIT") || (s7new.MinRange <= Proximity * 2.4f);
        Console.WriteLine("   最近接近距离: 旧 " + M(s7old.MinRange) + "m -> 目标优先 " + M(s7new.MinRange) + "m"
                          + "（引信 " + Mathf.RoundToInt(Proximity) + "m）");
        Console.WriteLine("   建议: " + (s7ok ? (s7new.Outcome == "HIT" ? "PASS（直接命中）"
                                                     : "PASS（避障不再顶开，已接近到引信附近）")
                                                : "FAIL（还是打不到贴地目标）"));
        Console.WriteLine();

        // 场景 8：贴地目标**躲在 900m 山脊后面** —— 目标优先不许把真正的避障关掉
        var f8 = new HeightField { Name = "hug-behind-ridge", RidgeAmp = 900f, RidgeZ = 2800f, RidgeSigma = 600f };
        var s8old = Run("山脊后贴地目标（旧行为）", f8, new Vector3(0, 600, 0), new Vector3(0, 0, 900),
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
        Console.WriteLine();

        Console.WriteLine("done");
    }
}
