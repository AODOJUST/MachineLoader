# -*- coding: utf-8 -*-
# 1) 尾部特效按模型实际包围盒对位（+Z 机头 -> 尾端 = 局部 z 最小），并按弹体长度缩放
# 2) 机炮：右键点射 / 长按连射 52 发每秒；子弹无制导，发射瞬间预计算整条航迹后沿航线插值
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []


def R(old, new):
    reps.append((old, new))


# ---------------------------------------------------------------- 1) 尾焰/烟轨对位
R("""                // 取尾部锚点：优先用调用方传入的 dummy；否则在 root 局部坐标 -Z 方向 1.6m 处造一个
                Transform anchor = dummy;
                if (anchor == null)
                {
                    GameObject aGo = new GameObject("AAM_TrailAnchor");
                    aGo.transform.SetParent(root.transform, false);
                    aGo.transform.localPosition = new Vector3(0f, 0f, -1.6f);
                    anchor = aGo.transform;
                }
""",
"""                // 尾部锚点：按模型实际包围盒算（+Z 是机头方向，所以尾端 = 局部 z 最小值）。
                //   不能再写死 -1.6m：实测 R-77 / PL-15 / AIM-9X 的尾端都在局部 -4.81m（特效整段埋在
                //   弹体里），MSDM 尾端在 -0.33m（特效掉在屁股后面 1.3m），机炮子弹尾端在 +0.93m。
                Bounds lb = LocalBounds(root);
                float lenZ = lb.size.z;
                if (lenZ < 1.2f)
                {
                    // 机炮子弹这类极小弹体：不挂尾焰/烟轨（52 发/秒挂粒子会直接拖垮帧率）
                    Machine.Core.Log.Info("[AAM] trail skipped, model too small len=" + lenZ.ToString("F2") + "m");
                    return;
                }
                // 特效尺度随弹体长度缩放：MSDM(3.9m) 的烟团要比 PL-15(12.5m) 小得多
                float fx = Mathf.Clamp(lenZ / 12f, 0.35f, 1.15f);

                Transform anchor = dummy;
                if (anchor == null)
                {
                    GameObject aGo = new GameObject("AAM_TrailAnchor");
                    aGo.transform.SetParent(root.transform, false);
                    aGo.transform.localPosition = new Vector3(0f, 0f, lb.min.z - 0.12f);
                    anchor = aGo.transform;
                }
                Machine.Core.Log.Info("[AAM] trail anchor local z=" + lb.min.z.ToString("F2")
                                      + " len=" + lenZ.ToString("F2") + " fxScale=" + fx.ToString("F2"));
""")

R("""                core.widthCurve = new AnimationCurve(new Keyframe(0f, 0.42f), new Keyframe(0.6f, 0.18f), new Keyframe(1f, 0.02f));""",
"""                core.widthCurve = new AnimationCurve(new Keyframe(0f, 0.42f * fx), new Keyframe(0.6f, 0.18f * fx), new Keyframe(1f, 0.02f * fx));""")

R("""                BuildSmokeTrail(root, anchor);""",
"""                BuildSmokeTrail(root, anchor, fx);""")

R("""        private static void BuildSmokeTrail(GameObject root, Transform anchor)""",
"""        private static void BuildSmokeTrail(GameObject root, Transform anchor, float fx)""")

R("""                main.startSize = new ParticleSystem.MinMaxCurve(3.4f, 5.6f);       // 尾迹主体：烟团要够大才看得见""",
"""                main.startSize = new ParticleSystem.MinMaxCurve(3.4f * fx, 5.6f * fx);   // 随弹体长度缩放""")

R("""                shape.radius = 0.12f;""",
"""                shape.radius = 0.12f * fx;""")

