# -*- coding: utf-8 -*-
# 一次性补丁：给 MachineAAM 加"每种弹药独立模型" + 新弹种 R-77 / MSDM + 拦截弹逻辑
import io, sys, os

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()

NL = "\r\n" if "\r\n" in s else "\n"
print("line ending:", repr(NL))

reps = []

# ---------------------------------------------------------------- R1 MissileSpec
reps.append((
"""        public float Lifetime;     // 飞行时间（s）
        public float MaxSpeed;     // 速度上限（节）
    }
""",
"""        public float Lifetime;     // 飞行时间（s）
        public float MaxSpeed;     // 速度上限（节）
        public string Design;      // 该弹种使用的 .planedesign 模型文件（放在 mods/MachineAAM/ 下）
        public bool AntiMissile;   // 拦截弹（MSDM）：只能锁定/击毁来袭导弹，不能锁定或击伤飞机
    }
"""))

# ---------------------------------------------------------------- R2 TargetRef 字段
reps.append((
"""        public Transform T;
        public Vector3 FixedPos;
        public bool UseFixed;
        public string Name = "";
        private Rigidbody _rb;
        private bool _rbScanned;
""",
"""        public Transform T;
        public Vector3 FixedPos;
        public bool UseFixed;
        public string Name = "";
        // 目标若是一枚导弹：直接读它的实时速度。导弹用的是"运动学刚体 + MovePosition"，
        // Unity 不会刷新 Rigidbody.linearVelocity，靠下面那条通用分支会拿到 0，
        // 比例导引就会算错提前量（拦截弹必失的根因）。
        public MissileController Msl;
        private Rigidbody _rb;
        private bool _rbScanned;
"""))

# ---------------------------------------------------------------- R3 TargetRef.Velocity
reps.append((
"""                if (UseFixed || T == null) return Vector3.zero;
                if (!_rbScanned)
""",
"""                if (Msl != null) return Msl.Body.Vel;
                if (UseFixed || T == null) return Vector3.zero;
                if (!_rbScanned)
"""))

# ---------------------------------------------------------------- R4 MissileController 字段
reps.append((
"""        public string ShooterCall = "";    // 发射者呼号（AI=Callsign；玩家=You）
""",
"""        public string ShooterCall = "";    // 发射者呼号（AI=Callsign；玩家=You）
        public bool InterceptOnly = false; // 拦截弹（MSDM）：只对来袭导弹有效，命中飞机不造成任何伤害
"""))

# ---------------------------------------------------------------- R5 Intercept()
reps.append((
"""        public bool Finished { get { return _done; } }
        public float MinRange { get { return _minRange; } }
        public float AliveTime { get { return _t; } }
""",
"""        public bool Finished { get { return _done; } }
        public float MinRange { get { return _minRange; } }
        public float AliveTime { get { return _t; } }

        /// <summary>被拦截弹命中：本弹立即失效（只炸自己，不伤及任何飞机）。</summary>
        public void Intercept()
        {
            if (_done) return;
            _done = true;
            SilentAi.Unmark(gameObject);
            TargetRegistry.Unregister(gameObject);
            Machine.Core.Log.Info("[AAM] INTERCEPTED: " + SpecName + " fired by " + ShooterCall
                + " destroyed in flight at " + Mathf.RoundToInt(Body.Pos.y) + "m after "
                + _t.ToString("F1") + "s");
            StartCoroutine(Boom());
        }
"""))

# ---------------------------------------------------------------- R6 StrikeTarget 拦截分支
reps.append((
"""                TargetRef tr = Target;
                if (tr == null || tr.T == null) return;

                PlaneContainer pc = PlaneContainer.Instance;
""",
"""                TargetRef tr = Target;
                if (tr == null || tr.T == null) return;

                // 拦截弹（MSDM）：只对"导弹"起作用 —— 命中飞机一律不造成伤害（用户要求）
                if (InterceptOnly)
                {
                    MissileController victim = null;
                    try { victim = tr.T.GetComponentInParent<MissileController>(); } catch { }
                    if (victim != null && victim != this)
                    {
                        victim.Intercept();
                        AamSystem sys0 = AamSystem.Live;
                        if (sys0 != null) sys0.PostMissileResult("INTERCEPT", 8);
                    }
                    else Machine.Core.Log.Info("[AAM] interceptor detonated but target was not a missile");
                    return;
                }

                PlaneContainer pc = PlaneContainer.Instance;
"""))

