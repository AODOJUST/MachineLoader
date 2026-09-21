import io

P = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(P, encoding='utf-8').read()
orig = s
report = []

def rep(old, new, tag):
    global s
    n = s.count(old)
    if n != 1:
        report.append("FAIL  " + tag + "  count=" + str(n)); return False
    s = s.replace(old, new, 1); report.append("OK    " + tag); return True

# ---- T1: BanditAircraft 诊断暴露 ----
old = '''        private float _lockMemT;
        private int _burstLeft = -1;              // 本次齐射还剩几发（<=0 = 下一发开新一轮）
        private MissileSpec _aiSpec;'''
new = '''        private float _lockMemT;
        private int _burstLeft = -1;              // 本次齐射还剩几发（<=0 = 下一发开新一轮）
        private MissileSpec _aiSpec;

        // ---- 诊断（供自测 aiCombatTest 读）----
        public int ShotsFired;                    // 本机累计发射了几发
        public float FirstShotDist = -1f;         // 第一次开火时的距离（判据：够不够"超视距"）
        public bool ReachNow;                     // 上一帧"发射可行性"判据的结果
        public float DistNow { get { return _dist; } }
        public float LockProgNow { get { return LockProgress; } }
        public float FireCdNow { get { return FireCooldown; } }'''
rep(old, new, "T1 诊断字段")

old = '''            FireCooldown -= Time.deltaTime;
            bool inFireRange = _dist <= RadarRange * FireRangeFrac && _dist >= MinRange;
            if (_locked && FireCooldown <= 0f && inFireRange && KinematicReach(t))
            {'''
new = '''            FireCooldown -= Time.deltaTime;
            bool inFireRange = _dist <= RadarRange * FireRangeFrac && _dist >= MinRange;
            ReachNow = inFireRange && KinematicReach(t);
            if (_locked && FireCooldown <= 0f && ReachNow)
            {'''
rep(old, new, "T2 ReachNow")

old = '''                    if (_burstLeft <= 0) _burstLeft = Mathf.Max(1, SalvoSize);
                    _burstLeft--;
                    FireCooldown = (_burstLeft <= 0) ? FireInterval : Mathf.Max(0.25f, SalvoGap);
                    sys.AiLaunchAt(this, t, _dist);'''
new = '''                    if (_burstLeft <= 0) _burstLeft = Mathf.Max(1, SalvoSize);
                    _burstLeft--;
                    FireCooldown = (_burstLeft <= 0) ? FireInterval : Mathf.Max(0.25f, SalvoGap);
                    if (ShotsFired == 0) FirstShotDist = _dist;
                    ShotsFired++;
                    sys.AiLaunchAt(this, t, _dist);'''
rep(old, new, "T3 计数")

# ---- T4: 自测配置字段 ----
old = '''        private bool cfgAiPersonaTest = false;   // 性格验证自测（aiPersonaTest）
        private bool _personaTestDone;'''
new = '''        private bool cfgAiPersonaTest = false;   // 性格验证自测（aiPersonaTest）
        private bool _personaTestDone;
        // ---- 自测：AI 超视距交战（aiCombatTest）----
        private bool cfgAiCombatTest = false;
        private float cfgAiCombatDist = 9000f;   // AI 生成在玩家正前方多远
        private float cfgAiCombatAlt = 0f;       // 相对玩家的高度差
        private string cfgAiCombatPersona = "SENTINEL";
        private int cfgAiCombatSeconds = 30;
        private bool _combatTestDone;
        private float _combatSpawnAt;
        private AiAircraft _combatAi;
        private float _combatLogAt;
        private int _combatLogN;'''
rep(old, new, "T4 自测配置字段")

old = '''                cfgAiPersonaTest = root.GetBool("aiPersonaTest", cfgAiPersonaTest);'''
new = '''                cfgAiPersonaTest = root.GetBool("aiPersonaTest", cfgAiPersonaTest);
                cfgAiCombatTest = root.GetBool("aiCombatTest", cfgAiCombatTest);
                cfgAiCombatDist = (float)root.GetNumber("aiCombatDist", cfgAiCombatDist);
                cfgAiCombatAlt = (float)root.GetNumber("aiCombatAlt", cfgAiCombatAlt);
                cfgAiCombatPersona = root.GetString("aiCombatPersona", cfgAiCombatPersona);
                cfgAiCombatSeconds = (int)root.GetNumber("aiCombatSeconds", cfgAiCombatSeconds);'''
rep(old, new, "T5 LoadConfig")

# ---- T6: Update 挂上 ----
old = '''                StepAiAircraft();
                StepPersonaTest();'''
new = '''                StepAiAircraft();
                StepPersonaTest();
                StepAiCombatTest();'''
rep(old, new, "T6 Update 挂载")

# ---- T7: 自测主体 ----
old = '''        // =================================================================
        // 专项自测：弹药模型 + MSDM 拦截弹（aam_config.json 里 testAmmo=true）
        // =================================================================
        private int IndexOfSpec(string name)'''