# 局部包围盒工具（放在 AddTrail 之前）
R("""        private static void AddTrail(GameObject root, Transform dummy)""",
"""        /// <summary>
        /// 模型在自身局部坐标下的合并包围盒（游戏里 +Z 是机头方向）。
        /// 跳过粒子渲染器：设计里的引擎尾焰粒子会把包围盒撑得离谱。
        /// </summary>
        private static Bounds LocalBounds(GameObject root)
        {
            Bounds fallback = new Bounds(Vector3.zero, new Vector3(2f, 1f, 10f));  // 兜底：10m 长弹体
            try
            {
                if (root == null) return fallback;
                Renderer[] rr = root.GetComponentsInChildren<Renderer>(true);
                if (rr == null || rr.Length == 0) return fallback;
                Transform rt = root.transform;
                Bounds lb = new Bounds();
                bool has = false;
                for (int i = 0; i < rr.Length; i++)
                {
                    Renderer r = rr[i];
                    if (r == null) continue;
                    if (r.GetComponent<ParticleSystem>() != null) continue;   // 尾焰/烟雾粒子不算进弹体
                    Bounds wb = r.bounds;
                    if (wb.size.x <= 0f && wb.size.y <= 0f && wb.size.z <= 0f) continue;
                    Vector3 c = rt.InverseTransformPoint(wb.center);
                    Vector3 e = rt.InverseTransformDirection(wb.extents);
                    if (e.x < 0f) e.x = -e.x; if (e.y < 0f) e.y = -e.y; if (e.z < 0f) e.z = -e.z;
                    if (!has) { lb = new Bounds(c, new Vector3(e.x * 2f, e.y * 2f, e.z * 2f)); has = true; }
                    else { lb.Encapsulate(c - e); lb.Encapsulate(c + e); }
                }
                if (!has) return fallback;
                if (lb.size.z < 0.2f || lb.size.z > 60f) return fallback;   // 明显不对就用兜底
                return lb;
            }
            catch { return fallback; }
        }

        private static void AddTrail(GameObject root, Transform dummy)""")

# ---------------------------------------------------------------- 2) 友军判定 / 击杀登记 提升可见性
R("""        private bool IsFriendlyTarget(Transform target)""",
"""        internal static bool IsFriendlyTarget(Transform target)""")

R("""        private static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)""",
"""        internal static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)""")

# 机炮命中：只炸一个部件 + 登记击杀（放在 FeedKill 之前）
R("""        internal static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)""",
"""        /// <summary>
        /// 机炮命中：只炸掉离弹着点最近的那一个部件（不像导弹那样直接引爆整机），
        /// 并在目标因此坠毁时登记击杀。由 AamSystem 的子弹命中检测调用。
        /// </summary>
        internal static bool ReportGunHit(Transform root, Vector3 at, string kModel, string kCall, int kFaction)
        {
            try
            {
                if (root == null) return false;
                if (IsFriendlyTarget(root)) return false;

                PartExploder ex = root.GetComponentInParent<PartExploder>();
                PlanePart[] parts = root.GetComponentsInChildren<PlanePart>(true);
                if (parts == null || parts.Length == 0) return false;

                PlanePart best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < parts.Length; i++)
                {
                    PlanePart p = parts[i];
                    if (p == null) continue;
                    bool isBase = false;
                    try
                    {
                        FieldInfo fb = p.GetType().GetField("isBasePart", F);
                        if (fb != null) isBase = (bool)fb.GetValue(p);
                    }
                    catch { }
                    if (isBase) continue;
                    float d = (p.transform.position - at).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = p; }
                }
                if (best == null) return false;

                if (ex != null)
                {
                    MethodInfo mm = typeof(PartExploder).GetMethod("ExplodePart");
                    if (mm != null) mm.Invoke(ex, new object[] { best, true, false });
                }

                bool exploded = false;
                var pc = root.GetComponentInParent<PlaneController>();
                if (pc != null)
                {
                    var ep = pc.GetType().GetProperty("Exploded");
                    if (ep != null) exploded = (bool)ep.GetValue(pc, null);
                }
                var ai = root.GetComponentInParent<AiAircraft>();
                if (exploded || (ai != null && best.name.Contains("Body")))
                {
                    Machine.Core.Log.Info("[AAM] gun hit part '" + best.name + "' on " + root.name + " -> DOWN");
                    string vModel = "Aircraft", vCall = "BANDIT";
                    if (ai != null) { vModel = ai.ModelName; vCall = ai.Callsign; }
                    int vf = -1;
                    var mk = FindFactionMarker(root);
                    if (mk != null)
                    {
                        var ff = mk.GetType().GetField("FactionId");
                        if (ff != null) vf = (int)ff.GetValue(mk);
                    }
                    try
                    {
                        var ft = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                        if (ft != null)
                        {
                            var om = ft.GetMethod("OnKill", new Type[] { typeof(int), typeof(int) });
                            if (om != null) om.Invoke(null, new object[] { kFaction, vf });
                        }
                    }
                    catch { }
                    FeedKill(kModel, kCall, kFaction, vModel, vCall, vf);
                }
                return true;
            }
            catch { return false; }
        }

        internal static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)""")

