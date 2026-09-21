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

# ---- C1: 新配置字段 ----
old = '''        private float cfgAiSpeedScale = 1.0f;      // 统一缩放所有性格的巡航速度（不用重编就能整体调快慢）'''
new = '''        private float cfgAiSpeedScale = 1.0f;      // 统一缩放所有性格的巡航速度（不用重编就能整体调快慢）
        // ---- AI 空战强化（2026-09-15）----
        // 旧行为：AI 用玩家当前选中的弹（默认 PL-15，中距）、无锁定记忆、够不着也放炮，
        // 于是"AI 只敢近战、玩家打超视距、AI 摸不到玩家"。见 BanditAircraft.DoCombat。
        private string cfgAiMissile = "R-37";      // aiMissileName：AI 用的空空导弹（远程弹）
        private float cfgAiLockMemory = 6f;        // aiLockMemory：锁定记忆（s）
        private bool cfgAiKinGate = true;          // aiKinematicGate：只在导弹够得着时才开火
        private bool cfgAiAutoPersona = true;      // aiAutoPersona：外部生成的 AI 自动抽一个人格'''
rep(old, new, "C1 AI 强化配置字段")

old = '''        private float cfgAvoidProfileMargin = 20f;'''
new = '''        private float cfgAvoidProfileMargin = 20f;
        private float cfgAvoidTerminalRange = 1600f;  // avoidTerminalRange：目标优先，终端让位距离（m）
        private float cfgAvoidTerminalMin = 0.30f;    // avoidTerminalMin：终端段保留的避障权重下限'''
rep(old, new, "C2 avoid Terminal 配置字段")

# ---- C3: 规避保持时长 2.6 -> 1.6（威胁消失后更快回头反击）----
old = '''            public static float Hold = 2.6f;        // 威胁消失后继续规避的时长（s）'''
new = '''            public static float Hold = 1.6f;        // 威胁消失后继续规避的时长（s）'''
rep(old, new, "C3a EvadeCfg.Hold")

old = '''        private float cfgEvadeHold = 2.6f;'''
new = '''        private float cfgEvadeHold = 1.6f;'''
rep(old, new, "C3b cfgEvadeHold")

# ---- C4: LoadConfig 读取 ----
old = '''                cfgAiSpeedScale = (float)root.GetNumber("aiSpeedScale", cfgAiSpeedScale);'''
new = '''                cfgAiSpeedScale = (float)root.GetNumber("aiSpeedScale", cfgAiSpeedScale);
                cfgAiMissile = root.GetString("aiMissileName", cfgAiMissile);
                cfgAiLockMemory = (float)root.GetNumber("aiLockMemory", cfgAiLockMemory);
                cfgAiKinGate = root.GetBool("aiKinematicGate", cfgAiKinGate);
                cfgAiAutoPersona = root.GetBool("aiAutoPersona", cfgAiAutoPersona);'''
rep(old, new, "C4a LoadConfig AI 强化")

old = '''                cfgAvoidProfileMargin = (float)root.GetNumber("avoidProfileMargin", cfgAvoidProfileMargin);'''
new = '''                cfgAvoidProfileMargin = (float)root.GetNumber("avoidProfileMargin", cfgAvoidProfileMargin);
                cfgAvoidTerminalRange = (float)root.GetNumber("avoidTerminalRange", cfgAvoidTerminalRange);
                cfgAvoidTerminalMin = (float)root.GetNumber("avoidTerminalMin", cfgAvoidTerminalMin);'''
rep(old, new, "C4b LoadConfig avoid terminal")

# ---- C5: 静态字段下发 ----
old = '''                AiAircraft.FlareDelay = cfgAiFlareDelay;'''
new = '''                AiAircraft.FlareDelay = cfgAiFlareDelay;

                // AI 空战强化（2026-09-15）：锁定记忆 / 发射可行性门 / 自动人格
                BanditAircraft.LockMemory = cfgAiLockMemory;
                BanditAircraft.KinematicGate = cfgAiKinGate;
                AiAircraft.AutoPersona = cfgAiAutoPersona;
                AiAircraft.AutoPersonaSpeedScale = cfgAiSpeedScale;'''
rep(old, new, "C5 静态下发")

# ---- C6: ApplyMissileConfig 下发 avoid terminal ----
old = '''            mc.Avoid.ProfileMargin = cfgAvoidProfileMargin;'''
new = '''            mc.Avoid.ProfileMargin = cfgAvoidProfileMargin;
            mc.Avoid.TerminalRange = cfgAvoidTerminalRange;
            mc.Avoid.TerminalMin = cfgAvoidTerminalMin;'''
rep(old, new, "C6 ApplyMissileConfig avoid terminal")

# ---- C7: AiMissileSpec 属性 ----
old = '''        private string AiDesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgAiDesign); }
        }'''
new = '''        /// <summary>
        /// AI 用的空空导弹规格。火控的"发射可行性"判据与真正发射必须用**同一份**，
        /// 否则会出现"按 A 弹算够得着、实际打出 B 弹"的错配。
        /// </summary>
        public MissileSpec AiMissileSpec
        {
            get
            {
                MissileSpec sp = SpecByName(cfgAiMissile);
                if (sp == null) sp = SpecByName(cfgCargoName);
                return sp;
            }
        }

        private string AiDesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgAiDesign); }
        }'''
rep(old, new, "C7 AiMissileSpec")

# ---- C8: AiLaunchAt 用 AI 远程弹 ----
old = '''                if (shooter == null || shooter.Container == null || target == null) return;
                string path = DesignPathFor(SpecByName(cfgCargoName));
                if (!File.Exists(path)) { _api.Log("AAM: AI launch failed - design missing"); return; }

                model = DesignModel.BuildWithGameLoader(path, "AAM_" + cfgCargoName, _api, false, false, true);
                if (model == null) model = DesignModel.BuildFallback(path, "AAM_" + cfgCargoName, _api);'''
new = '''                if (shooter == null || shooter.Container == null || target == null) return;
                // AI 用专门的远程弹（aiMissileName，默认 R-37）：中距弹对背向目标只有 3~5km
                // 有效射程，AI 在 8km 外打出去全是白费 —— 这就是"AI 摸不到玩家"的根因之一。
                MissileSpec sp = AiMissileSpec;
                string spName = (sp != null && !string.IsNullOrEmpty(sp.Name)) ? sp.Name : cfgCargoName;
                string path = DesignPathFor(sp);
                if (!File.Exists(path)) { _api.Log("AAM: AI launch failed - design missing"); return; }

                model = DesignModel.BuildWithGameLoader(path, "AAM_" + spName, _api, false, false, true);
                if (model == null) model = DesignModel.BuildFallback(path, "AAM_" + spName, _api);'''
rep(old, new, "C8a AiLaunchAt 远程弹")

old = '''                ApplyMissileConfig(mc, SpecByName(cfgCargoName));   // AI 用指定型号（默认 PL-15）'''
new = '''                ApplyMissileConfig(mc, sp);   // AI 用 aiMissileName 指定的远程弹（默认 R-37）'''
rep(old, new, "C8b AiLaunchAt ApplyMissileConfig")

old = '''                _api.Log("AAM: " + shooter.Callsign + " launched at player, dist=" + Mathf.RoundToInt(dist) + "m");'''
new = '''                _api.Log("AAM: " + shooter.Callsign + " launched " + spName
                         + " at target, dist=" + Mathf.RoundToInt(dist) + "m");'''
rep(old, new, "C8c AiLaunchAt 日志")

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print("\n".join(report))
print("changed:", s != orig, "len", len(orig), "->", len(s))
