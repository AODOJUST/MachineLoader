# -*- coding: utf-8 -*-
# 加一个 testAmmo 专项自测：逐个弹种还原模型 + MSDM 拦截来袭导弹
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"

reps = []

# --- A 字段
reps.append((
"""        private bool cfgTestAutoLoadSave = true;  // testAutoLoadSave
        private string cfgTestSaveName = "AutoSave 0"; // testSaveName（留空=取最近修改的存档）
""",
"""        private bool cfgTestAutoLoadSave = true;  // testAutoLoadSave
        private string cfgTestSaveName = "AutoSave 0"; // testSaveName（留空=取最近修改的存档）
        private bool cfgTestAmmo = false;        // testAmmo：弹药模型 + MSDM 拦截弹专项自测
        private int _ammoStage = -1;
        private int _ammoIdx;
        private float _ammoT;
        private float _ammoLogT;
        private MissileController _ammoThreat;
"""))

# --- B 配置读取
reps.append((
"""                cfgTestAutoLoadSave = root.GetBool("testAutoLoadSave", cfgTestAutoLoadSave);
                cfgTestSaveName = root.GetString("testSaveName", cfgTestSaveName);
""",
"""                cfgTestAutoLoadSave = root.GetBool("testAutoLoadSave", cfgTestAutoLoadSave);
                cfgTestSaveName = root.GetString("testSaveName", cfgTestSaveName);
                cfgTestAmmo = root.GetBool("testAmmo", cfgTestAmmo);
"""))

# --- C Update 挂接
reps.append((
"""                CleanupLive();
                HandleAutoTest();
""",
"""                CleanupLive();
                HandleAutoTest();
                StepAmmoTest();
"""))

# --- D 拦截计数
reps.append((
"""        public bool InterceptOnly = false; // 拦截弹（MSDM）：只对来袭导弹有效，命中飞机不造成任何伤害
""",
"""        public bool InterceptOnly = false; // 拦截弹（MSDM）：只对来袭导弹有效，命中飞机不造成任何伤害
        public static int InterceptCount = 0;   // 拦截成功计数（自测判据）
"""))

# --- E Intercept 计数自增
reps.append((
"""            if (_done) return;
            _done = true;
            SilentAi.Unmark(gameObject);
            TargetRegistry.Unregister(gameObject);
            Machine.Core.Log.Info("[AAM] INTERCEPTED: " + SpecName + " fired by " + ShooterCall
""",
"""            if (_done) return;
            _done = true;
            InterceptCount++;
            SilentAi.Unmark(gameObject);
            TargetRegistry.Unregister(gameObject);
            Machine.Core.Log.Info("[AAM] INTERCEPTED: " + SpecName + " fired by " + ShooterCall
"""))

