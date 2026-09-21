# -*- coding: utf-8 -*-
# 1) ReportGunHit 记录 best 选择与异常详情；2) 命中即消耗子弹（避免每帧重复重扫部件）
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

reps.append((
"""                if (best == null) return false;

                if (ex != null)
                {
                    MethodInfo mm = typeof(PartExploder).GetMethod("ExplodePart");
                    if (mm != null) mm.Invoke(ex, new object[] { best, true, false });
                }

                bool exploded = false;""",
"""                if (best == null)
                {
                    Machine.Core.Log.Info("[AAM] gun hit ABORT: all " + parts.Length + " parts are base parts");
                    return false;
                }

                if (ex != null)
                {
                    try
                    {
                        MethodInfo mm = typeof(PartExploder).GetMethod("ExplodePart");
                        if (mm != null) mm.Invoke(ex, new object[] { best, true, false });
                    }
                    catch (Exception exx)
                    {
                        string msg = exx.Message;
                        try { if (exx.InnerException != null) msg += " | inner=" + exx.InnerException.Message; } catch { }
                        Machine.Core.Log.Info("[AAM] gun ExplodePart FAILED on '" + best.name + "': " + msg);
                    }
                }

                bool exploded = false;"""))

# 命中即消耗子弹：ReportGunHit 返回 false（非友军情况下）也视为命中消耗
reps.append((
"""                    if (!SegSphere(a, b, tt.position, cfgGunHitRadius))
                        continue;
                    return MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);""",
"""                    if (!SegSphere(a, b, tt.position, cfgGunHitRadius))
                        continue;
                    bool friendly = MissileController.IsFriendlyTo(tt, r.FactionId);
                    if (friendly) return false;          // 友军：穿过去不打
                    MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);
                    return true;                          // 非友军命中：无论拆件成败都消耗这发弹"""))


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
