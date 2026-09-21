# -*- coding: utf-8 -*-
# 命中判定确定性验证：在 AI 靶机侧后方 100m 直接朝它生成短点射（必中），
# 同时保留对空瞄准点射；截图镜头贴到弹道正侧方。
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# 1) 抽出 SpawnBullet 核心（FireGunRound 调它）
reps.append((
"""        /// <summary>打一发机炮：预计算航迹 -> 实例化子弹 -> 之后完全靠插值飞行。</summary>
        public void FireGunRound()
        {
            try
            {
                if (_plane == null) return;
                MissileSpec gun = GunSpec();
                CargoType cargo = GunCargo();
                if (gun == null || cargo == null) return;
                if (BulletRound.LiveCount >= cfgGunMaxLive) return;

                int have = CombatCountOfType(cargo);
                if (have <= 0)
                {
                    if (Time.time - _gunEmptyT > 2.5f)
                    {
                        _gunEmptyT = Time.time;
                        Flash("NO GUN AMMO", 1f);
                        _api.Log("AAM: gun dry (0 rounds in hold)");
                    }
                    return;
                }
                CombatTakeOf(cargo, 1);

                Transform t = _plane.transform;
                Vector3 fwd = t.forward;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();

                // 小角度散布
                Vector3 dir = fwd;
                if (cfgGunSpread > 0f)
                {
                    float a = UnityEngine.Random.Range(-cfgGunSpread, cfgGunSpread);
                    float b = UnityEngine.Random.Range(-cfgGunSpread, cfgGunSpread);
                    dir = Quaternion.AngleAxis(a, t.up) * dir;
                    dir = Quaternion.AngleAxis(b, t.right) * dir;
                }
                dir.Normalize();

                float muzzle = (gun.MaxSpeed > 0f) ? gun.MaxSpeed * 0.5144f : cfgGunMuzzle;
                Vector3 start = t.position + fwd * NoseDistance() + Vector3.up * 0.4f;
                Vector3 v0 = _plane.GetVelocity() + dir * muzzle;
""",
"""        /// <summary>打一发机炮（玩家路径：扣弹药、加散布、从机口射出）。</summary>
        public void FireGunRound()
        {
            try
            {
                if (_plane == null) return;
                MissileSpec gun = GunSpec();
                CargoType cargo = GunCargo();
                if (gun == null || cargo == null) return;
                if (BulletRound.LiveCount >= cfgGunMaxLive) return;

                int have = CombatCountOfType(cargo);
                if (have <= 0)
                {
                    if (Time.time - _gunEmptyT > 2.5f)
                    {
                        _gunEmptyT = Time.time;
                        Flash("NO GUN AMMO", 1f);
                        _api.Log("AAM: gun dry (0 rounds in hold)");
                    }
                    return;
                }
                CombatTakeOf(cargo, 1);

                Transform t = _plane.transform;
                Vector3 fwd = t.forward;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();

                // 小角度散布
                Vector3 dir = fwd;
                if (cfgGunSpread > 0f)
                {
                    float a = UnityEngine.Random.Range(-cfgGunSpread, cfgGunSpread);
                    float b = UnityEngine.Random.Range(-cfgGunSpread, cfgGunSpread);
                    dir = Quaternion.AngleAxis(a, t.up) * dir;
                    dir = Quaternion.AngleAxis(b, t.right) * dir;
                }
                dir.Normalize();

                float muzzle = (gun.MaxSpeed > 0f) ? gun.MaxSpeed * 0.5144f : cfgGunMuzzle;
                Vector3 start = t.position + fwd * NoseDistance() + Vector3.up * 0.4f;
                Vector3 v0 = _plane.GetVelocity() + dir * muzzle;
                SpawnBullet(start, dir, muzzle, v0, true);
            }
            catch (Exception e) { _api.Log("AAM: gun round failed " + e.Message); }
        }

        /// <summary>
        /// 机炮核心：预计算航迹 -> 实例化子弹 -> 之后完全靠插值飞行（零物理、零寻的）。
        /// consumeAmmo=false 供自测直接调用（命中判定验证）。
        /// </summary>
        public void SpawnBullet(Vector3 start, Vector3 dir, float muzzle, Vector3 v0, bool consumeAmmo)
        {
            try
            {
"""))