# ---------------------------------------------------------------- 3) BulletRound 类
R("""    internal static class AiAircraftFactory""",
"""    // =====================================================================
    // 机炮子弹：无制导。发射瞬间用简单物理模型(重力+空气阻力)把整条航迹一次算完，
    // 之后每帧只在折线上做插值 —— 没有刚体、没有寻的、没有粒子，52 发/秒也很轻。
    // =====================================================================
    public class BulletRound : MonoBehaviour
    {
        private Vector3[] _pts;
        private float _dt;
        private float _life;
        private float _t;
        private Vector3 _prev;

        public string ShooterCall = "You";
        public string ShooterModel = "PlayerAircraft";
        public int FactionId = 0;

        public static int LiveCount = 0;

        private void OnEnable() { LiveCount++; }
        private void OnDisable() { LiveCount--; }

        public void Setup(Vector3[] pts, float dt, float life, string call, string model, int faction)
        {
            _pts = pts; _dt = dt; _life = life; _t = 0f;
            _prev = pts[0];
            ShooterCall = call; ShooterModel = model; FactionId = faction;
            transform.position = pts[0];
            if (pts.Length > 1)
            {
                Vector3 d = pts[1] - pts[0];
                if (d.sqrMagnitude > 0.0001f) transform.rotation = PnGuidance.SafeLook(d.normalized, Vector3.up);
            }
        }

        private void Update()
        {
            try
            {
                if (_pts == null || _pts.Length < 2) { Destroy(gameObject); return; }
                _t += Time.deltaTime;
                if (_t >= _life) { Destroy(gameObject); return; }

                Vector3 np = Sample(_t);
                AamSystem sys = AamSystem.Live;
                if (sys != null && sys.BulletHitTest(_prev, np, this)) { Destroy(gameObject); return; }

                Vector3 dd = np - _prev;
                if (dd.sqrMagnitude > 0.0001f) transform.rotation = PnGuidance.SafeLook(dd.normalized, Vector3.up);
                _prev = np;
                transform.position = np;
            }
            catch { Destroy(gameObject); }
        }

        private Vector3 Sample(float t)
        {
            float f = t / _dt;
            int i = (int)f;
            if (i < 0) i = 0;
            if (i >= _pts.Length - 1) return _pts[_pts.Length - 1];
            return Vector3.Lerp(_pts[i], _pts[i + 1], f - i);
        }
    }

    internal static class AiAircraftFactory""")

# ---------------------------------------------------------------- 4) AamSystem 配置字段
R("""        private bool cfgTestAmmo = false;        // testAmmo：弹药模型 + MSDM 拦截弹专项自测""",
"""        private bool cfgTestAmmo = false;        // testAmmo：弹药模型 + MSDM 拦截弹专项自测

        // ---- 机炮（右键点射 / 长按连射）----
        private bool cfgGunEnabled = true;
        private float cfgGunRps = 52f;           // 射速：发/秒
        private float cfgGunHoldDelay = 0.18f;   // 按住超过这么久才转入连射（短按 = 点射 1 发）
        private float cfgGunMuzzle = 713f;       // 初速 m/s（弹种表里 1386kt ≈ 2.1 马赫）
        private float cfgGunLife = 3.0f;         // 子弹寿命 s（预计算航迹长度）
        private float cfgGunStep = 0.08f;        // 航迹预计算步长 s
        private float cfgGunDrag = 0.00002f;     // 简单空气阻力系数（v * (1 - drag*|v|)）
        private float cfgGunSpread = 0.35f;      // 散布角（度）
        private int cfgGunMaxLive = 240;         // 同屏最多子弹数（保险丝）
        private float cfgGunHitRadius = 14f;     // 命中判定球半径 m
        private float cfgBulletScale = 3f;       // 子弹模型放大倍数（0.3m 的原模型看不见）""")