new = '''        // =================================================================
        // 自测：AI 超视距交战（aam_config.json 里 aiCombatTest=true）
        // =================================================================
        // 验证 2026-09-15 的火控强化真的生效。判据（全部落日志）：
        //   ① 在 cfgAiCombatDist（默认 9km）这种"超视距"距离上就能锁定并开火
        //      —— 旧行为要么够不着、要么打出去白费；
        //   ② 一轮齐射 2 发（SalvoSize=2，间隔 SalvoGap）；
        //   ③ 导弹能一路飞到玩家身上：把 aiDamagePlayer 设成 false 跑，命中会打
        //      "player hit (damage disabled)" —— 那就是"AI 打到了玩家"的铁证。
        private void StepAiCombatTest()
        {
            if (!cfgAiCombatTest || _combatTestDone) return;
            if (_plane == null || !_plane.FlightModeInitialized) { RequestFlyMode(); return; }

            if (_combatAi == null)
            {
                if (_combatSpawnAt <= 0f) _combatSpawnAt = Time.unscaledTime + 2f;
                if (Time.unscaledTime < _combatSpawnAt) return;
                try
                {
                    string path = AiDesignPath;
                    if (!File.Exists(path)) { _api.Log("AAM CTEST: design missing " + path); _combatTestDone = true; return; }
                    Transform pt = _plane.transform;
                    Vector3 fwd = pt.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    fwd = fwd.normalized;
                    Vector3 pos = pt.position + fwd * cfgAiCombatDist;
                    pos.y = Mathf.Max(400f, pt.position.y + cfgAiCombatAlt);
                    AiPersonality per = AiPersonalities.ById(cfgAiCombatPersona);
                    if (per == null) per = AiPersonalities.ById("SENTINEL");
                    float spd = per.Speed * cfgAiSpeedScale;
                    AiAircraft ai = AiAircraftFactory.Spawn(_api, path, pos, -fwd, spd, pos.y, true, "CTEST-1");
                    if (ai == null) { _api.Log("AAM CTEST: spawn FAILED"); _combatTestDone = true; return; }
                    ai.ApplyPersonality(per);
                    ai.CruiseSpeed = spd;
                    ai.FuelPlanEnabled = false;
                    _ai.Add(ai);
                    _combatAi = ai;
                    _combatLogAt = Time.unscaledTime + 1f;
                    _api.Log("AAM CTEST: hostile AI spawned dist=" + cfgAiCombatDist.ToString("F0")
                             + "m alt=" + pos.y.ToString("F0") + " persona=" + per.Id
                             + " radar=" + ai.RadarRange.ToString("F0")
                             + " cone=" + ai.RadarCone.ToString("F0")
                             + " fireFrac=" + ai.FireRangeFrac.ToString("F2")
                             + " salvo=" + ai.SalvoSize + " aiMissile="
                             + (AiMissileSpec != null ? AiMissileSpec.Name : "?"));
                }
                catch (Exception e) { _api.Log("AAM CTEST spawn error: " + e.Message); _combatTestDone = true; }
                return;
            }

            if (Time.unscaledTime < _combatLogAt) return;
            _combatLogAt = Time.unscaledTime + 1f;
            _combatLogN++;
            try
            {
                Transform pt = _plane.transform;
                float d = Vector3.Distance(_combatAi.transform.position, pt.position);
                BanditAircraft b = _combatAi as BanditAircraft;
                int shots = (b != null) ? b.ShotsFired : -1;
                _api.Log("AAM CTEST t=" + _combatLogN + "s dist=" + d.ToString("F0")
                         + " aiAlt=" + _combatAi.transform.position.y.ToString("F0")
                         + " aiSpd=" + (_combatAi.Rb != null ? _combatAi.Rb.linearVelocity.magnitude.ToString("F0") : "?")
                         + " locked=" + ((b != null) && b.LockedNow)
                         + " lockProg=" + (b != null ? b.LockProgNow.ToString("F1") : "?")
                         + " reach=" + ((b != null) && b.ReachNow)
                         + " shots=" + shots
                         + " firstShot=" + (b != null ? b.FirstShotDist.ToString("F0") : "?")
                         + " inFlight=" + MissileController.RegistrySnapshot().Count);
            }
            catch (Exception e) { _api.Log("AAM CTEST log error: " + e.Message); _combatTestDone = true; }

            if (_combatLogN >= cfgAiCombatSeconds)
            {
                BanditAircraft b = _combatAi as BanditAircraft;
                int shots = (b != null) ? b.ShotsFired : 0;
                string verdict = (shots <= 0) ? "FAIL（一发都没打出来：锁定/可行性门有 bug）"
                                              : (shots >= 2 ? "PASS" : "PARTIAL（只打了 1 发，齐射没生效？）");
                _api.Log("AAM CTEST RESULT shots=" + shots
                         + " firstShotDist=" + (b != null ? b.FirstShotDist.ToString("F0") : "?")
                         + " verdict=" + verdict + " | AI_COMBAT_SELFTEST_DONE");
                _combatTestDone = true;
            }
        }

        // =================================================================
        // 专项自测：弹药模型 + MSDM 拦截弹（aam_config.json 里 testAmmo=true）
        // =================================================================
        private int IndexOfSpec(string name)'''
rep(old, new, "T7 自测主体")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig, "len", len(orig), "->", len(s))
