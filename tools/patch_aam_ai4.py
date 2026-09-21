import io

P = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(P, encoding='utf-8').read()
orig = s
report = []

def rep(old, new, tag):
    global s
    n = s.count(old)
    if n != 1:
        report.append("FAIL  " + tag + "  count=" + str(n))
        return False
    s = s.replace(old, new, 1)
    report.append("OK    " + tag)
    return True

# ---- R1: Compute 增加 targetPos（LOS 外推用）----
old = '''        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
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
                                      out float threat, out float agl)'''
new = '''        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, out float threat, out float agl)
        {
            return Compute(probe, pos, vel, maxLatAccel, p, priorThreat, Vector3.zero, float.MaxValue,
                           out threat, out agl);
        }

        /// <summary>
        /// 同上，但带**目标**（targetPos / targetRange）：目标优先 —— 这是"打贴地目标打不中"的根因修法。
        ///   ① 探针视距 / 剖面采样点一律不超过"到目标的距离"：打到目标之后的地形与我无关；
        ///   ② 山脊剖面的"我在那一点的高度"改用**我→目标这条直线**外推，而不是用当前速度直线外推
        ///      —— 速度外推在尾追/俯冲末段会一路算到地面以下（当前下沉率比需要的平均下沉率更陡），
        ///      于是"马上要命中"被误判成"马上要撞地" → 抬升 → 脱靶；
        ///   ③ 进入 TerminalRange 后威胁度按比例打折，把控制权交回比例导引。
        /// targetRange >= float.MaxValue 表示没有目标（按旧的纯速度外推走）。
        /// </summary>
        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, Vector3 targetPos, float targetRange,
                                      out float threat, out float agl)'''
rep(old, new, "R1 Compute 带 targetPos")

# ---- R2: hasTarget 判定 ----
old = '''            // ---- 目标优先：把视距截到"到目标的距离"，并算出终端段的让步系数 ----
            float commit = 1f;
            if (targetRange > 1f && targetRange < float.MaxValue)
            {'''
new = '''            // ---- 目标优先：把视距截到"到目标的距离"，并算出终端段的让步系数 ----
            float commit = 1f;
            bool hasTarget = (targetRange > 1f) && (targetRange < float.MaxValue);
            if (hasTarget)
            {'''
rep(old, new, "R2 hasTarget")

# ---- R3: 剖面 myY 改走 LOS ----
old = '''                float groundY = o.y - dist;
                float tArrive = ahead / Mathf.Max(1f, spd);
                float myY = pos.y + vel.y * tArrive;                  // 直线外推的自身高度'''
new = '''                float groundY = o.y - dist;
                float tArrive = ahead / Mathf.Max(1f, spd);
                // 有目标时用"我 -> 目标"这条直线外推（导弹的意图是命中目标，它在该点的高度
                // 就是 LOS 上的高度）；没有目标才退回"当前速度直线"外推。
                // ⚠ 这里必须用 LOS：尾追贴地目标时速度直线的下沉率比需要的平均下沉率更陡，
                //   速度外推会在目标之前就先穿到地面以下，于是把"马上命中"误判成"马上撞地"。
                float myY = hasTarget
                    ? Mathf.Lerp(pos.y, targetPos.y, Mathf.Clamp01(ahead / Mathf.Max(1f, targetRange)))
                    : pos.y + vel.y * tArrive;'''
rep(old, new, "R3 剖面 LOS 外推")

# ---- R4: FixedUpdate 传目标位置 ----
old = '''            float tgtRange = float.MaxValue;    // 目标距离：避障"目标优先"的边界（打不到目标之后的地形与我无关）
            if (UseTarget && Target != null && Target.Valid)
            {
                Vector3 tp = Target.Position;
                range = Vector3.Distance(Body.Pos, tp);
                tgtRange = range;'''
new = '''            float tgtRange = float.MaxValue;    // 目标距离：避障"目标优先"的边界（打不到目标之后的地形与我无关）
            Vector3 tgtPos = Vector3.zero;      // 目标位置：山脊剖面按"我->目标"直线外推
            if (UseTarget && Target != null && Target.Valid)
            {
                Vector3 tp = Target.Position;
                range = Vector3.Distance(Body.Pos, tp);
                tgtRange = range;
                tgtPos = tp;'''
rep(old, new, "R4a 捕获 tgtPos")

old = '''                    _avoidDir = TerrainAvoid.Compute(Probe, Body.Pos, Body.Vel, MaxG * 9.81f, Avoid,
                                                     _avoidThreat, tgtRange, out newThreat, out agl);'''
new = '''                    _avoidDir = TerrainAvoid.Compute(Probe, Body.Pos, Body.Vel, MaxG * 9.81f, Avoid,
                                                     _avoidThreat, tgtPos, tgtRange, out newThreat, out agl);'''
rep(old, new, "R4b 传 tgtPos")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig, "len", len(orig), "->", len(s))