R("""                cfgTestAmmo = root.GetBool("testAmmo", cfgTestAmmo);""",
"""                cfgTestAmmo = root.GetBool("testAmmo", cfgTestAmmo);
                cfgGunEnabled = root.GetBool("gunEnabled", cfgGunEnabled);
                cfgGunRps = root.GetFloat("gunRoundsPerSecond", cfgGunRps);
                cfgGunHoldDelay = root.GetFloat("gunHoldDelay", cfgGunHoldDelay);
                cfgGunMuzzle = root.GetFloat("gunMuzzleSpeed", cfgGunMuzzle);
                cfgGunLife = root.GetFloat("gunBulletLife", cfgGunLife);
                cfgGunDrag = root.GetFloat("gunDrag", cfgGunDrag);
                cfgGunSpread = root.GetFloat("gunSpreadDeg", cfgGunSpread);
                cfgGunMaxLive = root.GetInt("gunMaxLive", cfgGunMaxLive);
                cfgGunHitRadius = root.GetFloat("gunHitRadius", cfgGunHitRadius);
                cfgBulletScale = root.GetFloat("gunBulletScale", cfgBulletScale);""")

# ---------------------------------------------------------------- 5) Update 挂上机炮输入
R("""                HandleFireInput();
                StepAiAircraft();""",
"""                HandleFireInput();
                HandleGunInput();
                StepAiAircraft();""")

# ---------------------------------------------------------------- 6) 右键不再触发导弹发射
R("""            bool down = false;
            // 主发射键：鼠标右键【双击】发射（两次按下间隔 < 0.4s）；F 键保留为兼容备用（单击）
            try
            {
                if (UnityEngine.Input.GetMouseButtonDown(1))
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _lastRmbDown < 0.4f) down = true;
                    _lastRmbDown = now;
                }
            }
            catch { }
""",
"""            bool down = false;
            // 鼠标右键已改为【机炮】：点射 1 发 / 按住连射（见 HandleGunInput）。
            // 导弹发射只走 fireKey（默认 F 键）。
""")

