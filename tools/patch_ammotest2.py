# -*- coding: utf-8 -*-
# 给 testAmmo 补：拦截弹截图 + 结束时打 SELFTEST done 标记（运行器靠它判完成）
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# P5 字段
reps.append((
"""        private MissileController _ammoThreat;
""",
"""        private MissileController _ammoThreat;
        private MissileController _ammoMsdm;
        private int _ammoShotN;
        private float _ammoShotNext;
"""))

# P1 记录拦截弹引用
reps.append((
"""                bool ok = TryFire();
                _api.Log("AAM AMMO: intercept fire issued ok=" + ok + " live=" + _live.Count);
                return;
""",
"""                bool ok = TryFire();
                _ammoMsdm = null;
                for (int i = 0; i < _live.Count; i++)
                    if (_live[i] != null && _live[i].SpecName == "MSDM") { _ammoMsdm = _live[i]; break; }
                _api.Log("AAM AMMO: intercept fire issued ok=" + ok + " live=" + _live.Count
                         + " msdmMissile=" + (_ammoMsdm != null ? "yes" : "no"));
                return;
"""))

# P2 拦截过程截图
reps.append((
"""                _ammoLogT += Time.deltaTime;
                if (_ammoLogT > 1.5f)
                {
""",
"""                _ammoLogT += Time.deltaTime;
                if (cfgCapture && _ammoMsdm != null && !_ammoMsdm.Finished && _ammoT > _ammoShotNext)
                {
                    _ammoShotNext = _ammoT + 0.9f;
                    _ammoShotN++;
                    AmmoShot(_ammoShotN);
                }
                if (_ammoLogT > 1.5f)
                {
"""))

# P3 阶段结束补拍一张
reps.append((
"""                if (threatDead || _ammoT > 16f)
                {
                    _ammoStage = 4; _ammoT = 0f;
                    _api.Log("AAM AMMO: intercept phase over | threatDead=" + threatDead
                             + " intercepts=" + MissileController.InterceptCount
                             + " | " + LiveMissileSummary());
                }
""",
"""                if (threatDead || _ammoT > 16f)
                {
                    _ammoStage = 4; _ammoT = 0f;
                    if (cfgCapture) { _ammoShotN++; AmmoShot(_ammoShotN); }
                    _api.Log("AAM AMMO: intercept phase over | threatDead=" + threatDead
                             + " intercepts=" + MissileController.InterceptCount
                             + " | " + LiveMissileSummary());
                }
"""))

# P4 结束标记
reps.append((
"""            if (_ammoStage == 4 && _ammoT > 2.5f)
            {
                _ammoStage = 5;
                _api.Log("AAM AMMO: done. intercepts=" + MissileController.InterceptCount);
            }
""",
"""            if (_ammoStage == 4 && _ammoT > 2.5f)
            {
                _ammoStage = 5;
                _api.Log("AAM AMMO: done. intercepts=" + MissileController.InterceptCount);
                _api.Log("AAM SELFTEST: done. live=" + _live.Count + " ai=" + _ai.Count);
            }
"""))

# P6 AmmoShot
reps.append((
"""        private string LiveMissileSummary()
""",
"""        /// <summary>自测用：给拦截弹（MSDM）拍近景/远景各一张。</summary>
        private void AmmoShot(int n)
        {
            try
            {
                MissileController m = _ammoMsdm;
                if (m != null && m.Finished) m = null;
                Vector3 p; Vector3 d;
                if (m != null) { p = m.Body.Pos; d = m.Body.Dir; _lastMPos = p; _lastMDir = d; _lastMT = Time.time; }
                else if (Time.time - _lastMT < 9f) { p = _lastMPos; d = _lastMDir; }
                else return;
                Vector3 side = Vector3.Cross(d, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                RenderAt(p - d * 15f + side * 7f + Vector3.up * 3f, p, "aam_msdm_" + n + "_chase.png");
                RenderAt(p - d * 55f + side * 30f + Vector3.up * 12f, p, "aam_msdm_" + n + "_trail.png");
                _api.Log("AAM AMMO: shot " + n + " at " + p);
            }
            catch (Exception e) { _api.Log("AAM AMMO: shot failed " + e.Message); }
        }

        private string LiveMissileSummary()
"""))

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
