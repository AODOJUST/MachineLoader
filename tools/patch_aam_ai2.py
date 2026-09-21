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

# ---------------- P4: AiPersonality 增加 Salvo ----------------
old = '''        public float FireInterval = 9f;    // 两次发射最小间隔
        public bool PreferRear;            // 喜欢从目标尾后接近（咬尾）
    }'''
new = '''        public float FireInterval = 9f;    // 两次发射最小间隔
        public bool PreferRear;            // 喜欢从目标尾后接近（咬尾）
        public int Salvo = 1;              // 一次交战连射几发（2026-09-15 强化：超视距型齐射 2 发）
    }'''
rep(old, new, "P4 AiPersonality.Salvo")

# ---------------- P5: 性格表整体强化 ----------------
old = '''            // 高速机动巡航：快、灵活、中低空，追着打
            new AiPersonality { Id = "VIPER", Desc = "高速机动巡航",
                Speed = 340f, Alt = 850f, TurnGain = 0.075f, PitchLimit = 24f,
                RadarRange = 10000f, RadarCone = 40f, FireFrac = 0.72f, FireInterval = 7f },

            // 爬坡后超高空俯冲锁定：先爬到作战高度待命，发现目标后一压机头扎下去
            new AiPersonality { Id = "FALCON", Desc = "爬升后超高空俯冲锁定",
                Speed = 300f, Alt = 3000f, TurnGain = 0.050f, MaxBank = 0.95f, PitchLimit = 34f,
                ClimbFirst = 1f, DiveAlt = 3000f,
                RadarRange = 12000f, RadarCone = 30f, FireFrac = 0.62f, FireInterval = 8f },

            // 近距离挑衅：贴脸绕圈、摇机翼，只在极近距离才打
            new AiPersonality { Id = "BRAWLER", Desc = "近距离挑衅缠斗",
                Speed = 265f, Alt = 600f, TurnGain = 0.110f, PitchLimit = 26f,
                Standoff = 0f, MinRange = 110f, Waggle = 0.55f,
                RadarRange = 3200f, RadarCone = 55f, FireFrac = 0.18f, FireInterval = 12f },

            // 低机动超视距锁定击杀：不缠斗，靠曲射保持 6km 外远距发射
            new AiPersonality { Id = "OVERWATCH", Desc = "低机动超视距锁定击杀",
                Speed = 250f, Alt = 3800f, TurnGain = 0.022f, MaxBank = 0.42f, PitchLimit = 16f,
                Standoff = 6500f, CrankAngle = 55f, MinRange = 2600f,
                RadarRange = 16000f, RadarCone = 62f, FireFrac = 0.60f, FireInterval = 8f },

            // 低空巡航：贴着地皮飞，慢悠悠
            new AiPersonality { Id = "LOWRIDER", Desc = "低空巡航",
                Speed = 215f, Alt = 165f, TurnGain = 0.045f, MaxBank = 0.8f,
                RadarRange = 8000f, RadarCone = 35f, FireFrac = 0.60f, FireInterval = 10f },

            // 中空巡逻截击：四平八稳的"正常机"
            new AiPersonality { Id = "SENTINEL", Desc = "中空巡逻截击",
                Speed = 275f, Alt = 1600f, TurnGain = 0.050f, MaxBank = 0.9f,
                RadarRange = 11000f, RadarCone = 35f, FireFrac = 0.70f, FireInterval = 9f },

            // 低空潜入、尾后偷袭：绕到玩家屁股后面再咬
            new AiPersonality { Id = "GHOST", Desc = "低空潜入、尾后偷袭",
                Speed = 290f, Alt = 300f, TurnGain = 0.065f, PitchLimit = 24f, PreferRear = true,
                RadarRange = 9000f, RadarCone = 28f, FireFrac = 0.45f, FireInterval = 8f },

            // 高速掠袭、一击脱离：全程高速平飞，中距离开火后绝不贴脸（MinRange 500m）
            new AiPersonality { Id = "RAIDER", Desc = "高速掠袭一击脱离",
                Speed = 345f, Alt = 430f, TurnGain = 0.085f, MaxBank = 0.95f, PitchLimit = 26f,
                MinRange = 500f,
                RadarRange = 8500f, RadarCone = 45f, FireFrac = 0.35f, FireInterval = 14f },

            // 垂直机动、剪刀缠斗：靠大俯仰权限上下翻飞抢高度，中低空近战
            new AiPersonality { Id = "SCREAMER", Desc = "垂直机动剪刀缠斗",
                Speed = 300f, Alt = 1250f, TurnGain = 0.115f, PitchLimit = 32f,
                Waggle = 0.20f, MinRange = 150f,
                RadarRange = 4200f, RadarCone = 50f, FireFrac = 0.30f, FireInterval = 11f },
        };'''