# ---------------------------------------------------------------- 7) 机炮实现
R("""        private void HandleFireInput()""",
"""        /// <summary>战斗货仓里指定弹种增减（机炮用，避免为了打炮改玩家选中的弹种）。</summary>
        private int CombatCountOfType(CargoType type)
        {
            if (type == null) return 0;
            return CombatCountOf(type);
        }

        private bool CombatTakeOf(CargoType type, int n)
        {
            if (_bhRemove == null || type == null) return false;
            try { return (bool)_bhRemove.Invoke(null, new object[] { type, n }); }
            catch { return false; }
        }

        private bool CombatGiveOf(CargoType type, int n)
        {
            if (_bhAdd == null || type == null) return false;
            try { return (bool)_bhAdd.Invoke(null, new object[] { type, n }); }
            catch { return false; }
        }

        private MissileSpec GunSpec()
        {
            int i = IndexOfSpec("Gun");
            return (i >= 0) ? _specs[i] : null;
        }

        private CargoType GunCargo()
        {
            int i = IndexOfSpec("Gun");
            if (i < 0 || i >= _missileTypes.Count) return null;
            return _missileTypes[i];
        }

        // ---------- 机炮输入：右键点射 / 长按连射 ----------
        private float _gunAcc;
        private float _gunHoldT;
        private bool _gunWasDown;
        private float _gunEmptyT = -99f;

        private void HandleGunInput()
        {
            if (!cfgGunEnabled) return;
            if (_plane == null || !_plane.FlightModeInitialized) return;

            bool rmb = false;
            try { rmb = UnityEngine.Input.GetMouseButton(1); } catch { return; }

            if (!rmb) { _gunWasDown = false; _gunHoldT = 0f; _gunAcc = 0f; return; }

            bool pressed = false;
            try { pressed = UnityEngine.Input.GetMouseButtonDown(1); } catch { }

            if (pressed)
            {
                // 点射：按下的这一帧先打 1 发
                _gunWasDown = true; _gunHoldT = 0f; _gunAcc = 0f;
                FireGunRound();
                return;
            }
            if (!_gunWasDown) { _gunWasDown = true; _gunHoldT = 0f; _gunAcc = 0f; }

            _gunHoldT += Time.deltaTime;
            if (_gunHoldT < cfgGunHoldDelay) return;      // 短按就只是点射那一发

            _gunAcc += Time.deltaTime * cfgGunRps;
            int n = (int)_gunAcc;
            if (n <= 0) return;
            _gunAcc -= n;
            if (n > 8) n = 8;                             // 掉帧时不要一次补一大堆
            for (int i = 0; i < n; i++) FireGunRound();
        }

        private GameObject _bulletProto;
        private float _noseDist = -1f;
        private Transform _nosePlane;

        private GameObject BulletProto()
        {
            if (_bulletProto != null) return _bulletProto;   // 被场景切换销毁时 Unity 的 == 会判 null，自动重建
            try
            {
                MissileSpec gun = GunSpec();
                if (gun == null) return null;
                string p = DesignPathFor(gun);
                GameObject m = DesignModel.BuildWithGameLoader(p, "AAM_BulletProto", _api);
                if (m == null) m = DesignModel.BuildFallback(p, "AAM_BulletProto", _api);
                if (m == null) { _api.Log("AAM: bullet model unavailable " + p); return null; }
                m.SetActive(false);
                _bulletProto = m;
                _api.Log("AAM: bullet proto ready from " + p);
            }
            catch (Exception e) { _api.Log("AAM: bullet proto failed " + e.Message); }
            return _bulletProto;
        }

        /// <summary>机头前方多远才是安全的枪口位置（按机体包围盒算一次）。</summary>
        private float NoseDistance()
        {
            if (_plane == null) return 10f;
            if (_nosePlane == _plane.transform && _noseDist > 0f) return _noseDist;
            _noseDist = 10f;
            _nosePlane = _plane.transform;
            try
            {
                Renderer[] rr = _plane.GetComponentsInChildren<Renderer>(true);
                bool has = false;
                Bounds b = new Bounds();
                for (int i = 0; i < rr.Length; i++)
                {
                    if (rr[i] == null) continue;
                    if (rr[i].GetComponent<ParticleSystem>() != null) continue;
                    if (!has) { b = rr[i].bounds; has = true; } else b.Encapsulate(rr[i].bounds);
                }
                if (has)
                {
                    float half = b.extents.magnitude;
                    _noseDist = Mathf.Clamp(half + 3f, 6f, 40f);
                }
            }
            catch { }
            return _noseDist;
        }

        /// <summary>打一发机炮：预计算航迹 -> 实例化子弹 -> 之后完全靠插值飞行。</summary>
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
                    float a = Random.Range(-cfgGunSpread, cfgGunSpread);
                    float b = Random.Range(-cfgGunSpread, cfgGunSpread);
                    dir = Quaternion.AngleAxis(a, t.up) * dir;
                    dir = Quaternion.AngleAxis(b, t.right) * dir;
                }
                dir.Normalize();

                float muzzle = (gun.MaxSpeed > 0f) ? gun.MaxSpeed * 0.5144f : cfgGunMuzzle;
                Vector3 start = t.position + fwd * NoseDistance() + Vector3.up * 0.4f;
                Vector3 v0 = _plane.GetVelocity() + dir * muzzle;

                // ---- 一次算完整条航迹（只有重力 + 极简空气阻力），之后不再做任何物理 ----
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

        private string PlayerCallSafe()
        {
            try
            {
                var pn = System.Type.GetType("Machine.Core.MachineLoader, Machine.Core");
                if (pn != null)
                {
                    var p = pn.GetProperty("PlayerName");
                    if (p != null) return (string)p.GetValue(null, null);
                }
            }
            catch { }
            return "You";
        }

        private string PlayerModelSafe()
        {
            try
            {
                if (_plane != null)
                {
                    var mn = _plane.GetType().GetMethod("GetModelName");
                    if (mn != null) return (string)mn.Invoke(_plane, null);
                }
            }
            catch { }
            return "PlayerAircraft";
        }

        /// <summary>线段 vs 球：子弹这一帧走过的那一段有没有穿过目标。</summary>
        private static bool SegSphere(Vector3 a, Vector3 b, Vector3 c, float r)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            float tt = (l2 > 0.0001f) ? Vector3.Dot(c - a, ab) / l2 : 0f;
            if (tt < 0f) tt = 0f; else if (tt > 1f) tt = 1f;
            Vector3 q = a + ab * tt;
            return (q - c).sqrMagnitude <= r * r;
        }

        /// <summary>子弹命中检测（只对 AI 飞机；自己的子弹不打自己）。</summary>
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
                    if (!SegSphere(a, b, tt.position, cfgGunHitRadius))
                        continue;
                    return MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);
                }
            }
            catch { }
            return false;
        }

        private void HandleFireInput()""")

