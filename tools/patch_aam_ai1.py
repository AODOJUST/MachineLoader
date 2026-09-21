import io, sys

P = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(P, encoding='utf-8').read()
orig = s
report = []

def rep(old, new, tag, count=1):
    global s
    n = s.count(old)
    if n < 1:
        report.append("MISS  " + tag)
        return False
    if n != count:
        report.append("WARN  " + tag + " count=" + str(n))
    s = s.replace(old, new, count)
    report.append("OK    " + tag)
    return True

# ---------------- P1: AvoidParams 新增"目标优先"参数 ----------------
old = '''        public float ProfileSpan = 300f;    // avoidProfileSpan：山脊剖面"缺多少米算满威胁"
        public float ProfileMargin = 20f;   // avoidProfileMargin：剖面判撞的余量（不是安全高度！）
    }'''
new = '''        public float ProfileSpan = 300f;    // avoidProfileSpan：山脊剖面"缺多少米算满威胁"
        public float ProfileMargin = 20f;   // avoidProfileMargin：剖面判撞的余量（不是安全高度！）
        // ---- 目标优先（2026-09-15）----
        // 打贴地 / 低空目标时，"为了不撞地而抬升"会把弹顶离目标，一路抬升就永远够不着
        // （用户实测："导弹攻击贴地目标时会触发规避，导致无法攻击到目标"）。
        // 修法是让**目标本身**成为避障的边界：
        //   · 探针视距 / 剖面采样点一律不超过"到目标的距离" —— 打到目标之后的地形与我无关；
        //   · 进入 TerminalRange 后威胁度按比例打折（TerminalMin 保底），把控制权交回比例导引，
        //     保证真的能落到目标上（而不是擦着目标飞过去）。
        public float TerminalRange = 1600f; // avoidTerminalRange：进入该距离开始让位给导引（m）
        public float TerminalMin = 0.30f;   // avoidTerminalMin：终端段保留的避障权重下限（0~1）
    }'''
rep(old, new, "P1 AvoidParams 目标优先参数")

# ---------------- P2: TerrainAvoid.Compute 目标优先 ----------------
old = '''        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, out float threat, out float agl)
        {
            threat = 0f;
            agl = -1f;
            if (probe == null || p == null || !p.Enabled) return Vector3.zero;
'''
new = '''        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, out float threat, out float agl)
        {
            return Compute(probe, pos, vel, maxLatAccel, p, priorThreat, float.MaxValue, out threat, out agl);
        }

        /// <summary>
        /// 同上，但带"到目标的距离"（targetRange）：**目标优先** ——
        /// 打到目标之后的地形不参与避障。这是打贴地目标打不中的根因修法：
        /// 导弹的直线外推在瞄一个贴地目标时必然"穿过地面"（目标就踩在地上），
        /// 旧逻辑据此判定"马上要撞地" → 一路抬升 → 永远够不着目标。
        /// </summary>
        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, float targetRange,
                                      out float threat, out float agl)
        {
            threat = 0f;
            agl = -1f;
            if (probe == null || p == null || !p.Enabled) return Vector3.zero;
'''
rep(old, new, "P2a Compute 重载头")

old = '''            // 只按 1.6s 算（900m/s 时 1440m）会短于 R（2066m）—— 于是"看见山脊时已经拐不过去了"，
            // 表现为开了避障还是削到山脊。视距至少要够一个转弯半径。
            float look = LookDistance(spd, maxLatAccel, p);
'''
new = '''            // 只按 1.6s 算（900m/s 时 1440m）会短于 R（2066m）—— 于是"看见山脊时已经拐不过去了"，
            // 表现为开了避障还是削到山脊。视距至少要够一个转弯半径。
            float look = LookDistance(spd, maxLatAccel, p);

            // ---- 目标优先：把视距截到"到目标的距离"，并算出终端段的让步系数 ----
            float commit = 1f;
            if (targetRange > 1f && targetRange < float.MaxValue)
            {
                look = Mathf.Min(look, Mathf.Max(60f, targetRange));
                if (targetRange < p.TerminalRange)
                    commit = Mathf.Lerp(Mathf.Max(0f, p.TerminalMin), 1f,
                                        Mathf.Clamp01(targetRange / Mathf.Max(1f, p.TerminalRange)));
            }
'''
rep(old, new, "P2b 视距截断 + commit")

old = '''            threat = Mathf.Clamp01(worst);
            if (acc.sqrMagnitude < 1e-6f) return Vector3.zero;'''
new = '''            // 终端段（贴着目标）把威胁度打折：目标优先，宁可冒一点擦地风险也要把弹送到目标上
            threat = Mathf.Clamp01(worst * commit);
            if (acc.sqrMagnitude < 1e-6f) return Vector3.zero;'''
rep(old, new, "P2c threat * commit")

# ---------------- P3: FixedUpdate 传入目标距离 + 撞地判定不越过目标 ----------------
old = '''            Vector3 aCmd = Vector3.zero;
            float range = float.MaxValue;
            if (UseTarget && Target != null && Target.Valid)
            {
                Vector3 tp = Target.Position;
                range = Vector3.Distance(Body.Pos, tp);
'''
new = '''            Vector3 aCmd = Vector3.zero;
            float range = float.MaxValue;
            float tgtRange = float.MaxValue;    // 目标距离：避障"目标优先"的边界（打不到目标之后的地形与我无关）
            if (UseTarget && Target != null && Target.Valid)
            {
                Vector3 tp = Target.Position;
                range = Vector3.Distance(Body.Pos, tp);
                tgtRange = range;
'''
rep(old, new, "P3a tgtRange 变量")

old = '''                    _avoidDir = TerrainAvoid.Compute(Probe, Body.Pos, Body.Vel, MaxG * 9.81f, Avoid,
                                                     _avoidThreat, out newThreat, out agl);'''
new = '''                    _avoidDir = TerrainAvoid.Compute(Probe, Body.Pos, Body.Vel, MaxG * 9.81f, Avoid,
                                                     _avoidThreat, tgtRange, out newThreat, out agl);'''
rep(old, new, "P3b 传 tgtRange")

old = '''                    float stepLen = Body.Speed * dt + Avoid.Nose;
                    if (Probe.Blocked(Body.Pos, Body.Vel.normalized, stepLen))'''
new = '''                    float stepLen = Body.Speed * dt + Avoid.Nose;
                    // 撞地判定也不许越过目标：目标在 30m 外而本帧要走 25m 时，打到的那块地
                    // 不是"撞山"而是"马上会命中" -> 交给近炸引信，别在这里判死。
                    float rayLen = (tgtRange < float.MaxValue)
                                   ? Mathf.Min(stepLen, Mathf.Max(2f, tgtRange - 1f)) : stepLen;
                    if (Probe.Blocked(Body.Pos, Body.Vel.normalized, rayLen))'''
rep(old, new, "P3c 撞地射线截到目标距离")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig, "len", len(orig), "->", len(s))
