# -*- coding: utf-8 -*-
"""2026-09-15 patch: 给 aiCombatTest 增加"规避中反击"实测。

用户诉求 ④：AI 看见导弹就干跑、不找角度反击。
代码已改成规避时走 CombatFireOnly()（保持火控解+开火）。本补丁让它变成**实测**：
从玩家机朝 AI 打一发引信阈值 0.5m（物理上不可能引爆）的探针弹，逼 AI 进入 beam 规避，
之后逐秒记录 ShotsFired 是否仍在增长。
"""
import io, os, sys

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"

with io.open(SRC, "r", encoding="utf-8-sig") as f:
    txt = f.read()

n_ok = 0


def rep(old, new, tag):
    global txt, n_ok
    if old not in txt:
        print("!! ANCHOR NOT FOUND:", tag)
        return False
    if txt.count(old) != 1:
        print("!! ANCHOR NOT UNIQUE (%d):" % txt.count(old), tag)
        return False
    txt = txt.replace(old, new, 1)
    n_ok += 1
    print("ok  ", tag)
    return True


# ---------------------------------------------------------------- 1) 暴露规避状态
rep(
    "        private bool _evading;                     // 本帧是否处于规避（Update 据此跳过火控/放开油门）\n",
    "        private bool _evading;                     // 本帧是否处于规避（Update 据此跳过火控/放开油门）\n"
    "        /// <summary>自测诊断：本帧是否处于规避（见 aiCombatTest 的 counterEvade 检验）。</summary>\n"
    "        public bool EvadingNow { get { return _evading; } }\n",
    "AiAircraft.EvadingNow",
)

# ---------------------------------------------------------------- 2) ProbeLaunchAt
rep(
    "        private void ApplyMissileConfig(MissileController mc, MissileSpec spec)\n",
    '''        /// <summary>
        /// 自测专用：从任意来源（这里用玩家机）朝 target 打一发**不会爆炸**的探针弹
        /// （引信阈值 0.5m，见 MissileController 的 "range &lt; Proximity" 判定，物理上不可能引爆），
        /// 用来逼 AI 进入规避，从而验证"规避中仍然保持火控解并反击"。
        /// 正式玩法不走这条路径（玩家开火走 HandleMissileInput/TryFire），因此没有副作用。
        /// </summary>
        public bool ProbeLaunchAt(Transform shooterT, Transform target)
        {
            try
            {
                if (shooterT == null || target == null) return false;
                MissileSpec sp = SpecByName(cfgCargoName);
                if (sp == null) sp = AiMissileSpec;
                if (sp == null) { _api.Log("AAM PROBE: no spec"); return false; }
                string path = DesignPathFor(sp);
                if (!File.Exists(path)) { _api.Log("AAM PROBE: design missing " + path); return false; }

                GameObject model = DesignModel.BuildWithGameLoader(path, "AAM_PROBE", _api, false, false, true);
                if (model == null) model = DesignModel.BuildFallback(path, "AAM_PROBE", _api);
                if (model == null) return false;

                Vector3 fwd = shooterT.forward;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                fwd = fwd.normalized;
                Rigidbody srb = shooterT.GetComponentInParent<Rigidbody>();
                Vector3 vel = (srb != null) ? srb.linearVelocity : fwd * 120f;
                if (vel.magnitude < 40f) vel = fwd * 120f;

                var tr = new TargetRef();
                tr.T = target;
                tr.UseFixed = false;
                tr.Name = "CTEST-AI";

                MissileController mc = MissileController.Spawn(
                    model, shooterT.position + fwd * 5f - shooterT.up * 1.5f, vel, tr, null);
                ApplyMissileConfig(mc, sp);
                // ★ 关键：不想让它把 AI 打死，只想要"来袭威胁"这个事件本身。
                mc.Proximity = 0.5f;
                mc.Lifetime = 14f;              // 威胁窗口有限，之后 AI 自然脱离规避
                mc.FactionId = -1;
                mc.ShooterModel = "CTEST";
                mc.ShooterCall = "CTEST";
                mc.enabled = true;
                _live.Add(mc);
                _api.Log("AAM PROBE: harmless probe (" + sp.Name + ") at CTEST-1, dist="
                         + Mathf.RoundToInt(Vector3.Distance(shooterT.position, target.position))
                         + "m proximity=0.5 lifetime=14");
                return true;
            }
            catch (Exception e) { _api.Log("AAM PROBE error: " + e.Message); return false; }
        }

        private void ApplyMissileConfig(MissileController mc, MissileSpec spec)
''',
    "AamSystem.ProbeLaunchAt",
)