# ---------------------------------------------------------------- 8) 自测：机炮连射阶段
R("""            // 4) 收尾
            if (_ammoStage == 4 && _ammoT > 2.5f)
            {
                _ammoStage = 5;
                _api.Log("AAM AMMO: done. intercepts=" + MissileController.InterceptCount);
                _api.Log("AAM SELFTEST: done. live=" + _live.Count + " ai=" + _ai.Count);
            }
""",
"""            // 4) 收尾
            if (_ammoStage == 4 && _ammoT > 2.5f)
            {
                _ammoStage = 5; _ammoT = 0f;
                _api.Log("AAM AMMO: done. intercepts=" + MissileController.InterceptCount);
                CargoType gc = GunCargo();
                if (gc != null) CombatGiveOf(gc, 400);
                _api.Log("AAM GUN: hold topped up, gun rounds=" + CombatCountOfType(gc));
                return;
            }

            // 5) 机炮连射 1.2 秒（52 发/秒）
            if (_ammoStage == 5)
            {
                if (_ammoT < 1.0f) return;
                _ammoStage = 6; _ammoT = 0f;
                _gunBurstN = 0; _gunBurstT = 0f; _gunBurstFps = 0f; _gunBurstFpsN = 0;
                _api.Log("AAM GUN: burst start target=" + cfgGunRps + " rps");
                return;
            }

            if (_ammoStage == 6)
            {
                _gunBurstT += Time.deltaTime;
                if (Time.deltaTime > 0.0001f) { _gunBurstFps += 1f / Time.deltaTime; _gunBurstFpsN++; }
                int want = (int)(_gunBurstT * cfgGunRps) - _gunBurstN;
                if (want > 0)
                {
                    if (want > 10) want = 10;
                    for (int i = 0; i < want; i++) { FireGunRound(); _gunBurstN++; }
                }
                if (_gunBurstT > 1.2f)
                {
                    _ammoStage = 7; _ammoT = 0f;
                    float fps = (_gunBurstFpsN > 0) ? (_gunBurstFps / _gunBurstFpsN) : 0f;
                    _api.Log("AAM GUN: fired " + _gunBurstN + " rounds in " + _gunBurstT.ToString("F2")
                             + "s -> " + (_gunBurstN / Mathf.Max(0.001f, _gunBurstT)).ToString("F1") + " rps"
                             + " live=" + BulletRound.LiveCount
                             + " avgFps=" + fps.ToString("F0"));
                    if (cfgCapture) GunShot();
                }
                return;
            }

            if (_ammoStage == 7 && _ammoT > 2.0f)
            {
                _ammoStage = 8;
                _api.Log("AAM GUN: done. bullets still live=" + BulletRound.LiveCount
                         + " gun rounds left=" + CombatCountOfType(GunCargo()));
                _api.Log("AAM SELFTEST: done. live=" + _live.Count + " ai=" + _ai.Count);
            }
""")

# 自测字段
R("""        private MissileController _ammoThreat;
        private MissileController _ammoMsdm;""",
"""        private MissileController _ammoThreat;
        private MissileController _ammoMsdm;
        private int _gunBurstN;
        private float _gunBurstT;
        private float _gunBurstFps;
        private int _gunBurstFpsN;""")

# 机炮截图
R("""        private string LiveMissileSummary()""",
"""        /// <summary>自测用：从机尾后方拍一张，看机炮弹流。</summary>
        private void GunShot()
        {
            try
            {
                if (_plane == null) return;
                Transform t = _plane.transform;
                Vector3 f = t.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) f = t.forward;
                f.Normalize();
                RenderAt(t.position - f * 60f + Vector3.up * 14f, t.position + f * 900f, "aam_gun_burst.png");
                _api.Log("AAM GUN: shot taken");
            }
            catch (Exception e) { _api.Log("AAM GUN: shot failed " + e.Message); }
        }

        private string LiveMissileSummary()""")


ok = 0
for old, new in reps:
    o = old.replace("\n", NL); n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d anchor:\n%s\n---" % (c, o[:150])); continue
    s = s.replace(o, n, 1); ok += 1

print("applied %d / %d" % (ok, len(reps)))
if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f: f.write(s)
    print("WROTE", len(s), "chars")
else:
    print("NOT WRITTEN")