new = '''            // 高速机动巡航：快、灵活、中低空，追着打
            new AiPersonality { Id = "VIPER", Desc = "高速机动巡航",
                Speed = 340f, Alt = 850f, TurnGain = 0.075f, PitchLimit = 24f,
                RadarRange = 14000f, RadarCone = 55f, FireFrac = 0.85f, FireInterval = 6f, Salvo = 2 },

            // 爬坡后超高空俯冲锁定：先爬到作战高度待命，发现目标后一压机头扎下去
            new AiPersonality { Id = "FALCON", Desc = "爬升后超高空俯冲锁定",
                Speed = 300f, Alt = 3000f, TurnGain = 0.050f, MaxBank = 0.95f, PitchLimit = 34f,
                ClimbFirst = 1f, DiveAlt = 3000f,
                RadarRange = 18000f, RadarCone = 45f, FireFrac = 0.82f, FireInterval = 7f, Salvo = 2 },

            // 近距离挑衅：贴脸绕圈、摇机翼，近战为主（但雷达/火控也放大到能打中距）
            new AiPersonality { Id = "BRAWLER", Desc = "近距离挑衅缠斗",
                Speed = 265f, Alt = 600f, TurnGain = 0.110f, PitchLimit = 26f,
                Standoff = 0f, MinRange = 110f, Waggle = 0.55f,
                RadarRange = 5000f, RadarCone = 65f, FireFrac = 0.35f, FireInterval = 9f, Salvo = 1 },

            // 低机动超视距锁定击杀：不缠斗，靠曲射保持远距发射
            new AiPersonality { Id = "OVERWATCH", Desc = "低机动超视距锁定击杀",
                Speed = 250f, Alt = 3800f, TurnGain = 0.022f, MaxBank = 0.42f, PitchLimit = 16f,
                Standoff = 6500f, CrankAngle = 55f, MinRange = 2600f,
                RadarRange = 22000f, RadarCone = 70f, FireFrac = 0.80f, FireInterval = 7f, Salvo = 2 },

            // 低空巡航：贴着地皮飞，慢悠悠
            new AiPersonality { Id = "LOWRIDER", Desc = "低空巡航",
                Speed = 215f, Alt = 165f, TurnGain = 0.045f, MaxBank = 0.8f,
                RadarRange = 11000f, RadarCone = 50f, FireFrac = 0.80f, FireInterval = 8f, Salvo = 1 },

            // 中空巡逻截击：四平八稳的"正常机"
            new AiPersonality { Id = "SENTINEL", Desc = "中空巡逻截击",
                Speed = 275f, Alt = 1600f, TurnGain = 0.050f, MaxBank = 0.9f,
                RadarRange = 15000f, RadarCone = 50f, FireFrac = 0.85f, FireInterval = 7f, Salvo = 2 },

            // 低空潜入、尾后偷袭：绕到玩家屁股后面再咬
            new AiPersonality { Id = "GHOST", Desc = "低空潜入、尾后偷袭",
                Speed = 290f, Alt = 300f, TurnGain = 0.065f, PitchLimit = 24f, PreferRear = true,
                RadarRange = 12000f, RadarCone = 45f, FireFrac = 0.70f, FireInterval = 7f, Salvo = 1 },

            // 高速掠袭、一击脱离：全程高速平飞，中距离开火后绝不贴脸（MinRange 350m）
            new AiPersonality { Id = "RAIDER", Desc = "高速掠袭一击脱离",
                Speed = 345f, Alt = 430f, TurnGain = 0.085f, MaxBank = 0.95f, PitchLimit = 26f,
                MinRange = 350f,
                RadarRange = 11000f, RadarCone = 55f, FireFrac = 0.55f, FireInterval = 10f, Salvo = 1 },

            // 垂直机动、剪刀缠斗：靠大俯仰权限上下翻飞抢高度，中低空近战
            new AiPersonality { Id = "SCREAMER", Desc = "垂直机动剪刀缠斗",
                Speed = 300f, Alt = 1250f, TurnGain = 0.115f, PitchLimit = 32f,
                Waggle = 0.20f, MinRange = 150f,
                RadarRange = 6500f, RadarCone = 60f, FireFrac = 0.50f, FireInterval = 9f, Salvo = 1 },
        };'''
