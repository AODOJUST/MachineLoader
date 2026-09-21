# -*- coding: utf-8 -*-
# 一次性诊断：BulletHitTest 记录近距通过；ReportGunHit 记录拒绝原因
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

reps.append((
"""        internal bool BulletHitTest(Vector3 a, Vector3 b, BulletRound r)
        {
            try
            {
                for (int i = 0; i < _ai.Count; i++)
                {
                    AiAircraft ai = _ai[i];
                    if (ai == null) continue;
                    Transform tt = ai.transform;
                    if (tt == null) continue;
                    if (!SegSphere(a, b, tt.position, cfgGunHitRadius))
                        continue;
                    return MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);
                }
            }
            catch { }
            return false;
        }""",
"""        private int _gunDbgNear;   // 诊断计数（只打前几条，避免刷屏）

        internal bool BulletHitTest(Vector3 a, Vector3 b, BulletRound r)
        {
            try
            {
                for (int i = 0; i < _ai.Count; i++)
                {
                    AiAircraft ai = _ai[i];
                    if (ai == null) continue;
                    Transform tt = ai.transform;
                    if (tt == null) continue;

                    // 诊断：记录从目标 60m 内穿过的子弹（前 8 条）
                    if (_gunDbgNear < 8)
                    {
                        Vector3 ab = b - a;
                        float l2 = ab.sqrMagnitude;
                        float tq = (l2 > 0.0001f) ? Vector3.Dot(tt.position - a, ab) / l2 : 0f;
                        if (tq >= 0f && tq <= 1f)
                        {
                            Vector3 q = a + ab * tq;
                            float dm = Vector3.Distance(q, tt.position);
                            if (dm < 60f)
                            {
                                _gunDbgNear++;
                                _api.Log("AAM GUN: bullet passed " + dm.ToString("F1") + "m from target "
                                         + ai.Callsign + " bulletPos=" + q.ToString("F1")
                                         + " tgtPos=" + tt.position.ToString("F1"));
                            }
                        }
                    }

                    if (!SegSphere(a, b, tt.position, cfgGunHitRadius))
                        continue;
                    return MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);
                }
            }
            catch { }
            return false;
        }"""))

reps.append((
"""                if (root == null) return false;
                if (IsFriendlyTo(root, kFaction)) return false;

                const BindingFlags FG = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                PartExploder ex = root.GetComponentInParent<PartExploder>();
                PlanePart[] parts = root.GetComponentsInChildren<PlanePart>(true);
                if (parts == null || parts.Length == 0) return false;""",
"""                if (root == null) return false;
                bool friendly = IsFriendlyTo(root, kFaction);
                PartExploder exC = root.GetComponentInParent<PartExploder>();
                PlanePart[] partsC = root.GetComponentsInChildren<PlanePart>(true);
                Machine.Core.Log.Info("[AAM] gun hit try: root=" + root.name
                                      + " friendly=" + friendly
                                      + " kFaction=" + kFaction
                                      + " partExploder=" + (exC != null)
                                      + " parts=" + (partsC != null ? partsC.Length : -1));
                if (friendly) return false;

                const BindingFlags FG = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                PartExploder ex = exC;
                PlanePart[] parts = partsC;
                if (parts == null || parts.Length == 0) return false;"""))


ok = 0
for old, new in reps:
    o = old.replace("\n", NL); n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d anchor:\n%s\n---" % (c, o[:130])); continue
    s = s.replace(o, n, 1); ok += 1

print("applied %d / %d" % (ok, len(reps)))
if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f: f.write(s)
    print("WROTE", len(s), "chars")
else:
    print("NOT WRITTEN")
