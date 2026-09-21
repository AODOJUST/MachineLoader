# -*- coding: utf-8 -*-
# 机炮诊断：记录真实生成数 / 在屏对象数；截图提前到弹流密集时
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# 1) BulletRound 加 SpawnedCount
reps.append((
"""        public static int LiveCount = 0;
""",
"""        public static int LiveCount = 0;
        public static int SpawnedCount = 0;   // 本次会话累计生成数（诊断双发用）
"""))

reps.append((
"""            ShooterCall = call; ShooterModel = model; FactionId = faction;
            transform.position = pts[0];""",
"""            ShooterCall = call; ShooterModel = model; FactionId = faction;
            SpawnedCount++;
            transform.position = pts[0];"""))

# 2) done 日志带上真实在屏对象数
reps.append((
"""                _api.Log("AAM GUN: done. bullets still live=" + BulletRound.LiveCount
                         + " gun rounds left=" + CombatCountOfType(GunCargo()));""",
"""                int realLive = 0;
                try { realLive = UnityEngine.Object.FindObjectsOfType<BulletRound>(true).Length; } catch { }
                _api.Log("AAM GUN: done. counter live=" + BulletRound.LiveCount
                         + " spawned=" + BulletRound.SpawnedCount
                         + " objectsInScene=" + realLive
                         + " gun rounds left=" + CombatCountOfType(GunCargo()));"""))

# 3) 连射中段拍一张近景弹流照（0.45s 时弹头约在 250~320m 处）
reps.append((
"""                if (_gunBurstT > 1.2f)
                {
                    _ammoStage = 7; _ammoT = 0f;""",
"""                if (cfgCapture && !_gunShotDone && _gunBurstT > 0.45f)
                {
                    _gunShotDone = true;
                    GunShot();
                }
                if (_gunBurstT > 1.2f)
                {
                    _ammoStage = 7; _ammoT = 0f;"""))

reps.append((
"""        private int _gunBurstFpsN;""",
"""        private int _gunBurstFpsN;
        private bool _gunShotDone;"""))

# 4) 近景机位：沿机头方向侧后方 25m、高 5m，看向 400m 外
reps.append((
"""                Transform t = _plane.transform;
                Vector3 f = t.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) f = t.forward;
                f.Normalize();
                RenderAt(t.position - f * 60f + Vector3.up * 14f, t.position + f * 900f, "aam_gun_burst.png");
                _api.Log("AAM GUN: shot taken");""",
"""                Transform t = _plane.transform;
                Vector3 f = t.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) f = t.forward;
                f.Normalize();
                Vector3 side = Vector3.Cross(f, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                Vector3 muzzle = t.position + f * NoseDistance();
                Vector3 look = muzzle + f * 400f;
                RenderAt(muzzle + f * 230f + side * 26f + Vector3.up * 6f, look, "aam_gun_burst.png");
                RenderAt(muzzle + f * 120f + side * 8f + Vector3.up * 2.5f, muzzle + f * 300f, "aam_gun_close.png");
                _api.Log("AAM GUN: shots taken");"""))

# 5) 每次 burst 开始时清零计数器（同会话多次跑测试不叠加）
reps.append((
"""                _gunBurstN = 0; _gunBurstT = 0f; _gunBurstFps = 0f; _gunBurstFpsN = 0;""",
"""                _gunBurstN = 0; _gunBurstT = 0f; _gunBurstFps = 0f; _gunBurstFpsN = 0; _gunShotDone = false;
                BulletRound.SpawnedCount = 0;"""))


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