# ---------------------------------------------------------------- 3) 自测字段
rep(
    "        private float _combatLogAt;\n        private int _combatLogN;\n",
    "        private float _combatLogAt;\n        private int _combatLogN;\n"
    "        private bool _combatProbeDone;              // 探针弹是否已发射\n"
    "        private bool _combatEvadeSeen;              // 是否观察到 AI 进入规避\n"
    "        private int _combatShotsAtEvade;            // 首次观察到规避时的发射数\n"
    "        private int _combatShotsDuringEvade;        // 规避期间观察到的最大发射数\n",
    "CTEST fields",
)

# ---------------------------------------------------------------- 4) 逐秒日志 + 探针 + 判据
OLD_BODY = '''                BanditAircraft b = _combatAi as BanditAircraft;
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
                         + " verdict=" + verdict + " | AI_COMBAT_SELFTEST_DONE | AAM SELFTEST: done");
                _combatTestDone = true;
            }
'''

NEW_BODY = '''                BanditAircraft b = _combatAi as BanditAircraft;
                int shots = (b != null) ? b.ShotsFired : -1;

                // ---- 规避中反击检验（用户诉求④：AI 看见导弹就干跑、不反击）----
                // 第 4 秒（AI 已经打过第一轮齐射）朝它打一发**不会爆炸**的探针弹，
                // 逼它进入 beam 规避；此后逐秒记录"规避中还能不能继续开火"。
                if (!_combatProbeDone && _combatLogN >= 4)
                {
                    _combatProbeDone = true;
                    ProbeLaunchAt(pt, _combatAi.transform);
                }
                bool ev = _combatAi.EvadingNow;
                if (ev && !_combatEvadeSeen) { _combatEvadeSeen = true; _combatShotsAtEvade = shots; }
                if (ev && shots > _combatShotsDuringEvade) _combatShotsDuringEvade = shots;

                _api.Log("AAM CTEST t=" + _combatLogN + "s dist=" + d.ToString("F0")
                         + " aiAlt=" + _combatAi.transform.position.y.ToString("F0")
                         + " aiSpd=" + (_combatAi.Rb != null ? _combatAi.Rb.linearVelocity.magnitude.ToString("F0") : "?")
                         + " locked=" + ((b != null) && b.LockedNow)
                         + " lockProg=" + (b != null ? b.LockProgNow.ToString("F1") : "?")
                         + " reach=" + ((b != null) && b.ReachNow)
                         + " evade=" + ev
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
                string cex = !_combatEvadeSeen ? "N/A（探针没逼出规避）"
                           : ((_combatShotsDuringEvade > _combatShotsAtEvade)
                              ? ("PASS（规避中又打了 " + (_combatShotsDuringEvade - _combatShotsAtEvade) + " 发）")
                              : "FAIL（规避中一发没打：反击没生效）");
                _api.Log("AAM CTEST RESULT shots=" + shots
                         + " firstShotDist=" + (b != null ? b.FirstShotDist.ToString("F0") : "?")
                         + " verdict=" + verdict
                         + " counterEvade=" + cex
                         + " evadeSeen=" + _combatEvadeSeen
                         + " evadeShots=" + _combatShotsAtEvade + "->" + _combatShotsDuringEvade
                         + " | AI_COMBAT_SELFTEST_DONE | AAM SELFTEST: done");
                _combatTestDone = true;
            }
'''

rep(OLD_BODY, NEW_BODY, "StepAiCombatTest body")

if n_ok != 4:
    print("!! only %d/4 applied - NOT writing" % n_ok)
    sys.exit(1)

with io.open(SRC, "w", encoding="utf-8-sig", newline="") as f:
    f.write(txt)
print("written", SRC, len(txt), "chars")
