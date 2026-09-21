# -*- coding: utf-8 -*-
# 截图镜头直接追踪最新子弹的预测位置（CaptureScreenshot 异步滞后 ~0.3s）
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# 1) BulletRound：Newest 引用 + 预测/方向接口
reps.append((
"""        public static int LiveCount = 0;
        public static int SpawnedCount = 0;   // 本次会话累计生成数（诊断双发用）
""",
"""        public static int LiveCount = 0;
        public static int SpawnedCount = 0;   // 本次会话累计生成数（诊断双发用）
        public static BulletRound Newest;      // 最新一颗（自测镜头追踪用）
"""))

reps.append((
"""            ShooterCall = call; ShooterModel = model; FactionId = faction;
            SpawnedCount++;
            transform.position = pts[0];""",
"""            ShooterCall = call; ShooterModel = model; FactionId = faction;
            SpawnedCount++;
            Newest = this;
            transform.position = pts[0];"""))

reps.append((
"""        private Vector3 Sample(float t)
        {
            float f = t / _dt;
            int i = (int)f;
            if (i < 0) i = 0;
            if (i >= _pts.Length - 1) return _pts[_pts.Length - 1];
            return Vector3.Lerp(_pts[i], _pts[i + 1], f - i);
        }
""",
"""        private Vector3 Sample(float t)
        {
            float f = t / _dt;
            int i = (int)f;
            if (i < 0) i = 0;
            if (i >= _pts.Length - 1) return _pts[_pts.Length - 1];
            return Vector3.Lerp(_pts[i], _pts[i + 1], f - i);
        }

        /// <summary>预测 ahead 秒后的位置（截图有异步滞后，镜头要对准"将来"的点）。</summary>
        public Vector3 Predict(float ahead) { return Sample(_t + ahead); }

        /// <summary>当前航向（近似）。</summary>
        public Vector3 DirNow()
        {
            Vector3 d = Sample(_t + 0.05f) - Sample(_t);
            if (d.sqrMagnitude < 0.0001f) return transform.forward;
            return d.normalized;
        }
"""))

# 2) GunShot：改用最新弹的预测位置取景
reps.append((
"""                Transform t = _plane.transform;
                Vector3 f = t.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) f = t.forward;
                f.Normalize();
                Vector3 side = Vector3.Cross(f, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                Vector3 muzzle = t.position + f * NoseDistance();
                Vector3 look = muzzle + f * 400f;
                RenderAt(muzzle + f * 180f + side * 20f + Vector3.up * 5f, look, "aam_gun_burst.png");
                RenderAt(muzzle + f * 70f + side * 7f + Vector3.up * 2f, muzzle + f * 150f, "aam_gun_close.png");
                _api.Log("AAM GUN: shots taken");""",
"""                BulletRound nb = BulletRound.Newest;
                if (nb != null)
                {
                    // 镜头直接跟着最新一颗弹：考虑到 CaptureScreenshot 有 ~0.3s 异步滞后，
                    // 对准它 0.35s 后的预测位置。
                    Vector3 p = nb.Predict(0.35f);
                    Vector3 d = nb.DirNow();
                    Vector3 side = Vector3.Cross(d, Vector3.up).normalized;
                    if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                    RenderAt(p - d * 16f + side * 8f + Vector3.up * 2.5f, p + d * 40f, "aam_gun_close.png");
                    Vector3 p2 = nb.Predict(0.35f);
                    RenderAt(p2 - d * 45f + side * 22f + Vector3.up * 7f, p2 + d * 120f, "aam_gun_burst.png");
                    _api.Log("AAM GUN: tracking shot at " + p + " spd~" + d.magnitude);
                }
                else
                {
                    Transform t = _plane.transform;
                    Vector3 f = t.forward; f.y = 0f;
                    if (f.sqrMagnitude < 0.01f) f = t.forward;
                    f.Normalize();
                    RenderAt(t.position - f * 60f + Vector3.up * 14f, t.position + f * 900f, "aam_gun_burst.png");
                    _api.Log("AAM GUN: no bullet to track, fallback shot");
                }"""))


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