rep(old, new, "P5 性格表强化")

# ---------------- P6a: AiAircraft 火控默认值强化 ----------------
old = '''        public float RadarRange = 9000f;           // 火控雷达作用距离
        public float RadarCone = 35f;              // 雷达锥半角（度）
        public float LockDelay = 2.5f;              // 稳定跟踪多久算锁定
        public float FireInterval = 9f;             // 两次发射的最小间隔'''
new = '''        // 【2026-09-15 强化】默认值上调：外部生成器（FactionSystem 等）刷出来的 AI 没人格，
        // 一直吃这几个默认值 —— 旧的 9km/35°/0.75 就是玩家感觉到的"敌机又瞎又只敢近战"。
        public float RadarRange = 13000f;          // 火控雷达作用距离
        public float RadarCone = 50f;              // 雷达锥半角（度）
        public float LockDelay = 2.5f;              // 稳定跟踪多久算锁定
        public float FireInterval = 7f;             // 两次发射的最小间隔'''
rep(old, new, "P6a AiAircraft 火控默认值")

old = '''        public float FireRangeFrac = 0.75f;        // 开火距离 = RadarRange * 该系数'''
new = '''        public float FireRangeFrac = 0.85f;        // 开火距离 = RadarRange * 该系数'''
rep(old, new, "P6b FireRangeFrac")

# ---------------- P6b: AiAircraft 新增齐射/自动人格字段 ----------------
old = '''        public bool PreferRear;                    // 咬尾
        private int _diveStage = -1;               // -1=无俯冲 0=爬升 1=待命 2=俯冲 3=拉起'''
new = '''        public bool PreferRear;                    // 咬尾
        public int SalvoSize = 2;                  // 一次交战连射几发（2026-09-15 强化）
        public float SalvoGap = 0.9f;              // 齐射间隔（s）
        // ---- 自动人格（2026-09-15）：外部生成器不传人格，这里给它随机抽一个 ----
        public static bool AutoPersona = true;
        public static float AutoPersonaSpeedScale = 1f;
        private bool _personaAutoInit;
        private int _diveStage = -1;               // -1=无俯冲 0=爬升 1=待命 2=俯冲 3=拉起'''
rep(old, new, "P6c AiAircraft 齐射/自动人格字段")

# ---------------- P7: ApplyPersonality 传 Salvo ----------------
old = '''            FireInterval = p.FireInterval;
            _diveStage = (p.DiveAlt > 0f) ? 0 : -1;'''
new = '''            FireInterval = p.FireInterval;
            SalvoSize = Mathf.Max(1, p.Salvo);
            _diveStage = (p.DiveAlt > 0f) ? 0 : -1;'''
rep(old, new, "P7 ApplyPersonality Salvo")

# ---------------- P8: Update 自动人格 + 规避中反击 ----------------
old = '''            // 纯净模式守卫：原版档不保留 AI 飞机（Mod Saves 进入后会重新生成）
            if (Machine.Mod.MachineState.PureMode) { Despawn(); return; }
'''
new = '''            // 纯净模式守卫：原版档不保留 AI 飞机（Mod Saves 进入后会重新生成）
            if (Machine.Mod.MachineState.PureMode) { Despawn(); return; }

            // ---- 自动人格（2026-09-15）----
            // FactionSystem 等外部生成器只调工厂 Spawn、不传人格，那些 AI 会一直用"平飞默认值"。
            // 没人格时这里随机抽一个：雷达距离/锥角、巡航速度、战术动作（俯冲/曲射/咬尾）一起生效。
            if (!_personaAutoInit)
            {
                _personaAutoInit = true;
                if (AutoPersona && Persona == null)
                {
                    ApplyPersonality(AiPersonalities.Pick(null));
                    if (Persona != null)
                    {
                        CruiseSpeed = Persona.Speed * Mathf.Max(0.2f, AutoPersonaSpeedScale);
                        Machine.Core.Log.Info("[AAM] " + Callsign + " auto persona -> " + Persona.Id
                            + " (" + Persona.Desc + ") spd=" + CruiseSpeed.ToString("F0")
                            + " radar=" + RadarRange.ToString("F0") + " cone=" + RadarCone.ToString("F0"));
                    }
                }
            }
'''
rep(old, new, "P8a 自动人格")