# --- F 新增自测方法
NEW = """        // =================================================================
        // 专项自测：弹药模型 + MSDM 拦截弹（aam_config.json 里 testAmmo=true）
        // =================================================================
        private int IndexOfSpec(string name)
        {
            for (int i = 0; i < _specs.Count; i++)
                if (string.Equals(_specs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>还原一个弹种自己的模型并报告部件数 / 包围盒尺寸（验证"换模型"是否真的生效）。</summary>
        private void CheckOneAmmoModel(MissileSpec sp)
        {
            string p = DesignPathFor(sp);
            bool ex = File.Exists(p);
            string info = "exists=" + ex;
            GameObject m = null;
            try
            {
                if (ex)
                {
                    m = DesignModel.BuildWithGameLoader(p, "AMMOCHK_" + sp.Name, _api);
                    if (m == null) info += " build=NULL";
                    else
                    {
                        Renderer[] rr = m.GetComponentsInChildren<Renderer>(true);
                        bool has = false;
                        Bounds b = new Bounds(m.transform.position, Vector3.zero);
                        for (int k = 0; k < rr.Length; k++)
                        {
                            if (rr[k] == null) continue;
                            if (!has) { b = rr[k].bounds; has = true; } else b.Encapsulate(rr[k].bounds);
                        }
                        info += " build=ok parts=" + m.transform.childCount + " renderers=" + rr.Length;
                        if (has) info += " size=" + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2")
                                             + "x" + b.size.z.ToString("F2");
                    }
                }
            }
            catch (Exception e) { info += " build=EX " + e.Message; }
            finally { if (m != null) UnityEngine.Object.Destroy(m); }

            _api.Log("AAM AMMO: " + sp.Name
                     + " design=" + (string.IsNullOrEmpty(sp.Design) ? (cfgDesignFile + "(fallback)") : sp.Design)
                     + " " + info
                     + " lt=" + sp.Lifetime.ToString("F0") + "s vmax=" + sp.MaxSpeed.ToString("F0") + "kt"
                     + " wt=" + sp.Weight.ToString("F2") + " sp=" + sp.Space
                     + (sp.AntiMissile ? " [INTERCEPTOR]" : ""));
        }

        private void StepAmmoTest()
        {
            if (!cfgTestAmmo) return;
            if (_plane == null || !_plane.FlightModeInitialized) { RequestFlyMode(); return; }

            if (_ammoStage < 0)
            {
                _ammoStage = 0;
                _ammoIdx = 0;
                _ammoT = 0f;
                _api.Log("AAM AMMO: ===== ammo model check (" + _specs.Count + " types) =====");
                return;
            }

            // 0) 每帧只还原一个弹种的模型，避免一帧建 7 个模型把帧率打崩
            if (_ammoStage == 0)
            {
                if (_ammoIdx >= _specs.Count)
                {
                    _api.Log("AAM AMMO: model check done");
                    _ammoStage = 1; _ammoT = 0f;
                    return;
                }
                CheckOneAmmoModel(_specs[_ammoIdx]);
                _ammoIdx++;
                return;
            }

            _ammoT += Time.deltaTime;

            // 1) 放一枚"来袭导弹"当靶子
            if (_ammoStage == 1)
            {
                if (_ammoT < 1.5f) return;
                _ammoStage = 2; _ammoT = 0f;
                SpawnTestThreatMissile();
                return;
            }

            // 2) 选 MSDM 并发射，看它能不能锁定那枚来袭导弹
            if (_ammoStage == 2)
            {
                if (_ammoT < 1.2f) return;
                _ammoStage = 3; _ammoT = 0f; _ammoLogT = 0f;
                int idx = IndexOfSpec("MSDM");
                if (idx < 0) { _api.Log("AAM AMMO: !! MSDM spec NOT FOUND"); _ammoStage = 4; return; }
                SelectMissile(idx);
                CombatGive(2);
                _api.Log("AAM AMMO: selected MSDM hold=" + CombatCount()
                         + " threatAlive=" + (_ammoThreat != null && !_ammoThreat.Finished));
                bool ok = TryFire();
                _api.Log("AAM AMMO: intercept fire issued ok=" + ok + " live=" + _live.Count);
                return;
            }

            // 3) 等拦截结果
            if (_ammoStage == 3)
            {
                _ammoLogT += Time.deltaTime;
                if (_ammoLogT > 1.5f)
                {
                    _ammoLogT = 0f;
                    _api.Log("AAM AMMO: t=" + _ammoT.ToString("F1") + "s intercepts=" + MissileController.InterceptCount
                             + " threat=" + (_ammoThreat != null ? (_ammoThreat.Finished ? "dead" : "alive") : "gone")
                             + " live=" + _live.Count + " " + LiveMissileSummary());
                }
                bool threatDead = (_ammoThreat == null) || _ammoThreat.Finished;
                if (threatDead || _ammoT > 16f)
                {
                    _ammoStage = 4; _ammoT = 0f;
                    _api.Log("AAM AMMO: intercept phase over | threatDead=" + threatDead
                             + " intercepts=" + MissileController.InterceptCount
                             + " | " + LiveMissileSummary());
                }
                return;
            }

            // 4) 收尾
            if (_ammoStage == 4 && _ammoT > 2.5f)
            {
                _ammoStage = 5;
                _api.Log("AAM AMMO: done. intercepts=" + MissileController.InterceptCount);
            }
        }

        private string LiveMissileSummary()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _live.Count; i++)
            {
                MissileController m = _live[i];
                if (m == null) continue;
                sb.Append('[').Append(m.SpecName).Append(" by ").Append(m.ShooterCall)
                  .Append(" d=").Append(Mathf.RoundToInt(Vector3.Distance(m.Body.Pos, _plane.transform.position)))
                  .Append("m spd=").Append(Mathf.RoundToInt(m.Body.Speed)).Append("] ");
            }
            return sb.ToString();
        }

        /// <summary>自测用：朝玩家放一枚来袭导弹当 MSDM 的靶子（InterceptOnly 保证它不伤玩家）。</summary>
        private void SpawnTestThreatMissile()
        {
            try
            {
                Transform t = _plane.transform;
                Vector3 fwd = t.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd = fwd.normalized;
                Vector3 spawn = t.position + fwd * 3500f + Vector3.up * 700f;
                Vector3 vel = (t.position - spawn).normalized * 420f;

                MissileSpec sp = SpecByName("PL-15");
                GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "AMMO_THREAT", _api);
                if (model == null) { _api.Log("AAM AMMO: threat model build failed"); return; }

                var tr = new TargetRef();
                tr.T = t;
                tr.UseFixed = false;
                tr.Name = "Player";
                MissileController mc = MissileController.Spawn(model, spawn, vel, tr, null);
                ApplyMissileConfig(mc, sp);
                mc.ShooterModel = "TEST-THREAT";
                mc.ShooterCall = "TESTBANDIT";
                mc.FactionId = -1;
                mc.InterceptOnly = true;    // 只当靶子：命中玩家也不造成伤害
                mc.enabled = true;
                _live.Add(mc);
                _ammoThreat = mc;
                _api.Log("AAM AMMO: threat missile (PL-15 model) spawned at " + spawn
                         + " speed=" + Mathf.RoundToInt(vel.magnitude));
            }
            catch (Exception e) { _api.Log("AAM AMMO: threat spawn failed " + e.Message); }
        }

        private void HandleAutoTest()
        {
            if (!cfgAutoTest) return;
"""

reps.append((
"""        private void HandleAutoTest()
        {
            if (!cfgAutoTest) return;
""",
NEW))

ok = 0
for old, new in reps:
    o = old.replace("\n", NL); n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d anchor:\n%s\n---" % (c, o[:140])); continue
    s = s.replace(o, n, 1); ok += 1

print("applied %d / %d" % (ok, len(reps)))
if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f: f.write(s)
    print("WROTE", len(s), "chars")
else:
    print("NOT WRITTEN")