# 2) 原来函数尾部的收尾改成核心函数自己的
reps.append((
"""                // ---- 一次算完整条航迹（只有重力 + 极简空气阻力），之后不再做任何物理 ----
                int n = Mathf.Max(4, Mathf.CeilToInt(cfgGunLife / cfgGunStep));
                Vector3[] pts = new Vector3[n + 1];
                Vector3 p = start, v = v0;
                pts[0] = p;
                for (int i = 1; i <= n; i++)
                {
                    v += (Physics.gravity - v * (cfgGunDrag * v.magnitude)) * cfgGunStep;
                    p += v * cfgGunStep;
                    pts[i] = p;
                }

                GameObject proto = BulletProto();
                if (proto == null) return;
                GameObject go = Instantiate(proto);
                go.name = "AAM_Bullet";
                go.transform.SetParent(null, false);
                go.transform.localScale = proto.transform.localScale * cfgBulletScale;
                go.SetActive(true);

                BulletRound br = go.GetComponent<BulletRound>();
                if (br == null) br = go.AddComponent<BulletRound>();
                br.Setup(pts, cfgGunStep, cfgGunLife, PlayerCallSafe(), PlayerModelSafe(), GetPlayerFactionId());
            }
            catch (Exception e) { _api.Log("AAM: gun round failed " + e.Message); }
        }
""",
"""                // ---- 一次算完整条航迹（只有重力 + 极简空气阻力），之后不再做任何物理 ----
                int n = Mathf.Max(4, Mathf.CeilToInt(cfgGunLife / cfgGunStep));
                Vector3[] pts = new Vector3[n + 1];
                Vector3 p = start, v = v0;
                pts[0] = p;
                for (int i = 1; i <= n; i++)
                {
                    v += (Physics.gravity - v * (cfgGunDrag * v.magnitude)) * cfgGunStep;
                    p += v * cfgGunStep;
                    pts[i] = p;
                }

                GameObject proto = BulletProto();
                if (proto == null) return;
                GameObject go = Instantiate(proto);
                go.name = "AAM_Bullet";
                go.transform.SetParent(null, false);
                go.transform.localScale = proto.transform.localScale * cfgBulletScale;
                go.SetActive(true);

                BulletRound br = go.GetComponent<BulletRound>();
                if (br == null) br = go.AddComponent<BulletRound>();
                br.Setup(pts, cfgGunStep, cfgGunLife, PlayerCallSafe(), PlayerModelSafe(), GetPlayerFactionId());
            }
            catch (Exception e) { _api.Log("AAM: gun round failed " + e.Message); }
        }
"""))

# 3) 自测加"贴脸点射"阶段（7=瞄准远目标点射，8=贴脸验证命中，9=收尾）
reps.append((
"""            if (_ammoStage == 7 && _ammoT > 2.0f)
            {
                _ammoStage = 8;
""",
"""            // 7) 贴脸点射：在 AI 靶机侧后方 100m 直接朝它打 12 发（必中，验证命中判定与拆件）
            if (_ammoStage == 7)
            {
                if (_ammoT < 0.8f) return;
                _ammoStage = 8; _ammoT = 0f;
                AiAircraft tgt0 = (_ai.Count > 0) ? _ai[0] : null;
                if (tgt0 == null) { _api.Log("AAM GUN: no AI target, skip point-blank test"); return; }
                _gunPtTgt = tgt0.transform;
                _gunPtLast = _gunPtTgt.position;
                _gunPtVel = Vector3.zero;
                _gunPtN = 0; _gunPtAcc = 0f;
                _api.Log("AAM GUN: point-blank test at " + tgt0.Callsign
                         + " d=" + Vector3.Distance(_gunPtTgt.position, _plane.transform.position).ToString("F0") + "m");
                return;
            }

            if (_ammoStage == 8)
            {
                _gunPtT += Time.deltaTime;
                if (_gunPtTgt == null) { _ammoStage = 9; _ammoT = 0f; return; }
                if (Time.deltaTime > 0.0001f)
                    _gunPtVel = (_gunPtTgt.position - _gunPtLast) / Time.deltaTime;
                _gunPtLast = _gunPtTgt.position;
                _gunPtAcc += Time.deltaTime * 20f;   // 20 发/秒
                while (_gunPtAcc >= 1f && _gunPtN < 12)
                {
                    _gunPtAcc -= 1f; _gunPtN++;
                    Vector3 tp = _gunPtTgt.position + _gunPtVel * 0.14f;   // 100m/713ms ≈ 0.14s 飞行
                    Vector3 from = tp - _gunPtTgt.forward * 60f - _gunPtTgt.up * 12f;
                    Vector3 dir = (tp - from).normalized;
                    MissileSpec g = GunSpec();
                    float mv = (g != null && g.MaxSpeed > 0f) ? g.MaxSpeed * 0.5144f : cfgGunMuzzle;
                    SpawnBullet(from, dir, mv, dir * mv, false);
                }
                if (_gunPtN >= 12 || _gunPtT > 1.2f)
                {
                    _ammoStage = 9; _ammoT = 0f;
                    _api.Log("AAM GUN: point-blank fired " + _gunPtN + " rounds at " + (_gunPtTgt != null ? _gunPtTgt.name : "gone"));
                }
                return;
            }

            if (_ammoStage == 9 && _ammoT > 2.5f)
            {
                _ammoStage = 10;
"""))

reps.append((
"""        private bool _gunShotDone;""",
"""        private bool _gunShotDone;
        private Transform _gunPtTgt;
        private Vector3 _gunPtLast;
        private Vector3 _gunPtVel;
        private int _gunPtN;
        private float _gunPtAcc;
        private float _gunPtT;"""))


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