old = '''            // ---------- 6) 火控 / 交战（顺手可能改写航向与油门） ----------
            // 规避时跳过：火控会把机头扭回目标，等于主动放弃规避
            if (Hostile && !_evading) CombatTick(ref wantDir, ref pitchInput, ref throttle);'''
new = '''            // ---------- 6) 火控 / 交战（顺手可能改写航向与油门） ----------
            // 规避中不再完全关掉火控：航向仍然交给规避机动（否则 beam 机动当场作废），
            // 但**保持火控解并开火** —— 这就是"看见导弹不干逃、能找到角度反击"。
            // 配合火控的锁定记忆，beam 时导弹虽然被放到 3/9 线外，锁定仍在、照样能打回去。
            if (Hostile)
            {
                if (!_evading) CombatTick(ref wantDir, ref pitchInput, ref throttle);
                else CombatFireOnly();
            }'''
rep(old, new, "P8b 规避中反击")

old = '''        /// <summary>火控：雷达搜索 -> 稳定跟踪 -> 锁定 -> 发射导弹。</summary>
        protected virtual void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
        }'''
new = '''        /// <summary>火控：雷达搜索 -> 稳定跟踪 -> 锁定 -> 发射导弹。</summary>
        protected virtual void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
        }

        /// <summary>
        /// 规避来袭导弹期间的"反击"：不改航向（beam 机动的航向必须保住），
        /// 只让子类继续维护火控解并开火。基类（无火控）空实现。
        /// </summary>
        protected virtual void CombatFireOnly()
        {
        }'''
rep(old, new, "P8c CombatFireOnly 基类")

# ---------------- P9: BanditAircraft 火控重写 ----------------
old = '''        protected override void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
            // 阵营 AI：优先攻击最近的敌方阵营 AI，其次玩家（若敌对）
            Transform t = PlayerTransform();
            bool attackPlayer = true;
            if (FactionId >= 0)
            {
                Transform enemy = NearestEnemyAi();
                if (enemy != null)
                {
                    t = enemy;
                    attackPlayer = false;
                }
                else
                {
                    // 玩家敌对性：FactionApi.IsEnemy(本机, 玩家机)
                    attackPlayer = IsPlayerEnemy();
                }
            }
            if (t == null || !attackPlayer && t == PlayerTransform() && !IsPlayerEnemy())
            {
                if (t == null) { LockProgress = 0f; _locked = false; return; }
                if (!attackPlayer) { LockProgress = 0f; _locked = false; wantDir = DesiredHeadingFor(t); return; }
            }

            Vector3 to = t.position - transform.position;
            _dist = to.magnitude;
            CurrentTarget = t;

            // 狗斗机动：未锁定时蛇形接近（横向 ±25%、垂直 ±12% 摆动），锁定后收敛瞄准
            if (!_locked && to.sqrMagnitude > 1f)
            {
                float tt = Time.time;
                Vector3 want = to.normalized;
                Vector3 up = Vector3.up;
                Vector3 right = Vector3.Cross(up, want);
                if (right.sqrMagnitude < 0.01f) right = transform.right;
                right = right.normalized;
                float sway = Mathf.Sin(tt * 0.8f + transform.position.x * 0.01f) * 0.25f;
                float bob = Mathf.Sin(tt * 1.2f + transform.position.z * 0.01f) * 0.12f;
                wantDir = (want + right * sway + up * bob).normalized;
            }

            Vector3 fwd = Container.Forward;
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;

            bool inCone = _dist <= RadarRange && Vector3.Angle(fwd, to) <= RadarCone;
            if (inCone) LockProgress += Time.deltaTime;
            else LockProgress = Mathf.Max(0f, LockProgress - Time.deltaTime * 2f);

            if (LockProgress >= LockDelay && !_locked)
            {
                _locked = true;
                Machine.Core.Log.Info("[AAM] " + Callsign + " [" + (Persona != null ? Persona.Id : "?")
                    + "] LOCKED target at " + _dist.ToString("F0") + "m");
            }
            else if (LockProgress <= 0.01f && _locked)
            {
                _locked = false;
            }

            // 开火距离由性格决定：超视距型在 10km 外就发射，近距挑衅型要贴到几百米才打。
            FireCooldown -= Time.deltaTime;
            bool inFireRange = _dist <= RadarRange * FireRangeFrac && _dist >= MinRange;
            if (_locked && FireCooldown <= 0f && inFireRange)
            {
                FireCooldown = FireInterval;
                AamSystem sys = AamSystem.Live;
                if (sys != null) sys.AiLaunchAt(this, t, _dist);
            }
        }'''