# ---------------------------------------------------------------- R7 DefineSpecs
reps.append((
"""            // 速度单位：节（1 马赫 ≈ 660 节）。用户最新参数：
            // PL-15 15s/1.20马赫/质量9/大小45；PL-10 10s/0.93马赫/4/40；
            // PL-17 20s/0.95马赫/13/60；AIM-9X 6s/1.00马赫/3/40；
            // 机炮(Gun) 6.3s/2.1马赫/质量0.05/大小0.5
            _specs.Clear();
            _specs.Add(new MissileSpec { Name = "PL-15", Price = 12000f, Weight = 9f, Space = 45, Lifetime = 15f, MaxSpeed = 792f });
            _specs.Add(new MissileSpec { Name = "PL-10", Price = 6000f, Weight = 4f, Space = 40, Lifetime = 10f, MaxSpeed = 614f });
            _specs.Add(new MissileSpec { Name = "PL-17", Price = 18000f, Weight = 13f, Space = 60, Lifetime = 20f, MaxSpeed = 627f });
            _specs.Add(new MissileSpec { Name = "AIM-9X Sidewinder", Price = 8000f, Weight = 3f, Space = 40, Lifetime = 6f, MaxSpeed = 660f });
            _specs.Add(new MissileSpec { Name = "Gun", Price = 100f, Weight = 0.05f, Space = 1, Lifetime = 6.3f, MaxSpeed = 1386f });
""",
"""            // 速度单位：节（1 马赫 ≈ 660 节）。用户最新参数：
            //   PL-15   15s / 1.20马赫 / 质量9  / 大小45
            //   PL-10   10s / 0.93马赫 / 质量4  / 大小40
            //   PL-17   20s / 0.95马赫 / 质量13 / 大小60
            //   R-77    13s / 0.90马赫 / 质量9  / 大小45   （新增，2026-09-12）
            //   AIM-9X  9s  / 1.00马赫 / 质量3  / 大小40   （更新：模型换成 AIM-9X 本尊）
            //   MSDM    8s  / 1.22马赫 / 质量2  / 大小35   （新增拦截弹：只打来袭导弹，不打飞机）
            //   Gun     6.3s/ 2.1马赫  / 质量0.05 / 大小1  （机炮子弹，模型换成 Bullet）
            // Design 为空则回落到 aam_config.json 的 designFile。
            _specs.Clear();
            _specs.Add(new MissileSpec { Name = "PL-15", Price = 12000f, Weight = 9f, Space = 45, Lifetime = 15f, MaxSpeed = 792f, Design = "PL-15.planedesign" });
            _specs.Add(new MissileSpec { Name = "PL-10", Price = 6000f, Weight = 4f, Space = 40, Lifetime = 10f, MaxSpeed = 614f, Design = "PL-15.planedesign" });
            _specs.Add(new MissileSpec { Name = "PL-17", Price = 18000f, Weight = 13f, Space = 60, Lifetime = 20f, MaxSpeed = 627f, Design = "PL-15.planedesign" });
            _specs.Add(new MissileSpec { Name = "R-77", Price = 12000f, Weight = 9f, Space = 45, Lifetime = 13f, MaxSpeed = 594f, Design = "R-77.planedesign" });
            _specs.Add(new MissileSpec { Name = "AIM-9X Sidewinder", Price = 8000f, Weight = 3f, Space = 40, Lifetime = 9f, MaxSpeed = 660f, Design = "AIM-9.planedesign" });
            _specs.Add(new MissileSpec { Name = "MSDM", Price = 15000f, Weight = 2f, Space = 35, Lifetime = 8f, MaxSpeed = 805f, Design = "MSDM.planedesign", AntiMissile = true });
            _specs.Add(new MissileSpec { Name = "Gun", Price = 100f, Weight = 0.05f, Space = 1, Lifetime = 6.3f, MaxSpeed = 1386f, Design = "Bullet.planedesign" });
"""))

