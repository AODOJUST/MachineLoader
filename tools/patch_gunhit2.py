# -*- coding: utf-8 -*-
# 修正贴脸测试的提前量（tof = 距离/弹速，而不是写死 0.14s），并在弹到达前拍追踪照
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

reps.append((
"""                _gunPtAcc += Time.deltaTime * 20f;   // 20 发/秒
                while (_gunPtAcc >= 1f && _gunPtN < 12)
                {
                    _gunPtAcc -= 1f; _gunPtN++;
                    Vector3 tp = _gunPtTgt.position + _gunPtVel * 0.14f;   // 100m/713ms ≈ 0.14s 飞行
                    Vector3 from = tp - _gunPtTgt.forward * 60f - _gunPtTgt.up * 12f;
                    Vector3 dir = (tp - from).normalized;
                    MissileSpec g = GunSpec();
                    float mv = (g != null && g.MaxSpeed > 0f) ? g.MaxSpeed * 0.5144f : cfgGunMuzzle;
                    SpawnBullet(from, dir, mv, dir * mv, false);
                }""",
"""                _gunPtAcc += Time.deltaTime * 20f;   // 20 发/秒
                MissileSpec g8 = GunSpec();
                float mv8 = (g8 != null && g8.MaxSpeed > 0f) ? g8.MaxSpeed * 0.5144f : cfgGunMuzzle;
                while (_gunPtAcc >= 1f && _gunPtN < 12)
                {
                    _gunPtAcc -= 1f; _gunPtN++;
                    // 提前量 = 目标速度 x 弹程时间（距离/弹速），写死会差出几十米
                    Vector3 tp = _gunPtTgt.position + _gunPtVel * 0.086f;
                    Vector3 from = tp - _gunPtTgt.forward * 60f - _gunPtTgt.up * 12f;
                    float tof = Vector3.Distance(from, tp) / Mathf.Max(1f, mv8);
                    tp = _gunPtTgt.position + _gunPtVel * tof;
                    from = tp - _gunPtTgt.forward * 60f - _gunPtTgt.up * 12f;
                    tof = Vector3.Distance(from, tp) / Mathf.Max(1f, mv8);
                    tp = _gunPtTgt.position + _gunPtVel * tof;
                    Vector3 dir = (tp - from).normalized;
                    SpawnBullet(from, dir, mv8, dir * mv8, false);
                }
                if (cfgCapture && !_gunPtShot && _gunPtN >= 4)
                {
                    _gunPtShot = true;
                    BulletRound nb = BulletRound.Newest;
                    if (nb != null)
                    {
                        Vector3 p = nb.Predict(0.3f);
                        Vector3 d = nb.DirNow();
                        Vector3 side = Vector3.Cross(d, Vector3.up).normalized;
                        if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                        RenderAt(p - d * 14f + side * 7f + Vector3.up * 2f, p + d * 40f, "aam_gun_hit.png");
                    }
                }"""))

reps.append((
"""        private float _gunPtT;""",
"""        private float _gunPtT;
        private bool _gunPtShot;"""))

reps.append((
"""                _gunPtN = 0; _gunPtAcc = 0f;""",
"""                _gunPtN = 0; _gunPtAcc = 0f; _gunPtShot = false; _gunPtT = 0f;"""))


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