new = '''        // ---- 火控强化（2026-09-15）：锁定记忆 / 发射可行性 / 齐射 ----
        // 用户实测："AI 只会在近距离才攻击，玩家打超视距，AI 几乎摸不到玩家"。两个根因：
        //   ① 没有锁定记忆 —— 曲射（crank）或规避机动时目标一出雷达锥，LockProgress 就衰减归零、
        //      锁定立刻丢；等于"机头不钉死目标就打不出去"，超视距交战根本没法打。
        //   ② 不看导弹够不够得着 —— AI 在 8~12km 外放炮，而中距弹对**背向**目标的实际有效
        //      射程只有 3~5km，打出去全是白费（打不到目标的火力 = 没有火力）。
        // 修法：锁定给 6s 记忆（现代雷达的记忆跟踪）；开火前先算"够不够得着"
        //      （有效接近率 × 导弹寿命）；一次交战连射 SalvoSize 发。
        public static float LockMemory = 6f;      // 锁定记忆（s）：角度解丢失后仍保持锁定多久
        public static bool KinematicGate = true;  // 只在导弹真的够得着时才开火

        private float _lockMemT;
        private int _burstLeft = -1;              // 本次齐射还剩几发（<=0 = 下一发开新一轮）
        private MissileSpec _aiSpec;

        private MissileSpec AiSpec()
        {
            if (_aiSpec == null)
            {
                AamSystem sys = AamSystem.Live;
                if (sys != null) _aiSpec = sys.AiMissileSpec;
            }
            return _aiSpec;
        }

        /// <summary>
        /// 发射可行性（粗估的不可逃逸包线）：这一发有没有必要打。
        /// 有效接近率 = 导弹 0.8×极速 − 目标背离速度（目标迎面时等效于拉长射程），
        /// 再要求"预计命中时间 ≤ 导弹寿命 × 0.8"。
        /// </summary>
        public bool KinematicReach(Transform t)
        {
            if (!KinematicGate) return true;
            MissileSpec sp = AiSpec();
            if (sp == null) return _dist <= RadarRange * FireRangeFrac;
            float ms = sp.MaxSpeed * 0.5144f;             // 规格表单位是节 -> m/s
            if (ms < 1f || sp.Lifetime < 1f) return _dist <= RadarRange * FireRangeFrac;
            float away = 0f;
            try
            {
                Rigidbody trb = t.GetComponentInParent<Rigidbody>();
                if (trb != null && _dist > 1f)
                {
                    Vector3 los = (t.position - transform.position) / _dist;
                    away = Mathf.Max(0f, Vector3.Dot(trb.linearVelocity, los));
                }
            }
            catch { }
            float eff = Mathf.Max(120f, ms * 0.80f - away);
            return (_dist / eff) <= sp.Lifetime * 0.80f;
        }

        protected override void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
            DoCombat(ref wantDir, true);
        }

        /// <summary>规避导弹期间的反击：不改航向，只保持火控解并开火（见 AiAircraft.CombatFireOnly）。</summary>
        protected override void CombatFireOnly()
        {
            Vector3 ignore = Vector3.zero;
            DoCombat(ref ignore, false);
        }

        /// <summary>火控主体。<paramref name="steer"/> = false 时只做"锁定 + 开火"，不动航向。</summary>
        private void DoCombat(ref Vector3 wantDir, bool steer)
        {
            // 阵营 AI：优先攻击最近的敌方阵营 AI，其次玩家（若敌对）
            Transform t = PlayerTransform();
            bool attackPlayer = true;
            if (FactionId >= 0)
            {
                Transform enemy = NearestEnemyAi();
                if (enemy != null)
                {
                    t = enemy;
                    attackPlayer = false;
                }
                else
                {
                    // 玩家敌对性：FactionApi.IsEnemy(本机, 玩家机)
                    attackPlayer = IsPlayerEnemy();
                }
            }
            if (t == null || !attackPlayer && t == PlayerTransform() && !IsPlayerEnemy())
            {
                if (t == null) { LockProgress = 0f; _locked = false; return; }
                if (!attackPlayer)
                {
                    LockProgress = 0f; _locked = false;
                    if (steer) wantDir = DesiredHeadingFor(t);
                    return;
                }
            }

            Vector3 to = t.position - transform.position;
            _dist = to.magnitude;
            CurrentTarget = t;

            // 狗斗机动：未锁定时蛇形接近（横向 ±25%、垂直 ±12% 摆动），锁定后收敛瞄准。
            // steer=false（规避中）绝不能动航向 —— 那会把正在做的 beam 机动当场掰回去。
            if (steer && !_locked && to.sqrMagnitude > 1f)
            {
                float tt = Time.time;
                Vector3 want = to.normalized;
                Vector3 up = Vector3.up;
                Vector3 right = Vector3.Cross(up, want);
                if (right.sqrMagnitude < 0.01f) right = transform.right;
                right = right.normalized;
                float sway = Mathf.Sin(tt * 0.8f + transform.position.x * 0.01f) * 0.25f;
                float bob = Mathf.Sin(tt * 1.2f + transform.position.z * 0.01f) * 0.12f;
                wantDir = (want + right * sway + up * bob).normalized;
            }

            Vector3 fwd = Container.Forward;
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;

            bool inCone = _dist <= RadarRange && Vector3.Angle(fwd, to) <= RadarCone;
            if (inCone)
            {
                LockProgress += Time.deltaTime;
                _lockMemT = Mathf.Max(0f, LockMemory);   // 有角度解就刷新记忆
            }
            else
            {
                LockProgress = Mathf.Max(0f, LockProgress - Time.deltaTime * 2f);
                _lockMemT -= Time.deltaTime;             // 记忆在角度解丢失期间消耗
            }

            if (LockProgress >= LockDelay && !_locked)
            {
                _locked = true;
                Machine.Core.Log.Info("[AAM] " + Callsign + " [" + (Persona != null ? Persona.Id : "?")
                    + "] LOCKED target at " + _dist.ToString("F0") + "m");
            }
            else if (_locked && LockProgress <= 0.01f && _lockMemT <= 0f)
            {
                _locked = false;                         // 角度解丢了、记忆也耗尽
            }

            // ---- 开火：雷达距离包线（性格）× 发射可行性（导弹）双重门 ----
            // 距离包线仍由性格决定（超视距型更远），但再叠一层"够不够得着"：
            // 否则 8~12km 外那些炮全是浪费，玩家感受到的就是"AI 摸不到我"。
            FireCooldown -= Time.deltaTime;
            bool inFireRange = _dist <= RadarRange * FireRangeFrac && _dist >= MinRange;
            if (_locked && FireCooldown <= 0f && inFireRange && KinematicReach(t))
            {
                AamSystem sys = AamSystem.Live;
                if (sys != null)
                {
                    // 齐射：一轮连射 SalvoSize 发（间隔 SalvoGap），打完进入 FireInterval 冷却
                    if (_burstLeft <= 0) _burstLeft = Mathf.Max(1, SalvoSize);
                    _burstLeft--;
                    FireCooldown = (_burstLeft <= 0) ? FireInterval : Mathf.Max(0.25f, SalvoGap);
                    sys.AiLaunchAt(this, t, _dist);
                }
            }
        }'''
rep(old, new, "P9 BanditAircraft 火控重写")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig, "len", len(orig), "->", len(s))