# ---------------------------------------------------------------- R8 DesignPathFor / SpecByName
reps.append((
"""        private string DesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgDesignFile); }
        }
""",
"""        private string DesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgDesignFile); }
        }

        /// <summary>某个弹种自己的模型文件路径（未指定时回落到全局 designFile）。</summary>
        private string DesignPathFor(MissileSpec spec)
        {
            string f = (spec != null && !string.IsNullOrEmpty(spec.Design)) ? spec.Design : cfgDesignFile;
            return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), f);
        }

        /// <summary>按型号名找弹种规格（找不到返回 null）。</summary>
        private MissileSpec SpecByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _specs.Count; i++)
                if (string.Equals(_specs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _specs[i];
            return null;
        }
"""))

# ---------------------------------------------------------------- R9 TryFire 模型路径
reps.append((
"""            GameObject model = null;
            try
            {
                model = DesignModel.BuildWithGameLoader(DesignPath, "AAM_" + spec.Name, _api);
                if (model == null)
                {
                    _api.Log("AAM: falling back to scene-clone model");
                    model = DesignModel.BuildFallback(DesignPath, "AAM_" + spec.Name, _api);
                }
""",
"""            GameObject model = null;
            string designPath = DesignPathFor(spec);   // 每个弹种用自己的模型文件
            try
            {
                model = DesignModel.BuildWithGameLoader(designPath, "AAM_" + spec.Name, _api);
                if (model == null)
                {
                    _api.Log("AAM: falling back to scene-clone model " + designPath);
                    model = DesignModel.BuildFallback(designPath, "AAM_" + spec.Name, _api);
                }
"""))

# ---------------------------------------------------------------- R10 锁定判定（拦截弹例外）
reps.append((
"""            // 必须雷达锁定后才能发射（用户要求；雷达未装/未锁定时禁止发射）
            string lockName = null;
            float preMass = -1f;
            try { preMass = _plane.GetMass(); } catch { }
            if (!forceNoLock && !RequireRadarLock(out lockName))
            {
                Flash("NO LOCK", 1.2f);
                _api.Log("AAM: fire aborted - no radar lock");
                return false;
            }
""",
"""            // 必须雷达锁定后才能发射（用户要求；雷达未装/未锁定时禁止发射）
            // MSDM 拦截弹例外：它锁的是"来袭导弹"而不是飞机，所以不走飞机的雷达锁定。
            string lockName = null;
            MissileController threat = null;
            float preMass = -1f;
            try { preMass = _plane.GetMass(); } catch { }
            if (spec.AntiMissile)
            {
                threat = FindIncomingMissile();
                if (threat == null)
                {
                    Flash("NO THREAT", 1.2f);
                    _api.Log("AAM: fire aborted - no incoming missile to intercept");
                    return false;
                }
                lockName = threat.SpecName;
            }
            else if (!forceNoLock && !RequireRadarLock(out lockName))
            {
                Flash("NO LOCK", 1.2f);
                _api.Log("AAM: fire aborted - no radar lock");
                return false;
            }
"""))

# ---------------------------------------------------------------- R11 TryFire 目标绑定（拦截弹）
reps.append((
"""                // 导弹制导目标：优先用雷达锁定目标实体（用户要求——攻击锁定的目标，不能自己就近找）
                TargetRef target = null;
                try
                {
                    Transform lockedTr = null;
""",
"""                // 导弹制导目标：优先用雷达锁定目标实体（用户要求——攻击锁定的目标，不能自己就近找）
                TargetRef target = null;
                if (threat != null)
                {
                    // MSDM 拦截弹：目标是那枚来袭导弹（绑它的实时速度，见 TargetRef.Msl 说明）
                    target = new TargetRef();
                    target.T = threat.transform;
                    target.Msl = threat;
                    target.UseFixed = false;
                    target.Name = threat.SpecName;
                    _api.Log("AAM: interceptor locked incoming " + threat.SpecName + " from " + threat.ShooterCall);
                }
                try
                {
                    Transform lockedTr = null;
"""))

# ---------------------------------------------------------------- R12 雷达锁定块加守卫
reps.append((
"""                    if (rt != null)
                    {
                        var mT = rt.GetMethod("TryGetLockedTransform", Type.EmptyTypes);
""",
"""                    if (rt != null && target == null)   // 拦截弹已经有目标，不要再被雷达锁定的飞机覆盖
                    {
                        var mT = rt.GetMethod("TryGetLockedTransform", Type.EmptyTypes);
"""))

# ---------------------------------------------------------------- R13 兜底目标不退化
reps.append((
"""                // 兜底：雷达不可用/无锁定实体时才用就近目标（正常流程不应走到这里，因为有 NO LOCK 拦截）
                if (target == null)
                    target = TargetRegistry.FindNearest(spawn, cfgTargetRange, true);
""",
"""                // 兜底：雷达不可用/无锁定实体时才用就近目标（正常流程不应走到这里，因为有 NO LOCK 拦截）
                // 拦截弹绝不退化成"锁飞机"——找不到导弹就宁可打空（用户要求：不可锁定并击毁敌机）。
                if (target == null && threat == null)
                    target = TargetRegistry.FindNearest(spawn, cfgTargetRange, true);
"""))

# ---------------------------------------------------------------- R14 ApplyMissileConfig
reps.append((
"""            mc.Proximity = cfgProximity;
            mc.NavConstant = cfgNavConstant;
""",
"""            mc.Proximity = cfgProximity;
            mc.NavConstant = cfgNavConstant;
            // 拦截弹：只认导弹目标（命中飞机不造成伤害）；对头接近率极高（两弹相加 ~800m/s，
            // 60fps 下每步就跨 16m），把引爆半径放大到 45m，避免高速穿靶。
            mc.InterceptOnly = spec != null && spec.AntiMissile;
            if (mc.InterceptOnly) mc.Proximity = Mathf.Max(cfgProximity, 45f);
"""))

# ---------------------------------------------------------------- R15 FindIncomingMissile
reps.append((
"""        private void CleanupLive()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null || _live[i].Finished) _live.RemoveAt(i);
            }
        }
""",
"""        private void CleanupLive()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null || _live[i].Finished) _live.RemoveAt(i);
            }
        }

        /// <summary>
        /// 找一个"正在朝我们飞"的来袭导弹（MSDM 拦截弹的目标）。
        /// 条件：不是自己发的、在扫描半径内、速度矢量指向我们（接近率 > 60m/s）。取最近的一枚。
        /// </summary>
        private MissileController FindIncomingMissile()
        {
            const float scanR = 12000f;
            try
            {
                if (_plane == null) return null;
                Vector3 me = _plane.transform.position;
                MissileController best = null;
                float bestD = float.MaxValue;
                List<MissileController> all = MissileController.RegistrySnapshot();
                for (int i = 0; i < all.Count; i++)
                {
                    MissileController m = all[i];
                    if (m == null || m.Finished) continue;
                    if (m.ShooterModel == "Player") continue;     // 自己的弹不打
                    Vector3 mp = m.Body.Pos;
                    Vector3 toMe = me - mp;
                    float d = toMe.magnitude;
                    if (d > scanR || d < 30f) continue;            // 太远不理、贴脸不追
                    if (toMe.sqrMagnitude > 1f && m.Body.Speed > 20f)
                    {
                        if (Vector3.Dot(m.Body.Vel, toMe.normalized) <= 60f) continue;   // 不是在朝我们飞
                    }
                    if (d < bestD) { bestD = d; best = m; }
                }
                return best;
            }
            catch { return null; }
        }
"""))

# ---------------------------------------------------------------- R16 AiLaunchAt 模型路径
reps.append((
"""                if (shooter == null || shooter.Container == null || target == null) return;
                string path = DesignPath;
                if (!File.Exists(path)) { _api.Log("AAM: AI launch failed - design missing"); return; }
""",
"""                if (shooter == null || shooter.Container == null || target == null) return;
                string path = DesignPathFor(SpecByName(cfgCargoName));
                if (!File.Exists(path)) { _api.Log("AAM: AI launch failed - design missing"); return; }
"""))

# ---------------------------------------------------------------- R17 AiLaunchAt 规格
reps.append((
"""                MissileController mc = MissileController.Spawn(model, spawn, vel, tr, null);
                ApplyMissileConfig(mc, null);   // AI 发射：沿用通用配置
""",
"""                MissileController mc = MissileController.Spawn(model, spawn, vel, tr, null);
                ApplyMissileConfig(mc, SpecByName(cfgCargoName));   // AI 用指定型号（默认 PL-15）
"""))

ok = 0
for old, new in reps:
    o = old.replace("\n", NL)
    n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d for anchor:\n%s\n---" % (c, o[:120]))
        continue
    s = s.replace(o, n, 1)
    ok += 1

print("applied %d / %d" % (ok, len(reps)))

if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f:
        f.write(s)
    print("WROTE", SRC, len(s), "chars")
else:
    print("NOT WRITTEN (fix anchors first)")
