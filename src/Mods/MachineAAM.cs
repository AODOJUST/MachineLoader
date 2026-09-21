// ---------------------------------------------------------------------------
// MachineAAM - 空空导弹 (Air-to-Air Missile) 实验版
// ---------------------------------------------------------------------------
// 目标
//   把用户提供的 .planedesign（PL-15）当成一枚可以在游戏里真正飞行、并由 AI 制导的
//   空空导弹。导弹不是"模型贴图"，而是从设计文件还原出的实体飞行器：
//     - 用游戏自己的 GameDataReader 读设计文件，用游戏自己的部件预制体还原外形；
//     - 有自己的刚体、推力曲线、阻力、最大过载与转弯角速度；
//     - 由比例导引（Proportional Navigation）AI 自动驾驶飞向目标。
//
// 关键事实（逆向自 Assembly-CSharp，见 tools/ 下的 IL dump）
//   1) .planedesign 的真实布局：
//        [int32 头部标记][float 造价][int32 必需部件数][必需部件名 xN]
//        [int32 部件数][ (name, pos, rot, scale, [ProceduralFuselage 数据]) x N ] ...
//      其中字符串编码是 GameDataReader.ReadString()：先 1 字节 bool（true = 空串），
//      非空再跟 BinaryReader 的 7bit 长度 + UTF8。
//      PlaneStorage.LoadPart() 的做法就是：GetPartPrefab(name) -> Instantiate ->
//      SetParent/SetLocal(*Position|*Rotation|*Scale) -> 有 ProceduralFuselage 就 Load(reader)。
//      本 Mod 完整照搬这一套，所以外形与游戏里加载出来的完全一致。
//   2) 游戏没有 Harmony / MonoMod，运行时只能靠反射 + 每帧驱动，所以 AI 不"改"游戏，
//      而是自己驱动自己的刚体。
//   3) PlaneContainer 是 Singleton，世界上只能有一架"玩家飞机"，所以 AI 实体不做成
//      第二架 PlaneContainer，而是独立实体：既不会污染玩家状态，也天然不触发任何
//      依赖 PlaneContainer 的玩家告警。
//
// 语音警报
//   本 Mod 生成的所有 AI 实体（导弹 / 靶机）都会：
//     - 挂 AiEntityMarker 标记组件；
//     - 注册进 SilentAi 静默表；
//     - 不含 PlaneContainer / PlaneController；
//     - 不含任何 Collider / AudioSource / 游戏音频组件。
//   VoiceAlerts 那边只需一行检查即可跳过（本 Mod 同时按类型名与静默表双重匹配）。
//
// 战斗货仓
//   导弹货物 MarkCombat 交给 BattleHold（machine.battlehold），本体不写进 currentCargo，
//   因此"清空货仓"永远清不掉导弹；数量由 BattleHold 记录，本 Mod 用大字号 HUD 显示。
//
// 地形避障（2026-09-13）
//   导弹是无碰撞体的运动学实体，撞山不会爆炸、会直接穿进山体（表现为"一头撞墙、目标丢了"）。
//   现在 MissileController 自己用射线探针看路：3x3 探针扇形（法线投影 -> 斜坡抬升 / 崖壁侧绕）
//   + 前方山脊剖面 + 离地高度 AGL + 绝对高度兜底 + 撞地兜底引爆，并与比例导引按威胁度融合
//   （规避时临时放宽过载、收油门降速，否则高速下转弯半径太大躲不开）。
//   关键设计：威胁的定义是"**会撞上**"，不是"没飞够高"。所有向下看的项（俯仰 -14° 探针、
//   AGL 项、绝对高度兜底）都乘一个下沉率门控 sinkK，山脊剖面用"外推高度 + 撞地余量"判撞。
//   否则从跑道（离地 5m）平飞发射的导弹会被 40m 安全余量当成满威胁，一道 45G 抬升把它顶到
//   964m（实测），等它掉回来目标早飞远了。
//   参数见 aam_config.json 的 avoid* 字段；纯数学部分在 TerrainAvoid 里，
//   可用 tools/avoid_sim.cs 在游戏外拿合成地形做回归仿真（含"低空平飞不许被顶上天"用例）。
//
// 兼容性
//   全部对 BattleHold / BattleCore 的调用都走反射，前置缺失时自动降级，不产生编译期依赖。
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Machine.Mod;
using Machine.Core;

namespace Machine.AAM
{
    // =====================================================================
    // 入口
    // =====================================================================
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.aam"; } }

        public void OnLoad(IMachineApi api)
        {
            if (AamSystem.Live != null)
            {
                api.Log("MachineAAM: runtime already alive, skipping duplicate init");
                return;
            }
            api.Log("MachineAAM loading...");
            var go = new GameObject("Machine.AAM");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AamSystem>().Init(api);
        }
    }

    /// <summary>导弹射程类别（用于火控自动化：根据锁定目标距离自动选弹）。</summary>
    public enum MissileRangeCategory
    {
        Short,    // 短程/格斗弹：< 3000m
        Medium,   // 中程：3000m - 10000m
        Long      // 远程/超远程：> 10000m
    }

    /// <summary>导弹弹型规格（速度单位：节，1 马赫 ≈ 660 节）。</summary>
    public class MissileSpec
    {
        public string Name;
        public float Price;
        public float Weight;
        public int Space;
        public float Lifetime;     // 加速时间（s）：结束后进入惯性巡航
        public float MaxSpeed;     // 速度上限（节）
        public float TurnRateDeg = 50f;  // 扭矩/转弯速率（度/秒），越大转弯半径越小机动性越强
        public float MaxG = 40f;   // 最大过载（G）：限制飞行轨迹的法向加速度
        public float CoastDrag = 100f;  // 惯性巡航阻力：100=PL-15最大速度下直线巡航10秒速度归零
        public float Acceleration = 0.5f;  // 加速度（马赫/秒）：导弹加速能力，决定多久达到最大速度
        public float TrackRate = 60f;  // 跟踪速率（度/秒）：导引头跟踪回路能够跟随目标视线转动的最大角速度
        public string Design;      // 该弹种使用的 .planedesign 模型文件
        public bool AntiMissile;   // 拦截弹（MSDM）：只能锁定/击毁来袭导弹
        public MissileRangeCategory RangeCat = MissileRangeCategory.Medium;   // 射程类别
        public float BattleRating;  // 战斗评级
    }

    // =====================================================================
    // AI 实体标记 / 静默表
    // =====================================================================
    /// <summary>
    /// 挂在所有"由 AI 驾驶"的实体上（导弹、靶机……）。
    /// 其它 Mod 只要 GetComponent("AiEntityMarker") != null 就知道这不是玩家飞机，
    /// 从而不去做语音报警等只属于玩家的事情。类型名是约定，不要改。
    /// </summary>
    public class AiEntityMarker : MonoBehaviour
    {
        public string Kind = "ai";
    }

    /// <summary>AI 实体静默表：按 GameObject 实例 ID 记录，供跨 Mod 查询。</summary>
    public static class SilentAi
    {
        private static readonly HashSet<int> _ids = new HashSet<int>();

        public static void Mark(GameObject go)
        {
            if (go == null) return;
            _ids.Add(go.GetInstanceID());
        }

        public static void Unmark(GameObject go)
        {
            if (go == null) return;
            _ids.Remove(go.GetInstanceID());
        }

        /// <summary>是否 AI 驾驶（= 不应触发玩家语音警报）。</summary>
        public static bool IsSilent(GameObject go)
        {
            if (go == null) return false;
            return _ids.Contains(go.GetInstanceID());
        }
    }

    // =====================================================================
    // 目标
    // =====================================================================
    public class TargetRef
    {
        public Transform T;
        public Vector3 FixedPos;
        public bool UseFixed;
        public string Name = "";
        // 目标若是一枚导弹：直接读它的实时速度。导弹用的是"运动学刚体 + MovePosition"，
        // Unity 不会刷新 Rigidbody.linearVelocity，靠下面那条通用分支会拿到 0，
        // 比例导引就会算错提前量（拦截弹必失的根因）。
        public MissileController Msl;
        private Rigidbody _rb;
        private bool _rbScanned;

        public bool Valid { get { return UseFixed || T != null; } }

        public Vector3 Position
        {
            get { return UseFixed ? FixedPos : (T != null ? T.position : FixedPos); }
        }

        public Vector3 Velocity
        {
            get
            {
                if (Msl != null) return Msl.Body.Vel;
                if (UseFixed || T == null) return Vector3.zero;
                if (!_rbScanned)
                {
                    _rbScanned = true;
                    try { _rb = T.GetComponent<Rigidbody>(); } catch { _rb = null; }
                }
                if (_rb != null) return _rb.linearVelocity;
                return Vector3.zero;
            }
        }
    }

    /// <summary>
    /// 目标登记处。机场是天然目标；未来的 AI 靶机 / AI 飞机只要 Register 进来，
    /// 导弹就能自动发现并攻击它们（这就是"空军 AI"的接入口）。
    /// </summary>
    public static class TargetRegistry
    {
        private class Entry
        {
            public GameObject Go;
            public bool Targetable;
            public string Name;
        }

        private static readonly List<Entry> _list = new List<Entry>();

        /// <summary>登记一个 AI 实体。<paramref name="targetable"/> = 是否允许被导弹选为目标
        /// （导弹自己是 false，避免两枚导弹互相追）。</summary>
        public static void Register(GameObject go, bool targetable, string name)
        {
            if (go == null) return;
            for (int i = 0; i < _list.Count; i++) if (_list[i].Go == go) return;
            var e = new Entry();
            e.Go = go;
            e.Targetable = targetable;
            e.Name = name;
            _list.Add(e);
        }

        public static void Unregister(GameObject go)
        {
            if (go == null) return;
            for (int i = _list.Count - 1; i >= 0; i--) if (_list[i].Go == go) _list.RemoveAt(i);
        }

        public static int Count { get { return _list.Count; } }

        /// <summary>最近的可攻击目标：优先登记的移动目标，其次机场。</summary>
        public static TargetRef FindNearest(Vector3 from, float maxRange, bool preferMoving)
        {
            TargetRef best = null;
            float bestD = float.MaxValue;

            for (int i = _list.Count - 1; i >= 0; i--)
            {
                Entry e = _list[i];
                if (e.Go == null) { _list.RemoveAt(i); continue; }
                if (!e.Targetable) continue;
                float d = Vector3.Distance(from, e.Go.transform.position);
                if (d > maxRange) continue;
                if (d < bestD)
                {
                    bestD = d;
                    var t = new TargetRef();
                    t.T = e.Go.transform;
                    t.Name = e.Name;
                    best = t;
                }
            }
            if (best != null && preferMoving) return best;

            // 机场
            try
            {
                AirportManager am = AirportManager.Instance;
                if (am != null && am.airports != null)
                {
                    for (int i = 0; i < am.airports.Count; i++)
                    {
                        Airport ap = am.airports[i];
                        if (ap == null) continue;
                        float d = Vector3.Distance(from, ap.position);
                        if (d > maxRange) continue;
                        if (d < bestD)
                        {
                            bestD = d;
                            var t = new TargetRef();
                            t.UseFixed = true;
                            t.FixedPos = ap.position;
                            t.Name = string.IsNullOrEmpty(ap.airportName) ? "Airport" : ap.airportName;
                            best = t;
                        }
                    }
                }
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] airport scan failed: " + e.Message); }

            return best;
        }
    }

    // =====================================================================
    // 红外系统（2026-09-14）
    // ---------------------------------------------------------------------
    // 红外值范围 0.0 ~ 200.0，分「期望值」与「实际值」：
    //   期望值（目标值）= 油门(0~100) × 1.2 + 速度(m/s) × 0.1
    //   实际值         = 当下瞬时值，按非线性规律向期望值逼近
    // 逼近规律刻意不是线性函数：
    //   升温（期望 > 实际）：差值越大变化越快 —— 指数逼近，变化率 ∝ 差值（无上限）
    //   降温（期望 < 实际）：取 cos(x) 在 x∈[π/2, π]（正半轴第一个零点 → 第一个极小值点）
    //                        的变化趋势。|d cos/dx| = sin(x) 由 1 递减到 0，
    //                        即"剩得越多掉得越快、越接近越拖"，且速率有上限。
    // 结果：推油门升温快；收油门/减速时降温拖沓 —— 不会瞬间掉下来。
    // =====================================================================

    /// <summary>
    /// 红外数学（纯函数，不碰任何引擎 API —— 离线仿真可以只引 DLL 直接跑真实代码）。
    /// </summary>
    public static class IrMath
    {
        /// <summary>期望值（目标值）= 油门(0~100)×throttleK + 速度(m/s)×speedK，夹在 0~200。</summary>
        public static float Expected(float throttlePct, float speed, float throttleK, float speedK)
        {
            float v = throttlePct * throttleK + speed * speedK;
            if (v < 0f) return 0f;
            if (v > IrSignature.Max) return IrSignature.Max;
            return v;
        }

        /// <summary>
        /// 实际值向期望值逼近一步（非线性，不是线性函数）。
        ///   升温（expected > actual）：变化率 ∝ 差值 —— 差值越大变化越快（指数逼近，无上限）
        ///   降温（expected < actual）：走 cos 在 [π/2, π]（正半轴第一个零点→第一个极小值点）的趋势。
        ///        |d cos/dx| = sin(x) 由 1 递减到 0，把归一化剩余量 s 映射到相角
        ///        x = π - s·π/2，变化率 ∝ sin(s·π/2)：剩得多掉得快、越接近越拖，且速率封顶。
        /// </summary>
        public static float Step(float actual, float expected, float dt,
                                 float heatK, float coolRate, float coolRef)
        {
            if (dt <= 0f) return actual;
            float gap = expected - actual;
            if (gap > IrSignature.Snap)
            {
                actual += gap * (1f - Mathf.Exp(-heatK * dt));
                if (actual > expected) actual = expected;
            }
            else if (gap < -IrSignature.Snap)
            {
                float s = (-gap) / coolRef;
                if (s > 1f) s = 1f;
                float rate = coolRate * Mathf.Sin(s * Mathf.PI * 0.5f);
                float drop = rate * dt;
                if (drop > -gap) drop = -gap;
                actual -= drop;
            }
            else actual = expected;
            return actual;
        }

        /// <summary>
        /// 热诱弹的红外时序：前 hold 秒保持峰值，之后余弦渐隐到 0，life 秒归零。
        /// </summary>
        public static float FlareIr(float age, float peak, float hold, float life)
        {
            if (age <= hold) return peak;
            if (age >= life) return 0f;
            float u = (age - hold) / (life - hold);
            return peak * (0.5f + 0.5f * Mathf.Cos(u * Mathf.PI));
        }
    }

    /// <summary>红外特征：挂在任何会被红外导引头看到的东西上（飞机、热诱弹）。</summary>
    public class IrSignature : MonoBehaviour
    {
        public const float Max = 200f;      // 飞机红外上限（热诱弹可以超过它）

        /// <summary>实际值：当下的瞬时红外强度。</summary>
        public float Actual;
        /// <summary>期望值（目标值）：公式算出来的、实际值未来要变成的值。</summary>
        public float Expected;
        /// <summary>油门 0~100。</summary>
        public float ThrottlePct;
        /// <summary>速度 m/s。</summary>
        public float Speed;
        /// <summary>速度矢量（导引头算提前量用；由挂载方每帧写入）。</summary>
        public Vector3 Vel;
        /// <summary>热诱弹：红外值由自身时序曲线驱动，不走飞机公式。</summary>
        public bool IsDecoy;

        /// <summary>总开关：关掉后所有红外源都不再演进（导引头随之失效）。配置 irEnabled。</summary>
        public static bool Enabled = true;

        /// <summary>期望值公式：油门(0~100) × ThrottleK + 速度(m/s) × SpeedK。</summary>
        public static float ThrottleK = 1.2f;
        public static float SpeedK = 0.1f;

        // ---- 逼近参数（静态共享，配置可调）----
        public static float HeatK = 0.55f;     // 升温：指数逼近系数 (1/s)
        public static float CoolRate = 26f;    // 降温：最大变化率 (/s)
        public static float CoolRef = 60f;     // 降温：达到最大速率所需的差值
        public const float Snap = 0.05f;       // 差值小于此值直接吸附，避免永远逼近不到

        public static IrSignature Ensure(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var ir = go.GetComponent<IrSignature>();
                if (ir == null) ir = go.AddComponent<IrSignature>();
                return ir;
            }
            catch { return null; }
        }

        private void OnEnable() { IrRegistry.Register(this); }
        private void OnDisable() { IrRegistry.Unregister(this); }

        /// <summary>喂入油门与速度：刷新期望值，并把实际值往前推一步。</summary>
        public void Feed(float throttlePct, float speed, float dt)
        {
            ThrottlePct = throttlePct;
            Speed = speed;
            if (IsDecoy) return;    // 诱饵的红外值由 IrFlare 的时序曲线直接写
            Expected = IrMath.Expected(throttlePct, speed, ThrottleK, SpeedK);
            Step(dt);
        }

        /// <summary>实际值向期望值逼近（非线性）。数学在 IrMath.Step（纯函数，可离线跑）。</summary>
        public void Step(float dt)
        {
            Actual = IrMath.Step(Actual, Expected, dt, HeatK, CoolRate, CoolRef);
            if (Actual < 0f) Actual = 0f;
            else if (!IsDecoy && Actual > Max) Actual = Max;   // 热诱弹允许超过飞机上限
        }
    }

    /// <summary>红外源登记处。导引头直接读它，不做全场景扫描。</summary>
    public static class IrRegistry
    {
        private static readonly List<IrSignature> _list = new List<IrSignature>();

        public static void Register(IrSignature s)
        {
            if (s == null) return;
            for (int i = 0; i < _list.Count; i++) if (_list[i] == s) return;
            _list.Add(s);
        }

        public static void Unregister(IrSignature s)
        {
            if (s == null) return;
            _list.Remove(s);
        }

        public static int Count { get { return _list.Count; } }

        /// <summary>把还活着的红外源填进 dst（调用方自备缓冲，避免每帧 new）。</summary>
        public static void Fill(List<IrSignature> dst)
        {
            dst.Clear();
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                if (_list[i] == null) { _list.RemoveAt(i); continue; }
                dst.Add(_list[i]);
            }
        }
    }

    /// <summary>
    /// 热诱弹（红外诱饵弹）。
    /// 抛出方式与空空导弹完全不同：导弹是火箭推进、持续加速去追目标；
    /// 热诱弹只是被"弹射"出去 —— 初速 = 载机速度 + 一个朝下方偏侧后方的冲量，
    /// 之后完全无动力，只受重力和很大的空气阻力，于是迅速减速并下坠（现实抛放轨迹）。
    /// 红外值 210（高于飞机上限 200，所以能抢走红外导引头），
    /// 抛出 4s 后开始逐渐衰减，第 12s 完全降到 0 并消失。
    /// </summary>
    public class IrFlare : MonoBehaviour
    {
        public const float PeakIr = 151f;
        public const float HoldTime = 4f;    // 前 4s 保持峰值
        public const float LifeTime = 12f;   // 第 12s 归零并消失

        // 抛放与飞行参数（静态共享，配置可调）
        public static float Gravity = 9.81f;
        public static float DragK = 0.50f;     // 指数阻力系数 (1/s)：约 1.4s 速度减半（旧值 0.85/0.8s 太软）
        public static float EjectDown = 24f;   // 向下冲量 (m/s)
        public static float EjectBack = 26f;   // 向后冲量 (m/s)
        public static float EjectSide = 15f;   // 侧向散布 (m/s)

        public float Age;
        public Vector3 Vel;
        public IrSignature Sig;

        /// <summary>同时点亮的火光上限。玩家 16 枚 + 多架 AI 各 6 枚，无上限时能堆到 60+ 盏灯。</summary>
        public static int LightBudget = 6;

        private static readonly List<IrFlare> _lit = new List<IrFlare>();   // 当前持灯的热诱弹（按先后）

        /// <summary>当前持灯数量（自测用；应恒 ≤ LightBudget）。</summary>
        public static int LitCount { get { return _lit.Count; } }

        private ParticleSystem _fire;
        private ParticleSystem _smoke;
        private Light _light;
        private float _ir = PeakIr;
        private bool _dying;

        /// <summary>当前红外值。</summary>
        public float Ir { get { return _ir; } }

        /// <summary>从载机抛出一枚热诱弹（返回实例；失败返回 null）。</summary>
        public static IrFlare Dispense(GameObject model, Transform carrier, Vector3 carrierVel)
        {
            if (model == null || carrier == null) return null;
            try
            {
                Vector3 back = -carrier.forward;
                Vector3 down = -carrier.up;
                Vector3 side = carrier.right;
                Vector3 pos = carrier.position + down * 1.8f + back * 3.8f;

                model.transform.position = pos;
                model.transform.rotation = carrier.rotation;

                var fl = model.GetComponent<IrFlare>();
                if (fl == null) fl = model.AddComponent<IrFlare>();
                fl.Age = 0f;
                // 初速 = 载机速度 + 抛射冲量（向下偏侧后 + 侧向散布）
                fl.Vel = carrierVel + down * EjectDown + back * EjectBack
                       + side * UnityEngine.Random.Range(-EjectSide, EjectSide);
                fl.Sig = IrSignature.Ensure(model);
                if (fl.Sig != null)
                {
                    fl.Sig.IsDecoy = true;
                    fl.Sig.Actual = PeakIr;
                    fl.Sig.Expected = PeakIr;
                }
                fl.BuildVfx();
                return fl;
            }
            catch (Exception e)
            {
                Machine.Core.Log.Info("[AAM] flare dispense failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 热诱弹的烟迹：世界空间慢速烟团，留在航迹上慢慢散开。
        /// 与"火焰粒子"分成两个系统，这样火焰可以短促明亮、烟迹可以拖得很长。
        /// </summary>
        private void BuildFlareSmoke()
        {
            try
            {
                GameObject go = new GameObject("FlareSmoke");
                go.transform.SetParent(transform, false);
                ParticleSystem ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.loop = false;
                main.duration = LifeTime;
                main.startLifetime = new ParticleSystem.MinMaxCurve(2.4f, 4.4f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 1.1f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.50f, 1.30f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28318f);
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.98f, 0.97f, 0.95f, 0.85f),
                    new Color(0.86f, 0.85f, 0.84f, 0.70f));
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Local;
                main.gravityModifier = -0.012f;
                main.maxParticles = 320;
                main.playOnAwake = false;

                var em = ps.emission;
                em.enabled = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(90f);

                var sh = ps.shape;
                sh.enabled = true;
                sh.shapeType = ParticleSystemShapeType.Sphere;
                sh.radius = 0.20f;

                var sz = ps.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0.00f, 0.35f),
                    new Keyframe(0.30f, 0.85f),
                    new Keyframe(1.00f, 1.45f)));

                var col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient g = new Gradient();
                g.SetKeys(
                    new GradientColorKey[]{
                        new GradientColorKey(new Color(0.99f, 0.98f, 0.96f), 0.00f),
                        new GradientColorKey(new Color(0.88f, 0.87f, 0.86f), 0.35f),
                        new GradientColorKey(new Color(0.74f, 0.74f, 0.75f), 1.00f)},
                    new GradientAlphaKey[]{
                        new GradientAlphaKey(0.00f, 0.00f),
                        new GradientAlphaKey(0.55f, 0.10f),
                        new GradientAlphaKey(0.30f, 0.55f),
                        new GradientAlphaKey(0.00f, 1.00f)});
                col.color = new ParticleSystem.MinMaxGradient(g);

                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);

                Shader shd = Shader.Find("Sprites/Default");
                if (shd == null) shd = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                var psr = go.GetComponent<ParticleSystemRenderer>();
                if (shd != null && psr != null)
                {
                    Material m = new Material(shd);
                    m.mainTexture = FlareTexture();
                    psr.sharedMaterial = m;
                    psr.renderMode = ParticleSystemRenderMode.Billboard;
                    psr.alignment = ParticleSystemRenderSpace.View;
                    psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    psr.receiveShadows = false;
                }
                ps.Play(true);
                _smoke = ps;
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] flare smoke failed: " + e.Message); }
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_dying) return;                 // 已经"熄灭"：只留下烟团自然散完
            Age += dt;
            if (Age >= LifeTime)
            {
                // 火焰到点就灭，但已经喷出的烟迹还要再飘几秒才散，直接销毁会把尾巴一刀切掉
                _dying = true;
                try { if (_fire != null) _fire.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } catch { }
                try { if (_smoke != null) _smoke.Stop(true, ParticleSystemStopBehavior.StopEmitting); } catch { }
                ReleaseLight();
                Destroy(gameObject, 5.0f);
                return;
            }

            // 无动力弹道：重力 + 大阻力（热诱弹阻力极大，很快就没速度、开始下坠）
            Vel.y -= Gravity * dt;
            Vel *= Mathf.Exp(-DragK * dt);
            transform.position += Vel * dt;

            // 红外时序：0~4s 峰值；4~12s 余弦渐隐到 0（数学在 IrMath.FlareIr）
            _ir = IrMath.FlareIr(Age, PeakIr, HoldTime, LifeTime);
            if (Sig != null) { Sig.Actual = _ir; Sig.Expected = _ir; }

            UpdateVfx();
        }

        private void BuildVfx()
        {
            try
            {
                GameObject go = new GameObject("FlareFire");
                go.transform.SetParent(transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.loop = false;
                main.duration = LifeTime;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.36f, 0.95f);   // 尾迹寿命 x2
                main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.42f, 1.25f);
                main.startColor = new Color(1f, 0.94f, 0.70f, 1f);
                main.gravityModifier = 0.05f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 400;
                main.playOnAwake = false;

                var em = ps.emission;
                em.enabled = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(260f);

                var sh = ps.shape;
                sh.enabled = true;
                sh.shapeType = ParticleSystemShapeType.Sphere;
                sh.radius = 0.18f;

                // 白热 -> 橙 -> 暗红 -> 透明
                var col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient g = new Gradient();
                g.SetKeys(
                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 0.99f, 0.92f), 0.00f),
                        new GradientColorKey(new Color(1.00f, 0.86f, 0.42f), 0.18f),
                        new GradientColorKey(new Color(1.00f, 0.62f, 0.16f), 0.42f),
                        new GradientColorKey(new Color(0.92f, 0.32f, 0.08f), 0.68f),
                        new GradientColorKey(new Color(0.45f, 0.44f, 0.43f), 1.00f)},
                    new GradientAlphaKey[]{
                        new GradientAlphaKey(1.00f, 0.00f),
                        new GradientAlphaKey(0.95f, 0.35f),
                        new GradientAlphaKey(0.55f, 0.72f),
                        new GradientAlphaKey(0.00f, 1.00f)});
                col.color = new ParticleSystem.MinMaxGradient(g);

                Shader shd = Shader.Find("Sprites/Default");
                if (shd == null) shd = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                var psr = go.GetComponent<ParticleSystemRenderer>();
                if (shd != null && psr != null)
                {
                    Material m = new Material(shd);
                    m.mainTexture = FlareTexture();
                    psr.sharedMaterial = m;
                    psr.renderMode = ParticleSystemRenderMode.Billboard;
                    psr.alignment = ParticleSystemRenderSpace.View;
                    psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    psr.receiveShadows = false;
                }
                ps.Play(true);
                _fire = ps;

                BuildFlareSmoke();   // 火焰之外再挂一条世界空间烟迹（寿命长得多 -> 尾迹更久）
                AcquireLight();
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] flare vfx failed: " + e.Message); }
        }

        /// <summary>
        /// 点一盏火光，但总数受 LightBudget 限制；超了就把最老那枚的灯收回来给新的
        /// （新的更亮、更值得照亮）。粒子火焰不受影响，所有热诱弹都看得见。
        /// </summary>
        private void AcquireLight()
        {
            try
            {
                if (LightBudget <= 0) return;
                // 自愈：万一有热诱弹被销毁时没走 OnDestroy（外部 Destroy、场景切换等），
                // 这里先把已经失效的条目清掉，避免列表只增不减、新热诱弹再也拿不到灯。
                for (int i = _lit.Count - 1; i >= 0; i--)
                {
                    if (_lit[i] == null) _lit.RemoveAt(i);
                }
                while (_lit.Count >= LightBudget)
                {
                    IrFlare old = _lit[0];
                    _lit.RemoveAt(0);
                    if (old != null && old != this) old.ReleaseLight();
                }
                GameObject lg = new GameObject("FlareLight");
                lg.transform.SetParent(transform, false);
                Light li = lg.AddComponent<Light>();
                li.type = LightType.Point;
                li.color = new Color(1f, 0.72f, 0.35f);
                li.range = 70f;
                li.intensity = 3.2f;
                li.shadows = LightShadows.None;
                _light = li;
                _lit.Add(this);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] flare light failed: " + e.Message); }
        }

        private void ReleaseLight()
        {
            _lit.Remove(this);
            if (_light != null)
            {
                try { Destroy(_light.gameObject); } catch { }
                _light = null;
            }
        }

        private void OnDestroy()
        {
            ReleaseLight();
        }

        /// <summary>亮度/喷射率随红外值一起衰减：IR 归零时火也灭了。</summary>
        private void UpdateVfx()
        {
            try
            {
                float k = Mathf.Clamp01(_ir / PeakIr);
                if (_light != null) _light.intensity = 3.2f * k;
                if (_fire != null)
                {
                    var em = _fire.emission;
                    em.rateOverTime = new ParticleSystem.MinMaxCurve(260f * k);
                }
                if (_smoke != null)
                {
                    var es = _smoke.emission;
                    es.rateOverTime = new ParticleSystem.MinMaxCurve(90f * Mathf.Clamp01(0.35f + 0.65f * k));
                }
            }
            catch { }
        }

        private static Texture2D _tex;
        private static Texture2D FlareTexture()
        {
            if (_tex != null) return _tex;
            try
            {
                int n = 32;
                var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
                float c = (n - 1) * 0.5f;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                        float a = Mathf.Clamp01(1f - d);
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                }
                t.Apply(false, true);
                _tex = t;
            }
            catch { _tex = null; }
            return _tex;
        }
    }

    // =====================================================================
    // 比例导引 (Proportional Navigation)
    // =====================================================================
    public static class PnGuidance
    {
        /// <summary>
        /// 返回"垂直于视线"的指令加速度。
        ///   a = N * Vc * (omega x los)，omega = (R x Vrel) / r^2
        /// 接近速度为负（在拉开）时退化为纯追踪，保证还能咬上去。
        /// </summary>
        public static Vector3 Command(Vector3 mPos, Vector3 mVel, Vector3 tPos, Vector3 tVel,
                                       float N, float maxAccel)
        {
            Vector3 R = tPos - mPos;
            float r = R.magnitude;
            if (r < 0.5f) return Vector3.zero;
            Vector3 los = R / r;
            Vector3 relV = tVel - mVel;
            float Vc = -Vector3.Dot(relV, los);

            Vector3 a;
            if (Vc > 1f)
            {
                Vector3 omega = Vector3.Cross(R, relV) / (r * r);
                a = N * Vc * Vector3.Cross(omega, los);
            }
            else
            {
                a = Vector3.Cross(Vector3.Cross(los, relV), los);
            }
            return Vector3.ClampMagnitude(a, maxAccel);
        }

        /// <summary>把"前方"安全地转成四元数（正前方与 up 平行时换一个参考轴）。</summary>
        public static Quaternion SafeLook(Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
            forward = forward.normalized;
            if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.999f)
                up = Mathf.Abs(Vector3.Dot(forward, Vector3.right)) > 0.999f ? Vector3.forward : Vector3.right;
            return Quaternion.LookRotation(forward, up);
        }
    }

    // =====================================================================
    // 地形 / 障碍避障 (Terrain & Obstacle Avoidance)
    // =====================================================================
    // 背景：导弹是"运动学刚体 + 无碰撞体"的纯视觉实体（见 SanitizeAi），
    //   它和地形/建筑之间没有任何物理交互 —— 撞上去既不会爆炸也不会被弹开，
    //   而是直接穿进山体里（玩家看到的就是"发射出去一头撞墙、目标没了"）。
    // 所以避障必须自己"看路"：不依赖物理引擎，只用射线探针。
    //
    //   1) 3x3 探针扇形（偏航 ±32°、俯仰 ±14°，共 9 条）：
    //      命中地形/建筑后，取"命中面法线在垂直于当前速度平面上的投影"作为规避方向 ——
    //      斜坡（法线朝上）-> 抬升；垂直崖壁（法线水平）-> 侧向绕开；
    //      迎面正撞（法线与速度反向，投影退化）-> 改用探针自身的侧向分量，再不行就抬升。
    //   2) 前方山脊剖面：在正前方 0.3 / 0.6 倍视距处向下探地形高度，
    //      要求航路高于"地形 + 安全余量"，缺多少就抬多少 ——
    //      这条挡住"山脊正好从探针扇形的缝隙里穿过去"的情况。
    //   3) 正下方探针给出真实离地高度 AGL，低于安全余量就抬升（低空咬尾时防擦地）。
    //   4) 绝对高度兜底：地形网格/碰撞体还没生成、或导弹已飞出地形范围时，
    //      AGL 探针什么都看不到，此时用"最低绝对高度"兜住。
    //   5) 撞地兜底：每帧沿速度方向补一条 = 本帧位移 + 弹头长度的射线，
    //      命中说明这一帧弹头会扎进地形 -> 立刻按"撞地"引爆，而不是穿山消失。
    //
    // 与比例导引的融合：威胁越大 PN 让位越多
    //      a = aPn * (1 - pnCut*threat) + avoidDir * aMax * threat
    // 同时临时放大可用过载（extraG）并收油门/加阻力（brake）：高机动转弯半径 R = v^2/a，
    // 不放一点过载、不掉一点速度，1250m/s 的导弹（40G 下 R≈4km）根本躲不开山。
    // =====================================================================

    /// <summary>
    /// 世界探针：把"沿射线找最近的地形/建筑"这件事抽象出来。
    /// 游戏内用 Physics 射线实现；离线仿真用合成地形实现（同一套避障算法，见 tools/avoid_sim.cs）。
    /// </summary>
    public interface IObstacleProbe
    {
        /// <summary>从 origin 沿 dir 找最近的世界障碍。命中返回 true，并给出距离与命中面法线。</summary>
        bool Cast(Vector3 origin, Vector3 dir, float maxDist, out float dist, out Vector3 normal);

        /// <summary>
        /// 只问"这一小段里有没有东西"的廉价版本（撞地兜底每帧都调，必须最省）。
        /// 不做动态实体过滤 —— 撞地形判定的对象是山体/建筑，飞机导弹不构成"撞地"。
        /// </summary>
        bool Blocked(Vector3 origin, Vector3 dir, float maxDist);
    }

    /// <summary>避障参数（aam_config.json 里的 avoid* 字段）。</summary>
    public class AvoidParams
    {
        public bool Enabled = true;         // avoidEnabled
        public float LookTime = 1.6f;       // avoidLookTime：视距 = 速度 x 该值（秒）
        public float LookTurn = 1.0f;       // avoidLookTurn：视距至少 = 转弯半径 x 该值（见下）
        public float MinLook = 250f;        // avoidMinLook：视距下限（米）
        public float MaxLook = 3600f;       // avoidMaxLook：视距上限（米）
        public float Clearance = 40f;       // avoidClearance：安全离地高度（米）
        public float Strength = 1f;         // avoidStrength：规避加速度占可用过载的比例
        public float PnCut = 0.85f;         // avoidPnCut：威胁满值时比例导引被压掉多少
        public float Interval = 0.05f;      // avoidInterval：探针扫描间隔（秒，低速基准）
        // 自适应扫描间隔：扫描频率必须够高，让"两次扫描之间的位移"远小于视距，
        // 否则山脊会从两条射线中间漏过去。但固定 20Hz 在高速下纯属浪费 ——
        // 探针成本 ∝ 速度²（视距按 v² 涨），而 20Hz 时 696m/s 每拍只前进 35m、视距却有 1234m。
        // 改成"每拍前进量不超过视距的 SpeedStepFrac"：位移上限固定，扫描次数随视距同步放大，
        // 于是高速时自动降频、近地低速时自动加密，威胁的发现时机基本不变。
        public float SpeedStepFrac = 0.03f;// avoidSpeedStepFrac：每拍允许前进的比例（占视距）
        public float MaxInterval = 0.30f;   // avoidMaxInterval：扫描间隔上限（秒），防止高空稀疏到漏检
        public float ThreatFast = 0.10f;    // avoidThreatFast：已有威胁时用这个更密的上限（秒）
        public float ExtraG = 1.35f;        // avoidExtraG：规避时可用过载倍率
        public float Brake = 0.4f;          // avoidBrake：规避时收油门 + 加阻力的比例
        public float AbsFloor = 30f;        // avoidAbsFloor：绝对高度兜底（探针无数据时）
        public float Warmup = 0.35f;        // avoidWarmup：发射后多少秒内不避障（避免刚离机就乱拐）
        public float Nose = 4f;             // avoidNose：弹头到质心的距离（撞地判定用，米）
        public bool ImpactDetonate = true;  // avoidImpactDetonate：真撞上地形就引爆
        public float ProfileSpan = 300f;    // avoidProfileSpan：山脊剖面"缺多少米算满威胁"
        public float ProfileMargin = 20f;   // avoidProfileMargin：剖面判撞的余量（不是安全高度！）
        // ---- 目标优先（2026-09-15）----
        // 打贴地 / 低空目标时，"为了不撞地而抬升"会把弹顶离目标，一路抬升就永远够不着
        // （用户实测："导弹攻击贴地目标时会触发规避，导致无法攻击到目标"）。
        // 修法是让**目标本身**成为避障的边界：
        //   · 探针视距 / 剖面采样点一律不超过"到目标的距离" —— 打到目标之后的地形与我无关；
        //   · 进入 TerminalRange 后威胁度按比例打折（TerminalMin 保底），把控制权交回比例导引，
        //     保证真的能落到目标上（而不是擦着目标飞过去）。
        public float TerminalRange = 1600f; // avoidTerminalRange：进入该距离开始让位给导引（m）
        public float TerminalMin = 0.30f;   // avoidTerminalMin：终端段保留的避障权重下限（0~1）
    }

    /// <summary>避障解算：纯数学，不碰任何 Unity 引擎 API（可在游戏外跑仿真）。</summary>
    public static class TerrainAvoid
    {
        private static readonly float[] YawDeg = new float[] { -32f, 0f, 32f };
        private static readonly float[] PitchDeg = new float[] { -14f, 0f, 14f };
        private static readonly float[] ProfileFrac = new float[] { 0.3f, 0.6f };

        /// <summary>
        /// 视距（米）：必须同时满足"反应时间"和"一个转弯半径 R = v²/a"。
        /// 抽成独立函数，供扫描节流与探针共用，避免两处算法漂移。
        /// </summary>
        public static float LookDistance(float spd, float maxLatAccel, AvoidParams p)
        {
            if (p == null) return 250f;
            float turnR = spd * spd / Mathf.Max(1f, maxLatAccel);
            return Mathf.Clamp(Mathf.Max(spd * p.LookTime, turnR * p.LookTurn), p.MinLook, p.MaxLook);
        }

        /// <summary>
        /// 自适应扫描间隔（秒）：让"两拍之间的位移"不超过视距的 SpeedStepFrac。
        /// 已处于规避机动中（priorThreat 高）时改用更密的上限，保证转弯过程中持续跟地形。
        /// 纯数学，可在游戏外仿真。
        /// </summary>
        public static float ScanInterval(float spd, float maxLatAccel, AvoidParams p, float priorThreat)
        {
            if (p == null) return 0.05f;
            float look = LookDistance(spd, maxLatAccel, p);
            float frac = Mathf.Clamp(p.SpeedStepFrac, 0.005f, 0.25f);
            // 每拍位移 = spd * dt <= look * frac  =>  dt <= look * frac / spd
            float dt = look * frac / Mathf.Max(1f, spd);
            dt = Mathf.Max(dt, Mathf.Max(0.01f, p.Interval));   // 不低于配置的基准间隔
            float hi = (priorThreat > 0.25f) ? p.ThreatFast : p.MaxInterval;
            return Mathf.Clamp(dt, Mathf.Max(0.01f, p.Interval), Mathf.Max(p.Interval, hi));
        }

        /// <summary>
        /// 扫描一次障碍，返回**单位**规避方向（无威胁时为零向量）。
        /// threat = 威胁度 0..1（越接近 1 越紧急）；agl = 离地高度，探针没数据时为 -1。
        /// priorThreat = 上一拍的威胁度（调用方的威胁记忆），用来判断"是否已经在规避机动中"。
        /// </summary>
        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, out float threat, out float agl)
        {
            return Compute(probe, pos, vel, maxLatAccel, p, priorThreat, Vector3.zero, float.MaxValue,
                           out threat, out agl);
        }

        /// <summary>
        /// 同上，但带**目标**（targetPos / targetRange）：目标优先 —— 这是"打贴地目标打不中"的根因修法。
        ///   ① 探针视距 / 剖面采样点一律不超过"到目标的距离"：打到目标之后的地形与我无关；
        ///   ② 山脊剖面的"我在那一点的高度"改用**我→目标这条直线**外推，而不是用当前速度直线外推
        ///      —— 速度外推在尾追/俯冲末段会一路算到地面以下（当前下沉率比需要的平均下沉率更陡），
        ///      于是"马上要命中"被误判成"马上要撞地" → 抬升 → 脱靶；
        ///   ③ 进入 TerminalRange 后威胁度按比例打折，把控制权交回比例导引。
        /// targetRange >= float.MaxValue 表示没有目标（按旧的纯速度外推走）。
        /// </summary>
        public static Vector3 Compute(IObstacleProbe probe, Vector3 pos, Vector3 vel, float maxLatAccel,
                                      AvoidParams p, float priorThreat, Vector3 targetPos, float targetRange,
                                      out float threat, out float agl)
        {
            threat = 0f;
            agl = -1f;
            if (probe == null || p == null || !p.Enabled) return Vector3.zero;

            float spd = vel.magnitude;
            Vector3 fwd = spd > 0.5f ? vel / spd : Vector3.forward;
            // 视距不能只看"反应时间"：导弹的最大机动能力是有限过载，转弯半径 R = v^2/a。
            // 只按 1.6s 算（900m/s 时 1440m）会短于 R（2066m）—— 于是"看见山脊时已经拐不过去了"，
            // 表现为开了避障还是削到山脊。视距至少要够一个转弯半径。
            float look = LookDistance(spd, maxLatAccel, p);

            // ---- 目标优先：把视距截到"到目标的距离"，并算出终端段的让步系数 ----
            float commit = 1f;
            bool hasTarget = (targetRange > 1f) && (targetRange < float.MaxValue);
            if (hasTarget)
            {
                look = Mathf.Min(look, Mathf.Max(60f, targetRange));
                if (targetRange < p.TerminalRange)
                    commit = Mathf.Lerp(Mathf.Max(0f, p.TerminalMin), 1f,
                                        Mathf.Clamp01(targetRange / Mathf.Max(1f, p.TerminalRange)));
                // 末段（最后一个量级引信半径的距离内）把避障彻底交还给导引：
                // 离线仿真实测，这里哪怕只留 0.2 的威胁度，也会让最近接近距离从 22m 变 26m ——
                // 正好跨过 25m 引信、变成"扎进地里"的脱靶。地面撞击判定仍然是最后一道保险。
                commit = Mathf.Min(commit,
                                   Mathf.Clamp01((targetRange - p.Clearance) / Mathf.Max(60f, p.Clearance * 5f)));
            }

            // 弹体系：up 是"世界 up 在垂直于速度平面上的投影"，保证规避力不推着导弹加减速
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(Vector3.forward, fwd);
            right.Normalize();
            Vector3 up = Vector3.Cross(fwd, right).normalized;

            Vector3 acc = Vector3.zero;
            float worst = 0f;

            // "是否真的在往地里扎"的门控 —— 避障威胁的定义必须是**会撞上**，不是**没飞够高**。
            // 不加这道门会出大事故：从跑道（离地 5m）平飞发射的导弹会被 40m 安全余量
            // 当成满威胁，一道 45G 的抬升把它从 5m 直接推到 964m，等它掉回来目标早飞远了。
            // sinkK = 0（平飞/爬升）-> 向下看的探针与离地高度项完全不参与；
            //       = 1（明显下沉，按 10% 速度折算）-> 全额参与。
            float sinkK = Mathf.Clamp01(-vel.y / Mathf.Max(6f, spd * 0.10f));

            // ---- 1) 3x3 探针扇形 ----
            // 方向直接用球坐标在 (fwd,right,up) 正交基上合成，不走 Quaternion.AngleAxis ——
            // 那个是 Unity 的引擎调用（ECall），在游戏外跑仿真时会炸。
            for (int i = 0; i < YawDeg.Length; i++)
            {
                float yaw = YawDeg[i] * Mathf.Deg2Rad;
                float cy = Mathf.Cos(yaw);
                float sy = Mathf.Sin(yaw);
                for (int j = 0; j < PitchDeg.Length; j++)
                {
                    bool lookDown = PitchDeg[j] < 0f;
                    if (lookDown && sinkK <= 0.01f) continue;   // 平飞时"脚下的地"不是障碍
                    float pit = PitchDeg[j] * Mathf.Deg2Rad;
                    float cp = Mathf.Cos(pit);
                    Vector3 d = fwd * (cp * cy) + right * (cp * sy) + up * Mathf.Sin(pit);
                    float dist;
                    Vector3 n;
                    if (!probe.Cast(pos, d, look, out dist, out n)) continue;

                    float w = 1f - Mathf.Clamp01(dist / look);   // 越近越紧急
                    w *= w;
                    if (lookDown) w *= sinkK;
                    if (w < 0.003f) continue;

                    Vector3 push = n - fwd * Vector3.Dot(n, fwd);   // 只保留垂直于速度的分量
                    if (push.sqrMagnitude < 0.04f)
                    {
                        push = d - fwd * Vector3.Dot(d, fwd);       // 迎面墙：改用探针侧向分量
                        if (push.sqrMagnitude < 0.04f) push = up;   // 正前方退化：抬升
                    }
                    acc += push.normalized * w;
                    if (w > worst) worst = w;
                }
            }

            // ---- 2) 前方山脊剖面（探针缝隙的补丁）----
            // 判据是"按当前垂直速度外推，到那个点我会不会撞上"，所以用**外推高度 + 余量**，
            // 不能用当前高度 + 安全高度 —— 后者在贴地平飞时会一直喊"高度不够"，把弹顶上天。
            // 余量是自适应的：还没进入规避（priorThreat 低）只要"不撞"（ProfileMargin 20m）；
            // 已经在规避机动中（priorThreat 高）就按完整安全高度 Clearance 跟地形，
            // 保证越过山脊时留的是真余量，而不是擦着 1m 过去。
            float margin = Mathf.Lerp(p.ProfileMargin, p.Clearance, Mathf.Clamp01(priorThreat));
            for (int k = 0; k < ProfileFrac.Length; k++)
            {
                float ahead = look * ProfileFrac[k];
                Vector3 pt = pos + fwd * ahead;
                Vector3 o = pt + Vector3.up * 400f;
                float dist;
                Vector3 n;
                if (!probe.Cast(o, Vector3.down, 1400f, out dist, out n)) continue;
                float groundY = o.y - dist;
                float tArrive = ahead / Mathf.Max(1f, spd);
                // 有目标时用"我 -> 目标"这条直线外推（导弹的意图是命中目标，它在该点的高度
                // 就是 LOS 上的高度）；没有目标才退回"当前速度直线"外推。
                // ⚠ 这里必须用 LOS：尾追贴地目标时速度直线的下沉率比需要的平均下沉率更陡，
                //   速度外推会在目标之前就先穿到地面以下，于是把"马上命中"误判成"马上撞地"。
                float myY = hasTarget
                    ? Mathf.Lerp(pos.y, targetPos.y, Mathf.Clamp01(ahead / Mathf.Max(1f, targetRange)))
                    : pos.y + vel.y * tArrive;
                float need = groundY + margin - myY;
                if (need <= 0f) continue;
                float w = Mathf.Clamp01(need / Mathf.Max(20f, p.ProfileSpan));
                acc += up * w;
                if (w > worst) worst = w;
            }

            // ---- 3) 正下方：真实离地高度（只在往下扎的时候算威胁）----
            {
                float dist;
                Vector3 n;
                if (probe.Cast(pos, Vector3.down, 2500f, out dist, out n))
                {
                    agl = dist;
                    float w = Mathf.Clamp01((p.Clearance - dist) / Mathf.Max(8f, p.Clearance)) * sinkK;
                    if (w > 0f)
                    {
                        acc += up * (w * 0.9f);
                        if (w > worst) worst = w;
                    }
                }
            }

            // ---- 4) 绝对高度兜底（探针拿不到地面数据时）----
            if (agl < 0f && pos.y < p.AbsFloor)
            {
                float w = Mathf.Clamp01((p.AbsFloor - pos.y) / Mathf.Max(10f, p.AbsFloor)) * sinkK;
                if (w > 0f)
                {
                    acc += up * w;
                    if (w > worst) worst = w;
                }
            }

            // 终端段（贴着目标）把威胁度打折：目标优先，宁可冒一点擦地风险也要把弹送到目标上
            threat = Mathf.Clamp01(worst * commit);
            if (acc.sqrMagnitude < 1e-6f) return Vector3.zero;
            return acc.normalized;
        }
    }

    /// <summary>游戏内探针：Physics.RaycastNonAlloc + 过滤掉"飞机/导弹"（它们是目标，不是障碍）。</summary>
    public class UnityObstacleProbe : IObstacleProbe
    {
        // 缓冲必须够大：打向密集地形 chunk 时 32 会被打满并**静默截断**，
        // 最近命中可能根本不在缓冲里 —— 既是性能浪费（白跑一遍过滤）也是正确性问题。
        // 256 条目 × 约 48 字节 ≈ 12KB，静态复用不产生 GC。
        private static readonly RaycastHit[] Buf = new RaycastHit[256];
        private static readonly Dictionary<int, bool> DynCache = new Dictionary<int, bool>();

        // ---- 诊断：射线打满缓冲 / 过滤命中的次数（供自测确认地形密度） ----
        private static int _overflows;
        private static int _dynHits;
        public static int BufferOverflows { get { return _overflows; } }
        public static int DynamicFiltered { get { return _dynHits; } }
        public static void ResetStats() { _overflows = 0; _dynHits = 0; }

        public bool Cast(Vector3 origin, Vector3 dir, float maxDist, out float dist, out Vector3 normal)
        {
            dist = 0f;
            normal = Vector3.up;
            int n;
            // DefaultRaycastLayers = 除 "Ignore Raycast" 外的全部层，和游戏自己的探地射线一致。
            // 不能收窄到"地形层"：游戏侧是禁区，无法确认建筑/山体各在哪层，收窄会漏掉障碍。
            try { n = Physics.RaycastNonAlloc(origin, dir, Buf, maxDist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore); }
            catch { return false; }

            if (n >= Buf.Length) _overflows++;   // 打满 = 结果可能不完整

            float best = float.MaxValue;
            Vector3 bn = Vector3.up;
            int bestIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (Buf[i].distance >= best) continue;   // 先比距离，绝大多数条目在这里就被刷掉
                Collider c = Buf[i].collider;
                if (c == null) continue;
                if (IsDynamic(c)) { _dynHits++; continue; }   // 飞机/导弹：目标不是墙
                best = Buf[i].distance;
                bn = Buf[i].normal;
                bestIdx = i;
            }
            if (bestIdx < 0) return false;
            dist = best;
            normal = bn;
            return true;
        }

        /// <summary>
        /// 廉价"前方是否有东西"：单次 Physics.Raycast 直接拿最近命中，
        /// 不收集全部命中、不做动态过滤（山体/建筑一定不是动态实体）。
        /// 撞地兜底每帧都跑，这是它唯一负担得起的做法。
        /// </summary>
        public bool Blocked(Vector3 origin, Vector3 dir, float maxDist)
        {
            try
            {
                RaycastHit h;
                return Physics.Raycast(origin, dir, out h, maxDist, Physics.DefaultRaycastLayers,
                                       QueryTriggerInteraction.Ignore);
            }
            catch { return false; }
        }

        /// <summary>动态实体（玩家/AI 飞机、其它导弹）不是地形障碍 —— 否则导弹会把目标当墙躲开。</summary>
        private static bool IsDynamic(Collider c)
        {
            // 地形/静态物体是绝大多数：用 layer + isTrigger 先做一个便宜的预筛。
            // 引擎层里只有少量动态物件（飞机/导弹都是 Rigidbody 挂载物），
            // 所以"没有 Rigidbody 的 collider 一律当静态"能省掉几乎全部 GetComponentInParent。
            int id = c.GetInstanceID();
            bool dyn;
            if (DynCache.TryGetValue(id, out dyn)) return dyn;

            dyn = false;
            try
            {
                // 便宜判据优先：静态地形块没有 attachedRigidbody
                Rigidbody rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic)
                {
                    if (c.GetComponentInParent<PlaneContainer>() != null) dyn = true;
                    else if (c.GetComponentInParent<MissileController>() != null) dyn = true;
                    else if (c.GetComponentInParent<AiEntityMarker>() != null) dyn = true;
                }
            }
            catch { }
            if (DynCache.Count > 8192) DynCache.Clear();
            DynCache[id] = dyn;
            return dyn;
        }
    }

    // =====================================================================
    // 设计文件 (.planedesign) 解析 + 实体还原
    // =====================================================================
    public class DesignNode
    {
        public string Name;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
    }

    public static class DesignModel
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags BFS = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---------------- 解析缓存：避免重复读取/解析 .planedesign 文件 ----------------
        private class ParseCacheEntry
        {
            public long LastWriteTime;
            public float Cost;
            public List<string> RequiredParts;
            public List<DesignNode> Nodes;
        }
        private static readonly Dictionary<string, ParseCacheEntry> _parseCache = new Dictionary<string, ParseCacheEntry>();
        private const int MaxCacheEntries = 64; // 最多缓存64个设计文件

        // ---------------- 纯字节解析（不依赖游戏加载流程，用于诊断与降级） ----------------

        private static string ReadStr(BinaryReader br)
        {
            // GameDataReader.ReadString(): 先 1 字节 bool（true -> 空串），再 7bit 长度 + UTF8
            if (br.ReadBoolean()) return "";
            int len = 0, shift = 0, b;
            do
            {
                b = br.ReadByte();
                len |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0 && shift < 35);
            byte[] raw = br.ReadBytes(len);
            return Encoding.UTF8.GetString(raw);
        }

        private static bool LooksLikeName(byte[] data, long pos, string expect)
        {
            if (pos < 0 || pos + 2 >= data.Length) return false;
            if (data[pos] != 0) return false;
            int len = data[pos + 1];
            if (len < 1 || len > 40) return false;
            if (pos + 2 + len >= data.Length) return false;
            for (int i = 0; i < len; i++)
            {
                byte c = data[pos + 2 + i];
                if (c < 0x20 || c > 0x7e) return false;
            }
            if (expect.Length == 0) return true;
            string s = Encoding.ASCII.GetString(data, (int)pos + 2, len);
            return s.Replace("(Clone)", "") == expect;
        }

        /// <summary>
        /// 只把"部件名 + 局部变换"读出来。Body 类部件后面跟一段 ProceduralFuselage
        /// 数据（长度不固定），用"下一个节点名必须对得上头部需求部件表"来做对齐校验。
        /// </summary>
        public static List<DesignNode> ParseNodes(string path, out float cost, out List<string> requiredParts)
        {
            cost = 0f;
            requiredParts = new List<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<DesignNode>();

            // 检查缓存
            long lastWrite = 0;
            try { lastWrite = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
            ParseCacheEntry cached = null;
            lock (_parseCache)
            {
                if (_parseCache.TryGetValue(path, out cached) && cached.LastWriteTime == lastWrite)
                {
                    cost = cached.Cost;
                    requiredParts = new List<string>(cached.RequiredParts);
                    // 返回副本，避免调用方修改缓存
                    var copy = new List<DesignNode>(cached.Nodes.Count);
                    for (int i = 0; i < cached.Nodes.Count; i++)
                    {
                        var src = cached.Nodes[i];
                        copy.Add(new DesignNode { Name = src.Name, Pos = src.Pos, Rot = src.Rot, Scale = src.Scale });
                    }
                    return copy;
                }
            }

            var nodes = new List<DesignNode>();
            byte[] data = File.ReadAllBytes(path);

            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                br.ReadInt32();            // 头部标记
                cost = br.ReadSingle();    // 造价
                int req = br.ReadInt32();
                for (int i = 0; i < req; i++) requiredParts.Add(ReadStr(br));

                int n = br.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var node = new DesignNode();
                    node.Name = ReadStr(br);
                    node.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    node.Rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    node.Scale = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    nodes.Add(node);

                    long after = ms.Position;
                    string expect = (i + 1 < requiredParts.Count) ? requiredParts[i + 1] : "";
                    // Body 类部件后面跟一段 ProceduralFuselage 数据（18 个 float = 72 字节），
                    // 其余部件紧跟下一个部件。两条路都试，用"下一个部件名对得上"来确认。
                    bool bodyish = node.Name != null && node.Name.ToLowerInvariant().Contains("body");
                    long first = bodyish ? after + 72 : after;
                    long second = bodyish ? after : after + 72;
                    if (LooksLikeName(data, first, expect)) { ms.Position = first; continue; }
                    if (LooksLikeName(data, second, expect)) { ms.Position = second; continue; }
                    // 兜底：向后扫一个能对上号的偏移
                    long found = -1;
                    for (long off = 0; off <= 200; off += 2)
                    {
                        if (LooksLikeName(data, after + off, expect)) { found = after + off; break; }
                    }
                    if (found < 0) break;   // 对不齐了，后面的不要了
                    ms.Position = found;
                }
            }

            // 更新缓存
            if (nodes.Count > 0)
            {
                lock (_parseCache)
                {
                    // 缓存满了就清空最旧的（简单策略）
                    if (_parseCache.Count >= MaxCacheEntries) _parseCache.Clear();
                    _parseCache[path] = new ParseCacheEntry
                    {
                        LastWriteTime = lastWrite,
                        Cost = cost,
                        RequiredParts = new List<string>(requiredParts),
                        Nodes = new List<DesignNode>(nodes)
                    };
                }
            }
            return nodes;
        }

        // ---------------- 用游戏自己的加载流程还原实体 ----------------
        //
        // 与 PlaneStorage.LoadPart 完全一致，因此外形与游戏加载的结果相同。
        // 解析与实例化必须交错进行：ProceduralFuselage 会从同一个 reader 里吃掉它那段数据。

        public static GameObject BuildWithGameLoader(string path, string rootName, IMachineApi api,
                                                     bool keepColliders = false, bool startInactive = false,
                                                     bool ammoLook = false)
        {
            GameObject root = new GameObject(rootName);
            // AI 飞机要先把整棵部件树压成 inactive：这样部件的 Awake/Start 会等到
            // 我们挂完 PlaneContainer / PlaneController 之后再跑，否则引擎找父级控制器会拿到 null。
            if (startInactive) root.SetActive(false);
            if (!File.Exists(path)) { api.Log("AAM: design file not found " + path); return null; }

            // 建模前一次性建好材质来源缓存：之后每个部件只查字典，不再各自全场景扫描。
            System.Diagnostics.Stopwatch _bsw = System.Diagnostics.Stopwatch.StartNew();
            long _primeMs = 0;
            PrimeMaterialCache(api);
            _primeMs = _bsw.ElapsedMilliseconds;
            _matApplied = 0;
            _matMissed = 0;

            int built = 0;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    var r = new GameDataReader(br);
                    // 头部第一个 int 就是游戏 GetVersion() 会算出的 version（本文件是 27）。
                    // 必须先设好：ProceduralFuselage.Load 里 `if (version < 9) LegacyLoad()`
                    // 走旧格式分支会读错字节数，后面的部件名就全乱了。
                    r.version = r.ReadInt();
                    r.ReadFloat();
                    int req = r.ReadInt();
                    for (int i = 0; i < req; i++) r.ReadString();

                    int n = r.ReadInt();
                    for (int i = 0; i < n; i++)
                    {
                        string name = r.ReadString();
                        Vector3 pos = r.ReadVector3();
                        Quaternion rot = r.ReadQuaternion();
                        Vector3 scale = r.ReadVector3();

                        GameObject prefab = GetPartPrefabSource(name);
                        if (prefab == null)
                        {
                            api.Log("AAM: no part prefab for '" + name + "', abort model stream at part " + i + "/" + n);
                            break;
                        }
                        GameObject go = UnityEngine.Object.Instantiate(prefab);
                        go.name = name;
                        go.transform.SetParent(root.transform, false);
                        go.transform.localPosition = pos;
                        go.transform.localRotation = rot;
                        go.transform.localScale = scale;

                        // ProceduralFuselage.Load(reader) 是私有的，反射调用
                        try
                        {
                            Component pf = go.GetComponent("ProceduralFuselage");
                            if (pf != null)
                            {
                                MethodInfo m = pf.GetType().GetMethod("Load", BF, null, new Type[] { typeof(GameDataReader) }, null);
                                if (m != null) m.Invoke(pf, new object[] { r });
                            }
                        }
                        catch (Exception e) { api.Log("AAM: procedural load failed: " + e.Message); }

                        SanitizeAi(go, keepColliders);
                        ApplyDesignMaterials(go, name, api, ammoLook);
                        built++;
                    }
                }
            }
            catch (Exception e)
            {
                api.Log("AAM: model build error " + e.Message);
            }

            if (built == 0)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }
            // 计时留档：优化前生成一架 66~95 部件的 AI 飞机单帧要 473~684ms，
            // 主因是每个部件各扫一次全场景。改完后这里应该是个位数毫秒。
            api.Log("AAM: design model built from " + System.IO.Path.GetFileName(path)
                    + " parts=" + built + " mats=" + _matApplied + " miss=" + _matMissed
                    + " prime=" + _primeMs + "ms total=" + _bsw.ElapsedMilliseconds + "ms");
            return root;
        }

        /// <summary>降级路径：只拿部件名+变换，外形从场景里已有的同类部件克隆（纯视觉，不带任何组件）。</summary>
        public static GameObject BuildFallback(string path, string rootName, IMachineApi api)
        {
            float cost;
            List<string> req;
            List<DesignNode> nodes = ParseNodes(path, out cost, out req);
            if (nodes.Count == 0) { api.Log("AAM: fallback parse produced 0 nodes"); return null; }

            // 建好"部件名 -> 场景实例"缓存，下面每个节点只查字典（原实现每个节点全场景扫一次）
            PrimeMaterialCache(api);

            GameObject root = new GameObject(rootName);
            int cloned = 0, prim = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                DesignNode nd = nodes[i];
                GameObject src = FindScenePartVisual(nd.Name);
                GameObject part = null;
                if (src != null)
                {
                    part = CloneVisualOnly(src, nd.Name);
                    cloned++;
                }
                else
                {
                    part = MakePrimitive(nd.Name);
                    prim++;
                }
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = nd.Pos;
                part.transform.localRotation = nd.Rot;
                part.transform.localScale = nd.Scale;
                SanitizeAi(part);
            }
            api.Log("AAM: fallback model built nodes=" + nodes.Count + " cloned=" + cloned + " primitive=" + prim);
            return root;
        }

        /// <summary>
        /// 去掉一切会与"玩家飞机 / 音频 / 物理"扯上关系的组件。
        /// keepColliders = true 时保留碰撞体：AI 飞机（靶机）要用游戏原版的机体碰撞，
        /// 这样它和玩家的飞机在物理上是同一类东西；导弹那种纯视觉模型则不需要。
        /// </summary>
        public static void SanitizeAi(GameObject go, bool keepColliders = false)
        {
            if (go == null) return;
            try
            {
                if (!keepColliders)
                {
                    Collider[] cols = go.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < cols.Length; i++) UnityEngine.Object.Destroy(cols[i]);
                }

                // 引擎音效一律去掉：AI 实体不允许发出声音（语音警报的最外层保险）
                AudioSource[] auds = go.GetComponentsInChildren<AudioSource>(true);
                for (int i = 0; i < auds.Length; i++) UnityEngine.Object.Destroy(auds[i]);

                // 部件自带的刚体要清掉：整架飞机最终只共用 PlaneContainer 那一个刚体
                Rigidbody[] rbs = go.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < rbs.Length; i++) UnityEngine.Object.Destroy(rbs[i]);

                // 纯视觉模型（导弹/机炮）不允许带引擎：避免导弹自带推力作用于场景
                if (!keepColliders)
                {
                    Engine[] engs = go.GetComponentsInChildren<Engine>(true);
                    for (int i = 0; i < engs.Length; i++) UnityEngine.Object.Destroy(engs[i]);
                }
            }
            catch { }
        }

        /// <summary>探测 BuildingPart 是否有颜色/涂装字段（一次性，诊断用）。</summary>
        private static bool _bpProbed;
        private static void ProbeBuildingPartFields(IMachineApi api)
        {
            if (_bpProbed) return;
            _bpProbed = true;
            try
            {
                FieldInfo[] fields = typeof(BuildingPart).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < fields.Length; i++)
                {
                    sb.Append(fields[i].Name).Append(":").Append(fields[i].FieldType.Name).Append(" ");
                }
                api.Log("AAM: BuildingPart fields: " + sb.ToString());

                System.Reflection.MethodInfo[] methods = typeof(BuildingPart).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                System.Text.StringBuilder sb2 = new System.Text.StringBuilder();
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name.IndexOf("Color", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || methods[i].Name.IndexOf("Paint", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || methods[i].Name.IndexOf("Mat", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        sb2.Append(methods[i].Name).Append(" ");
                }
                api.Log("AAM: BuildingPart color/paint/mat methods: " + sb2.ToString());
            }
            catch { }
        }

        // =================================================================
        // 材质来源缓存（2026-09-13：AI 生成 / 导弹发射瞬间卡顿的主因）
        //
        // 旧实现的复杂度是灾难性的：ApplyDesignMaterials **对每个部件**都会
        //   1) GetComponentsInChildren<BuildingPart>  —— 扫一遍玩家整机（95+ 部件）
        //   2) 没命中再 Resources.FindObjectsOfTypeAll —— 全场景扫一遍（步骤2）
        //   3) 还没命中再全场景扫一遍                  ——（步骤3兜底）
        // 一架 66~95 部件的 AI 飞机 = 上百次全场景扫描。场景里有 6k~18k 个对象时，
        // 实测生成瞬间单帧 473~684ms；发射一枚导弹（16 部件）也要 236~262ms。
        // 这正是"起飞/点火/降落时特别卡"的来源 —— 与速度、转向本身无关，
        // 只是起降和开火恰好是触发建模的时刻，所以看起来像"机动导致的卡顿"。
        //
        // 现在：每次建模（或缓存失效时）只做 1 次玩家整机扫描 + 1 次全场景扫描，
        // 之后所有部件走字典 O(1) 查表。判定顺序与命中条件与旧实现等价。
        // =================================================================
        private static readonly Dictionary<string, Material[]> _matCache
            = new Dictionary<string, Material[]>(StringComparer.Ordinal);
        // 颜色来源只记玩家飞机上的部件（严格保持旧语义：步骤2/3 不复制涂装）
        private static readonly Dictionary<string, BuildingPart> _matSrcCache
            = new Dictionary<string, BuildingPart>(StringComparer.Ordinal);
        private static Material[] _matFallback;
        // 场景里"部件名 -> 部件实例"：BuildFallback 克隆外形用，同样只扫一次而不是每节点扫一次
        private static readonly Dictionary<string, GameObject> _scenePartGo
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static bool _matCacheReady;
        private static int _matCacheScene = -1;              // 缓存所属场景句柄
        private static UnityEngine.Object _matCachePlane;     // 缓存所属玩家飞机
        private static MethodInfo _miGetCurColor, _miSetColor;
        private static int _miSetColorArgN;
        private static bool _miColorProbed;
        private static int _matApplied, _matMissed;           // 本次建模命中/未命中计数
        // 弹药（导弹 / 机炮 / 热诱弹）专用：与场景无关的中性灰白材质。
        // 按部件名缓存，避免每发一枚导弹都新建一批 Material。
        private static readonly Dictionary<string, Material> _ammoMatCache
            = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static readonly HashSet<string> _ammoMatLogged = new HashSet<string>();
        private static readonly Color AmmoTint = new Color(0.88f, 0.89f, 0.91f, 1f);
        private static string _matFallbackSrc = "";   // 诊断：兜底材质是从哪个场景物体上抓的

        /// <summary>物体在场景层级里的路径（诊断用：告诉我们"兜底材质"到底是从哪个东西上抓的）。</summary>
        private static string ScenePath(Transform t)
        {
            try
            {
                string path = t.name;
                Transform p = t.parent;
                int guard = 0;
                while (p != null && guard++ < 6) { path = p.name + "/" + path; p = p.parent; }
                return path;
            }
            catch { return "?"; }
        }

        /// <summary>
        /// 部件上第一个"网格"渲染器。不要用 GetComponentInChildren&lt;Renderer&gt; 直接取 ——
        /// 发动机 / 损伤件这类部件底下挂着粒子渲染器，先拿到它就会把火焰粒子材质当成机体材质。
        /// </summary>
        private static Renderer FirstMeshRenderer(GameObject go)
        {
            if (go == null) return null;
            try
            {
                MeshRenderer mr = go.GetComponentInChildren<MeshRenderer>(true);
                if (mr != null) return mr;
                SkinnedMeshRenderer sm = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (sm != null) return sm;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 建立材质来源缓存。按"场景 + 玩家飞机"失效：飞行中反复发射导弹不会重建，
        /// 换场景或换飞机才重建。整局游戏通常只扫几次，而不是几百次。
        /// </summary>
        private static void PrimeMaterialCache(IMachineApi api)
        {
            System.Diagnostics.Stopwatch sw = null;
            try
            {
                int scene = -1;
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { }
                PlaneContainer src = null;
                try { src = PlaneContainer.Instance; } catch { }

                bool samePlane = (src == null) ? (_matCachePlane == null) : (src == _matCachePlane);
                if (_matCacheReady && scene == _matCacheScene && samePlane) return;   // 缓存仍有效

                sw = System.Diagnostics.Stopwatch.StartNew();
                _matCache.Clear();
                _matSrcCache.Clear();
                _scenePartGo.Clear();
                _ammoMatCache.Clear();     // 上一场景的材质对象已随场景失效
                _matFallback = null;
                _matFallbackSrc = "";

                ProbeBuildingPartFields(api);

                // 1) 玩家整机：一次扫描建索引（等价于旧步骤1，同时作为涂装来源）
                if (src != null)
                {
                    BuildingPart[] parts = src.GetComponentsInChildren<BuildingPart>(true);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        BuildingPart bp = parts[i];
                        if (bp == null) continue;
                        string pn = string.IsNullOrEmpty(bp.partName) ? bp.gameObject.name : bp.partName;
                        pn = pn.Replace("(Clone)", "").Trim();
                        if (pn.Length == 0 || _matCache.ContainsKey(pn)) continue;
                        Renderer sr = null;
                        try { sr = FirstMeshRenderer(bp.gameObject); } catch { }
                        if (sr == null) { try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { } }
                        if (sr == null || sr.sharedMaterials == null || sr.sharedMaterials.Length == 0) continue;
                        _matCache[pn] = sr.sharedMaterials;
                        _matSrcCache[pn] = bp;      // 只有玩家飞机上的部件才复制涂装
                    }
                }

                // 2) 全场景一次扫描：补齐"场景里同名部件"（旧步骤2）与"任意带贴图部件"（旧步骤3）
                try
                {
                    UnityEngine.Object[] arr = Resources.FindObjectsOfTypeAll(typeof(BuildingPart));
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            BuildingPart bp = arr[i] as BuildingPart;
                            if (bp == null || bp.gameObject == null) continue;
                            if (!bp.gameObject.scene.IsValid() || !bp.gameObject.scene.isLoaded) continue;  // 排除预制体资源
                            string pn = string.IsNullOrEmpty(bp.partName) ? bp.gameObject.name : bp.partName;
                            pn = pn.Replace("(Clone)", "").Trim();
                            Renderer sr = null;
                            try { sr = FirstMeshRenderer(bp.gameObject); } catch { }
                            if (sr == null) { try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { } }
                            if (sr == null || sr.sharedMaterials == null || sr.sharedMaterials.Length == 0) continue;
                            if (pn.Length > 0)
                            {
                                if (!_matCache.ContainsKey(pn)) _matCache[pn] = sr.sharedMaterials;
                                if (!_scenePartGo.ContainsKey(pn)) _scenePartGo[pn] = bp.gameObject;
                            }
                            if (_matFallback == null && sr.sharedMaterial != null && sr.sharedMaterial.mainTexture != null)
                            {
                                _matFallback = sr.sharedMaterials;
                                _matFallbackSrc = ScenePath(bp.transform);
                            }
                        }
                    }
                }
                catch { }

                _matCacheReady = true;
                _matCacheScene = scene;
                _matCachePlane = src;
            }
            catch (Exception e) { if (api != null) api.Log("AAM: material cache prime failed " + e.Message); }
            finally
            {
                if (sw != null && api != null)
                    api.Log("AAM: material cache primed in " + sw.ElapsedMilliseconds + "ms"
                            + " names=" + _matCache.Count + " fallback=" + (_matFallback != null)
                            + " fallbackSrc=" + (_matFallbackSrc.Length > 0 ? _matFallbackSrc : "none"));
            }
        }

        /// <summary>
        /// 应用部件材质/贴图：游戏正常流程加载设计时会为每个部件挂上带贴图的材质，
        /// 而 AI 实体是 Instantiate(裸预制体) 拼出来的，运行时预制体资源往往没有材质，
        /// 结果整机变成白模。这里从玩家飞机（或场景里同类部件）把材质整份复制过来。
        ///
        /// 注意：这里必须保持 O(1) —— 每个部件都要走一次，任何全场景扫描都会被放大
        /// 到"部件数 × 全场景对象数"。材质来源一律查 PrimeMaterialCache 建的字典。
        /// </summary>
        /// <summary>
        /// ammoLook = true 时用于弹药（导弹 / 机炮 / 热诱弹）：材质不再直接复用从场景里抓来的
        /// materials（那套东西随场景变化，曾经导致正式版里弹体整根变成亮紫色），
        /// 改成"照抄一个来源材质的贴图，但色调用固定的中性灰白"，并且把每个材质槽都填满。
        /// </summary>
        private static void ApplyDesignMaterials(GameObject partGo, string partName, IMachineApi api, bool ammoLook = false)
        {
            if (partGo == null) return;
            try
            {
                Renderer pr = FirstMeshRenderer(partGo);
                if (pr == null) { try { pr = partGo.GetComponentInChildren<Renderer>(true); } catch { } }
                if (pr == null) return;

                string want = partName.Replace("(Clone)", "").Trim();

                // ---- 弹药：固定灰白，与场景解耦 ----
                if (ammoLook)
                {
                    Material[] srcs;
                    bool hit = _matCache.TryGetValue(want, out srcs) && srcs != null && srcs.Length > 0;
                    string srcTag = hit ? "cache" : "fallback";
                    if (!hit) srcs = _matFallback;
                    Material am = AmmoMaterialFor(want, srcs, api, srcTag);
                    if (am != null)
                    {
                        int n = 1;
                        try { if (pr.sharedMaterials != null && pr.sharedMaterials.Length > 0) n = pr.sharedMaterials.Length; } catch { }
                        Material[] arr = new Material[n];
                        for (int i = 0; i < n; i++) arr[i] = am;
                        pr.sharedMaterials = arr;
                        _matApplied++;
                        return;
                    }
                }

                // 1) 玩家飞机上的同类部件（材质带游戏贴图与配色）—— 命中才复制涂装
                Material[] mats;
                if (_matCache.TryGetValue(want, out mats) && mats != null && mats.Length > 0)
                {
                    pr.sharedMaterials = mats;
                    BuildingPart srcBp;
                    if (_matSrcCache.TryGetValue(want, out srcBp) && srcBp != null)
                        CopyPartColor(partGo, srcBp, api);
                    _matApplied++;
                    return;
                }

                // 2) 兜底：场景任意带贴图的部件材质（避免白模）
                if (_matFallback != null && _matFallback.Length > 0)
                {
                    pr.sharedMaterials = _matFallback;
                    _matApplied++;
                    return;
                }

                _matMissed++;
            }
            catch { }
        }

        /// <summary>
        /// 弹药的固定外观材质：照抄一个来源材质（保住游戏里的金属贴图），
        /// 但色调强制成中性灰白，且绝不返回"着色器丢失"的材质（那种会渲染成品红）。
        /// 每个部件名只建一次，之后复用。
        /// </summary>
        private static Material AmmoMaterialFor(string partName, Material[] sources, IMachineApi api, string srcTag)
        {
            Material cached;
            if (_ammoMatCache.TryGetValue(partName, out cached) && cached != null) return cached;

            Material src = null;
            if (sources != null)
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] != null && sources[i].shader != null) { src = sources[i]; break; }
                }
            }

            Material built = null;
            if (src != null)
            {
                try { built = new Material(src); } catch { built = null; }
            }
            if (built == null)
            {
                // 兜底：来源不可用时自己造一个，着色器按"一定有"的顺序找
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh != null) { try { built = new Material(sh); } catch { built = null; } }
            }
            if (built == null) { _matMissed++; return null; }

            try { if (built.HasProperty("_Color")) built.color = AmmoTint; } catch { }
            try { if (built.HasProperty("_BaseColor")) built.SetColor("_BaseColor", AmmoTint); } catch { }
            built.name = "AAM_Ammo_" + partName;
            _ammoMatCache[partName] = built;

            if (api != null && _ammoMatLogged.Add(partName))
            {
                string sn = "?", tn = "none";
                try { if (built.shader != null) sn = built.shader.name; } catch { }
                try { if (built.mainTexture != null) tn = built.mainTexture.name; } catch { }
                api.Log("AAM: ammo material '" + partName + "' from=" + srcTag
                        + " src=" + (src != null ? src.name : "none")
                        + " srcObj=" + (_matFallbackSrc.Length > 0 ? _matFallbackSrc : "none")
                        + " shader=" + sn + " tex=" + tn + " color=0.88/0.89/0.91");
            }
            return built;
        }

        /// <summary>把源部件的涂装颜色复制到目标部件（BuildingPart.SetColor / GetCurrentColor）。</summary>
        private static void CopyPartColor(GameObject dstGo, BuildingPart srcPart, IMachineApi api)
        {
            try
            {
                BuildingPart dstBp = dstGo.GetComponent<BuildingPart>();
                if (dstBp == null || srcPart == null) return;

                // 反射句柄一次解析终身复用：SetColor 的参数个数（Color[, bool]）也只判断一次
                if (!_miColorProbed)
                {
                    _miColorProbed = true;
                    const BindingFlags BFc = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _miGetCurColor = srcPart.GetType().GetMethod("GetCurrentColor", BFc);
                    _miSetColor = dstBp.GetType().GetMethod("SetColor", BFc);
                    _miSetColorArgN = (_miSetColor != null) ? _miSetColor.GetParameters().Length : 0;
                }
                if (_miGetCurColor == null || _miSetColor == null) return;

                object c = _miGetCurColor.Invoke(srcPart, null);
                if (c == null) return;
                if (_miSetColorArgN >= 2) _miSetColor.Invoke(dstBp, new object[] { c, true });
                else _miSetColor.Invoke(dstBp, new object[] { c });
            }
            catch { }
        }


        private static object _ppObject;
        private static MethodInfo _miGetPartPrefab;
        private static bool _ppScanned;
        private static bool _ppUsable;

        public static bool PrefabsUsable { get { return _ppUsable; } }

        private static void ScanPrefabs()
        {
            if (_ppScanned) return;
            _ppScanned = true;
            try
            {
                FieldInfo f = typeof(PartPrefabs).GetField("instance", BFS);
                object pp = f != null ? f.GetValue(null) : null;
                if (pp == null)
                {
                    UnityEngine.Object[] arr = Resources.FindObjectsOfTypeAll(typeof(PartPrefabs));
                    if (arr != null && arr.Length > 0) pp = arr[0];
                }
                if (pp == null) return;
                _miGetPartPrefab = typeof(PartPrefabs).GetMethod("GetPartPrefab", new Type[] { typeof(string) });
                if (_miGetPartPrefab != null)
                {
                    _ppObject = pp;
                    _ppUsable = true;
                }
            }
            catch { }
        }

        private static GameObject GetPartPrefabSource(string name)
        {
            ScanPrefabs();
            if (_ppUsable && _ppObject != null && _miGetPartPrefab != null)
            {
                try
                {
                    GameObject go = _miGetPartPrefab.Invoke(_ppObject, new object[] { name }) as GameObject;
                    if (go != null) return go;
                }
                catch { }
            }
            return null;
        }

        /// <summary>在场景里找一个同类部件实例（玩家飞机上的部件就是部件预制体的实例）。</summary>
        private static GameObject FindScenePartVisual(string name)
        {
            try
            {
                string want = name.Replace("(Clone)", "").Trim();
                // 先查缓存：BuildFallback 对每个节点都调一次，全场景扫描会被放大成
                // "节点数 × 场景对象数" —— 和材质那里完全同一个坑。
                GameObject cached;
                if (_scenePartGo.TryGetValue(want, out cached) && cached != null) return cached;

                // 缓存未命中（部件还没实例化等罕见情况）才兜底扫一次，并回填缓存
                UnityEngine.Object[] arr = Resources.FindObjectsOfTypeAll(typeof(BuildingPart));
                if (arr == null) return null;
                for (int i = 0; i < arr.Length; i++)
                {
                    BuildingPart bp = arr[i] as BuildingPart;
                    if (bp == null || bp.gameObject == null) continue;
                    if (!bp.gameObject.scene.IsValid() || !bp.gameObject.scene.isLoaded) continue; // 排除预制体资源
                    string pn = string.IsNullOrEmpty(bp.partName) ? bp.gameObject.name : bp.partName;
                    if (pn.Replace("(Clone)", "").Trim() == want)
                    {
                        _scenePartGo[want] = bp.gameObject;
                        return bp.gameObject;
                    }
                }
            }
            catch { }
            return null;
        }

        // ---------------- 纯视觉克隆 ----------------

        private static GameObject CloneVisualOnly(GameObject src, string name)
        {
            GameObject root = new GameObject(name);
            CloneRec(src.transform, src.transform, root.transform);
            return root;
        }

        private static void CloneRec(Transform srcRoot, Transform s, Transform dParent)
        {
            GameObject go = new GameObject(s.name);
            go.transform.SetParent(dParent, false);
            if (s == srcRoot)
            {
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }
            else
            {
                go.transform.localPosition = s.localPosition;
                go.transform.localRotation = s.localRotation;
                go.transform.localScale = s.localScale;
            }
            MeshFilter mf = s.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                MeshFilter nmf = go.AddComponent<MeshFilter>();
                nmf.sharedMesh = mf.sharedMesh;
                MeshRenderer mr = s.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    MeshRenderer nmr = go.AddComponent<MeshRenderer>();
                    nmr.sharedMaterials = mr.sharedMaterials;
                    nmr.shadowCastingMode = mr.shadowCastingMode;
                    nmr.receiveShadows = mr.receiveShadows;
                }
            }
            for (int i = 0; i < s.childCount; i++) CloneRec(srcRoot, s.GetChild(i), go.transform);
        }

        // ---------------- 最后的兜底：程序化几何 ----------------

        private static GameObject MakePrimitive(string name)
        {
            string n = name == null ? "" : name.ToLowerInvariant();
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = name;
            if (n.Contains("wing") || n.Contains("canard") || n.Contains("tail"))
            {
                go.transform.localScale = new Vector3(2.2f, 0.08f, 0.9f);
            }
            else if (n.Contains("ramjet") || n.Contains("engine") || n.Contains("tank"))
            {
                go.transform.localScale = new Vector3(0.5f, 0.5f, 1.6f);
            }
            else
            {
                go.transform.localScale = new Vector3(0.55f, 0.55f, 2.2f);
            }
            return go;
        }
    }

    // =====================================================================
    // 飞行体：推力 + 阻力 + 姿态控制（导弹与靶机共用）
    // =====================================================================
    public class FlightBody
    {
        public Rigidbody Rb;
        public Vector3 Vel;

        public Vector3 Pos { get { return Rb != null ? Rb.position : Vector3.zero; } }
        public float Speed { get { return Vel.magnitude; } }
        public Vector3 Dir
        {
            get
            {
                if (Vel.sqrMagnitude > 0.25f) return Vel.normalized;
                return Rb != null ? Rb.rotation * Vector3.forward : Vector3.forward;
            }
        }

        public void Step(float dt, Vector3 accelCmd, float thrust, float dragK, float maxSpeed)
        {
            if (Rb == null) return;
            float sp = Vel.magnitude;
            float dragDecel = dragK * sp * sp;
            Vector3 dir = sp > 0.5f ? Vel / sp : (Rb.rotation * Vector3.forward);

            Vel += (accelCmd + dir * (thrust - dragDecel)) * dt;

            float m = Vel.magnitude;
            if (m > maxSpeed && m > 0.001f) Vel *= maxSpeed / m;

            if (Rb.isKinematic)
            {
                // 运动学刚体：直接移动位置（零物理交互，不推挤任何物体）
                Rb.MovePosition(Rb.position + Vel * dt);
            }
            else
            {
                Rb.linearVelocity = Vel;
            }
        }

        public void FaceVelocity(float dt, float turnRateDeg)
        {
            if (Rb == null) return;
            if (Vel.sqrMagnitude < 1f) return;
            Quaternion want = PnGuidance.SafeLook(Vel.normalized, Vector3.up);
            Rb.MoveRotation(Quaternion.RotateTowards(Rb.rotation, want, turnRateDeg * dt));
        }
    }

    // =====================================================================
    // 导弹
    // =====================================================================
    public class MissileController : MonoBehaviour
    {
        public float BoostTime = 2.5f;
        public float BoostThrust = 320f;
        public float SustainThrust = 45f;
        public float DragK = 0.00009f;
        public float MaxSpeed = 1250f;
        public float MaxG = 40f;
        public float TurnRateDeg = 55f;
        public float Lifetime = 120f;       // 加速时间（s）：结束后进入惯性巡航
        public float CoastDrag = 100f;       // 惯性巡航阻力：100=PL-15最大速度下直线巡航10秒速度归零
        public float Acceleration = 0.5f;    // 加速度（马赫/秒）：导弹加速能力，决定多久达到最大速度
        public float TrackRate = 60f;         // 跟踪速率（度/秒）：导引头跟踪回路能够跟随目标视线转动的最大角速度
        public float Proximity = 25f;
        public float NavConstant = 4f;
        public float SelfDestructY = -50000f;
        public float IgnitionDist = 10f;   // 离开发射点该距离后才点火推进（安全脱离飞机）
        public bool UseTarget = true;
        public int FactionId = -1;    // 发射者阵营（阵营系统；-1 = 旧系统）
        public string SpecName = "";  // 导弹型号（PL-15 / PL-10 / ...），雷达识别显示用
        public string ShooterModel = "";   // 发射者机型（AI=planedesign 名；玩家=Player）
        public string ShooterCall = "";    // 发射者呼号（AI=Callsign；玩家=You）
        public bool InterceptOnly = false; // 拦截弹（MSDM）：只对来袭导弹有效，命中飞机不造成任何伤害
        public static int InterceptCount = 0;   // 拦截成功计数（自测判据）

        public FlightBody Body = new FlightBody();
        public TargetRef Target;

        // ---- 惯性巡航状态 ----
        private bool _coasting;           // 是否在惯性巡航（加速时间结束后）
        private float _coastT;            // 惯性巡航时间（s）
        private const float CoastBaseDragK = 0.025f;  // 阻力值=100时对应的实际dragK（PL-15基准）

        // ---- 特效引用（惯性巡航时关闭尾焰与拖尾）----
        private TrailRenderer _flameTrail;    // 尾焰TrailRenderer
        private ParticleSystem _smokeTrail;   // 烟轨ParticleSystem

        // ---- 红外导引头（2026-09-14）：全向锁定 + 缩圈 IRCCM ----
        // 全向锁定：不挑进入角，视场里任何红外源都能锁（尾后/迎头/侧向一视同仁）。
        // 缩圈 IRCCM：发现热诱弹后把跟踪门从宽圈收成窄圈，并把诱饵降权；
        //             热诱弹一抛出就迅速减速下坠，角向上很快落到窄圈外，于是被剔除。
        public bool IrSeeker = true;         // 红外导引头（拦截弹 MSDM 关掉：它追的是导弹，不是热源）
        public float SeekerFov = 30f;        // 导引头视场半角（度）
        public float SeekerRange = 9000f;    // 导引头作用距离（m）
        public bool IrcmEnabled = true;      // 缩圈 IRCCM
        public float IrcmReact = 0.35f;      // 识别诱饵的反应时间（s）：这个窗口内诱饵还能把弹骗走
        public float GateWide = 18f;         // 宽圈（正常跟踪）
        public float GateNarrow = 2.5f;      // 窄圈（IRCCM 收拢后）
        public float GateShrink = 1.5f;      // 宽圈收到窄圈用多久（s）
        public float IrcmDecoyPenalty = 0.85f;   // IRCCM 生效后诱饵的加权惩罚（真正的判别是下面这条）
        public float IrcmSpeedFrac = 0.55f;      // IRCCM 运动学判别：诱饵速度掉到真目标的这个比例以下就踢掉

        private readonly List<IrSignature> _irBuf = new List<IrSignature>();
        private float _ircmT;                // IRCCM 已生效时长（s）
        private float _decoySeenT;           // 连续看到诱饵的时长（s，用于反应延迟）
        private Vector3 _seekAim;            // 导引头当前指向（上一帧的加权质心）
        private bool _seekHas;
        private bool _decoyWasIn;            // 上一帧门限里是否还有诱饵（用于一次性记 reject）
        public int IrcmEvents;               // 诊断：触发过几次 IRCCM
        public int DecoyRejects;             // 诊断：缩圈剔除过几次诱饵
        public bool IrcmActive;              // 诊断：此刻 IRCCM 是否生效

        // ---- 常态缩圈（2026-09-16）：锁定后就开始缩圈，不是看到干扰弹才缩 ----
        private float _gateShrinkT;          // 缩圈进度（从锁定开始递增，0~GateShrink）
        private bool _gateShrinking;         // 是否正在缩圈（锁定后启动）

        // ---- 闭眼抗干扰系统（2026-09-16）：检测到强热源突然出现→闭眼→INS制导→重新捕获 ----
        public bool BlindEnabled = true;      // 闭眼抗干扰开关
        public float BlindIrDiffThreshold = 20f;  // 闭眼触发：新源红外值比锁定目标高多少才触发（20 = 高20以上）
        public float BlindDistTolerance = 0.30f;  // 距离容差：新源与目标距离相差不超过30%视为"距离相同"
        public float BlindDuration = 0.20f;   // 闭眼持续时间（s）
        public float BlindCooldown = 1.5f;    // 两次闭眼之间的冷却（s）
        private bool _blind;                  // 是否正在闭眼
        private float _blindT;                // 闭眼剩余时间（s）
        private float _blindCd;               // 闭眼冷却剩余时间（s）
        private Vector3 _insTargetPos;        // INS记忆的目标位置（闭眼时用）
        private Vector3 _insTargetVel;        // INS记忆的目标速度（闭眼时用）
        private readonly System.Collections.Generic.HashSet<int> _seenSourceIds = new System.Collections.Generic.HashSet<int>();  // 上一帧已见过的红外源ID
        private float _lockedTargetIr = -1f;  // 当前锁定目标的红外值（用于闭眼检测对比）
        private float _lockedTargetDist = -1f; // 当前锁定目标的距离（用于闭眼检测对比）
        public int BlindEvents;               // 诊断：触发过几次闭眼

        private float _t;
        private bool _done;
        private float _minRange = float.MaxValue;
        private Vector3 _ignitionPos;
        private bool _ignited;

        // ---- 地形避障 ----
        public AvoidParams Avoid = new AvoidParams();
        public IObstacleProbe Probe;          // 世界探针（Spawn 时挂共享实例）
        private Vector3 _avoidDir = Vector3.zero;
        private float _avoidThreat;
        private float _avoidTimer;
        private float _minAgl = float.MaxValue;
        private int _avoidCount;              // 触发避障的扫描次数（诊断）
        private bool _impacted;               // 撞地形引爆
        private static readonly IObstacleProbe SharedProbe = new UnityObstacleProbe();

        // ---- 静态注册表（CPU 优化）：导弹启用即登记、销毁自动移除，
        //      雷达 RWR 每 0.5s 直接读缓存，避免每 0.5s 全场景 FindObjectsOfType 扫描 ----
        private static readonly object _regLock = new object();
        private static readonly List<MissileController> _registry = new List<MissileController>();

        /// <summary>注册表快照（清理已销毁引用后返回副本）。</summary>
        public static List<MissileController> RegistrySnapshot()
        {
            lock (_regLock)
            {
                for (int i = _registry.Count - 1; i >= 0; i--)
                {
                    if (_registry[i] == null) _registry.RemoveAt(i);
                }
                return new List<MissileController>(_registry);
            }
        }

        private void OnEnable()
        {
            lock (_regLock)
            {
                if (!_registry.Contains(this)) _registry.Add(this);
            }
        }

        private void OnDisable()
        {
            lock (_regLock)
            {
                _registry.Remove(this);
            }
        }

        public static MissileController Spawn(GameObject model, Vector3 pos, Vector3 vel, TargetRef target,
                                             Transform trailDummy)
        {
            GameObject root = model;
            root.transform.position = pos;
            if (vel.sqrMagnitude > 0.01f)
                root.transform.rotation = PnGuidance.SafeLook(vel.normalized, Vector3.up);

            Rigidbody rb = root.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 10f;
            rb.isKinematic = true;   // 运动学刚体：与任何物体零物理交互（不会推挤/撞坏玩家飞机）
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.linearVelocity = vel;

            var mc = root.AddComponent<MissileController>();
            mc.enabled = false;          // 调用方配置完参数后再启用，避免第一帧用默认值
            mc.Body.Rb = rb;
            mc.Body.Vel = vel;
            mc.Target = target;
            mc._ignitionPos = pos;       // 记录发射点：离开 10m 后才点火推进
            mc._ignited = false;
            mc.Probe = SharedProbe;      // 地形避障探针（静态共享，内部缓冲非重入但在 FixedUpdate 里串行使用）

            root.AddComponent<AiEntityMarker>().Kind = "missile";
            SilentAi.Mark(root);
            TargetRegistry.Register(root, false, "Missile");   // 导弹不参与"被选为目标"
            AddTrail(root, trailDummy);
            return mc;
        }

        /// <summary>
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

        private static void AddTrail(GameObject root, Transform dummy)
        {
            try
            {
                if (root == null) { Machine.Core.Log.Info("[AAM] trail root null"); return; }
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh == null) { Machine.Core.Log.Info("[AAM] trail shader not found"); return; }

                // 尾部锚点：按模型实际包围盒算（+Z 是机头方向，所以尾端 = 局部 z 最小值）。
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

                // ----- 1) 尾焰：只有 0.4 秒的短羽状火焰（长条会变成"一条线"，所以刻意做短） -----
                GameObject coreGo = new GameObject("AAM_Flame");
                coreGo.transform.SetParent(root.transform, false);
                coreGo.transform.localPosition = anchor.localPosition;
                TrailRenderer core = coreGo.AddComponent<TrailRenderer>();
                // 保存尾焰引用（惯性巡航时关闭）
                MissileController mcSelf = root.GetComponentInParent<MissileController>();
                if (mcSelf != null) mcSelf._flameTrail = core;
                // 故意做得很短：导弹 400~600m/s，0.5 秒的拖尾就是 200~300 米的长条（看起来还是"一条线"）。
                // 0.12 秒只留喷口附近一小簇火焰，烟轨主体交给下面的世界空间烟团。
                core.time = 0.12f;
                core.widthCurve = new AnimationCurve(new Keyframe(0f, 0.42f * fx), new Keyframe(0.6f, 0.18f * fx), new Keyframe(1f, 0.02f * fx));
                core.minVertexDistance = 0.05f;
                core.material = new Material(sh);
                try { core.material.color = new Color(1f, 0.85f, 0.45f, 0.90f); }
                catch (Exception ce) { Machine.Core.Log.Info("[AAM] core color exception: " + ce.Message); }
                ApplySmokeTextureToTrail(core);
                BuildGradient(core, new Color[]{
                    new Color(1f, 1f, 0.95f, 0.92f),     // 喷口：白热
                    new Color(1f, 0.62f, 0.18f, 0.55f),  // 中：橙红
                    new Color(0.75f, 0.15f, 0.05f, 0f),  // 尾：透明暗红
                });
                core.autodestruct = false;
                core.emitting = true;

                // ----- 2) 烟轨主体：世界空间烟团，规则喷出 -> 变大 -> 渐变 -> 消失 -----
                BuildSmokeTrail(root, anchor, fx);

                Machine.Core.Log.Info("[AAM] trail attached to " + root.name);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] trail failed: " + e); }
        }

        /// <summary>把渐变颜色灌进 TrailRenderer（head 在 t=0，tail 在 t=1）。</summary>
        private static void BuildGradient(TrailRenderer tr, Color[] stops)
        {
            try
            {
                Gradient g = new Gradient();
                GradientColorKey[] ck = new GradientColorKey[stops.Length];
                for (int i = 0; i < stops.Length; i++)
                {
                    float t = (stops.Length == 1) ? 0f : (float)i / (stops.Length - 1);
                    ck[i] = new GradientColorKey(new Color(stops[i].r, stops[i].g, stops[i].b, 1f), t);
                }
                GradientAlphaKey[] ak = new GradientAlphaKey[stops.Length];
                for (int i = 0; i < stops.Length; i++)
                {
                    float t = (stops.Length == 1) ? 0f : (float)i / (stops.Length - 1);
                    ak[i] = new GradientAlphaKey(stops[i].a, t);
                }
                g.SetKeys(ck, ak);
                tr.colorGradient = g;
            }
            catch { }
        }

        // =================================================================
        // 尾迹烟雾（2026-09-12 重做）
        //   用户要求：烟雾要"规则地随航迹出现、变大、渐变与消失"，
        //   并且先看游戏里有没有现成材质 —— 有就直接引用。
        //   逆出来游戏本来就自带烟雾粒子：
        //     PlaneParticleTypes { Smoke, DirtTrail, Dirt }
        //     ParticleSpawner.particlePrefabs[(int)PlaneParticleTypes.Smoke] = 原版烟雾粒子预制体
        //     PartExploder.explosionParticlePrefab                         = 原版爆炸粒子预制体
        //   所以这里直接借它们的材质（贴图 + 着色器 + 渲染模式），只把行为改成导弹要的烟轨。
        //   注意：借来的材质是共享资源，一律用 sharedMaterial 只读引用，绝不修改。
        //
        //   三个关键点：
        //     ① SimulationSpace = World —— 烟团留在原地，导弹飞走了烟还在航迹上；
        //     ② 关掉"继承发射器速度" —— 否则 400m/s 的导弹会把刚喷出的烟一起拖着走，
        //        整条烟轨会贴死在弹体上（这就是之前看不到烟雾的原因之一）；
        //     ③ 出生 alpha=0 → 快速升到 0.85 → 逐渐变灰 → 归零，配合 size 曲线就是
        //        "出现 → 变大 → 渐变 → 消失"。
        // =================================================================
        // =================================================================
        // 尾迹：烟雾 + 尾焰（2026-09-12 重做）
        //   用户要求：烟雾要"规则地随航迹出现、变大、渐变与消失"，
        //   并且先看游戏里有没有现成材质 —— 有就直接引用。
        //   逆出来游戏本来就自带烟雾粒子：
        //     PlaneParticleTypes { Smoke, DirtTrail, Dirt }
        //     ParticleSpawner.particlePrefabs[(int)PlaneParticleTypes.Smoke] = 原版烟雾粒子
        //     PartExploder.explosionParticlePrefab                         = 原版爆炸粒子
        //   所以材质/贴图/渲染模式整套借来用（sharedMaterial 只读引用，绝不改）。
        //
        //   关键：**不要再挂长条 TrailRenderer**。导弹 400~600m/s，2~3 秒的 TrailRenderer
        //   就是一根上千米的纯色横条（上一版就是这样，看起来还是"一条线"）。
        //   尾迹的主体必须是**世界空间的烟团**：
        //     ① SimulationSpace = World —— 烟团留在原地，导弹飞走了烟还在航迹上；
        //     ② 关掉"继承发射器速度"—— 否则高速弹体把刚喷出的烟一起拖着走，烟轨会贴死在弹体上；
        //     ③ alpha 0 → 0.85 → 灰 → 0，size 0.18 → 1.25 倍 —— 就是
        //        "出现 → 变大 → 渐变 → 消失"。
        //   尾焰只留一段 0.4s 的短 TrailRenderer（喷口附近的火焰），不再是长条。
        // =================================================================
        private static ParticleSystemRenderer _cachedSmokeSrc;   // 原版烟雾渲染器（只读借用）
        private static Texture _cachedSmokeTex;
        private static float _smokeNextProbe;
        private static bool _smokeMissLogged;
        private static Texture2D _fallbackPuff;

        private const BindingFlags BF_SMOKE = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>取渲染器上第一个带材质的粒子渲染器。</summary>
        private static ParticleSystemRenderer FirstParticleRenderer(GameObject go)
        {
            if (go == null) return null;
            try
            {
                ParticleSystemRenderer[] rs = go.GetComponentsInChildren<ParticleSystemRenderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null) continue;
                    if (rs[i].sharedMaterial == null) continue;
                    return rs[i];
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 找游戏自带的烟雾粒子渲染器，依次尝试：
        ///   ① ParticleSpawner.particlePrefabs[PlaneParticleTypes.Smoke]（原版烟雾）
        ///   ② 玩家飞机身上的 PlaneParticle（受损烟雾 / 轮胎尘）
        ///   ③ PartExploder.explosionParticlePrefab（爆炸粒子）
        /// 注意：主菜单阶段这些对象都还没生成，所以**失败不做负缓存**，隔几秒重试，
        /// 等真的进了飞行场景自然就拿到了（上一版就是在这里栽的：开机探测失败被永久缓存）。
        /// </summary>
        internal static ParticleSystemRenderer TryGetGameSmokeRenderer()
        {
            if (_cachedSmokeSrc != null) return _cachedSmokeSrc;
            if (Time.unscaledTime < _smokeNextProbe) return null;
            _smokeNextProbe = Time.unscaledTime + 3f;

            // ① 原版粒子池
            try
            {
                ParticleSpawner sp = null;
                try { sp = ParticleSpawner.Instance; } catch { }
                if (sp == null)
                {
                    UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(ParticleSpawner));
                    if (all != null && all.Length > 0) sp = all[0] as ParticleSpawner;
                }
                if (sp != null)
                {
                    FieldInfo f = typeof(ParticleSpawner).GetField("particlePrefabs", BF_SMOKE);
                    GameObject[] arr = (f != null) ? (f.GetValue(sp) as GameObject[]) : null;
                    if (arr != null && arr.Length > 0)
                    {
                        int idx = (int)PlaneParticleTypes.Smoke;
                        if (idx < 0 || idx >= arr.Length) idx = 0;
                        ParticleSystemRenderer r = FirstParticleRenderer(arr[idx]);
                        if (r != null) { BindSmokeSource(r, "particlePrefabs[Smoke]=" + arr[idx].name); return r; }
                    }
                }
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] smoke lookup(particlePrefabs) failed: " + e.Message); }

            // ② 玩家飞机身上的 PlaneParticle
            try
            {
                PlaneContainer pc = null;
                try { pc = PlaneContainer.Instance; } catch { }
                if (pc != null)
                {
                    PlaneParticle[] pps = pc.GetComponentsInChildren<PlaneParticle>(true);
                    for (int i = 0; i < pps.Length; i++)
                    {
                        if (pps[i] == null) continue;
                        ParticleSystemRenderer r = FirstParticleRenderer(pps[i].gameObject);
                        if (r != null) { BindSmokeSource(r, "plane " + pps[i].GetType().Name); return r; }
                    }
                }
            }
            catch { }

            // ③ 爆炸粒子
            try
            {
                PartExploder pe = null;
                try { pe = PartExploder.Instance; } catch { }
                if (pe != null)
                {
                    FieldInfo f = typeof(PartExploder).GetField("explosionParticlePrefab", BF_SMOKE);
                    GameObject go = (f != null) ? (f.GetValue(pe) as GameObject) : null;
                    ParticleSystemRenderer r = FirstParticleRenderer(go);
                    if (r != null) { BindSmokeSource(r, "explosionParticlePrefab=" + go.name); return r; }
                }
            }
            catch { }

            if (!_smokeMissLogged)
            {
                _smokeMissLogged = true;
                Machine.Core.Log.Info("[AAM] smoke material: none in scene yet (menu?) - using generated puff for now");
            }
            return null;
        }

        private static void BindSmokeSource(ParticleSystemRenderer r, string src)
        {
            _cachedSmokeSrc = r;
            _cachedSmokeTex = (r.sharedMaterial != null) ? r.sharedMaterial.mainTexture : null;
            Machine.Core.Log.Info("[AAM] smoke material borrowed: '" + r.sharedMaterial.name
                + "' shader='" + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?")
                + "' tex='" + (_cachedSmokeTex != null ? _cachedSmokeTex.name : "none")
                + "' mode=" + r.renderMode + " | from " + src);
        }

        /// <summary>原版烟雾贴图（没找到就用程序生成的柔和烟团）。</summary>
        private static Texture SmokeTexture()
        {
            TryGetGameSmokeRenderer();
            return (_cachedSmokeTex != null) ? _cachedSmokeTex : (Texture)MakeFallbackPuffTexture();
        }

        /// <summary>整套照搬原版烟雾渲染器的材质/渲染模式；拿不到就用生成的烟团贴图兜底。</summary>
        private static void BindSmokeRenderer(ParticleSystemRenderer psr, string tag)
        {
            if (psr == null) return;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psr.receiveShadows = false;
            psr.sortingFudge = 2f;
            psr.enabled = true;
            // 一律用最简单的公告板：原版那个材质是"火焰"，它的对齐/渲染模式未必适合烟团，
            // 照抄 alignment=Velocity 之类会让速度接近 0 的烟团退化成看不见的薄片。
            psr.renderMode = ParticleSystemRenderMode.Billboard;
            psr.alignment = ParticleSystemRenderSpace.View;

            // ① 原版烟雾贴图 + 一定渲染得出来的 Sprite 着色器
            //    （导弹尾焰用的就是这个着色器，实机确认可见；贴图用原版烟雾的）
            Shader sh0 = Shader.Find("Sprites/Default");
            if (sh0 == null) sh0 = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh0 != null)
            {
                Material m0 = new Material(sh0);
                m0.mainTexture = SmokeTexture();
                psr.sharedMaterial = m0;
                Machine.Core.Log.Info("[AAM] smoke material [" + tag + "]: " + sh0.name
                    + " tex=" + (m0.mainTexture != null ? m0.mainTexture.name : "null")
                    + " src=" + (TryGetGameSmokeRenderer() != null ? "game" : "generated"));
                return;
            }
            Machine.Core.Log.Info("[AAM] smoke material [" + tag + "]: NO SHADER FOUND");
        }

        /// <summary>给 TrailRenderer 贴原版烟雾贴图，让尾焰是柔和羽状而不是纯色横条。</summary>
        private static void ApplySmokeTextureToTrail(TrailRenderer tr)
        {
            if (tr == null || tr.material == null) return;
            try
            {
                tr.material.mainTexture = SmokeTexture();
                tr.textureMode = LineTextureMode.Stretch;
                tr.numCapVertices = 6;
                tr.numCornerVertices = 2;
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] trail texture failed: " + e.Message); }
        }

        /// <summary>兜底烟团贴图：径向衰减 + 低频噪声，避免一根死板的纯色带。</summary>
        private static Texture2D MakeFallbackPuffTexture()
        {
            if (_fallbackPuff != null) return _fallbackPuff;
            const int N = 64;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c;
                    float dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);                                  // smoothstep 软边
                    a *= 0.55f + 0.45f * Mathf.PerlinNoise(x * 0.17f, y * 0.17f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
                }
            }
            t.Apply();
            _fallbackPuff = t;
            return t;
        }

        private static void BuildSmokeTrail(GameObject root, Transform anchor, float fx)
        {
            try
            {
                if (root == null || anchor == null) { Machine.Core.Log.Info("[AAM] smoke: root/anchor null"); return; }

                GameObject go = new GameObject("AAM_SmokeTrail");
                go.transform.SetParent(anchor, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                ParticleSystem ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                // 保存烟轨引用（惯性巡航时关闭）
                MissileController mcSelf = root.GetComponentInParent<MissileController>();
                if (mcSelf != null) mcSelf._smokeTrail = ps;

                ParticleSystem.MainModule main = ps.main;
                main.duration = 6f;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(6.0f, 10.4f);  // 尾迹寿命 x2（用户要求维持更久）
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 2.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(3.4f * fx, 5.6f * fx);   // 随弹体长度缩放
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28318f);
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(1.00f, 0.995f, 0.99f, 0.96f),
                    new Color(0.93f, 0.93f, 0.94f, 0.86f));
                main.simulationSpace = ParticleSystemSimulationSpace.World;        // ① 烟留在航迹上
                main.scalingMode = ParticleSystemScalingMode.Local;                // 不受模型缩放干扰
                main.gravityModifier = -0.010f;                                    // 极轻微上浮
                main.maxParticles = 2400;   // 寿命翻倍后同时存活粒子也翻倍，不抬上限会提前淘汰旧粒子、把尾迹截断
                // 注意：曾经在这里设过 emitterVelocityMode = Custom，但 Custom 模式下
                // ParticleSystem.emitterVelocity 没人赋值，粒子可能拿到非法速度而不显示。
                // 现在只靠下面的 InheritVelocity 曲线归零来"不继承发射器速度"，更安全。

                // ② 明确不继承发射器速度（否则烟会被高速弹体拖着走）
                ParticleSystem.InheritVelocityModule inh = ps.inheritVelocity;
                inh.enabled = true;
                inh.mode = ParticleSystemInheritVelocityMode.Initial;
                inh.curve = new ParticleSystem.MinMaxCurve(0f);

                // ① 规则喷出（导弹 400~600m/s 时靠高发射率把烟团连成一条轨迹）
                ParticleSystem.EmissionModule em = ps.emission;
                em.enabled = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(150f);

                ParticleSystem.ShapeModule shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 18f;
                shape.radius = 0.12f * fx;
                shape.radiusThickness = 1f;
                shape.rotation = new Vector3(0f, 180f, 0f);                        // 朝机尾方向喷

                // 变大
                ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0.00f, 0.20f),
                    new Keyframe(0.16f, 0.55f),
                    new Keyframe(0.52f, 0.95f),
                    new Keyframe(1.00f, 1.30f)));

                // 渐变 + 消失
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient g = new Gradient();
                g.SetKeys(
                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 1.00f, 0.99f), 0.00f),
                        new GradientColorKey(new Color(0.98f, 0.98f, 0.98f), 0.14f),
                        new GradientColorKey(new Color(0.94f, 0.94f, 0.95f), 0.48f),
                        new GradientColorKey(new Color(0.87f, 0.87f, 0.89f), 0.80f),
                        new GradientColorKey(new Color(0.79f, 0.79f, 0.82f), 1.00f)},
                    new GradientAlphaKey[]{
                        new GradientAlphaKey(0.00f, 0.00f),   // 出生透明 -> "出现"
                        new GradientAlphaKey(0.85f, 0.07f),
                        new GradientAlphaKey(0.58f, 0.32f),
                        new GradientAlphaKey(0.24f, 0.70f),
                        new GradientAlphaKey(0.00f, 1.00f)});  // 耗尽 -> "消失"
                col.color = new ParticleSystem.MinMaxGradient(g);

                ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-0.55f, 0.55f);             // 慢速翻滚，有体积感

                ParticleSystem.NoiseModule nz = ps.noise;
                nz.enabled = true;
                nz.strength = new ParticleSystem.MinMaxCurve(0.22f, 0.55f);
                nz.frequency = 0.30f;
                nz.scrollSpeed = new ParticleSystem.MinMaxCurve(0.25f);

                BindSmokeRenderer(go.GetComponent<ParticleSystemRenderer>(), "missile");

                SmokeDetacher sd = go.AddComponent<SmokeDetacher>();
                sd.Ps = ps;
                ps.Play(true);
                Machine.Core.Log.Info("[AAM] smoke trail built on " + root.name);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] smoke trail failed: " + e.Message); }
        }

        /// <summary>
        /// 红外导引头：全向锁定 + 常态缩圈 + 闭眼抗干扰 + 跟踪速率限制。
        ///   全向锁定 —— 不挑进入角，视场内任何红外源都能咬（迎头 / 尾后 / 侧向一视同仁），
        ///                取舍只看"在不在门限里"和"红外有多强"。
        ///   常态缩圈 —— 锁定目标后就开始把跟踪门从宽圈收到窄圈（不是看到干扰弹才缩），
        ///                提高角分辨率，减少干扰弹进入门限的概率。
        ///   闭眼抗干扰 —— 检测到突然出现的强红外热源（如热诱弹）时触发闭眼，
        ///                  闭眼期间依靠INS惯性制导（记忆的目标运动轨迹）继续飞行，
        ///                  短暂关闭后导引头重新开启并尝试再次捕获目标，如此循环。
        ///   跟踪速率 —— 导引头跟踪回路能够跟随目标视线转动的最大角速度（度/秒），
        ///                限制了导弹跟踪高机动目标的能力。
        /// 返回 true 时 aim = 导引头真正咬的点（门限内红外的加权质心）。
        /// decoyDist / decoyShare 供近炸引信判断"是不是咬在诱饵上"。
        /// </summary>
        private bool SeekerAim(out Vector3 aim, out Vector3 vel, out float decoyDist, out float decoyShare)
        {
            aim = Vector3.zero;
            vel = Vector3.zero;
            decoyDist = float.MaxValue;
            decoyShare = 0f;
            float dt = Time.fixedDeltaTime;
            try
            {
                if (!IrSignature.Enabled) return false;   // 红外总开关关掉 -> 导引头没有可用的热源

                // ========== 闭眼抗干扰状态机 ==========
                if (_blindCd > 0f) _blindCd -= dt;
                if (_blind)
                {
                    _blindT -= dt;
                    // INS制导：基于记忆的目标位置和速度预测当前目标位置
                    float elapsed = BlindDuration - _blindT;  // 闭眼已过时间
                    aim = _insTargetPos + _insTargetVel * elapsed;
                    vel = _insTargetVel;
                    if (_blindT <= 0f)
                    {
                        // 闭眼结束：重新开启导引头，清除已见源列表（避免立即再次触发闭眼）
                        _blind = false;
                        _seenSourceIds.Clear();
                        _blindCd = BlindCooldown;
                    }
                    return true;
                }

                IrRegistry.Fill(_irBuf);
                if (_irBuf.Count == 0) return false;

                // 导引头指向：优先沿用上一帧的咬合点（"正在盯哪儿"的记忆），没有就用弹体速度方向
                Vector3 boresight = _seekHas ? (_seekAim - Body.Pos).normalized
                                             : (Body.Vel.sqrMagnitude > 1f ? Body.Vel.normalized : transform.forward);
                if (boresight.sqrMagnitude < 0.5f) boresight = transform.forward;

                // ========== 闭眼触发检测：视野内突然出现的、与目标距离相近、红外远大于目标的热源 ==========
                bool triggerBlind = false;
                if (BlindEnabled && _blindCd <= 0f && _seekHas && _lockedTargetIr > 0f && _lockedTargetDist > 0f)
                {
                    float detectCos = Mathf.Cos(Mathf.Min(SeekerFov, GateWide) * Mathf.Deg2Rad);
                    for (int i = 0; i < _irBuf.Count; i++)
                    {
                        var s = _irBuf[i];
                        if (s == null || s.Actual <= 1f) continue;
                        int sid = s.GetInstanceID();
                        // 只检测"新出现"的源（上一帧没见过的）
                        if (_seenSourceIds.Contains(sid)) continue;
                        Vector3 to = s.transform.position - Body.Pos;
                        float d = to.magnitude;
                        if (d > SeekerRange || d < 1f) continue;
                        if (Vector3.Dot(to / d, boresight) < detectCos) continue;
                        // 条件1：与目标距离相近（相差不超过容差比例）
                        float distDiff = Mathf.Abs(d - _lockedTargetDist) / _lockedTargetDist;
                        if (distDiff > BlindDistTolerance) continue;
                        // 条件2：红外值远大于目标（高出阈值以上）
                        if (s.Actual < _lockedTargetIr + BlindIrDiffThreshold) continue;
                        // 两个条件都满足 → 触发闭眼
                        triggerBlind = true;
                        break;
                    }
                }

                if (triggerBlind)
                {
                    // 触发闭眼：记录当前目标的INS状态，进入闭眼
                    _blind = true;
                    _blindT = BlindDuration;
                    _insTargetPos = _seekAim;  // 记忆当前目标位置
                    _insTargetVel = Vector3.zero;  // 记忆目标速度（简化：假设匀速）
                    BlindEvents++;
                    // 闭眼第一帧就用INS制导
                    aim = _insTargetPos;
                    vel = _insTargetVel;
                    return true;
                }

                // 更新已见源列表
                _seenSourceIds.Clear();
                for (int i = 0; i < _irBuf.Count; i++)
                {
                    if (_irBuf[i] != null) _seenSourceIds.Add(_irBuf[i].GetInstanceID());
                }

                // ========== 常态缩圈：锁定后就开始缩圈（不是看到干扰弹才缩）==========
                if (_seekHas && !_gateShrinking)
                {
                    _gateShrinking = true;
                    _gateShrinkT = 0f;
                }
                if (_gateShrinking)
                {
                    _gateShrinkT += dt;
                }
                float gateU = Mathf.Clamp01(_gateShrinkT / Mathf.Max(0.01f, GateShrink));
                float gateDeg = Mathf.Lerp(GateWide, GateNarrow, gateU);
                gateDeg = Mathf.Min(gateDeg, SeekerFov);
                float gateCos = Mathf.Cos(gateDeg * Mathf.Deg2Rad);

                // 宽圈扫描：视场里有没有诱饵（用于IRCCM运动学判别和诱饵降权）
                bool decoyInView = false;
                float wideCos = Mathf.Cos(Mathf.Min(SeekerFov, GateWide) * Mathf.Deg2Rad);
                for (int i = 0; i < _irBuf.Count; i++)
                {
                    var s = _irBuf[i];
                    if (s == null || !s.IsDecoy || s.Actual <= 1f) continue;
                    Vector3 to = s.transform.position - Body.Pos;
                    float d = to.magnitude;
                    if (d > SeekerRange || d < 1f) continue;
                    if (Vector3.Dot(to / d, boresight) < wideCos) continue;
                    decoyInView = true;
                    break;
                }

                // IRCCM状态：看到诱饵后启动运动学判别和诱饵降权（缩圈已经是常态了）
                _decoySeenT = decoyInView ? (_decoySeenT + dt) : 0f;
                bool ircm = IrcmEnabled && _decoySeenT >= IrcmReact;
                if (ircm && _ircmT <= 0f) IrcmEvents++;
                _ircmT = ircm ? (_ircmT + dt) : 0f;
                IrcmActive = ircm;

                // 先找门限内最亮的**真目标**，作为 IRCCM 运动学判别的基准
                float realBest = -1f, realBestSpeed = 0f;
                for (int i = 0; i < _irBuf.Count; i++)
                {
                    var s = _irBuf[i];
                    if (s == null || s.IsDecoy || s.Actual <= 1f) continue;
                    Vector3 to = s.transform.position - Body.Pos;
                    float d = to.magnitude;
                    if (d > SeekerRange || d < 1f) continue;
                    if (Vector3.Dot(to / d, boresight) < gateCos) continue;
                    if (s.Actual > realBest)
                    {
                        realBest = s.Actual;
                        realBestSpeed = SourceVelocity(s).magnitude;
                    }
                }

                // 咬门限内最亮的那个热点（取argmax，不是加权质心）
                IrSignature pick = null;
                float pickW = -1f, pickDist = float.MaxValue;
                Vector3 pickPos = Vector3.zero, pickVel = Vector3.zero;
                int decoysInGate = 0;
                for (int i = 0; i < _irBuf.Count; i++)
                {
                    var s = _irBuf[i];
                    if (s == null || s.Actual <= 1f) continue;
                    Vector3 to = s.transform.position - Body.Pos;
                    float d = to.magnitude;
                    if (d > SeekerRange || d < 1f) continue;
                    if (Vector3.Dot(to / d, boresight) < gateCos) continue;   // 角门限外 -> 剔除

                    float w = s.Actual;
                    if (s.IsDecoy)
                    {
                        // IRCCM 运动学判别：热诱弹抛出后急剧减速，真飞机做不出这种减速
                        if (ircm && realBest > 0f
                            && SourceVelocity(s).magnitude < realBestSpeed * IrcmSpeedFrac) continue;
                        decoysInGate++;
                        if (d < decoyDist) decoyDist = d;
                        if (ircm) w *= IrcmDecoyPenalty;
                    }
                    if (w > pickW)
                    {
                        pickW = w; pick = s; pickPos = s.transform.position;
                        pickVel = SourceVelocity(s); pickDist = d;
                    }
                }

                bool decoyInGate = decoysInGate > 0;
                if (ircm && _decoyWasIn && !decoyInGate) DecoyRejects++;
                _decoyWasIn = decoyInGate;

                if (pick == null)
                {
                    // 门限里空了：保持上一帧指向，别瞎甩
                    if (!_seekHas) return false;
                    aim = _seekAim;
                    vel = Vector3.zero;
                    return true;
                }

                // ========== 跟踪速率限制：导引头转动角速度不超过TrackRate ==========
                Vector3 desiredAim = pickPos;
                if (_seekHas)
                {
                    Vector3 fromDir = (_seekAim - Body.Pos).normalized;
                    Vector3 toDir = (desiredAim - Body.Pos).normalized;
                    float dot = Mathf.Clamp(Vector3.Dot(fromDir, toDir), -1f, 1f);
                    float angleDiff = Mathf.Acos(dot) * Mathf.Rad2Deg;  // 角度差（度）
                    float maxAngleThisFrame = TrackRate * dt;  // 本帧最大转动角度
                    if (angleDiff > maxAngleThisFrame && angleDiff > 0.01f)
                    {
                        // 超过跟踪速率：沿fromDir到toDir的方向旋转maxAngleThisFrame度
                        float t = maxAngleThisFrame / angleDiff;
                        Vector3 limitedDir = Vector3.Slerp(fromDir, toDir, t).normalized;
                        // 保持目标距离不变，只改变方向
                        float dist = (desiredAim - Body.Pos).magnitude;
                        desiredAim = Body.Pos + limitedDir * dist;
                    }
                }

                decoyShare = pick.IsDecoy ? 1f : 0f;
                aim = desiredAim;
                vel = pickVel;
                _seekAim = aim;
                _seekHas = true;
                // 更新锁定目标的红外值和距离（用于闭眼检测对比）
                if (!pick.IsDecoy)
                {
                    _lockedTargetIr = pick.Actual;
                    _lockedTargetDist = pickDist;
                }
                return true;
            }
            catch { return false; }
        }

        private static Vector3 SourceVelocity(IrSignature s)
        {
            try
            {
                var fl = s.GetComponent<IrFlare>();
                if (fl != null) return fl.Vel;
            }
            catch { }
            return s.Vel;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _t += dt;

            Vector3 aCmd = Vector3.zero;
            float range = float.MaxValue;
            float tgtRange = float.MaxValue;    // 目标距离：避障"目标优先"的边界（打不到目标之后的地形与我无关）
            Vector3 tgtPos = Vector3.zero;      // 目标位置：山脊剖面按"我->目标"直线外推
            if (UseTarget && Target != null && Target.Valid)
            {
                Vector3 tp = Target.Position;
                range = Vector3.Distance(Body.Pos, tp);
                tgtRange = range;
                tgtPos = tp;
                if (range < _minRange) _minRange = range;
                if (range < Proximity) { Detonate(true, range); return; }

                // 红外导引头：把"直接追目标点"换成"追视场内的红外加权质心"。
                // 全向锁定 + 缩圈 IRCCM；真被诱饵带偏时，导弹会实实在在地追着诱饵跑。
                Vector3 tv = Target.Velocity;
                if (IrSeeker && !InterceptOnly)
                {
                    Vector3 aim, aimVel;
                    float decoyDist, decoyShare;
                    if (SeekerAim(out aim, out aimVel, out decoyDist, out decoyShare))
                    {
                        tp = aim;
                        tv = aimVel;
                        // 近炸引信咬在诱饵上：在诱饵边上炸开（算脱靶），给"热诱弹奏效"的反馈。
                        // 必须要求诱饵在质心里占主导，否则贴着飞机抛饵会把本该命中的弹白白引爆。
                        if (decoyShare > 0.6f && decoyDist <= Proximity)
                        {
                            Detonate(false, decoyDist);
                            return;
                        }
                    }
                }
                aCmd = PnGuidance.Command(Body.Pos, Body.Vel, tp, tv, NavConstant, MaxG * 9.81f);
            }

            // 点火延迟：离开发射点 IgnitionDist 距离前不推进（只靠初始速度滑行），
            // 确保导弹先安全脱离飞机再加速追击目标
            float thrust = 0f;
            if (!_ignited)
            {
                if ((Body.Pos - _ignitionPos).sqrMagnitude >= IgnitionDist * IgnitionDist)
                {
                    _ignited = true;
                    Machine.Core.Log.Info("[AAM] missile ignited after " + Vector3.Distance(Body.Pos, _ignitionPos).ToString("F1") + "m"
                        + " | avoid=" + (Avoid.Enabled ? ("on look=" + Avoid.LookTime + "s clear=" + Avoid.Clearance + "m") : "off"));
                }
            }

            // ---- 加速阶段 vs 惯性巡航阶段 ----
            // Lifetime = 加速时间（助推+续航），结束后进入惯性巡航：
            //   - 推力=0，只靠惯性飞行
            //   - 阻力由 CoastDrag 决定（100=PL-15最大速度下直线巡航10秒速度归零）
            //   - 关闭尾焰与拖尾特效，但雷达仍可识别
            //   - 速度过低时自毁
            if (_ignited && !_coasting && _t >= Lifetime)
            {
                _coasting = true;
                _coastT = 0f;
                // 关闭尾焰特效
                if (_flameTrail != null)
                {
                    try { _flameTrail.emitting = false; } catch { }
                }
                // 关闭烟轨特效
                if (_smokeTrail != null)
                {
                    try
                    {
                        var em = _smokeTrail.emission;
                        em.enabled = false;
                        _smokeTrail.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    } catch { }
                }
                Machine.Core.Log.Info("[AAM] missile entered coasting: " + SpecName
                    + " speed=" + Mathf.RoundToInt(Body.Speed / 0.5144f) + "kt"
                    + " after " + _t.ToString("F1") + "s boost");
            }
            if (_coasting) _coastT += dt;

            // 阻力：加速阶段用基础DragK，惯性巡航阶段用CoastDrag换算的阻力
            // CoastDrag=100 对应实际dragK=0.025（PL-15最大速度下直线巡航10秒速度归零）
            float effectiveDragK = _coasting
                ? (CoastDrag / 100f) * CoastBaseDragK
                : DragK;

            // 推进：加速阶段使用加速度参数，惯性巡航阶段推力=0
            // 单位约定：Body.Speed 是 m/s，MaxSpeed 是节；比较时统一转 m/s
            // 加速度转换：1马赫/秒 = 660节/秒 = 660 × 0.5144 m/s² ≈ 339.5 m/s²
            float accelMs2 = Acceleration * 339.5f;
            // 当前阻力加速度（Body.Speed 已经是 m/s，直接算二次阻力）
            float currentDrag = effectiveDragK * Body.Speed * Body.Speed;
            // 最大速度（节 -> m/s）
            float maxSpeedMs = MaxSpeed * 0.5144f;

            if (_ignited && !_coasting)
            {
                if (Body.Speed < maxSpeedMs * 0.99f)
                {
                    // 未达到最大速度：推力 = 目标加速度 + 阻力
                    thrust = accelMs2 + currentDrag;
                }
                else
                {
                    // 达到最大速度：推力 = 阻力（保持速度）
                    thrust = currentDrag;
                }
            }
            else
            {
                thrust = 0f;
            }

            // ---- 地形避障 ----
            // 探针按 avoidInterval 节流扫描（默认 20Hz），两次扫描之间沿用上一次的规避方向；
            // 撞地兜底每帧都做（高速下漏一帧就是穿山）。
            float avoidThreat = 0f;
            if (Avoid.Enabled && _ignited && _t > Avoid.Warmup && Probe != null)
            {
                _avoidTimer -= dt;
                if (_avoidTimer <= 0f)
                {
                    // 自适应间隔：高速时探针成本是低速的几十倍（视距按 v² 涨），
                    // 固定 20Hz 会把射线全发在"根本来不及撞的距离"上。按速度放宽，
                    // 但保证两拍之间前进量不超过视距的 SpeedStepFrac（山脊不会从射线缝里漏掉）。
                    _avoidTimer = TerrainAvoid.ScanInterval(Body.Speed, MaxG * 9.81f, Avoid, _avoidThreat);
                    float agl;
                    float newThreat;
                    _avoidDir = TerrainAvoid.Compute(Probe, Body.Pos, Body.Vel, MaxG * 9.81f, Avoid,
                                                     _avoidThreat, tgtPos, tgtRange, out newThreat, out agl);
                    // 威胁记忆：导弹一开始转开，探针距离立刻变远、威胁就掉下来，规避力跟着消失，
                    // 结果"转到一半停住"又拐回去。按 1.6/s 衰减，给机动留出把动作做完的时间。
                    _avoidThreat = Mathf.Max(newThreat, _avoidThreat - 0.08f);
                    // minAgl 统计跳过离机/爬升的第一秒：那时候还贴着跑道，"离地 5m"没有任何诊断意义
                    if (agl >= 0f && _t > Avoid.Warmup + 1f && agl < _minAgl) _minAgl = agl;
                    if (_avoidThreat > 0.25f) _avoidCount++;
                }
                avoidThreat = _avoidThreat;

                if (Avoid.ImpactDetonate && Body.Speed > 30f)
                {
                    // 本帧位移 + 弹头长度：这一枪命中 = 弹头马上会扎进地形。
                    // 用 Blocked（单次 Raycast，不过滤动态实体）—— 每帧都要跑的判定，
                    // 原来走 Cast 的全量收集 + 逐条过滤是纯浪费。
                    float stepLen = Body.Speed * dt + Avoid.Nose;
                    // 撞地判定也不许越过目标：目标在 30m 外而本帧要走 25m 时，打到的那块地
                    // 不是"撞山"而是"马上会命中" -> 交给近炸引信，别在这里判死。
                    float rayLen = (tgtRange < float.MaxValue)
                                   ? Mathf.Min(stepLen, Mathf.Max(2f, tgtRange - 1f)) : stepLen;
                    if (Probe.Blocked(Body.Pos, Body.Vel.normalized, rayLen))
                    {
                        TerrainImpact(stepLen);   // 单次 Raycast 拿不到精确距离，用本帧前探长度作上界
                        return;
                    }
                }
            }

            float aMax = MaxG * 9.81f;
            if (avoidThreat > 0.01f)
            {
                // 规避时临时放宽过载上限并收油门：R = v^2/a，高速下不放这一点就躲不开
                aMax *= 1f + (Avoid.ExtraG - 1f) * avoidThreat;
                if (_avoidDir.sqrMagnitude > 1e-6f)
                {
                    aCmd = aCmd * (1f - Avoid.PnCut * avoidThreat)
                           + _avoidDir * (aMax * Avoid.Strength * avoidThreat);
                }
                thrust *= 1f - Avoid.Brake * avoidThreat;
            }
            aCmd = Vector3.ClampMagnitude(aCmd, aMax);

            Body.Step(dt, aCmd, thrust, effectiveDragK * (1f + 2f * Avoid.Brake * avoidThreat), MaxSpeed * 0.5144f);
            Body.FaceVelocity(dt, TurnRateDeg);

            // 自毁条件：
            // - 遁地/低于SelfDestructY
            // - 加速阶段结束（不再用Lifetime自毁，改为进入惯性巡航）
            // - 惯性巡航阶段速度过低（<8节）
            if (Body.Pos.y < SelfDestructY) { Detonate(false, range); return; }
            if (Body.Pos.y < -10f) { Detonate(false, range); return; }   // 遁地自毁：钻到海面/地面以下即失效
            if (_coasting && Body.Speed < 8f) {
                Machine.Core.Log.Info("[AAM] missile coasting speed depleted: " + SpecName
                    + " coastT=" + _coastT.ToString("F1") + "s"
                    + " finalSpeed=" + Mathf.RoundToInt(Body.Speed));
                Detonate(false, range); return;
            }
            if (!_coasting && Body.Speed < 8f && _t > 4f) { Detonate(false, range); return; }
        }

        public bool Finished { get { return _done; } }
        public float MinRange { get { return _minRange; } }
        public float AliveTime { get { return _t; } }
        /// <summary>全程最低离地高度（探针没数据时为 -1）。</summary>
        public float MinAgl { get { return _minAgl; } }
        /// <summary>触发避障的扫描次数。</summary>
        public int AvoidEvents { get { return _avoidCount; } }
        /// <summary>当前威胁度 0..1。</summary>
        public float AvoidThreat { get { return _avoidThreat; } }
        public bool TerrainImpacted { get { return _impacted; } }

        /// <summary>
        /// 撞地形（山体/建筑）：按命中引爆，不再"穿山消失"。
        /// 运动学刚体与静态碰撞体之间没有物理交互，所以这一枪必须自己打。
        /// </summary>
        private void TerrainImpact(float dist)
        {
            if (_done) return;
            _impacted = true;
            Machine.Core.Log.Info("[AAM] MISSILE TERRAIN IMPACT: " + SpecName + " fired by " + ShooterCall
                + " hit terrain/building " + Mathf.RoundToInt(dist) + "m ahead at alt="
                + Mathf.RoundToInt(Body.Pos.y) + " after " + _t.ToString("F1") + "s minAgl="
                + (_minAgl < 0f || float.IsInfinity(_minAgl) ? "-1" : Mathf.RoundToInt(_minAgl).ToString())
                + " avoid=" + _avoidCount);
            Detonate(false, dist);
        }

        /// <summary>被拦截弹命中：本弹立即失效（只炸自己，不伤及任何飞机）。</summary>
        public void Intercept()
        {
            if (_done) return;
            _done = true;
            InterceptCount++;
            SilentAi.Unmark(gameObject);
            TargetRegistry.Unregister(gameObject);
            Machine.Core.Log.Info("[AAM] INTERCEPTED: " + SpecName + " fired by " + ShooterCall
                + " destroyed in flight at " + Mathf.RoundToInt(Body.Pos.y) + "m after "
                + _t.ToString("F1") + "s");
            StartCoroutine(Boom());
        }

        private void Detonate(bool hit, float range)
        {
            if (_done) return;
            _done = true;
            SilentAi.Unmark(gameObject);
            TargetRegistry.Unregister(gameObject);
            Machine.Core.Log.Info("[AAM] MISSILE " + SpecName + " " + (hit ? "HIT target at " : "expired/broke at ")
                      + Mathf.RoundToInt(range) + "m after " + _t.ToString("F1") + "s minRange="
                      + Mathf.RoundToInt(_minRange) + "m speed=" + Mathf.RoundToInt(Body.Speed / 0.5144f) + "kt"
                      + " minAgl=" + (_minAgl < 0f || float.IsInfinity(_minAgl) ? "-1" : Mathf.RoundToInt(_minAgl).ToString())
                      + " avoid=" + _avoidCount
                      + " shooter=" + (string.IsNullOrEmpty(ShooterCall) ? "unknown" : ShooterCall));
            if (hit) StrikeTarget();
            else
            {
                var sys = AamSystem.Live;
                if (sys != null) sys.PostMissileResult("MISS", 5);   // 脱靶反馈到战斗部
            }
            StartCoroutine(Boom());
        }

        /// <summary>
        /// 命中处理：AI 飞机整机击落；玩家飞机同样整机击落（游戏原版爆炸）并把阵亡上报计分板。
        /// 玩家是否吃伤害由 aiDamagePlayer 开关控制。
        /// </summary>
        private void StrikeTarget()
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                TargetRef tr = Target;
                if (tr == null || tr.T == null) return;

                // 拦截弹（MSDM）：只对"导弹"起作用 —— 命中飞机一律不造成伤害（用户要求）
                if (InterceptOnly)
                {
                    MissileController victim = null;
                    try { victim = tr.T.GetComponentInParent<MissileController>(); } catch { }
                    if (victim != null && victim != this)
                    {
                        victim.Intercept();
                        AamSystem sys0 = AamSystem.Live;
                        if (sys0 != null) sys0.PostMissileResult("INTERCEPT", 8);
                    }
                    else Machine.Core.Log.Info("[AAM] interceptor detonated but target was not a missile");
                    return;
                }

                PlaneContainer pc = PlaneContainer.Instance;
                if (pc == null) return;
                if (tr.T != pc.transform && !tr.T.IsChildOf(pc.transform))
                {
                    // 命中的不是玩家 —— 如果是一架 AI 飞机，同样炸掉它一个部件
                    try
                    {
                        if (tr.T.GetComponentInParent<AiEntityMarker>() != null)
                        {
                            // 友军过滤：同阵营的 AI 不攻击（阵营系统启用时）
                            if (!IsFriendlyTarget(tr.T)) StrikeAiAircraft(tr.T);
                            else Machine.Core.Log.Info("[AAM] missile ignored friendly target");
                        }
                    }
                    catch { }
                    return;
                }

                AamSystem sys = AamSystem.Live;
                if (sys != null && !sys.AiDamagePlayerEnabled)
                {
                    Machine.Core.Log.Info("[AAM] player hit (damage disabled)");
                    return;
                }

                // 出生保护：导弹出生 2 秒内命中玩家一律忽略——
                // 覆盖出生点与机体重叠 / 误锁自己的情况，避免玩家被自己的导弹秒杀
                if (_t < 2f)
                {
                    Machine.Core.Log.Info("[AAM] player hit ignored (spawn guard " + _t.ToString("F1") + "s)");
                    return;
                }

                // ---- 命中玩家 = 直接击落 ----
                // 旧实现只调 PartExploder.ExplodePart 炸掉"离爆点最近的一个部件"，于是：
                //   ① 玩家看到的是"炸出碎片、飞机照飞"，不是坠毁；
                //   ② 挑部件时 isBase 的全被 skip，被炸的那个必然不是基础机身，
                //      后面 `best.name.Contains("Body")` 那个判定永不成立
                //      → 计分板从来收不到玩家阵亡。
                // 现在与 AI 走同一条路：PlaneController.ExplodePlane() 整机爆炸 + 上报击杀。
                PlaneController pctrl = pc.GetComponent<PlaneController>();
                if (pctrl == null) pctrl = pc.GetComponentInChildren<PlaneController>(true);
                if (pctrl == null) { Machine.Core.Log.Info("[AAM] player hit, no PlaneController"); return; }

                // 名字必须在爆炸前取 —— ExplodePlane 会把整机 SetActive(false)
                string playerModel = "PlayerAircraft";
                string playerCall = PlayerDisplayName();
                int playerFaction = PlayerFactionOf();

                // 游戏原版整机爆炸：爆炸音效 + 爆炸粒子 + 全部件炸开 + 整机 SetActive(false)
                try
                {
                    pctrl.ExplodePlane();
                    Machine.Core.Log.Info("[AAM] player destroyed by missile - plane exploded (crash)"
                                          + " exploded=" + pctrl.Exploded
                                          + " active=" + pc.gameObject.activeSelf);
                }
                catch (Exception e1)
                {
                    Machine.Core.Log.Info("[AAM] player ExplodePlane failed: " + e1.Message);
                    try
                    {
                        PartExploder pe = null;
                        FieldInfo fe = typeof(PlaneContainer).GetField("exploder", F);
                        if (fe != null) pe = fe.GetValue(pc) as PartExploder;
                        if (pe == null) pe = pc.GetComponent<PartExploder>();
                        if (pe != null) pe.ExplodePlane();
                    }
                    catch (Exception e2) { Machine.Core.Log.Info("[AAM] player explode fallback failed: " + e2.Message); }
                }

                // 对战积分：玩家机也是阵营飞机，先上报战损再登记击杀
                ReportFactionAircraftLost(pc.transform, "shot down");
                ReportPlayerKill(playerModel, playerCall, playerFaction, SpecName);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] player strike failed: " + e.Message); }
        }

        /// <summary>
        /// 按"程序集名 + 类型全名"找类型（找不到返回 null）。
        /// MissileController 是 mod 侧类，够不到 AamSystem 里的私有查找，所以自带一份。
        /// </summary>
        private static Type FindTypeIn(string asmName, string typeName)
        {
            try
            {
                Type t = Type.GetType(typeName + ", " + asmName);
                if (t != null) return t;
                // ⚠ 回退不能按 Assembly.GetName().Name 匹配（本机加载器加载的 mod 程序集简单名对不上）；
                //    改为按"类型全名"扫描所有已加载程序集。
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try { t = asms[i].GetType(typeName); } catch { t = null; }
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 玩家显示名：优先 Machine.Core.Net.PlayerName（运行时记下的用户名），取不到就用 "Player"。
        /// 计分板是按名字匹配行的，所以这个名字必须稳定 —— 玩家的击杀与阵亡要落到同一行，
        /// 名字变了就会在榜上多出一行。
        /// </summary>
        private static string PlayerDisplayName()
        {
            try
            {
                Type t = FindTypeIn("Machine.Core", "Machine.Core.Net");
                if (t != null)
                {
                    PropertyInfo p = t.GetProperty("PlayerName", BindingFlags.Public | BindingFlags.Static);
                    if (p != null)
                    {
                        string s = p.GetValue(null, null) as string;
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch { }
            return "Player";
        }

        /// <summary>玩家所在阵营；阵营系统不可用时返回 0（与既有代码的兜底一致）。</summary>
        private static int PlayerFactionOf()
        {
            try
            {
                Type ft = FindTypeIn("FactionSystem", "Machine.Faction.FactionApi");
                if (ft != null)
                {
                    // 注意：这是**属性** PlayerFaction，不是方法 GetPlayerFaction（旧代码找错了，
                    // 一直静默返回 0）。
                    PropertyInfo p = ft.GetProperty("PlayerFaction", BindingFlags.Public | BindingFlags.Static);
                    if (p != null) return (int)p.GetValue(null, null);
                }
            }
            catch { }
            return 0;
        }

        /// <summary>获取指定阵营的主岛基地坐标；阵营系统不可用或越界时返回 Vector3.zero。</summary>
        private static Vector3 FactionHomePos(int factionId)
        {
            if (factionId < 0) return Vector3.zero;
            try
            {
                Type ft = FindTypeIn("FactionSystem", "Machine.Faction.FactionApi");
                if (ft == null) return Vector3.zero;
                // 通过反射获取 Factions 列表，再取 HomePos
                FieldInfo sysField = ft.GetField("_sys", BindingFlags.NonPublic | BindingFlags.Static);
                if (sysField == null) return Vector3.zero;
                object sys = sysField.GetValue(null);
                if (sys == null) return Vector3.zero;
                PropertyInfo factionsProp = sys.GetType().GetProperty("Factions", BindingFlags.Public | BindingFlags.Instance);
                if (factionsProp == null) return Vector3.zero;
                System.Collections.IList factions = factionsProp.GetValue(sys, null) as System.Collections.IList;
                if (factions == null || factionId >= factions.Count) return Vector3.zero;
                object fd = factions[factionId];
                if (fd == null) return Vector3.zero;
                FieldInfo homeField = fd.GetType().GetField("HomePos", BindingFlags.Public | BindingFlags.Instance);
                if (homeField == null) return Vector3.zero;
                return (Vector3)homeField.GetValue(fd);
            }
            catch { }
            return Vector3.zero;
        }

        // ---- 玩家击杀上报：阵营系统是**延迟初始化**的，必须等它就绪再写 ----
        // 实测教训：FactionSystem 的阵营列表 Factions 要等场景加载后若干秒才建好
        // （Init 里 `if (Factions.Count == 0)` 走分帧补齐，最后才打 "ready (deferred)"）。
        // 若在它建好之前调 RegisterPlayerKill，内部 RegisterKill 会访问 Factions[...] 下标越界，
        // 而 RegisterPlayerKill / RegisterKill 都自带 catch{} 把异常吞掉 —— 于是"调用没抛异常、
        // 上报看起来成功"，实际上一个字都没记上，计分板毫无变化。
        // 所以：就绪就直接写；没就绪就把这一笔挂起来，等 Update 里 FlushPendingKills 补报。
        private class PendingKill
        {
            public string KillerModel, KillerCall;
            public int KillerFaction;
            public string VictimModel, VictimCall;
            public int VictimFaction;
            public string MissileName;
            public float T;
        }
        private static readonly List<PendingKill> _pendingKills = new List<PendingKill>();

        /// <summary>
        /// 阵营系统是否已就绪。判据是 Factions 列表非空 —— **不能**用 FactionApi.Available，
        /// 那个只看 _sys 有没有绑定，此时阵营列表可能还是空的。
        /// </summary>
        private static bool FactionReady()
        {
            try
            {
                Type ft = FindTypeIn("FactionSystem", "Machine.Faction.FactionApi");
                if (ft == null) return false;
                PropertyInfo p = ft.GetProperty("FactionCount", BindingFlags.Public | BindingFlags.Static);
                if (p == null) return false;
                return (int)p.GetValue(null, null) > 0;
            }
            catch { return false; }
        }

        /// <summary>写一次玩家级击杀战绩。阵营未就绪返回 false（此时什么都没写）。</summary>
        private static bool WritePlayerKill(string killerModel, string killerCall, int killerFaction,
                                            string victimModel, string victimCall, int victimFaction)
        {
            if (!FactionReady()) return false;
            try
            {
                Type fs = FindTypeIn("FactionSystem", "Machine.Faction.FactionSystem");
                if (fs == null) return false;
                FieldInfo live = fs.GetField("Live", BindingFlags.Public | BindingFlags.Static);
                object sys = (live != null) ? live.GetValue(null) : null;
                MethodInfo m = fs.GetMethod("RegisterPlayerKill");
                if (sys == null || m == null) return false;
                // 签名顺序：(击杀方名, 击杀方机型, 击杀方阵营, 击杀方是 AI,
                //            受害方名, 受害方机型, 受害方阵营, 受害方是 AI)
                m.Invoke(sys, new object[] {
                    killerCall, killerModel, killerFaction, true,
                    victimCall, victimModel, victimFaction, false });
                return true;
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] RegisterPlayerKill failed: " + e.Message); return false; }
        }

        /// <summary>把计分板上每个阵营的玩家行 dump 进日志 —— 用来实证"阵亡真的记上了"，
        /// 而不是只看反射调用有没有抛异常（那会被静默 catch 骗过）。</summary>
        private static void DumpScoreboard()
        {
            try
            {
                Type fs = FindTypeIn("FactionSystem", "Machine.Faction.FactionSystem");
                if (fs == null) return;
                FieldInfo live = fs.GetField("Live", BindingFlags.Public | BindingFlags.Static);
                object sys = (live != null) ? live.GetValue(null) : null;
                FieldInfo ff = fs.GetField("Factions");
                IList facs = (sys != null && ff != null) ? ff.GetValue(sys) as IList : null;
                if (facs == null) { Machine.Core.Log.Info("[AAM] scoreboard dump: no factions"); return; }
                for (int i = 0; i < facs.Count; i++)
                {
                    object fd = facs[i];
                    if (fd == null) continue;
                    Type ft2 = fd.GetType();
                    FieldInfo nf = ft2.GetField("Name");
                    string fname = (nf != null) ? (nf.GetValue(fd) as string) : "?";
                    FieldInfo pf = ft2.GetField("Players");
                    IList players = (pf != null) ? pf.GetValue(fd) as IList : null;
                    if (players == null) continue;
                    for (int j = 0; j < players.Count; j++)
                    {
                        object ps = players[j];
                        if (ps == null) continue;
                        Type pt = ps.GetType();
                        Machine.Core.Log.Info("[AAM] scoreboard row: faction=" + fname
                            + " name=" + pt.GetField("Name").GetValue(ps)
                            + " model=" + pt.GetField("Model").GetValue(ps)
                            + " K=" + pt.GetField("Kills").GetValue(ps)
                            + " D=" + pt.GetField("Deaths").GetValue(ps)
                            + " ai=" + pt.GetField("IsAI").GetValue(ps));
                    }
                }
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] scoreboard dump failed: " + e.Message); }
        }

        /// <summary>每帧调用：阵营系统就绪后，把之前挂起的玩家击杀补报上去。</summary>
        public static void FlushPendingKills()
        {
            if (_pendingKills.Count == 0) return;
            for (int i = _pendingKills.Count - 1; i >= 0; i--)
            {
                PendingKill pk = _pendingKills[i];
                // 挂起期间玩家可能才刚被分配阵营 —— 补报时重取一次，
                // 免得把阵亡记到解析时那个默认的 0 号阵营上。
                if (FactionReady()) pk.VictimFaction = PlayerFactionOf();
                if (WritePlayerKill(pk.KillerModel, pk.KillerCall, pk.KillerFaction,
                                    pk.VictimModel, pk.VictimCall, pk.VictimFaction))
                {
                    Machine.Core.Log.Info("[AAM] pending player kill flushed - scoreboard updated"
                                          + " victim=" + pk.VictimCall
                                          + " waited=" + (Time.time - pk.T).ToString("F1") + "s");
                    DumpScoreboard();
                    _pendingKills.RemoveAt(i);
                }
                else if (Time.time - pk.T > 90f)
                {
                    Machine.Core.Log.Info("[AAM] pending player kill dropped (faction never became ready)");
                    _pendingKills.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 上报"玩家被击落"。
        /// 首选 FactionSystem.RegisterPlayerKill：它一次同时更新阵营 K/D 与**每个玩家**的 K/D，
        /// 而计分板界面画的就是后者；阵营未就绪时把这一笔挂起、等 Update 里补报。
        /// 另发一条 KillFeed 击杀信息。
        /// 注意：**不要**把玩家标成 Alive=false —— 计分板对 !Alive 的行是直接 continue 跳过的，
        /// 那样玩家会整行从榜上消失，而不是"多一个阵亡"。
        /// </summary>
        private void ReportPlayerKill(string victimModel, string victimCall, int victimFaction, string missileName)
        {
            if (FactionReady())
            {
                bool ok = WritePlayerKill(ShooterModel, ShooterCall, FactionId,
                                          victimModel, victimCall, victimFaction);
                if (!ok)
                {
                    // 首选路径失败：退回 FactionApi.OnKill（只记阵营总计，不碰玩家行）
                    try
                    {
                        Type ft = FindTypeIn("FactionSystem", "Machine.Faction.FactionApi");
                        MethodInfo m = (ft != null)
                            ? ft.GetMethod("OnKill", new Type[] { typeof(int), typeof(int) }) : null;
                        if (m != null) m.Invoke(null, new object[] { FactionId, victimFaction });
                    }
                    catch (Exception e) { Machine.Core.Log.Info("[AAM] OnKill fallback failed: " + e.Message); }
                }
                FeedKill(ShooterModel, ShooterCall, FactionId, missileName, victimModel, victimCall, victimFaction);
                Machine.Core.Log.Info("[AAM] player shot down - scoreboard=" + ok
                                      + " victim=" + victimCall + "/" + victimModel
                                      + " missile=" + (missileName ?? "?")
                                      + " faction=" + victimFaction);
                DumpScoreboard();
            }
            else
            {
                // 阵营系统还没建好：挂起，等 Update 里补报（否则这一笔会被静默丢掉）
                _pendingKills.Add(new PendingKill {
                    KillerModel = ShooterModel, KillerCall = ShooterCall, KillerFaction = FactionId,
                    VictimModel = victimModel, VictimCall = victimCall, VictimFaction = victimFaction,
                    MissileName = missileName,
                    T = Time.time });
                FeedKill(ShooterModel, ShooterCall, FactionId, missileName, victimModel, victimCall, victimFaction);
                Machine.Core.Log.Info("[AAM] player shot down - scoreboard pending (faction not ready yet)"
                                      + " victim=" + victimCall + "/" + victimModel
                                      + " missile=" + (missileName ?? "?")
                                      + " faction=" + victimFaction);
            }
        }

        /// <summary>命中目标是否为友军（同阵营；阵营系统未启用时返回 false = 可攻击）。</summary>
        internal bool IsFriendlyTarget(Transform target)
        {
            return IsFriendlyTo(target, FactionId);
        }

        /// <summary>静态版：给定"我方阵营 id"判断目标是否友军（机炮子弹用）。</summary>
        internal static bool IsFriendlyTo(Transform target, int myFaction)
        {
            try
            {
                var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "FactionSystem") { t = asm.GetType("Machine.Faction.FactionApi"); break; }
                    }
                }
                if (t == null) return false;
                var m = t.GetMethod("FactionOf", new Type[] { typeof(GameObject) });
                if (m == null) return false;
                int vt = (int)m.Invoke(null, new object[] { target.gameObject });
                if (myFaction < 0 || vt < 0) return false;
                return vt == myFaction;
            }
            catch { return false; }
        }

        /// <summary>沿 parent 链找 FactionMarker 组件（跨程序集按类型名，避免编译期引用）。</summary>
        private static Component FindFactionMarker(Transform root)
        {
            try
            {
                var t = root;
                while (t != null)
                {
                    var comps = t.GetComponents<MonoBehaviour>();
                    for (int i = 0; i < comps.Length; i++)
                    {
                        if (comps[i] != null && comps[i].GetType().Name == "FactionMarker") return comps[i];
                    }
                    t = t.parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>命中 AI 飞机：整机爆炸销毁（不再只拆一个部件——用户反馈 AI 机被击中后仍能继续飞）。</summary>
        private void StrikeAiAircraft(Transform root)
        {
            try
            {
                if (root == null) return;

                // 击杀登记（阵营计分板）：导弹发射者阵营 击杀 AI 所属阵营
                int victimFaction = -1;
                try
                {
                    var m2 = FindFactionMarker(root);
                    if (m2 != null)
                    {
                        var ff = m2.GetType().GetField("FactionId");
                        if (ff != null) victimFaction = (int)ff.GetValue(m2);
                    }
                    var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                    if (t == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "FactionSystem") { t = asm.GetType("Machine.Faction.FactionApi"); break; }
                        }
                    }
                    if (t != null)
                    {
                        var m = t.GetMethod("OnKill", new Type[] { typeof(int), typeof(int) });
                        if (m != null) m.Invoke(null, new object[] { FactionId, victimFaction });
                    }
                }
                catch { }

                // 爆炸：使用游戏原版爆炸效果（PlaneController.ExplodePlane），不再新增自定义特效
                try
                {
                    var pc = root.GetComponentInParent<PlaneController>();
                    if (pc != null)
                    {
                        pc.ExplodePlane();
                        Machine.Core.Log.Info("[AAM] AI explode via PlaneController.ExplodePlane OK");
                    }
                    else
                    {
                        var pe = root.GetComponentInParent<PartExploder>();
                        if (pe != null)
                        {
                            pe.ExplodePlane();
                            Machine.Core.Log.Info("[AAM] AI explode via PartExploder.ExplodePlane OK");
                        }
                        else Machine.Core.Log.Info("[AAM] AI explode: no PlaneController/PartExploder");
                    }
                }
                catch (Exception e) { Machine.Core.Log.Info("[AAM] AI explode failed: " + e.Message); }

                // 销毁 AI 飞机（含刚体/控制器/目标登记），彻底无法再飞行
                var ai = root.GetComponentInParent<AiAircraft>();
                if (ai != null)
                {
                    // 对战积分：先上报战损（按整机造价扣其阵营积分），再销毁
                    ReportFactionAircraftLost(root, "shot down");
                    ai.Despawn();
                    Machine.Core.Log.Info("[AAM] AI aircraft destroyed by missile: " + ai.Callsign);
                    var sys2 = AamSystem.Live;
                    if (sys2 != null) sys2.PostMissileResult("HIT " + ai.Callsign, 9);   // 战斗部显示命中机型
                    // 战斗事件表：型号（呼号, 阵营）导弹型号 击毁 型号（呼号, 阵营）
                    try { FeedKill(ShooterModel, ShooterCall, FactionId, SpecName, ai.ModelName, ai.Callsign, victimFaction); }
                    catch { }
                }
                else
                {
                    try { TargetRegistry.Unregister(root.gameObject); } catch { }
                    try { SilentAi.Unmark(root.gameObject); } catch { }
                    UnityEngine.Object.Destroy(root.gameObject);
                    Machine.Core.Log.Info("[AAM] AI aircraft (raw) destroyed by missile");
                }
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] AI strike failed: " + e.Message); }
        }

        // ---- 战斗事件表（KillFeed mod）上报：KillFeed 未加载时静默 ----
        // ---- 机炮对 AI 飞机：累积损伤 ----
        // ExplodePart 对 AI 飞机会抛 NRE（Wheel 等部件的爆炸逻辑依赖玩家侧上下文），
        // 所以机炮改为"命中计数"，打满 GunHitsToKill 发走导弹同款整机击毁路径。
        private const int GunHitsToKill = 8;
        private static readonly Dictionary<Transform, int> GunDamage = new Dictionary<Transform, int>();
        private static int _gunDbgLog;

        /// <summary>
        /// 机炮命中（非友军）：AI 飞机累积损伤，打满后整机击毁并登记击杀。
        /// 返回 true = 这发弹算命中（应被消耗）。
        /// </summary>
        internal static bool ReportGunHit(Transform root, Vector3 at, string kModel, string kCall, int kFaction)
        {
            try
            {
                if (root == null) return false;
                if (IsFriendlyTo(root, kFaction)) return false;

                var ai = root.GetComponentInParent<AiAircraft>();
                if (ai != null)
                {
                    AddGunDamage(ai, kModel, kCall, kFaction);
                    return true;
                }

                // 非 AI 目标（玩家飞机等）：逐部件爆炸（这条路径只在玩家机上验证过）
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
                        FieldInfo fb = p.GetType().GetField("isBasePart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                return true;
            }
            catch { return false; }
        }

        /// <summary>机炮累积损伤：第 GunHitsToKill 发命中 -> 整机击毁（与导弹同一条路径）。</summary>
        private static void AddGunDamage(AiAircraft ai, string kModel, string kCall, int kFaction)
        {
            try
            {
                if (ai == null) return;
                Transform root = ai.transform;
                int d;
                GunDamage.TryGetValue(root, out d);
                d++;
                GunDamage[root] = d;
                if (_gunDbgLog < 40) { _gunDbgLog++; Machine.Core.Log.Info("[AAM] gun damage " + d + "/" + GunHitsToKill + " on " + ai.Callsign); }
                if (d < GunHitsToKill) return;
                GunDamage.Remove(root);
                DestroyAiPlane(root, kModel, kCall, kFaction);
            }
            catch { }
        }

        /// <summary>机炮打满后的整机击毁：与导弹 StrikeAiAircraft 同一条链路（阵营计分 + 原版爆炸 + Despawn + KillFeed）。</summary>
        internal static void DestroyAiPlane(Transform root, string kModel, string kCall, int kFaction)
        {
            try
            {
                if (root == null) return;
                GunDamage.Remove(root);

                int victimFaction = -1;
                try
                {
                    var m2 = FindFactionMarker(root);
                    if (m2 != null)
                    {
                        var ff = m2.GetType().GetField("FactionId");
                        if (ff != null) victimFaction = (int)ff.GetValue(m2);
                    }
                    var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                    if (t == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "FactionSystem") { t = asm.GetType("Machine.Faction.FactionApi"); break; }
                        }
                    }
                    if (t != null)
                    {
                        var m = t.GetMethod("OnKill", new Type[] { typeof(int), typeof(int) });
                        if (m != null) m.Invoke(null, new object[] { kFaction, victimFaction });
                    }
                }
                catch { }

                try
                {
                    var pc = root.GetComponentInParent<PlaneController>();
                    if (pc != null)
                    {
                        pc.ExplodePlane();
                        Machine.Core.Log.Info("[AAM] gun kill via PlaneController.ExplodePlane OK");
                    }
                    else
                    {
                        var pe = root.GetComponentInParent<PartExploder>();
                        if (pe != null) pe.ExplodePlane();
                    }
                }
                catch (Exception e) { Machine.Core.Log.Info("[AAM] gun explode failed: " + e.Message); }

                var ai = root.GetComponentInParent<AiAircraft>();
                if (ai != null)
                {
                    // 对战积分：先上报战损（按整机造价扣其阵营积分），再销毁
                    ReportFactionAircraftLost(root, "shot down");
                    ai.Despawn();
                    Machine.Core.Log.Info("[AAM] AI aircraft destroyed by gun: " + ai.Callsign);
                    FeedKill(kModel, kCall, kFaction, "Gun", ai.ModelName, ai.Callsign, victimFaction);
                }
                else
                {
                    try { TargetRegistry.Unregister(root.gameObject); } catch { }
                    try { SilentAi.Unmark(root.gameObject); } catch { }
                    UnityEngine.Object.Destroy(root.gameObject);
                }
            }
            catch (Exception e2) { Machine.Core.Log.Info("[AAM] gun kill failed: " + e2.Message); }
        }

        internal static void FeedKill(string kModel, string kCall, int kFaction, string missileName, string vModel, string vCall, int vFaction)
        {
            try
            {
                Machine.Core.Log.Info("[AAM] FeedKill called: " + kModel + "(" + kCall + ") [" + (missileName ?? "?") + "] vs " + vModel + "(" + vCall + ")");
                System.Type t = FindFeedApiType();
                if (t == null) { Machine.Core.Log.Info("[AAM] FeedKill: FeedApi type NOT FOUND in any assembly"); return; }
                Machine.Core.Log.Info("[AAM] FeedKill: found type " + t.FullName + " in " + t.Assembly.GetName().Name);
                // 优先调用带导弹型号的新版PostKill
                var m = t.GetMethod("PostKill", new System.Type[] { typeof(string), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string), typeof(int) });
                if (m != null)
                {
                    m.Invoke(null, new object[] { kModel, kCall, kFaction, missileName, vModel, vCall, vFaction });
                    Machine.Core.Log.Info("[AAM] FeedKill: PostKill (with missile) invoked successfully");
                }
                else
                {
                    // 兼容旧版PostKill（无导弹型号）
                    var mOld = t.GetMethod("PostKill", new System.Type[] { typeof(string), typeof(string), typeof(int), typeof(string), typeof(string), typeof(int) });
                    if (mOld != null)
                    {
                        mOld.Invoke(null, new object[] { kModel, kCall, kFaction, vModel, vCall, vFaction });
                        Machine.Core.Log.Info("[AAM] FeedKill: PostKill (legacy) invoked successfully");
                    }
                    else
                    {
                        Machine.Core.Log.Info("[AAM] FeedKill: PostKill method NOT FOUND");
                    }
                }
            }
            catch (Exception ex) { Machine.Core.Log.Info("[AAM] FeedKill FAILED: " + ex.Message + "\n" + ex.StackTrace); }
        }

        /// <summary>兼容旧版FeedKill（无导弹型号）。</summary>
        internal static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)
        {
            FeedKill(kModel, kCall, kFaction, null, vModel, vCall, vFaction);
        }

        /// <summary>
        /// 上报"一架阵营飞机被摧毁"给 FactionSystem 的对战积分模式（按其整机造价扣其阵营积分）。
        /// 必须在 Destroy/Despawn **之前**调用 —— 阵营系统按 root 的实例 ID 查登记表，
        /// 对象一旦被销毁就拿不到了。FactionSystem 未加载时静默。
        /// reason: "shot down"(被击落) / "crashed"(坠毁)。
        /// </summary>
        internal static void ReportFactionAircraftLost(Transform root, string reason)
        {
            try
            {
                if (root == null) return;
                Type t = FindFactionApiType();
                if (t == null) return;
                MethodInfo m = t.GetMethod("OnAircraftLost",
                    new Type[] { typeof(GameObject), typeof(int), typeof(string) });
                if (m == null) return;
                m.Invoke(null, new object[] { root.gameObject, -1, reason });
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] ReportFactionAircraftLost failed: " + e.Message); }
        }

        /// <summary>在已加载程序集里找 Machine.Faction.FactionApi（不依赖程序集名 —— 实测 mod 程序集的
        /// Assembly.GetName().Name 与 "FactionSystem" 不相等，按名字匹配匹配不到）。</summary>
        internal static Type FindFactionApiType()
        {
            try
            {
                Type t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (t != null) return t;
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try { t = asms[i].GetType("Machine.Faction.FactionApi"); } catch { t = null; }
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>在所有已加载程序集中查找 KillFeed.FeedApi 类型（不依赖程序集名称）。</summary>
        /// internal：AiAircraft.FeedCrash 也用这套查找（坠毁上报），不能只留在 MissileController 里。
        internal static System.Type FindFeedApiType()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        // 先按程序集名称快速匹配
                        if (asm.GetName().Name == "KillFeed")
                        {
                            var t = asm.GetType("KillFeed.FeedApi");
                            if (t != null) return t;
                        }
                        // 再遍历所有类型查找 FeedApi
                        var types = asm.GetTypes();
                        for (int i = 0; i < types.Length; i++)
                        {
                            if (types[i].Name == "FeedApi" && types[i].FullName != null && types[i].FullName.Contains("FeedApi"))
                                return types[i];
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static bool _explosionProbed;
        internal static void ProbeExplosion()
        {
            if (_explosionProbed) return;
            _explosionProbed = true;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                FieldInfo[] fs = typeof(PartExploder).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < fs.Length; i++)
                    sb.Append(fs[i].Name).Append(":").Append(fs[i].FieldType.Name).Append(" ");
                Machine.Core.Log.Info("[AAM] PartExploder fields: " + sb.ToString());

                System.Text.StringBuilder sb2 = new System.Text.StringBuilder();
                System.Reflection.MethodInfo[] ms = typeof(PartExploder).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name.IndexOf("Explode", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        sb2.Append(ms[i].Name).Append(" ");
                }
                Machine.Core.Log.Info("[AAM] PartExploder explode methods: " + sb2.ToString());

                // Assembly-CSharp 里名字含 Explosion 的类型
                System.Text.StringBuilder sb3 = new System.Text.StringBuilder();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.GetName().Name != "Assembly-CSharp") continue;
                        Type[] tys = asm.GetTypes();
                        for (int i = 0; i < tys.Length; i++)
                        {
                            if (tys[i].Name.IndexOf("Explosion", System.StringComparison.OrdinalIgnoreCase) >= 0
                                || tys[i].Name.IndexOf("Explode", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                sb3.Append(tys[i].FullName).Append(" ");
                        }
                    }
                    catch { }
                }
                Machine.Core.Log.Info("[AAM] Explosion types: " + sb3.ToString());
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] explosion probe failed: " + e.Message); }
        }

        private static GameObject _originalExplosionPrefab;

        /// <summary>用游戏原版爆炸粒子预制体（PartExploder.explosionParticlePrefab）生成爆炸，可放大。</summary>
        private void SpawnOriginalExplosion(float scaleMul)
        {
            try
            {
                if (_originalExplosionPrefab == null)
                {
                    PartExploder ex = null;
                    try { ex = PlaneContainer.Instance != null ? PlaneContainer.Instance.GetComponent<PartExploder>() : null; } catch { }
                    if (ex == null)
                    {
                        try { ex = UnityEngine.Object.FindFirstObjectByType(typeof(PartExploder)) as PartExploder; } catch { }
                    }
                    if (ex == null)
                    {
                        Machine.Core.Log.Info("[AAM] boom: no PartExploder source");
                        return;
                    }
                    FieldInfo f = typeof(PartExploder).GetField("explosionParticlePrefab",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null)
                    {
                        Machine.Core.Log.Info("[AAM] boom: explosionParticlePrefab field missing");
                        return;
                    }
                    _originalExplosionPrefab = f.GetValue(ex) as GameObject;
                    Machine.Core.Log.Info("[AAM] boom: original explosion prefab " + (_originalExplosionPrefab != null ? "OK" : "NULL"));
                }
                if (_originalExplosionPrefab == null) return;

                GameObject go = UnityEngine.Object.Instantiate(_originalExplosionPrefab, Body.Pos, Quaternion.identity);
                go.transform.localScale = Vector3.one * scaleMul;
                float dur = 2.5f;
                ParticleSystem[] pss = go.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < pss.Length; i++)
                {
                    if (pss[i] != null && pss[i].main.duration > dur) dur = pss[i].main.duration;
                }
                UnityEngine.Object.Destroy(go, dur + 2f);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] boom: " + e.Message); }
        }

        /// <summary>把已经喷出的烟团从弹体上摘下来（见 SmokeDetacher）。</summary>
        private void DetachSmoke()
        {
            try
            {
                SmokeDetacher[] ds = GetComponentsInChildren<SmokeDetacher>(true);
                for (int i = 0; i < ds.Length; i++) if (ds[i] != null) ds[i].Detach();
            }
            catch { }
        }

        private IEnumerator Boom()
        {
            ProbeExplosion();
            DetachSmoke();   // 已喷出的烟留在原地自然散，别跟着弹体一起消失
            // 原版爆炸粒子，导弹爆炸放大 3 倍（用户要求更夸张更大）
            SpawnOriginalExplosion(3f);
            // 爆炸冲击光球（瞬闪，非主特效，仅补足视觉中心）
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(ball.GetComponent<Collider>());
            ball.transform.position = Body.Pos;
            ball.transform.localScale = Vector3.one * 4f;
            Renderer rd = ball.GetComponent<Renderer>();
            float t = 0f;
            while (t < 0.35f)
            {
                t += Time.deltaTime;
                float k = t / 0.35f;
                ball.transform.localScale = Vector3.one * (4f + 22f * k);
                if (rd != null && rd.material != null)
                    rd.material.color = new Color(1f, 0.55f + 0.35f * (1f - k), 0.1f, (1f - k) * 0.9f);
                yield return null;
            }
            UnityEngine.Object.Destroy(ball);
            yield return new WaitForSeconds(1.5f);
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    // =====================================================================
    // AI 靶机（未来所有 AI 驾驶飞机的基座）
    // =====================================================================
    public class DroneController : MonoBehaviour
    {
        public FlightBody Body = new FlightBody();
        public Vector3 CruiseDir = Vector3.forward;
        public float CruiseSpeed = 90f;
        public float Thrust = 60f;
        public float DragK = 0.00012f;

        public static DroneController Spawn(GameObject model, Vector3 pos, Vector3 dir, float speed)
        {
            GameObject root = model;
            root.name = "AI_TargetDrone";
            root.transform.position = pos;
            root.transform.rotation = PnGuidance.SafeLook(dir.normalized, Vector3.up);

            Rigidbody rb = root.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 200f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = dir.normalized * speed;

            var dc = root.AddComponent<DroneController>();
            dc.Body.Rb = rb;
            dc.Body.Vel = dir.normalized * speed;
            dc.CruiseDir = dir.normalized;
            dc.CruiseSpeed = speed;

            root.AddComponent<AiEntityMarker>().Kind = "drone";
            SilentAi.Mark(root);
            TargetRegistry.Register(root, true, "TargetDrone");
            Machine.Core.Log.Info("[AAM] target drone spawned at " + pos + " speed=" + speed);
            return dc;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            // 保持巡航速度与航向
            Vector3 want = CruiseDir * CruiseSpeed;
            Vector3 steer = Vector3.ClampMagnitude(want - Body.Vel, BodySpeedClamp);
            Body.Step(dt, steer, Thrust, DragK, CruiseSpeed * 1.5f);
            Body.FaceVelocity(dt, 25f);
        }

        private const float BodySpeedClamp = 30f;
    }

    // =====================================================================
    // AI 飞行员：每架 AI 飞机一个专属的输入源
    // =====================================================================
    // 为什么需要这个：
    //   PlaneController.UpdateControlls()（在 FixedUpdate 里跑）读的是**实例字段**
    //   PlaneController::inputManager 的 pitch/roll/yaw/throttleInput，而不是去拿全局的
    //   InputManager.Instance —— IL 已确认：
    //       ldarg.0 ; ldfld PlaneController::inputManager ; ldfld InputManager::pitchInput
    //   所以只要给 AI 飞机换上一个"自己的" InputManager 实例，AI 就能完全独立地操纵它，
    //   玩家键盘上的输入一个字节都不会串过去。
    //
    //   唯一的坑：InputManager 继承 Singleton<InputManager>，而且它的 Awake() 会调
    //   RefreshDisplayNames() / RefreshBindingPaths()，那两个方法直接读 playerInput 和三个
    //   字典，直接 AddComponent 会立刻 NPE。做法是：
    //     1) 先 new 一个 **inactive** 的 GameObject —— Unity 在 inactive 时 AddComponent
    //        不会触发 Awake，给了我们补字段的机会；
    //     2) 把玩家 InputManager 的 playerInput / displayStrings / primaryBindingPath /
    //        secondaryBindingPath 引用复制过来；
    //     3) 还原 Singleton 的 m_Instance（假实例绝不能抢走全局单例）；
    //     4) SetActive(true) 让 Awake 正常跑，然后 enabled = false 挡掉它自己的
    //        Start()/Update() —— 4 个输入字段从此只归 AI 写。
    internal static class AiInputSource
    {
        private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static FieldInfo _fPitch, _fRoll, _fYaw, _fThrottle;
        private static FieldInfo _fSingletonInstance;

        private static void BindFields()
        {
            if (_fPitch != null) return;
            Type t = typeof(InputManager);
            _fPitch = t.GetField("pitchInput", BF);
            _fRoll = t.GetField("rollInput", BF);
            _fYaw = t.GetField("yawInput", BF);
            _fThrottle = t.GetField("throttleInput", BF);
            Type bt = t.BaseType;
            if (bt != null)
                _fSingletonInstance = bt.GetField("m_Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>AI 唯一的操纵手段：把四通道输入直接写进它自己的 InputManager。</summary>
        internal static void Write(InputManager im, float pitch, float roll, float yaw, float throttle)
        {
            if (im == null) return;
            BindFields();
            try
            {
                if (_fPitch != null) _fPitch.SetValue(im, Mathf.Clamp(pitch, -1f, 1f));
                if (_fRoll != null) _fRoll.SetValue(im, Mathf.Clamp(roll, -1f, 1f));
                if (_fYaw != null) _fYaw.SetValue(im, Mathf.Clamp(yaw, -1f, 1f));
                if (_fThrottle != null) _fThrottle.SetValue(im, Mathf.Clamp01(throttle));
            }
            catch { }
        }

        internal static InputManager New(string name)
        {
            BindFields();
            object saved = null;
            try { if (_fSingletonInstance != null) saved = _fSingletonInstance.GetValue(null); } catch { }

            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);                       // 关键：inactive 时 AddComponent 不跑 Awake

            InputManager im = go.AddComponent<InputManager>();

            InputManager real = null;
            try { real = InputManager.Instance; } catch { }
            if (real != null && !ReferenceEquals(real, im))
            {
                CopyField(real, im, "playerInput");
                CopyField(real, im, "displayStrings");
                CopyField(real, im, "primaryBindingPath");
                CopyField(real, im, "secondaryBindingPath");
            }

            try
            {
                if (_fSingletonInstance != null && ReferenceEquals(_fSingletonInstance.GetValue(null), im))
                    _fSingletonInstance.SetValue(null, saved);
            }
            catch { }

            go.SetActive(true);                        // 这时才跑 Awake（playerInput 已就位）
            im.enabled = false;                        // 挡掉 Start()/Update()，输入只由 AI 写
            return im;
        }

        private static void CopyField(object from, object to, string fieldName)
        {
            try
            {
                FieldInfo f = from.GetType().GetField(fieldName, BF);
                if (f != null) f.SetValue(to, f.GetValue(from));
            }
            catch { }
        }
    }

    /// <summary>
    /// 烟轨脱离器（2026-09-12）：导弹自爆/命中时把已喷出的烟团从弹体上摘下来，
    /// 让它在原地按自己的生命周期渐变消失，而不是跟着弹体一起被 Destroy 掉、整条烟凭空不见。
    /// 未脱离时什么也不做（导弹可以飞 100 多秒，不能提前回收）。
    /// </summary>
    public class SmokeDetacher : MonoBehaviour
    {
        public ParticleSystem Ps;
        public float Afterlife = 14f;     // 脱离后再留几秒，够粒子自己散完（粒子寿命已加倍到 10.4s）
        private bool _detached;
        private float _t;

        public void Detach()
        {
            if (_detached) return;
            _detached = true;
            _t = 0f;
            try { transform.SetParent(null, true); } catch { }
            if (Ps != null)
            {
                try { Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting); } catch { }
            }
        }

        private void Update()
        {
            if (!_detached) return;
            _t += Time.deltaTime;
            if (_t > Afterlife) UnityEngine.Object.Destroy(gameObject);
        }
    }

    // =====================================================================
    // AI 性格（人格）—— 2026-09-12
    // =====================================================================
    // 之前的 AI 只有一种行为：爬不起来、贴着低空平飞、直线扑向玩家。
    // 现在按"人格"分型：每种性格有自己的一套飞行/交战偏好，
    //   * 在哪飞：Speed / Alt / PitchLimit
    //   * 怎么打：RadarRange / RadarCone / FireFrac / Standoff / CrankAngle / MinRange
    //   * 什么脾气：ClimbFirst(先爬高) / DiveAlt(俯冲) / Waggle(挑衅摇摆) / PreferRear(咬尾)
    // Id 同时用作呼号前缀（游戏 TMP 字体没有中文字形，所以必须是纯 ASCII）。
    // =====================================================================
    public class AiPersonality
    {
        public string Id;                  // 代号 / 呼号前缀
        public string Desc = "";           // 中文说明（只进日志，不进游戏 UI）
        public float Speed = 260f;         // 巡航速度 m/s
        public float Alt = 900f;           // 作战高度 m
        public float TurnGain = 0.055f;    // 航向误差 -> 坡度增益（机动性）
        public float MaxBank = 1f;         // 坡度上限
        public float PitchLimit = 18f;     // 俯仰角上限（度）——爬升/俯冲型要更大
        public float ClimbFirst;           // >0 = 先爬到作战高度再交战
        public float DiveAlt;              // >0 = 拉到该高度后向目标俯冲（超高空俯冲型）
        public float Standoff;             // >0 = 保持这个距离打（超视距型）
        public float CrankAngle;           // 超出 Standoff 时的侧向曲射角（度），需配大雷达锥
        public float MinRange = 90f;       // 不允许近于该距离（避免撞机 / 禁止近战）
        public float RadarRange = 9000f;
        public float RadarCone = 35f;
        public float FireFrac = 0.75f;     // 开火距离 = RadarRange * FireFrac
        public float Waggle;               // 挑衅摇摆幅度（0 = 不摇）
        public float FireInterval = 9f;    // 两次发射最小间隔
        public bool PreferRear;            // 喜欢从目标尾后接近（咬尾）
        public int Salvo = 1;              // 一次交战连射几发（2026-09-15 强化：超视距型齐射 2 发）
    }

    /// <summary>内置性格表。想加减/调参只要改这里，或者用 aam_config.json 的 aiPersonalities 开关。</summary>
    public static class AiPersonalities
    {
        public static readonly AiPersonality[] All = new AiPersonality[]
        {
            // 高速机动巡航：快、灵活、中低空，追着打
            new AiPersonality { Id = "VIPER", Desc = "高速机动巡航",
                Speed = 340f, Alt = 850f, TurnGain = 0.075f, PitchLimit = 24f,
                RadarRange = 14000f, RadarCone = 55f, FireFrac = 0.85f, FireInterval = 6f, Salvo = 2 },

            // 爬坡后超高空俯冲锁定：先爬到作战高度待命，发现目标后一压机头扎下去
            new AiPersonality { Id = "FALCON", Desc = "爬升后超高空俯冲锁定",
                Speed = 300f, Alt = 3000f, TurnGain = 0.050f, MaxBank = 0.95f, PitchLimit = 34f,
                ClimbFirst = 1f, DiveAlt = 3000f,
                RadarRange = 18000f, RadarCone = 45f, FireFrac = 0.82f, FireInterval = 7f, Salvo = 2 },

            // 近距离挑衅：贴脸绕圈、摇机翼，近战为主（但雷达/火控也放大到能打中距）
            new AiPersonality { Id = "BRAWLER", Desc = "近距离挑衅缠斗",
                Speed = 265f, Alt = 600f, TurnGain = 0.110f, PitchLimit = 26f,
                Standoff = 0f, MinRange = 110f, Waggle = 0.55f,
                RadarRange = 5000f, RadarCone = 65f, FireFrac = 0.35f, FireInterval = 9f, Salvo = 1 },

            // 低机动超视距锁定击杀：不缠斗，靠曲射保持远距发射
            new AiPersonality { Id = "OVERWATCH", Desc = "低机动超视距锁定击杀",
                Speed = 250f, Alt = 3800f, TurnGain = 0.022f, MaxBank = 0.42f, PitchLimit = 16f,
                Standoff = 6500f, CrankAngle = 55f, MinRange = 2600f,
                RadarRange = 22000f, RadarCone = 70f, FireFrac = 0.80f, FireInterval = 7f, Salvo = 2 },

            // 低空巡航：贴着地皮飞，慢悠悠
            new AiPersonality { Id = "LOWRIDER", Desc = "低空巡航",
                Speed = 215f, Alt = 165f, TurnGain = 0.045f, MaxBank = 0.8f,
                RadarRange = 11000f, RadarCone = 50f, FireFrac = 0.80f, FireInterval = 8f, Salvo = 1 },

            // 中空巡逻截击：四平八稳的"正常机"
            new AiPersonality { Id = "SENTINEL", Desc = "中空巡逻截击",
                Speed = 275f, Alt = 1600f, TurnGain = 0.050f, MaxBank = 0.9f,
                RadarRange = 15000f, RadarCone = 50f, FireFrac = 0.85f, FireInterval = 7f, Salvo = 2 },

            // 低空潜入、尾后偷袭：绕到玩家屁股后面再咬
            new AiPersonality { Id = "GHOST", Desc = "低空潜入、尾后偷袭",
                Speed = 290f, Alt = 300f, TurnGain = 0.065f, PitchLimit = 24f, PreferRear = true,
                RadarRange = 12000f, RadarCone = 45f, FireFrac = 0.70f, FireInterval = 7f, Salvo = 1 },

            // 高速掠袭、一击脱离：全程高速平飞，中距离开火后绝不贴脸（MinRange 350m）
            new AiPersonality { Id = "RAIDER", Desc = "高速掠袭一击脱离",
                Speed = 345f, Alt = 430f, TurnGain = 0.085f, MaxBank = 0.95f, PitchLimit = 26f,
                MinRange = 350f,
                RadarRange = 11000f, RadarCone = 55f, FireFrac = 0.55f, FireInterval = 10f, Salvo = 1 },

            // 垂直机动、剪刀缠斗：靠大俯仰权限上下翻飞抢高度，中低空近战
            new AiPersonality { Id = "SCREAMER", Desc = "垂直机动剪刀缠斗",
                Speed = 300f, Alt = 1250f, TurnGain = 0.115f, PitchLimit = 32f,
                Waggle = 0.20f, MinRange = 150f,
                RadarRange = 6500f, RadarCone = 60f, FireFrac = 0.50f, FireInterval = 9f, Salvo = 1 },
        };

        public static AiPersonality ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Id, id, System.StringComparison.OrdinalIgnoreCase)) return All[i];
            return null;
        }

        /// <summary>从启用列表里随机抽一个（列表为空或全部非法时退回"全部性格"）。</summary>
        public static AiPersonality Pick(string[] enabled)
        {
            List<AiPersonality> pool = new List<AiPersonality>();
            if (enabled != null)
            {
                for (int i = 0; i < enabled.Length; i++)
                {
                    AiPersonality p = ById(enabled[i]);
                    if (p != null && !pool.Contains(p)) pool.Add(p);
                }
            }
            if (pool.Count == 0) pool.AddRange(All);
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        public static string IdsCsv()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < All.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(All[i].Id);
            }
            return sb.ToString();
        }
    }

    // =====================================================================
    // AI 飞机（靶机 / 敌机）—— 直接接游戏原版飞机物理
    // =====================================================================
    // 和导弹完全不同的思路：这里**不自己写空气动力学**。
    //   部件树用游戏自己的部件预制体从 .planedesign 还原（Wing / Engine / Fuselage ...），
    //   根上挂游戏自己的 PlaneContainer + PlaneController，再调 PlaneContainer.ActivateFlyMode()
    //   让游戏自己算质量、重心、惯量张量、机翼升力、引擎推力、气动阻尼。
    //   于是"玩家怎么飞，AI 就怎么飞" —— 同一套物理、同一套操控手感，没有任何简化。
    //   这既是靶机的意义（打的是真飞机），也是未来所有 AI 驾驶飞机的基座。
    public class AiAircraft : MonoBehaviour
    {
        // ---- 由生成器注入 ----
        public PlaneContainer Container;
        public PlaneController Controller;
        public Rigidbody Rb;
        public InputManager Input;

        // ---- 角色与飞行参数 ----
        public bool Hostile;                       // true = 敌机：会用火控雷达锁定并攻击玩家
        public int FactionId = -1;                 // 阵营系统归属（-1 = 旧系统，按 Hostile 判定）
        public string Callsign = "BANDIT";
        public string ModelName = "";              // 模型名（机型，如 F-22 / J-20，来自 planedesign 文件名）
        public float CruiseSpeed = 130f;           // 目标巡航速度 m/s
        public float CruiseAlt = 400f;             // 目标巡航高度
        public float TurnGain = 0.030f;            // 航向误差 -> 坡度 的增益
        public float MaxBankMix = 1f;
        // 【2026-09-15 强化】默认值上调：外部生成器（FactionSystem 等）刷出来的 AI 没人格，
        // 一直吃这几个默认值 —— 旧的 9km/35°/0.75 就是玩家感觉到的"敌机又瞎又只敢近战"。
        public float RadarRange = 13000f;          // 火控雷达作用距离
        public float RadarCone = 50f;              // 雷达锥半角（度）
        public float LockDelay = 2.5f;              // 稳定跟踪多久算锁定
        public float FireInterval = 7f;             // 两次发射的最小间隔

        // ---- 巡逻（防止"远走高飞"消失在世界里） ----
        public Vector3 HomePos;                    // 出生点（生成器注入）
        public float PatrolRadius = 6000f;         // 距出生点超过该值则掉头回航
        public float RallyRadius = 4000f;          // 友军会合半径：进入该范围则盘旋待命（留在玩家作战空域）

        // ---- 燃油规划（返航加油） ----
        public bool FuelPlanEnabled = true;
        public float RefuelRatio = 0.25f;          // 燃油低于该比例返航
        private bool _returning;
        private float _refuelTimer;
        private float _patrolAlt = 400f;           // 平时巡航高度（可被返航临时压低）

        // ---- 性格（人格，2026-09-12）----
        public AiPersonality Persona;              // null = 用默认"平飞"行为
        public float PitchLimitDeg = 18f;          // 俯仰角上限（度）
        public float PitchGain = 0.07f;            // 俯仰误差 -> 杆量
        public float StandoffRange;                // >0：保持这个距离（超视距型）
        public float CrankAngleDeg;                // 曲射侧偏角（度）
        public float MinRange = 90f;               // 不允许近于该距离
        public float WaggleAmp;                    // 挑衅摇摆幅度
        public float FireRangeFrac = 0.85f;        // 开火距离 = RadarRange * 该系数
        public bool PreferRear;                    // 咬尾
        public int SalvoSize = 2;                  // 一次交战连射几发（2026-09-15 强化）
        public float SalvoGap = 0.9f;              // 齐射间隔（s）
        // ---- 自动人格（2026-09-15）：外部生成器不传人格，这里给它随机抽一个 ----
        public static bool AutoPersona = true;
        public static float AutoPersonaSpeedScale = 1f;
        private bool _personaAutoInit;
        private int _diveStage = -1;               // -1=无俯冲 0=爬升 1=待命 2=俯冲 3=拉起
        private float _stageT;
        private float _waggleCmd;
        private float _personaLogT;

        // ---- 导弹规避（2026-09-13）----
        //   AI 飞机原本只会攻击机动，来袭导弹一律硬吃。这里加一套"看到导弹就躲"的行为：
        //   扫描在飞导弹 -> 判断哪一枚会打到自己 -> 做 beam 机动（把导弹放在侧向 3/9 线，
        //   逼它提前量转弯、掉能量）+ 俯冲换速度，规避期间满油门并放弃攻击。
        //   参数是全局的（所有 AI 共用），由 aam_config.json 的 evade* 字段填充。
        public static class EvadeCfg
        {
            public static bool Enabled = true;
            // 【2026-09-13 实测调整】原来 2600m/11s：对 2 马赫的弹只有 ~6s 预警，
            // 而 AI 的转弯速率实测约 4.3°/s，6s 只能掰出 25 度，根本压不出 3/9 线，
            // 结果"规避动作做了、弹还是照吃"。放宽到 6000m/16s，预警时间翻倍以上，
            // 让 beam 机动有机会在弹到达前真正建立起来。
            public static float Range = 6000f;      // 探测距离（m），超过就不理会
            public static float TTC = 16f;          // 预计最近接近时间小于该值才算威胁（s）
            public static float MissDist = 220f;    // 预计最近接近距离小于该值才算威胁（m）
            public static float Hold = 1.6f;        // 威胁消失后继续规避的时长（s）
            public static float Scan = 0.15f;       // 扫描间隔（s）
            public static float BeamMix = 0.85f;    // 0=纯背向逃 1=纯 beam（垂直导弹来向）
            public static float DropAlt = 600f;     // 规避时把高度目标压低多少（m）
            public static float Agl = 300f;         // 规避俯冲至少保留的离地高度（m）
        }

        /// <summary>在指定程序集中查找类型；找不到时扫描所有已加载程序集。</summary>
        private static Type FindTypeIn(string asmName, string typeName)
        {
            try
            {
                Type t = Type.GetType(typeName + ", " + asmName);
                if (t != null) return t;
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try
                    {
                        Type[] ts = asms[i].GetTypes();
                        for (int j = 0; j < ts.Length; j++)
                        {
                            if (ts[j].FullName == typeName) return ts[j];
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>获取指定阵营的主岛基地坐标；阵营系统不可用或越界时返回 Vector3.zero。</summary>
        private static Vector3 FactionHomePos(int factionId)
        {
            if (factionId < 0) return Vector3.zero;
            try
            {
                Type ft = FindTypeIn("FactionSystem", "Machine.Faction.FactionApi");
                if (ft == null) return Vector3.zero;
                FieldInfo sysField = ft.GetField("_sys", BindingFlags.NonPublic | BindingFlags.Static);
                if (sysField == null) return Vector3.zero;
                object sys = sysField.GetValue(null);
                if (sys == null) return Vector3.zero;
                PropertyInfo factionsProp = sys.GetType().GetProperty("Factions", BindingFlags.Public | BindingFlags.Instance);
                if (factionsProp == null) return Vector3.zero;
                System.Collections.IList factions = factionsProp.GetValue(sys, null) as System.Collections.IList;
                if (factions == null || factionId >= factions.Count) return Vector3.zero;
                object fd = factions[factionId];
                if (fd == null) return Vector3.zero;
                FieldInfo homeField = fd.GetType().GetField("HomePos", BindingFlags.Public | BindingFlags.Instance);
                if (homeField == null) return Vector3.zero;
                return (Vector3)homeField.GetValue(fd);
            }
            catch { }
            return Vector3.zero;
        }

        private bool _evading;                     // 本帧是否处于规避（Update 据此跳过火控/放开油门）
        /// <summary>自测诊断：本帧是否处于规避（见 aiCombatTest 的 counterEvade 检验）。</summary>
        public bool EvadingNow { get { return _evading; } }
        private float _evadeScanT, _evadeHoldT, _evadeLogT;
        private Vector3 _evadeLos;                 // 规避时锁定的导弹来向（我 -> 导弹，已水平化）
        private int _evadeDir = 1;                 // beam 往哪一侧转（+1 右 / -1 左）
        private bool _evadeAltSaved;
        private float _evadeSavedAlt;              // 进入规避前的巡航高度目标（退出时还原）
        private string _evadeWho = "";             // 正在躲哪枚弹（诊断）
        private float _evadeDist;                  // 诊断：发现威胁时的弹目距离（m）
        private float _evadeTcpa;                  // 诊断：发现威胁时的预计最近接近时间（s）
        private float _evadeStartT = -1f;          // 诊断：本次规避起始时刻（用来算持续了多久）
        private static readonly IObstacleProbe SharedAirProbe = new UnityObstacleProbe();

        // ---- 运行时 ----
        private static FieldInfo _fCtrlInput;
        private static bool _ctrlInputBound;
        private float _life;
        private float _logTimer;
        private bool _everAirborne;
        private float _stuckTime;

        // ---- 性能优化：决策低频更新（0.2s一拍），输入每帧写入 ----
        private float _decisionT;
        private const float DecisionInterval = 0.2f;   // 5Hz决策，足够AI响应又省CPU
        private Vector3 _cachedWantDir;
        private float _cachedPitchInput;
        private float _cachedRollInput;
        private float _cachedThrottle;
        private bool _decisionValid;

        // 火控状态（敌机子类要用）
        protected float LockProgress;
        protected float FireCooldown;

        private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // =================================================================
        // 性格注入
        // =================================================================
        /// <summary>把性格参数灌进这架飞机（生成器在 Spawn 之后调用）。</summary>
        public void ApplyPersonality(AiPersonality p)
        {
            if (p == null) return;
            Persona = p;
            CruiseSpeed = p.Speed;
            CruiseAlt = p.Alt;
            _patrolAlt = p.Alt;
            TurnGain = p.TurnGain;
            MaxBankMix = p.MaxBank;
            PitchLimitDeg = p.PitchLimit;
            // 俯仰增益刻意压低：0.10 时"20 度误差"就满舵，出生瞬间直接把机头拉爆、速度掉光失速。
            // 0.045 让中等误差不至于满舵，同时大误差仍能到满舵（34 度 * 0.045 = 1.5 > 1）。
            PitchGain = 0.045f;
            StandoffRange = p.Standoff;
            CrankAngleDeg = p.CrankAngle;
            MinRange = p.MinRange;
            WaggleAmp = p.Waggle;
            FireRangeFrac = p.FireFrac;
            PreferRear = p.PreferRear;
            RadarRange = p.RadarRange;
            RadarCone = p.RadarCone;
            FireInterval = p.FireInterval;
            SalvoSize = Mathf.Max(1, p.Salvo);
            _diveStage = (p.DiveAlt > 0f) ? 0 : -1;
        }

        /// <summary>
        /// 当前交战目标（子类可覆写）。基类默认盯着玩家 —— 这样"非敌对"的性格机
        /// 也会执行自己的性格飞行（俯冲 / 曲射拉距 / 挑衅摇翼），只是不开火
        /// （开火只发生在 CombatTick 里，而它只有 Hostile 才被调用）。
        /// </summary>
        protected virtual Transform CurrentAimTarget() { return PlayerTransformOrNull(); }

        /// <summary>玩家飞机 Transform（取不到返回 null，性格飞行会降级为纯平飞）。</summary>
        internal static Transform PlayerTransformOrNull()
        {
            try
            {
                PlaneContainer pc = PlaneContainer.Instance;
                return (pc != null) ? pc.transform : null;
            }
            catch { return null; }
        }

        /// <summary>瞄准点：咬尾型瞄目标尾后 600m（从背后接近），其余瞄目标本体。</summary>
        protected Vector3 AimPoint(Transform tgt)
        {
            if (tgt == null) return transform.position + transform.forward * 200f;
            if (PreferRear)
            {
                Vector3 back = tgt.forward;
                if (back.sqrMagnitude < 0.01f) back = tgt.up;
                return tgt.position - back.normalized * 600f;
            }
            return tgt.position;
        }

        /// <summary>
        /// 俯冲安全地板：低于这个高度就必须终止俯冲、拉起。
        /// 取三者最大值：自身作战高度的 1/3、目标高度上方 350m、绝对地板 650m。
        /// 之前没有这道闸（只有"俯冲 16s"计时），目标贴地时飞机就会一路扎进地里。
        /// </summary>
        private float DiveAbortAlt(Transform tgt)
        {
            // 【2026-09-12 修正】原来 0.34 倍俯冲起始高度（FALCON = 1020m）压得太低：
            // 实测俯冲到底后速度只有 300m/s 上下，改出拉升再吃掉 100+ m/s，
            // 直接掉出"功率曲线"变成慢速下坠的失速泥浆态（一路沉到 14m 靠地形保护捞回来）。
            // 抬到 0.55 倍 + 最低 900m，让它带着 1300m+ 的高度余量改出。
            float ty = (tgt != null) ? tgt.position.y : 0f;
            float rel = (Persona != null && Persona.DiveAlt > 0f) ? Persona.DiveAlt * 0.55f : 1100f;
            return Mathf.Max(900f, Mathf.Max(rel, ty + 500f));
        }

        /// <summary>诊断：读第一个引擎的实际 currentThrottle（0=引擎完全没吃到油门）。</summary>
        private Engine _eng0;
        /// <summary>诊断：燃油剩余比例（0~1）。</summary>
        private float FuelPercent()
        {
            try
            {
                if (Container == null || Container.fuelCapacity <= 0f) return -1f;
                return Container.fuel / Container.fuelCapacity;
            }
            catch { return -1f; }
        }

        private float EngineThrottle()
        {
            try
            {
                if (_eng0 == null)
                {
                    if (Container == null) return -1f;
                    Engine[] es = Container.GetComponentsInChildren<Engine>(true);
                    _eng0 = (es != null && es.Length > 0) ? es[0] : null;
                }
                return (_eng0 != null) ? _eng0.currentThrottle : -1f;
            }
            catch { return -1f; }
        }

        // =================================================================
        // 引擎油门必须自己喂 —— AI 飞不起来的真正原因
        // =================================================================
        // 反编译 Engine.UpdatePart(PlaneContainer) 得到的真实逻辑：
        //     input = throttleInputOverride.ReadValue<float>();
        //     currentThrottle += input * Time.deltaTime * controller.throttleSpeed;
        // 这里的 throttleInputOverride 是部件预制体上的 **玩家输入 InputAction**，
        // 它直连玩家设备，和我们写 InputManager.throttleInput 完全是两条路。
        // 结果：AI 的引擎只有"玩家恰好推了油门"时才有推力 —— 实测 engThr=0.00，
        // 于是 AI 只能靠出生初速滑翔，越滑越慢、最后失速栽地（时好时坏全是这个原因）。
        // 推力 = thrust * currentThrottle * clamp01(1-thrustHandicap) * maxSpeed.Evaluate(空速)，
        // 所以直接写 currentThrottle 就能完整绕开它对玩家输入的依赖。
        // =================================================================
        private Engine[] _engines;
        private static FieldInfo _engThrField;
        private static bool _engThrProbed;
        private float _fuelTopT;   // 补油计时：油耗是秒级的，不必每帧读写

        // currentThrottle 的属性 setter 是 private（C# 侧只读），只能走 backing field。
        private static FieldInfo EngThrottleField()
        {
            if (!_engThrProbed)
            {
                _engThrProbed = true;
                try { _engThrField = typeof(Engine).GetField("<currentThrottle>k__BackingField", BF); }
                catch { _engThrField = null; }
            }
            return _engThrField;
        }

        private void SetEngineThrottle(float t)
        {
            try
            {
                if (_engines == null || _engines.Length == 0)
                {
                    if (Container == null) return;
                    _engines = Container.GetComponentsInChildren<Engine>(true);
                    if (_engines == null) _engines = new Engine[0];
                    // 顺便补齐 container.engines（原版由 ResetPlane 填）
                    try
                    {
                        FieldInfo fe = typeof(PlaneContainer).GetField("engines", BF);
                        if (fe != null) fe.SetValue(Container, _engines);
                    }
                    catch { }
                }
                // 必须每帧覆盖，不能"值没变就跳过"：
                // Engine.UpdatePart 每帧执行 currentThrottle += 玩家输入 × dt × throttleSpeed，
                // 而玩家输入是全局 InputAction —— 玩家一推油门，所有 AI 引擎的 currentThrottle
                // 都会被带着漂移。这里的每帧写入就是为了持续压住那个漂移（AI 推力全靠它）。
                FieldInfo f = EngThrottleField();
                if (f != null)
                {
                    for (int i = 0; i < _engines.Length; i++)
                    {
                        Engine e = _engines[i];
                        if (e == null) continue;
                        f.SetValue(e, t);
                    }
                }

                // 燃油：AI 无人机不该像玩家那样被油量卡死。
                // 实测 FALCON 前两次俯冲速度正常（593/600m/s），第三次只有 374 —— 因为它一直
                // 满油门爬升+俯冲，两分钟后油量见底，推不出速度就开始"俯冲失速"。
                // AI 机由本 mod 生成、没有玩家加油的途径，这里直接维持满油。
                // 油耗是秒级的，没必要每帧读属性再写回 —— 降到 1s 一次。
                _fuelTopT -= Time.deltaTime;
                if (_fuelTopT > 0f) return;
                _fuelTopT = 1f;
                try
                {
                    if (Container.fuelCapacity > 0f)
                    {
                        float need = Container.fuelCapacity * 0.90f;
                        if (Container.fuel < need) Container.fuel = need;
                    }
                }
                catch { }
            }
            catch { }
        }

        private void PersonaLog(string what)
        {
            if (_personaLogT > 0f && Time.time - _personaLogT < 1f) return;
            _personaLogT = Time.time;
            Machine.Core.Log.Info("[AAM] " + Callsign + " [" + (Persona != null ? Persona.Id : "?") + "] " + what);
        }

        /// <summary>
        /// 性格驱动的飞行决策：爬升待命 / 超高空俯冲 / 超视距曲射保持距离 /
        /// 近距挑衅掠过 / 咬尾。可改写 wantDir 与 throttle（高度目标改写 CruiseAlt）。
        /// </summary>
        private void PersonaFlight(ref Vector3 wantDir, ref float throttle, Vector3 vel, float spd)
        {
            _waggleCmd = 0f;
            if (Persona == null) return;
            Transform tgt = CurrentAimTarget();
            float dist = (tgt != null) ? Vector3.Distance(transform.position, tgt.position) : float.MaxValue;

            // ---- (a) "爬坡后超高空俯冲"型：先爬升 -> 待命 -> 俯冲 -> 拉起 ----
            if (_diveStage >= 0)
            {
                if (_diveStage == 0)
                {
                    CruiseAlt = Persona.DiveAlt;      // 重新爬回高空待命高度（脱离后回到本状态也要能爬回去）
                    throttle = 1f;
                    if (transform.position.y >= Persona.DiveAlt - 250f)
                    {
                        _diveStage = 1; _stageT = 0f;
                        PersonaLog("climb done at " + transform.position.y.ToString("F0") + "m, on station");
                    }
                }
                else if (_diveStage == 1)
                {
                    // 只有"目标明显比自己低"且"自己速度够"才俯冲（没速度冲下去就拉不起来了）。
                    bool hasAdvantage = (tgt != null) && (tgt.position.y < transform.position.y - 400f)
                                        && spd > Persona.Speed * 0.90f;
                    if (hasAdvantage && dist < Persona.RadarRange)
                    {
                        _diveStage = 2; _stageT = 0f;
                        PersonaLog("dive attack, target " + dist.ToString("F0") + "m, advantage "
                            + (transform.position.y - tgt.position.y).ToString("F0") + "m");
                    }
                }
                else if (_diveStage == 2)
                {
                    _stageT += Time.deltaTime;
                    float floor = DiveAbortAlt(tgt);
                    if (tgt != null)
                    {
                        Vector3 aim = AimPoint(tgt);
                        Vector3 to = aim - transform.position;
                        if (to.sqrMagnitude > 1f)
                        {
                            wantDir = to.normalized;                     // 机头压向目标 = 俯冲
                            CruiseAlt = Mathf.Max(aim.y, floor);         // 高度目标留安全余量，别压到地板以下
                        }
                        throttle = 1f;
                    }
                    // 终止条件：计时到 / 已压到安全地板 / 速度掉到 0.86 倍巡航速度。
                    // 最后一条很关键：引擎在低速几乎推不出力，俯冲把速度榨干就再也拉不起来了。
                    // 0.80 太晚（实测第三次俯冲 240m/s 才脱离，拉起后掉到 151m/s 贴地）；
                    // 0.86 让它带着余量脱离，拉起再耗一点也还有能量。
                    bool tooSlow = spd < Persona.Speed * 0.90f;
                    if (_stageT > 12f || transform.position.y < floor || tooSlow)
                    {
                        _diveStage = 3; _stageT = 0f;
                        PersonaLog("dive break-off at " + transform.position.y.ToString("F0")
                                   + "m spd=" + spd.ToString("F0"));
                    }
                }
                else if (_diveStage == 3)
                {
                    _stageT += Time.deltaTime;
                    // 拉起脱离：先**平飞养速度**，再回 0 阶段重新爬升。
                    // 实测"俯冲改出后立刻大角度爬升"是最费速度的：FALCON 从 609m/s 改出，
                    // 3 秒内掉到 330m/s，然后一路掉进低速泥浆态再也爬不出来。
                    // 所以这里只要求保持在当前高度（略高于俯冲地板），等速度回来再爬。
                    CruiseAlt = Mathf.Max(transform.position.y, DiveAbortAlt(tgt) + 100f);
                    throttle = 1f;
                    // 拉起阶段：航向指回水平前方（把机头拉平再爬），不要再盯着目标压机头
                    Vector3 flatF = transform.forward; flatF.y = 0f;
                    if (flatF.sqrMagnitude > 0.01f) wantDir = flatF.normalized;
                    if (_stageT > 6f && spd > Persona.Speed)
                    {
                        _diveStage = 0; _stageT = 0f;
                        PersonaLog("extended, climbing back at " + transform.position.y.ToString("F0")
                                   + "m spd=" + spd.ToString("F0"));
                    }
                }
            }

            if (tgt == null) return;

            // ---- (b) 超视距型：进了 Standoff 就曲射（crank）拖开，又不丢雷达接触 ----
            if (Persona.Standoff > 0f && dist < Persona.Standoff)
            {
                Vector3 away = transform.position - tgt.position;
                away.y = 0f;
                if (away.sqrMagnitude > 1f)
                {
                    float side = (Mathf.Sin(Time.time * 0.13f) >= 0f) ? 1f : -1f;
                    float ang = Mathf.Max(20f, Persona.CrankAngle);
                    wantDir = Quaternion.AngleAxis(side * ang, Vector3.up) * away.normalized;
                    throttle = 1f;
                }
            }
            // ---- (c) 近距型：逼近到 MinRange 就侧身掠过，别撞上 ----
            else if (Persona.Standoff <= 0f && dist < MinRange)
            {
                Vector3 away = (transform.position - tgt.position).normalized;
                Vector3 lat = Vector3.Cross(Vector3.up, away);
                wantDir = (away + lat * 0.7f).normalized;
                throttle = Mathf.Max(throttle, 0.85f);
            }

            // ---- (d) 挑衅：贴近时左右摇机翼 ----
            if (Persona.Waggle > 0f && dist < 1200f)
                _waggleCmd = Mathf.Sin(Time.time * 3.4f) * Persona.Waggle;
        }

        // =================================================================
        // 导弹规避
        // =================================================================
        /// <summary>
        /// 扫描在飞导弹，挑出最紧迫的一枚，刷新规避保持计时。
        /// 判据不是"有没有导弹锁我"，而是"它会不会打到我" —— 用最近接近点算：
        ///   tcpa = 预计最近接近时间，miss = 最近接近点上的距离。
        /// 只看锁定会导致"导弹已经打偏飞过身边也不躲"，只看距离会"远在天边就乱躲"。
        /// </summary>
        private void ScanThreats(Vector3 vel)
        {
            List<MissileController> all = MissileController.RegistrySnapshot();
            float best = 0f;
            float bestTcpa = 0f;
            MissileController bestMsl = null;

            for (int i = 0; i < all.Count; i++)
            {
                MissileController m = all[i];
                if (m == null) continue;
                if (m.InterceptOnly) continue;              // 拦截弹（MSDM）只打导弹，不威胁飞机

                Vector3 mp = m.Body.Pos;
                Vector3 mv = m.Body.Vel;
                Vector3 rel = transform.position - mp;       // 导弹 -> 我
                float d = rel.magnitude;
                if (d > EvadeCfg.Range || d < 1f) continue;

                Vector3 rv = vel - mv;                       // 相对速度（我相对导弹）
                float rv2 = rv.sqrMagnitude;
                if (rv2 < 1e-4f) continue;
                float tcpa = -Vector3.Dot(rel, rv) / rv2;    // >0 = 正在接近
                if (tcpa < 0f || tcpa > EvadeCfg.TTC) continue;

                float closing = -Vector3.Dot(rel.normalized, rv);
                if (closing < 5f) continue;                  // 没在接近（导弹掉头了/我甩开了）

                Vector3 missVec = rel + rv * tcpa;           // 最近接近点上的相对位置
                float miss = missVec.magnitude;

                bool lockedOnMe = (m.Target != null) && m.Target.Valid && (m.Target.T == transform);
                if (!lockedOnMe && miss > EvadeCfg.MissDist) continue;   // 没锁我、也擦不到 -> 不管

                // 越紧迫越急着躲：时间越短越危险；会擦到/被锁定再给一个下限
                float urgency = Mathf.Clamp01(1f - tcpa / Mathf.Max(0.1f, EvadeCfg.TTC));
                if (miss < EvadeCfg.MissDist) urgency = Mathf.Max(urgency, 0.6f);
                if (lockedOnMe) urgency = Mathf.Max(urgency, 0.5f);
                if (urgency > best) { best = urgency; bestMsl = m; bestTcpa = tcpa; }
            }

            if (bestMsl == null) return;

            Vector3 los = bestMsl.Body.Pos - transform.position;   // 我 -> 导弹
            los.y = 0f;
            _evadeLos = (los.sqrMagnitude > 1f) ? los.normalized : transform.forward;

            // beam 往哪一侧转：优先选朝向基地的一侧，但如果离机头太远（>90度）则选离机头更近的
            // 【2026-09-16 修改】加入基地倾向，防止规避时越跑越远
            Vector3 fwdFlat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            Vector3 perp = Vector3.Cross(Vector3.up, _evadeLos);
            Vector3 perpRight = perp.normalized;
            Vector3 perpLeft = -perp.normalized;

            // 默认选离机头更近的一侧
            int dirByNose = (Vector3.Dot(perp, fwdFlat.normalized) >= 0f) ? 1 : -1;

            // 计算哪一侧更朝向基地
            int dirByHome = dirByNose;
            Vector3 homePos = FactionHomePos(FactionId);
            if (homePos.sqrMagnitude > 1f && fwdFlat.sqrMagnitude > 0.01f)
            {
                Vector3 toHome = homePos - transform.position;
                toHome.y = 0f;
                if (toHome.sqrMagnitude > 100f)
                {
                    toHome.Normalize();
                    float dotRight = Vector3.Dot(perpRight, toHome);
                    float dotLeft = Vector3.Dot(perpLeft, toHome);
                    dirByHome = (dotRight >= dotLeft) ? 1 : -1;
                }
            }

            // 决策：如果朝向基地的一侧离机头不太远（<100度），选基地侧；否则选机头侧
            if (fwdFlat.sqrMagnitude > 0.01f)
            {
                Vector3 chosenDir = (dirByHome == 1) ? perpRight : perpLeft;
                float angleToNose = Vector3.Angle(chosenDir, fwdFlat.normalized);
                _evadeDir = (angleToNose <= 100f) ? dirByHome : dirByNose;
            }
            else
            {
                _evadeDir = dirByNose;
            }

            _evadeWho = string.IsNullOrEmpty(bestMsl.SpecName) ? "MSL" : bestMsl.SpecName;
            _evadeDist = Vector3.Distance(bestMsl.Body.Pos, transform.position);
            _evadeTcpa = bestTcpa;
            _evadeHoldT = Mathf.Max(_evadeHoldT, EvadeCfg.Hold);
            _evading = true;
        }

        /// <summary>规避俯冲的地板高度（世界 y）：至少留 EvadeCfg.Agl 的离地余量。</summary>
        private float EvadeFloorY()
        {
            float agl = -1f;
            try
            {
                float hit; Vector3 nrm;
                if (SharedAirProbe.Cast(transform.position, Vector3.down, 4000f, out hit, out nrm)) agl = hit;
            }
            catch { }
            // 探针拿不到地形时（远离玩家的区块没有碰撞体）保守退化成绝对高度
            float floorY = (agl > 0f) ? (transform.position.y - agl + EvadeCfg.Agl) : EvadeCfg.Agl;
            return Mathf.Max(floorY, 130f);
        }

        /// <summary>结束规避：还原被压低的巡航高度目标。</summary>
        private void EndEvade()
        {
            if (_evadeAltSaved) { CruiseAlt = _evadeSavedAlt; _evadeAltSaved = false; }
            // 诊断：规避一共持续了多久（只闪一下说明阈值太敏感，全程不停说明门槛太松）
            if (_evading && _evadeStartT > 0f && cfgLogVerbose)
                Machine.Core.Log.Info("[AAM] " + Callsign + " EVADE END " + _evadeWho
                    + " held=" + (Time.time - _evadeStartT).ToString("F1") + "s");
            _evadeStartT = -1f;
            _evading = false;
            _evadeHoldT = 0f;
        }

        /// <summary>
        /// 导弹规避主逻辑。返回 true = 本帧处于规避（调用方据此跳过火控、放开油门限制）。
        /// 输出：wantDir 改成 beam 航向，CruiseAlt 压低（俯冲），throttle 拉满。
        /// </summary>
        private bool MissileEvade(ref Vector3 wantDir, ref float throttle, Vector3 vel, float spd)
        {
            if (!EvadeCfg.Enabled) { if (_evading || _evadeAltSaved) EndEvade(); return false; }

            _evadeScanT -= Time.deltaTime;
            if (_evadeScanT <= 0f)
            {
                _evadeScanT = Mathf.Max(0.05f, EvadeCfg.Scan);
                try { ScanThreats(vel); } catch { }
            }

            // 保持计时：威胁消失后仍要躲一会儿，否则"探针一断就停手"会转到一半停住
            if (_evadeHoldT > 0f) _evadeHoldT -= Time.deltaTime;
            else if (_evading) { EndEvade(); return false; }
            if (!_evading) return false;
            if (_evadeStartT < 0f) _evadeStartT = Time.time;

            Vector3 los = _evadeLos;
            if (los.sqrMagnitude < 0.01f) los = transform.forward;
            los = Vector3.ProjectOnPlane(los, Vector3.up).normalized;
            if (los.sqrMagnitude < 0.01f) los = transform.forward;

            // beam（垂直导弹来向）与背向的混合：纯 beam 逼导弹提前量转弯最费它的能量，
            // 掺一点背向能拉开距离。EvadeBeamMix=1 就是教科书式的 3/9 线机动。
            Vector3 beam = Vector3.Cross(Vector3.up, los).normalized * _evadeDir;
            Vector3 away = -los;
            Vector3 dir = beam * EvadeCfg.BeamMix + away * (1f - EvadeCfg.BeamMix);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = beam;

            // 【2026-09-16 新增】向基地飞的倾向：规避时混入基地方向，防止AI跑到偏远地方
            // 权重 0.25：75% 规避方向 + 25% 基地方向，既保证规避效果又能慢慢往回靠
            Vector3 homePos = FactionHomePos(FactionId);
            if (homePos.sqrMagnitude > 1f)
            {
                Vector3 toHome = homePos - transform.position;
                toHome.y = 0f;
                if (toHome.sqrMagnitude > 100f)  // 离基地超过10m才需要往回飞
                {
                    toHome.Normalize();
                    dir = dir.normalized * 0.75f + toHome * 0.25f;
                }
            }

            wantDir = dir.normalized;

            // 俯冲掉高度：压低高度目标（留离地余量，别规避规避进山里）
            // 【2026-09-13 修正】基准必须用"进入规避前的高度"(_evadeSavedAlt)，不能每帧拿当前
            // CruiseAlt 再减一次 —— 那样 CruiseAlt 会以 DropAlt/帧 的速率一路棘轮到地板，
            // DropAlt 形同虚设（实测 1600m 的 SENTINEL 一帧内就把目标高度从 1000 掉到 300）。
            if (!_evadeAltSaved) { _evadeSavedAlt = CruiseAlt; _evadeAltSaved = true; }
            float lowAlt = Mathf.Max(_evadeSavedAlt - Mathf.Max(0f, EvadeCfg.DropAlt), EvadeFloorY());
            CruiseAlt = Mathf.Min(CruiseAlt, lowAlt);

            throttle = 1f;

            if (cfgLogVerbose)
            {
                _evadeLogT -= Time.deltaTime;
                if (_evadeLogT <= 0f)
                {
                    _evadeLogT = 1.5f;
                    // off = 机头与"我->导弹"方向的夹角：进场时导弹在正后方（≈180），
                    // beam 机动做出来之后应该收敛到 ≈90（导弹被放到 3/9 线上）。
                    Vector3 noseFlat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                    float off = (noseFlat.sqrMagnitude > 0.01f)
                        ? Vector3.Angle(noseFlat, los) : -1f;
                    Machine.Core.Log.Info("[AAM] " + Callsign + " EVADE " + _evadeWho
                        + " side=" + _evadeDir
                        + " off=" + off.ToString("F0")
                        + " d=" + _evadeDist.ToString("F0")
                        + " tcpa=" + _evadeTcpa.ToString("F1")
                        + " alt=" + transform.position.y.ToString("F0")
                        + " tgtAlt=" + CruiseAlt.ToString("F0")
                        + " spd=" + spd.ToString("F0"));
                }
            }
            return true;
        }

        /// <summary>
        /// AI 飞机的红外源（与玩家机同一套算法）：
        /// 期望值 = 油门(0~100)×1.2 + 速度×0.1，实际值由 IrSignature.Step 非线性逼近。
        /// </summary>
        private void AircraftIrTick(Vector3 vel, float spd, float dt)
        {
            try
            {
                if (!IrSignature.Enabled) return;
                if (_ir == null || _ir.gameObject != gameObject)
                    _ir = IrSignature.Ensure(gameObject);
                if (_ir == null) return;
                float thr = EngineThrottle();          // 0~1；读不到时返回 -1
                if (thr < 0f) thr = 0f;
                _ir.Vel = vel;
                _ir.Feed(Mathf.Clamp01(thr) * 100f, spd, dt);
            }
            catch { }
        }
        private IrSignature _ir;

        /// <summary>是否在遥测行里带红外实际值/期望值（跟随 AAM 的 irLog 开关）。</summary>
        public static bool AiIrLog = true;

        // ---- 热诱弹自卫（AI 被导弹追时抛饵）----
        public static bool FlareEnabled = true;
        public static int FlareStock = 6;        // 每架 AI 携带量
        public static int FlareBurst = 2;        // 一次抛几枚
        public static float FlareCooldown = 2.5f;// 两次投放之间的冷却
        public static float FlareDelay = 0.6f;   // 开始规避后多久才反应过来抛
        private int _flares = -1;                // -1 = 还没初始化
        private float _flareCd;
        private float _flareGap;
        private int _flarePend;
        private float _evadeFor;

        /// <summary>每帧：抛饵冷却与连抛间隔。</summary>
        private void FlareDefendTick(float dt)
        {
            if (!FlareEnabled) return;
            if (_flares < 0) _flares = FlareStock;
            if (_flareCd > 0f) _flareCd -= dt;
            if (_flarePend > 0)
            {
                _flareGap -= dt;
                if (_flareGap <= 0f)
                {
                    SpawnAiFlare();
                    _flarePend--;
                    _flareGap = 0.12f;
                }
            }
        }

        /// <summary>正在被导弹追（且已经追了一会儿）-> 抛热诱弹。</summary>
        private void TryPopFlares(float dt)
        {
            if (!FlareEnabled || _flares <= 0 || _flareCd > 0f || _flarePend > 0) return;
            _evadeFor += dt;
            if (_evadeFor < FlareDelay) return;     // 给一点反应时间，别一发现就撒
            _flarePend = Mathf.Min(FlareBurst, _flares);
            _flareGap = 0f;
            _flareCd = FlareCooldown;
            Machine.Core.Log.Info("[AAM] " + Callsign + " FLARE dispensing " + _flarePend
                                  + " (stock=" + _flares + ")");
        }

        private void SpawnAiFlare()
        {
            try
            {
                var sys = AamSystem.Live;
                if (sys == null) return;
                GameObject model = sys.BuildFlareModel();
                if (model == null) return;
                Vector3 vel = (Rb != null) ? Rb.linearVelocity : Vector3.zero;
                var fl = IrFlare.Dispense(model, transform, vel);
                if (fl == null) { UnityEngine.Object.Destroy(model); return; }
                _flares--;
                // 注意：AI投放热诱弹不播放声音，只有玩家投放时才播放（避免玩家听到敌机的热诱弹声音）
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] ai flare failed " + e.Message); }
        }

        /// <summary>诊断：本机当前的红外实际值（-1 = 还没挂上红外源）。</summary>
        public float IrActual { get { return (_ir != null) ? _ir.Actual : -1f; } }
        /// <summary>诊断：本机当前的红外期望值（-1 = 还没挂上红外源）。</summary>
        public float IrExpected { get { return (_ir != null) ? _ir.Expected : -1f; } }

        private void Update()
        {
            if (Container == null || Rb == null) return;
            // 纯净模式守卫：原版档不保留 AI 飞机（Mod Saves 进入后会重新生成）
            if (Machine.Mod.MachineState.PureMode) { Despawn(); return; }

            // ---- 自动人格（2026-09-15）----
            // FactionSystem 等外部生成器只调工厂 Spawn、不传人格，那些 AI 会一直用"平飞默认值"。
            // 没人格时这里随机抽一个：雷达距离/锥角、巡航速度、战术动作（俯冲/曲射/咬尾）一起生效。
            if (!_personaAutoInit)
            {
                _personaAutoInit = true;
                if (AutoPersona && Persona == null)
                {
                    ApplyPersonality(AiPersonalities.Pick(null));
                    if (Persona != null)
                    {
                        CruiseSpeed = Persona.Speed * Mathf.Max(0.2f, AutoPersonaSpeedScale);
                        Machine.Core.Log.Info("[AAM] " + Callsign + " auto persona -> " + Persona.Id
                            + " (" + Persona.Desc + ") spd=" + CruiseSpeed.ToString("F0")
                            + " radar=" + RadarRange.ToString("F0") + " cone=" + RadarCone.ToString("F0"));
                    }
                }
            }

            // PlaneController.Start() 会把 inputManager 设成全局单例，这里每帧夺回来。
            if (!_ctrlInputBound)
            {
                _ctrlInputBound = true;
                try { _fCtrlInput = typeof(PlaneController).GetField("inputManager", BF); } catch { }
            }
            if (_fCtrlInput != null && Controller != null)
            {
                try { _fCtrlInput.SetValue(Controller, Input); } catch { }
            }

            _life += Time.deltaTime;

            Vector3 fwd = Container.Forward;
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;
            fwd = fwd.normalized;

            Vector3 up = transform.up;
            Vector3 vel = Rb.linearVelocity;
            float spd = vel.magnitude;

            // ---------- 1.1) 红外特征：期望值 = 油门×1.2 + 速度×0.1，实际值非线性逼近 ----------
            AircraftIrTick(vel, spd, Time.deltaTime);

            // ---------- 性能优化：决策逻辑 0.2s 一拍（5Hz），输入每帧写入 ----------
            _decisionT -= Time.deltaTime;
            if (_decisionT <= 0f || !_decisionValid)
            {
                _decisionT = DecisionInterval;
                RunDecision(fwd, up, vel, spd);
                _decisionValid = true;
            }

            // 被导弹追 -> 抛热诱弹自卫（有反应延迟；威胁解除就复位计时）—— 每帧执行（轻量）
            if (_evading) TryPopFlares(Time.deltaTime);
            else _evadeFor = 0f;
            FlareDefendTick(Time.deltaTime);

            // ---------- 输入写入：每帧执行（使用缓存的决策结果）----------
            AiInputSource.Write(Input, _cachedPitchInput, _cachedRollInput, 0f, _cachedThrottle);
            // 引擎油门得单独喂：Engine 内部读的是玩家输入 InputAction，不认我们的 InputManager。
            SetEngineThrottle(_cachedThrottle);

            // ---------- 诊断日志 ----------
            if (cfgLogVerbose)
            {
                _logTimer -= Time.deltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = 3f;
                    Vector3 eul = transform.eulerAngles;
                    Machine.Core.Log.Info("[AAM] " + Callsign + " t=" + _life.ToString("F1")
                        + "s pos=" + transform.position.ToString("F0")
                        + " spd=" + spd.ToString("F0")
                        + " vy=" + vel.y.ToString("F1")
                        + " aspd=" + Rb.angularVelocity.magnitude.ToString("F2")
                        + " eul=(" + eul.x.ToString("F0") + "," + eul.y.ToString("F0") + "," + eul.z.ToString("F0") + ")"
                        + " pitchIn=" + _cachedPitchInput.ToString("F2")
                        + " rollIn=" + _cachedRollInput.ToString("F2")
                        + " thr=" + _cachedThrottle.ToString("F2")
                        + " engThr=" + EngineThrottle().ToString("F2")
                        + " fuelPct=" + FuelPercent().ToString("F2")
                        + (AiIrLog ? (" ir=" + IrActual.ToString("F1") + "/" + IrExpected.ToString("F1")) : "")
                        + (Hostile ? (" lock=" + LockProgress.ToString("F1") + "/" + LockDelay) : ""));
                }
            }

            // ---------- 掉在地上太久就回收（首 30s 豁免：允许跑道式生成后滑跑起飞） ----------
            if (transform.position.y < 1f && spd < 5f) _stuckTime += Time.deltaTime;
            else _stuckTime = 0f;
            if (spd > 20f) _everAirborne = true;
            // 遁地回收：掉到海面/地面以下很深（钻地）直接回收，避免雷达扫到地底目标。
            // 首 15s 豁免：跑道式生成后需要滑跑/拉起时间，防止起飞瞬间误判
            if (transform.position.y < -15f && _life > 15f)
            {
                Machine.Core.Log.Info("[AAM] " + Callsign + " underground, recovered");
                FeedCrash(ModelName, Callsign, FactionId);
                MissileController.ReportFactionAircraftLost(transform, "crashed");
                Despawn();
                return;
            }
            // 坠地（掉地上 6s 起不来的）算坠毁；600s 超时退役不算坠毁
            if (_stuckTime > 6f && _life > 30f)
            {
                Machine.Core.Log.Info("[AAM] " + Callsign + " crashed, retired");
                FeedCrash(ModelName, Callsign, FactionId);
                MissileController.ReportFactionAircraftLost(transform, "crashed");
                Despawn();
            }
            else if (_life > 600f) { Machine.Core.Log.Info("[AAM] " + Callsign + " retired"); Despawn(); }
        }

        /// <summary>
        /// 决策逻辑：0.2s 一拍（5Hz），计算期望航向、俯仰、滚转、油门、火控。
        /// 结果缓存到 _cached* 变量，输入写入每帧执行（使用缓存值）。
        /// 这样可以将 AI 的 CPU 开销降低约 60-80%。
        /// </summary>
        private void RunDecision(Vector3 fwd, Vector3 up, Vector3 vel, float spd)
        {
            // ---------- 1) 选中一个期望航向 ----------
            Vector3 wantDir = DesiredHeading(fwd, vel, spd);
            float throttle = Mathf.Clamp(0.85f + (CruiseSpeed - spd) * 0.02f, 0.35f, 1f);

            // ---------- 1.2) 性格飞行：爬升待命 / 超高空俯冲 / 距离保持 / 挑衅 ----------
            PersonaFlight(ref wantDir, ref throttle, vel, spd);

            // ---------- 1.5) 燃油规划：低油返航回基地补给（优先于交战/巡逻） ----------
            if (FuelPlanEnabled) FuelPlan(ref wantDir, ref throttle);

            // ---------- 1.7) 导弹规避：最高优先级，压过性格机动与燃油规划 ----------
            _evading = MissileEvade(ref wantDir, ref throttle, vel, spd);
            if (_evading) _waggleCmd = 0f;

            Vector3 myFlat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            Vector3 wantFlat = Vector3.ProjectOnPlane(wantDir, Vector3.up);
            float bankCmd = 0f;
            if (myFlat.sqrMagnitude > 0.01f && wantFlat.sqrMagnitude > 0.01f)
            {
                float turn = Vector3.SignedAngle(myFlat.normalized, wantFlat.normalized, Vector3.up);
                bankCmd = Mathf.Clamp(turn * TurnGain, -MaxBankMix, MaxBankMix);
                if (_diveStage == 2 && !_evading) bankCmd = Mathf.Clamp(bankCmd, -0.22f, 0.22f);
            }

            // ---------- 3) 滚转回正 ----------
            Vector3 myRight = Vector3.Cross(up, fwd).normalized;
            float bankOff = Vector3.Dot(Vector3.up, myRight);
            float levelCmd = Mathf.Clamp(bankOff * 2.2f, -1f, 1f);
            float rollInput = Mathf.Clamp(bankCmd + levelCmd + _waggleCmd, -1f, 1f) * RollSign;

            // ---------- 4) 高度保持 -> 俯仰 ----------
            float altErr = CruiseAlt - transform.position.y;
            Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, Vector3.up);
            float curPitch = 0f;
            if (flatFwd.sqrMagnitude > 0.0001f)
                curPitch = Vector3.Angle(fwd, flatFwd) * Mathf.Sign(Vector3.Dot(fwd, Vector3.up));
            float maxClimbDeg = Mathf.Max(12f, PitchLimitDeg * 0.55f);
            float climbFrac = Mathf.Clamp01((spd - CruiseSpeed * 0.85f) / (CruiseSpeed * 0.40f));
            float cruiseClimbDeg = maxClimbDeg * Mathf.Lerp(0.62f, 1f, climbFrac);
            float wantPitch = Mathf.Clamp(altErr * 0.08f - vel.y * 0.12f, -PitchLimitDeg, cruiseClimbDeg);
            if (_diveStage == 2) wantPitch = Mathf.Max(wantPitch, -24f);

            // 失速/能量保护
            float lowSpd = Mathf.Max(170f, CruiseSpeed * 0.75f);
            if (spd < lowSpd)
            {
                float need = Mathf.Clamp01(1f - spd / lowSpd);
                float altFactor = Mathf.Clamp01((transform.position.y - 200f) / 1000f);
                float downDeg = Mathf.Lerp(0f, 9f, need) * altFactor;
                wantPitch = Mathf.Min(wantPitch, -downDeg);
            }
            float emergencySpd = Mathf.Max(115f, CruiseSpeed * 0.42f);
            if (spd < emergencySpd) wantPitch = -PitchLimitDeg;

            // 地形保护
            float guardAlt = 130f + Mathf.Max(0f, -vel.y) * 2f;
            if (transform.position.y < guardAlt && vel.y < -3f)
            {
                float g = Mathf.Clamp((guardAlt - transform.position.y) * 0.06f, 0f, 1f) * maxClimbDeg;
                if (g > wantPitch) wantPitch = g;
            }

            float pitchInput = Mathf.Clamp((wantPitch - curPitch) * PitchGain, -1f, 1f) * PitchSign;

            // ---------- 5) 油门限速 ----------
            float capSpd = CruiseSpeed * 1.15f;
            bool freeThrottle = (_diveStage == 0 || _diveStage == 2 || _diveStage == 3) || _evading;
            if (!freeThrottle && spd > capSpd) throttle = Mathf.Min(throttle, 0.15f);

            // ---------- 6) 火控 / 交战 ----------
            if (Hostile)
            {
                if (!_evading) CombatTick(ref wantDir, ref pitchInput, ref throttle);
                else CombatFireOnly();
            }

            // 缓存决策结果
            _cachedWantDir = wantDir;
            _cachedPitchInput = pitchInput;
            _cachedRollInput = rollInput;
            _cachedThrottle = throttle;
        }

        // 舵面符号：实机校正过 —— 正的 pitch 输入是**低头**（力矩轴 = cross(-Forward, up) = +X），
        // 所以"抬头"要用负值；正的 roll 输入是右滚。
        public float PitchSign = -1f;
        public float RollSign = 1f;
        public bool cfgLogVerbose = true;   // 诊断状态日志默认开启（每 AI 3s 一行 → 12 个 AI 每秒 4 行，纯 CPU/磁盘开销）

        /// <summary>燃油规划：燃油低于阈值 → 返航回基地 → 低空盘旋模拟降落加油 → 重新起飞巡逻。</summary>
        private void FuelPlan(ref Vector3 wantDir, ref float throttle)
        {
            try
            {
                if (Container == null) return;
                float cap = Container.fuelCapacity;
                if (cap <= 0f) return;
                float ratio = Container.fuel / cap;

                if (!_returning && ratio < RefuelRatio)
                {
                    _returning = true;
                    _patrolAlt = CruiseAlt;
                    Machine.Core.Log.Info("[AAM] " + Callsign + " fuel low (" + ratio.ToString("F2") + ") returning to base");
                }
                if (!_returning) return;

                Vector3 toHome = HomePos - transform.position;
                float dHome = toHome.magnitude;
                wantDir = toHome.sqrMagnitude > 1f ? toHome.normalized : Vector3.zero;
                if (wantDir == Vector3.zero) wantDir = transform.forward;

                if (dHome < 900f)
                {
                    // 已到基地上空：低空盘旋 + 补油
                    _refuelTimer += Time.deltaTime;
                    CruiseAlt = Mathf.Min(CruiseAlt, 90f);
                    throttle = 0.35f;
                    if (_refuelTimer > 8f)
                    {
                        try
                        {
                            MethodInfo m = typeof(PlaneContainer).GetMethod("Refuel", BF);
                            if (m != null) m.Invoke(Container, null);
                        }
                        catch { }
                        CruiseAlt = _patrolAlt;
                        _returning = false;
                        _refuelTimer = 0f;
                        Machine.Core.Log.Info("[AAM] " + Callsign + " refueled, back to patrol");
                    }
                }
                else
                {
                    // 返航途中：保持巡航高度，油门足量
                    throttle = Mathf.Max(throttle, 0.75f);
                }
            }
            catch { }
        }

        /// <summary>
        /// 默认行为：保持当前航向平飞；超出巡逻半径时掉头回出生点，
        /// 避免 AI 飞机"远走高飞"消失在世界里。敌机子类覆写为扑向玩家。
        /// </summary>
        protected virtual Vector3 DesiredHeading(Vector3 fwd, Vector3 vel, float spd)
        {
            // 友军 / 非交战 AI：会合到玩家所在的作战空域，而不是随机直飞消失在偏远海域。
            // （敌对 AI 由 BanditAircraft 覆写，本分支只服务非敌对单位）
            if (!Hostile)
            {
                Transform p = PlayerTransformOrNull();
                if (p != null)
                {
                    Vector3 to = p.position - transform.position;
                    float d = to.magnitude;
                    if (d > RallyRadius) return to.normalized;   // 离玩家太远 -> 飞向玩家会合
                    return fwd;                                   // 已进入会合半径 -> 盘旋待命（边界拉回防止冲出）
                }
                // 玩家机不可用：退回己方基地上空巡逻
                float dh = Vector3.Distance(transform.position, HomePos);
                if (dh > PatrolRadius) return (HomePos - transform.position).normalized;
                return fwd;
            }
            // 敌对兜底（正常走 Bandit 覆写，不会到这）：回基地
            float d2 = Vector3.Distance(transform.position, HomePos);
            if (d2 > PatrolRadius) return (HomePos - transform.position).normalized;
            return fwd;
        }

        /// <summary>火控：雷达搜索 -> 稳定跟踪 -> 锁定 -> 发射导弹。</summary>
        protected virtual void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
        }

        /// <summary>
        /// 规避来袭导弹期间的"反击"：不改航向（beam 机动的航向必须保住），
        /// 只让子类继续维护火控解并开火。基类（无火控）空实现。
        /// </summary>
        protected virtual void CombatFireOnly()
        {
        }

        /// <summary>回收这架 AI 飞机。</summary>
        public void Despawn()
        {
            try
            {
                if (Container != null) TargetRegistry.Unregister(Container.gameObject);
                SilentAi.Unmark(gameObject);
            }
            catch { }
            // AI 的输入源挂在独立对象上，跟着一起回收，否则会一直堆在场景里
            try { if (Input != null) UnityEngine.Object.Destroy(Input.gameObject); } catch { }
            UnityEngine.Object.Destroy(gameObject);
        }

        // ---- 战斗事件表（KillFeed mod）坠毁上报：KillFeed 未加载时静默 ----
        private static void FeedCrash(string model, string call, int faction)
        {
            try
            {
                Machine.Core.Log.Info("[AAM] FeedCrash called: " + model + "(" + call + ") faction=" + faction);
                System.Type t = MissileController.FindFeedApiType();
                if (t == null) { Machine.Core.Log.Info("[AAM] FeedCrash: FeedApi type NOT FOUND"); return; }
                var m = t.GetMethod("PostCrash", new System.Type[] { typeof(string), typeof(string), typeof(int) });
                if (m == null) { Machine.Core.Log.Info("[AAM] FeedCrash: PostCrash method NOT FOUND"); return; }
                m.Invoke(null, new object[] { model, call, faction });
                Machine.Core.Log.Info("[AAM] FeedCrash: invoked successfully");
            }
            catch (Exception ex) { Machine.Core.Log.Info("[AAM] FeedCrash FAILED: " + ex.Message); }
        }

        // ---- 魔法击杀上报（/kill指令，单个AI/玩家）：格式与坠毁相同 ----
        internal static void FeedMagicKill(string model, string call, int faction)
        {
            try
            {
                Machine.Core.Log.Info("[AAM] FeedMagicKill called: " + model + "(" + call + ") faction=" + faction);
                System.Type t = MissileController.FindFeedApiType();
                if (t == null) { Machine.Core.Log.Info("[AAM] FeedMagicKill: FeedApi type NOT FOUND"); return; }
                var m = t.GetMethod("PostMagicKill", new System.Type[] { typeof(string), typeof(string), typeof(int) });
                if (m == null) { Machine.Core.Log.Info("[AAM] FeedMagicKill: PostMagicKill method NOT FOUND"); return; }
                m.Invoke(null, new object[] { model, call, faction });
                Machine.Core.Log.Info("[AAM] FeedMagicKill: invoked successfully");
            }
            catch (Exception ex) { Machine.Core.Log.Info("[AAM] FeedMagicKill FAILED: " + ex.Message); }
        }

        // ---- 批量清除实体上报（/kill @missile等，不含AI/玩家）----
        internal static void FeedClearEntities(int count)
        {
            try
            {
                if (count <= 0) return;
                Machine.Core.Log.Info("[AAM] FeedClearEntities called: count=" + count);
                System.Type t = MissileController.FindFeedApiType();
                if (t == null) { Machine.Core.Log.Info("[AAM] FeedClearEntities: FeedApi type NOT FOUND"); return; }
                var m = t.GetMethod("PostClearEntities", new System.Type[] { typeof(int) });
                if (m == null) { Machine.Core.Log.Info("[AAM] FeedClearEntities: PostClearEntities method NOT FOUND"); return; }
                m.Invoke(null, new object[] { count });
                Machine.Core.Log.Info("[AAM] FeedClearEntities: invoked successfully");
            }
            catch (Exception ex) { Machine.Core.Log.Info("[AAM] FeedClearEntities FAILED: " + ex.Message); }
        }
    }

    // =====================================================================
    // AI 飞机工厂：把 .planedesign 变成一架"真飞机"
    // =====================================================================
    // =====================================================================
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
        public static int SpawnedCount = 0;   // 本次会话累计生成数（诊断双发用）
        public static BulletRound Newest;      // 最新一颗（自测镜头追踪用）

        private void OnEnable() { LiveCount++; }
        private void OnDisable() { LiveCount--; }

        public void Setup(Vector3[] pts, float dt, float life, string call, string model, int faction)
        {
            _pts = pts; _dt = dt; _life = life; _t = 0f;
            _prev = pts[0];
            ShooterCall = call; ShooterModel = model; FactionId = faction;
            SpawnedCount++;
            Newest = this;
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

        /// <summary>预测 ahead 秒后的位置（截图有异步滞后，镜头要对准"将来"的点）。</summary>
        public Vector3 Predict(float ahead) { return Sample(_t + ahead); }

        /// <summary>当前航向（近似）。</summary>
        public Vector3 DirNow()
        {
            Vector3 d = Sample(_t + 0.05f) - Sample(_t);
            if (d.sqrMagnitude < 0.0001f) return transform.forward;
            return d.normalized;
        }
    }

    internal static class AiAircraftFactory
    {
        private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 这些字段决定飞机能不能飞。游戏里它们在场景/预制体上配好了，
        // 而代码 AddComponent 出来的组件只有 0 —— 所以必须从一架"已经在飞的"飞机上搬过来。
        private static readonly string[] ContainerTuning = new string[] {
            "helicopter", "yawDrag", "rollDrag", "pitchDrag", "verticalLiniarDrag",
            "angleDragMultiplier", "liftSymmetry", "thrustSymmetry", "gravityForce",
            "planeGravityMultiplier", "helicopterGravityMultiplier", "liftMultiplier",
            "maxCGShift", "groundedCheckLayerMask"
        };

        private static readonly string[] ControllerTuning = new string[] {
            "controlHandicap", "airTraction", "stablisationForce", "helicopterStabilisationForce",
            "landingHelper", "rollTurnForce", "rollRestoreForce", "rollForce", "pitchForce",
            "yawForce", "throttleSpeed", "rudderSmoothTime", "maxTriggerVelocity", "minTriggerVelocity"
        };

        /// <summary>把参照飞机（玩家飞机）上的气动/操控调参整份搬到 AI 飞机上。</summary>
        private static void CopyPlaneTuningFromReference(IMachineApi api, PlaneContainer dst, PlaneController dstCtrl)
        {
            try
            {
                PlaneContainer src = null;
                try { src = PlaneContainer.Instance; } catch { }
                if (src == null || src == dst)
                {
                    api.Log("AAM: no reference plane - AI aircraft keeps code defaults (forces will be 0)");
                    return;
                }

                CopyFields(src, dst, ContainerTuning);
                PlaneController srcCtrl = src.GetComponent<PlaneController>();
                int ctrlN = 0;
                if (srcCtrl != null && dstCtrl != null) { CopyFields(srcCtrl, dstCtrl, ControllerTuning); ctrlN = ControllerTuning.Length; }

                api.Log("AAM: AI tuning copied | container=" + ContainerTuning.Length
                        + " controller=" + ctrlN
                        + " srcMass=" + src.GetMass().ToString("F0")
                        + " srcParts=" + src.GetComponentsInChildren<PlanePart>(true).Length);
            }
            catch (Exception e) { api.Log("AAM: tuning copy failed: " + e.Message); }
        }

        private static void CopyFields(object src, object dst, string[] names)
        {
            if (src == null || dst == null) return;
            Type st = src.GetType();
            Type dt = dst.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    FieldInfo f = dt.GetField(names[i], BF);
                    if (f == null) continue;
                    FieldInfo g = st.GetField(names[i], BF);
                    if (g == null) continue;
                    f.SetValue(dst, g.GetValue(src));
                }
                catch { }
            }
        }

        internal static AiAircraft Spawn(IMachineApi api, string designPath, Vector3 pos, Vector3 dir,
                                         float speed, float cruiseAlt, bool hostile, string callsign)
        {
            // keepColliders = true ：AI 飞机会有和玩家一模一样的机体碰撞
            // startInactive = true ：部件的 Awake/Start 推迟到我们挂完控制器之后
            GameObject root = DesignModel.BuildWithGameLoader(designPath, "AI_" + callsign, api, true, true);
            if (root == null) { api.Log("AAM: AI aircraft load failed: " + designPath); return null; }

            root.SetActive(false);

            // 游戏自己的三个组件。添加顺序有讲究：
            //   PlaneController.Awake() : container  = GetComponent<PlaneContainer>()
            //   PlaneContainer.Awake()  : controller = GetComponent<PlaneController>()
            // 两者互相要求对方"已经挂上"，所以必须在 inactive 状态下全部挂完再一起激活。
            root.AddComponent<PartExploder>();
            PlaneController controller = root.AddComponent<PlaneController>();
            PlaneContainer container = root.AddComponent<PlaneContainer>();

            root.SetActive(true);   // 此刻才跑 Awake（顺序 = 添加顺序，两边都能拿到引用）

            // 把"能飞的调参"从玩家飞机整份拷过来。
            // 升力系数、舵面力矩、各种阻力这些数值在游戏里是**场景/预制体里配好的**，
            // 刚 AddComponent 出来的组件只会拿到 C# 字段默认值 —— 那些力全是 0，飞机根本没有舵面。
            CopyPlaneTuningFromReference(api, container, controller);

            // AI 的专属输入源：玩家的键盘从此与它无关
            InputManager aiInput = AiInputSource.New("AI_Input_" + callsign);

            // 机体前方缓存（PlaneContainer.Forward 依赖它；它会去找名字里带 "Cock" 的部件）
            try
            {
                MethodInfo m = typeof(PlaneContainer).GetMethod("UpdateForwardDirection", BF);
                if (m != null) m.Invoke(container, null);
            }
            catch (Exception e) { api.Log("AAM: UpdateForwardDirection failed: " + e.Message); }

            // 进入飞行模式：游戏自己建刚体、累加质量、算重心与惯量张量、收集部件并挂 UpdatePart
            // ⚠ 游戏副作用（09-18 日志+IL 实锤）：ActivateFlyMode 末尾调全局 CargoInventory.ApplyCargo()
            //   （IL_0374），ApplyCargo 对字典每件货 ChangeMass(weight×count)，而 CargoInventory 是
            //   全局单例、绑定玩家 → 玩家每次 AI 生成无端增重 全货仓重量/15（combatW=75 时 +5.0 物理，
            //   与 decomp[JUMP] 逐位吻合）。修复：前后快照玩家 mass 字段，走正规 ChangeMass(-Δ*15) 回退。
            try
            {
                PlaneContainer player = null;
                try { player = PlaneContainer.Instance; } catch { }
                FieldInfo fMass = typeof(PlaneContainer).GetField("mass", BF);
                MethodInfo mChg = typeof(PlaneContainer).GetMethod("ChangeMass", BF);
                bool guard = player != null && player != container && fMass != null && mChg != null;
                float massBefore = 0f;
                if (guard) massBefore = Convert.ToSingle(fMass.GetValue(player));

                MethodInfo m = typeof(PlaneContainer).GetMethod("ActivateFlyMode", BF);
                if (m != null) m.Invoke(container, null);

                if (guard)
                {
                    float massAfter = Convert.ToSingle(fMass.GetValue(player));
                    float delta = massAfter - massBefore;
                    if (Mathf.Abs(delta) > 0.001f)
                    {
                        mChg.Invoke(player, new object[] { -delta * 15f });
                        api.Log("AAM: AI init reverted player cargo re-apply " + delta.ToString("F3")
                                + " phys (ActivateFlyMode/ApplyCargo side effect)");
                    }
                }
            }
            catch (Exception e) { api.Log("AAM: ActivateFlyMode failed: " + e.Message); }

            Rigidbody rb = null;
            try { rb = container.GetComponent<Rigidbody>(); } catch { }
            if (rb == null)
            {
                api.Log("AAM: no Rigidbody on AI aircraft, abort");
                UnityEngine.Object.Destroy(root);
                return null;
            }

            // 引擎数组 + 加满油（原版是 ResetPlane() 干的活，这里手动补上）
            try
            {
                FieldInfo fe = typeof(PlaneContainer).GetField("engines", BF);
                if (fe != null) fe.SetValue(container, root.GetComponentsInChildren<Engine>(true));
                MethodInfo mR = typeof(PlaneContainer).GetMethod("Refuel", BF);
                if (mR != null) mR.Invoke(container, null);
            }
            catch (Exception e) { api.Log("AAM: AI engines/refuel failed: " + e.Message); }

            // 引擎诊断：推力的真实来源是 Engine.thrust * currentThrottle * clamp01(1-thrustHandicap)
            //   * maxSpeed.Evaluate(空速)；油门上升速率 = PlaneController.throttleSpeed。
            // 这些都是部件预制体上的值，AI 飞机取的就是预制体默认值，打出来才能确认推力够不够。
            try
            {
                Engine[] engs = root.GetComponentsInChildren<Engine>(true);
                if (engs.Length > 0 && engs[0] != null)
                {
                    Engine e0 = engs[0];
                    api.Log("AAM: AI engine diag n=" + engs.Length
                        + " thrust=" + e0.thrust.ToString("F2")
                        + " handicap=" + e0.thrustHandicap.ToString("F2")
                        + " engThrSpeed=" + e0.throttleSpeed.ToString("F2")
                        + " maxPower=" + e0.maxPower.ToString("F2")
                        + " speedKeys=" + (e0.maxSpeed != null ? e0.maxSpeed.keys.Length : 0)
                        + " afterburner=" + e0.useAfterburner
                        + " | ctrl.throttleSpeed=" + controller.throttleSpeed.ToString("F2")
                        + " liftMul=" + container.GetType().GetField("liftMultiplier", BF)
                                              .GetValue(container));
                }
                else api.Log("AAM: AI engine diag n=0 (no Engine parts!)");
            }
            catch (Exception e) { api.Log("AAM: engine diag failed: " + e.Message); }

            // 摆位 + 初速。必须放在 ActivateFlyMode 之后：CenterPlane() 会挪动整机位置。
            // 注意：一定要用 rb.position / rb.rotation，不能只改 transform。
            // Unity 默认 autoSyncTransforms = false，只改 transform 的话物理引擎会在下一次
            // FixedUpdate 用旧的 rb.position 把 transform 覆盖回去 —— 飞机立刻被拽回原点掉进地里。
            Quaternion rot = PnGuidance.SafeLook(dir.normalized, Vector3.up);
            rb.position = pos;
            rb.rotation = rot;
            root.transform.position = pos;
            root.transform.rotation = rot;
            try { Physics.SyncTransforms(); } catch { }
            rb.linearVelocity = dir.normalized * speed;
            rb.angularVelocity = Vector3.zero;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // 标记成 AI 实体：语音警报必须完全无视它
            root.AddComponent<AiEntityMarker>().Kind = hostile ? "bandit" : "drone";
            SilentAi.Mark(root);

            AiAircraft ai = hostile ? (AiAircraft)root.AddComponent<BanditAircraft>()
                                    : root.AddComponent<AiAircraft>();
            ai.Container = container;
            ai.Controller = controller;
            ai.Rb = rb;
            ai.Input = aiInput;
            ai.Hostile = hostile;
            ai.Callsign = callsign;
            try { ai.ModelName = System.IO.Path.GetFileNameWithoutExtension(designPath); }
            catch { ai.ModelName = ""; }
            ai.CruiseSpeed = Mathf.Max(speed, 230f);   // 巡航目标下限 230 m/s（≈447 节，凶猛），初速由 speed 单独控制
            ai.CruiseAlt = cruiseAlt;
            ai.HomePos = pos;
            ai.TurnGain = 0.055f;   // 机动强化：转弯更敏捷（原 0.03，转弯半径明显变小）

            // 登记成可被导弹锁定的目标
            TargetRegistry.Register(root, true, callsign);

            // 诊断：确认飞机真的被放到指定位置、部件真的进了 PlaneContainer.planeParts
            int partsInContainer = -1;
            try
            {
                FieldInfo fp = typeof(PlaneContainer).GetField("planeParts", BF);
                System.Collections.IList list = fp != null ? fp.GetValue(container) as System.Collections.IList : null;
                if (list != null) partsInContainer = list.Count;
            }
            catch { }

            api.Log("AAM: AI aircraft spawned [" + callsign + "] children=" + root.transform.childCount
                    + " hostile=" + hostile + " mass=" + rb.mass.ToString("F0")
                    + " engines=" + root.GetComponentsInChildren<Engine>(true).Length
                    + " wings=" + root.GetComponentsInChildren<Wing>(true).Length
                    + " planeParts=" + partsInContainer
                    + " want=" + pos.ToString("F0")
                    + " got=" + root.transform.position.ToString("F0")
                    + " rbv=" + rb.linearVelocity.magnitude.ToString("F0"));
            return ai;
        }
    }

    // =====================================================================
    // 敌机：用火控雷达锁定目标（阵营 AI 优先打敌方 AI，其次玩家）并发射导弹
    // =====================================================================
    public class BanditAircraft : AiAircraft
    {
        private bool _locked;
        private float _dist;
        /// <summary>当前锁定目标（供 RWR 判定锁定对象是否玩家）。</summary>
        public Transform CurrentTarget;

        /// <summary>公开属性：是否已锁定目标（RWR 低频警报用）。</summary>
        public bool LockedNow { get { return _locked; } }
        /// <summary>公开属性：锁定目标是否为玩家（RWR 低频警报只对锁定玩家触发）。</summary>
        public bool TargetIsPlayer
        {
            get
            {
                if (!_locked || CurrentTarget == null) return false;
                Transform pt = PlayerTransform();
                return pt != null && CurrentTarget == pt;
            }
        }

        /// <summary>玩家飞机的 Transform（拿不到就返回 null，AI 会降级为纯平飞）。</summary>
        public static Transform PlayerTransform()
        {
            return PlayerTransformOrNull();
        }

        /// <summary>当前交战目标（供基类性格飞行使用）：优先已跟踪目标，否则玩家。</summary>
        protected override Transform CurrentAimTarget()
        {
            if (CurrentTarget != null) return CurrentTarget;
            return PlayerTransform();
        }

        protected override Vector3 DesiredHeading(Vector3 fwd, Vector3 vel, float spd)
        {
            Transform t = CurrentAimTarget();
            if (t == null)
            {
                // 没有可见目标（玩家机暂时不可解析等）：退回己方基地上空待命，
                // 而不是保持随机航向直飞消失在偏远海域
                float dh = Vector3.Distance(transform.position, HomePos);
                if (dh > PatrolRadius) return (HomePos - transform.position).normalized;
                return fwd;
            }
            Vector3 aim = AimPoint(t);                    // 咬尾型瞄目标尾后点，其余瞄本体
            Vector3 to = aim - transform.position;
            if (to.sqrMagnitude < 1f) return fwd;
            return to.normalized;
        }

        // ---- 火控强化（2026-09-15）：锁定记忆 / 发射可行性 / 齐射 ----
        // 用户实测："AI 只会在近距离才攻击，玩家打超视距，AI 几乎摸不到玩家"。两个根因：
        //   ① 没有锁定记忆 —— 曲射（crank）或规避机动时目标一出雷达锥，LockProgress 就衰减归零、
        //      锁定立刻丢；等于"机头不钉死目标就打不出去"，超视距交战根本没法打。
        //   ② 不看导弹够不够得着 —— AI 在 8~12km 外放炮，而中距弹对**背向**目标的实际有效
        //      射程只有 3~5km，打出去全是白费（打不到目标的火力 = 没有火力）。
        // 修法：锁定给 6s 记忆（现代雷达的记忆跟踪）；开火前先算"够不够得着"
        //      （有效接近率 × 导弹寿命）；一次交战连射 SalvoSize 发。
        public static float LockMemory = 6f;      // 锁定记忆（s）：角度解丢失后仍保持锁定多久
        public static bool KinematicGate = true;  // 只在导弹真的够得着时才开火

        private float _lockMemT;
        private int _burstLeft = -1;              // 本次齐射还剩几发（<=0 = 下一发开新一轮）
        private MissileSpec _aiSpec;

        // ---- 诊断（供自测 aiCombatTest 读）----
        public int ShotsFired;                    // 本机累计发射了几发
        public float FirstShotDist = -1f;         // 第一次开火时的距离（判据：够不够"超视距"）
        public bool ReachNow;                     // 上一帧"发射可行性"判据的结果
        public float DistNow { get { return _dist; } }
        public float LockProgNow { get { return LockProgress; } }
        public float FireCdNow { get { return FireCooldown; } }

        private MissileSpec AiSpec()
        {
            if (_aiSpec == null)
            {
                AamSystem sys = AamSystem.Live;
                if (sys != null) _aiSpec = sys.AiMissileSpec;
            }
            return _aiSpec;
        }

        /// <summary>
        /// 发射可行性（粗估的不可逃逸包线）：这一发有没有必要打。
        /// 有效接近率 = 导弹 0.8×极速 − 目标背离速度（目标迎面时等效于拉长射程），
        /// 再要求"预计命中时间 ≤ 导弹寿命 × 0.8"。
        /// </summary>
        public bool KinematicReach(Transform t)
        {
            if (!KinematicGate) return true;
            MissileSpec sp = AiSpec();
            if (sp == null) return _dist <= RadarRange * FireRangeFrac;
            float ms = sp.MaxSpeed * 0.5144f;             // 规格表单位是节 -> m/s
            if (ms < 1f || sp.Lifetime < 1f) return _dist <= RadarRange * FireRangeFrac;
            float away = 0f;
            try
            {
                Rigidbody trb = t.GetComponentInParent<Rigidbody>();
                if (trb != null && _dist > 1f)
                {
                    Vector3 los = (t.position - transform.position) / _dist;
                    away = Mathf.Max(0f, Vector3.Dot(trb.linearVelocity, los));
                }
            }
            catch { }
            float eff = Mathf.Max(120f, ms * 0.80f - away);
            return (_dist / eff) <= sp.Lifetime * 0.80f;
        }

        protected override void CombatTick(ref Vector3 wantDir, ref float pitchInput, ref float throttle)
        {
            DoCombat(ref wantDir, true);
        }

        /// <summary>规避导弹期间的反击：不改航向，只保持火控解并开火（见 AiAircraft.CombatFireOnly）。</summary>
        protected override void CombatFireOnly()
        {
            Vector3 ignore = Vector3.zero;
            DoCombat(ref ignore, false);
        }

        /// <summary>火控主体。<paramref name="steer"/> = false 时只做"锁定 + 开火"，不动航向。</summary>
        private void DoCombat(ref Vector3 wantDir, bool steer)
        {
            // 阵营 AI：优先攻击最近的敌方阵营 AI，其次玩家（若敌对）
            Transform t = PlayerTransform();
            bool attackPlayer = true;
            if (FactionId >= 0)
            {
                Transform enemy = NearestEnemyAi();
                if (enemy != null)
                {
                    t = enemy;
                    attackPlayer = false;
                }
                else
                {
                    // 玩家敌对性：FactionApi.IsEnemy(本机, 玩家机)
                    attackPlayer = IsPlayerEnemy();
                }
            }
            if (t == null || !attackPlayer && t == PlayerTransform() && !IsPlayerEnemy())
            {
                if (t == null) { LockProgress = 0f; _locked = false; return; }
                if (!attackPlayer)
                {
                    LockProgress = 0f; _locked = false;
                    if (steer) wantDir = DesiredHeadingFor(t);
                    return;
                }
            }

            Vector3 to = t.position - transform.position;
            _dist = to.magnitude;
            CurrentTarget = t;

            // 狗斗机动：未锁定时蛇形接近（横向 ±25%、垂直 ±12% 摆动），锁定后收敛瞄准。
            // steer=false（规避中）绝不能动航向 —— 那会把正在做的 beam 机动当场掰回去。
            if (steer && !_locked && to.sqrMagnitude > 1f)
            {
                float tt = Time.time;
                Vector3 want = to.normalized;
                Vector3 up = Vector3.up;
                Vector3 right = Vector3.Cross(up, want);
                if (right.sqrMagnitude < 0.01f) right = transform.right;
                right = right.normalized;
                float sway = Mathf.Sin(tt * 0.8f + transform.position.x * 0.01f) * 0.25f;
                float bob = Mathf.Sin(tt * 1.2f + transform.position.z * 0.01f) * 0.12f;
                wantDir = (want + right * sway + up * bob).normalized;
            }

            Vector3 fwd = Container.Forward;
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;

            bool inCone = _dist <= RadarRange && Vector3.Angle(fwd, to) <= RadarCone;
            if (inCone)
            {
                LockProgress += Time.deltaTime;
                _lockMemT = Mathf.Max(0f, LockMemory);   // 有角度解就刷新记忆
            }
            else
            {
                LockProgress = Mathf.Max(0f, LockProgress - Time.deltaTime * 2f);
                _lockMemT -= Time.deltaTime;             // 记忆在角度解丢失期间消耗
            }

            if (LockProgress >= LockDelay && !_locked)
            {
                _locked = true;
                Machine.Core.Log.Info("[AAM] " + Callsign + " [" + (Persona != null ? Persona.Id : "?")
                    + "] LOCKED target at " + _dist.ToString("F0") + "m");
            }
            else if (_locked && LockProgress <= 0.01f && _lockMemT <= 0f)
            {
                _locked = false;                         // 角度解丢了、记忆也耗尽
            }

            // ---- 开火：雷达距离包线（性格）× 发射可行性（导弹）双重门 ----
            // 距离包线仍由性格决定（超视距型更远），但再叠一层"够不够得着"：
            // 否则 8~12km 外那些炮全是浪费，玩家感受到的就是"AI 摸不到我"。
            FireCooldown -= Time.deltaTime;
            bool inFireRange = _dist <= RadarRange * FireRangeFrac && _dist >= MinRange;
            ReachNow = inFireRange && KinematicReach(t);
            if (_locked && FireCooldown <= 0f && ReachNow)
            {
                AamSystem sys = AamSystem.Live;
                if (sys != null)
                {
                    // 齐射：一轮连射 SalvoSize 发（间隔 SalvoGap），打完进入 FireInterval 冷却
                    if (_burstLeft <= 0) _burstLeft = Mathf.Max(1, SalvoSize);
                    _burstLeft--;
                    FireCooldown = (_burstLeft <= 0) ? FireInterval : Mathf.Max(0.25f, SalvoGap);
                    if (ShotsFired == 0) FirstShotDist = _dist;
                    ShotsFired++;
                    sys.AiLaunchAt(this, t, _dist);
                }
            }
        }

        /// <summary>最近的敌方阵营 AI（FactionApi 判定），无则 null。</summary>
        private Transform _enemyCache;
        private float _enemyCacheT;

        // ---- FactionApi 反射缓存（避免每帧 Type.GetType + GetMethod）----
        private static Type _factionApiType;
        private static MethodInfo _factionGetAllAi;
        private static MethodInfo _factionIsEnemy;
        private static bool _factionApiProbed;

        private static void ProbeFactionApi()
        {
            if (_factionApiProbed) return;
            _factionApiProbed = true;
            try
            {
                _factionApiType = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (_factionApiType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "FactionSystem") { _factionApiType = asm.GetType("Machine.Faction.FactionApi"); break; }
                    }
                }
                if (_factionApiType != null)
                {
                    _factionGetAllAi = _factionApiType.GetMethod("GetAllAi", Type.EmptyTypes);
                    _factionIsEnemy = _factionApiType.GetMethod("IsEnemy", new Type[] { typeof(GameObject), typeof(GameObject) });
                }
            }
            catch { _factionApiType = null; _factionGetAllAi = null; _factionIsEnemy = null; }
        }

        /// <summary>最近的敌方阵营 AI（FactionApi 判定），0.5s 缓存避免每帧反射遍历拖慢游戏。</summary>
        private Transform NearestEnemyAi()
        {
            _enemyCacheT -= Time.deltaTime;
            if (_enemyCacheT > 0f) return _enemyCache;
            _enemyCacheT = 0.5f;
            _enemyCache = ComputeNearestEnemyAi();
            return _enemyCache;
        }

        private Transform ComputeNearestEnemyAi()
        {
            try
            {
                ProbeFactionApi();
                if (_factionApiType == null || _factionGetAllAi == null || _factionIsEnemy == null) return null;
                var list = _factionGetAllAi.Invoke(null, null) as System.Collections.IEnumerable;
                if (list == null) return null;
                Transform best = null;
                float bestD = float.MaxValue;
                Vector3 myPos = transform.position;
                foreach (var o in list)
                {
                    var go = o as GameObject;
                    if (go == null || go == gameObject) continue;
                    bool enemy = (bool)_factionIsEnemy.Invoke(null, new object[] { gameObject, go });
                    if (!enemy) continue;
                    float d = Vector3.Distance(myPos, go.transform.position);
                    if (d < bestD) { bestD = d; best = go.transform; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>玩家是否与本机敌对（FactionApi；玩家机带 FactionMarker）。</summary>
        private bool IsPlayerEnemy()
        {
            try
            {
                var pc = PlaneContainer.Instance;
                if (pc == null) return false;
                ProbeFactionApi();
                if (_factionApiType == null || _factionIsEnemy == null) return true;   // 无阵营系统：保持旧的"敌机打玩家"
                return (bool)_factionIsEnemy.Invoke(null, new object[] { gameObject, pc.gameObject });
            }
            catch { return true; }
        }

        private Vector3 DesiredHeadingFor(Transform target)
        {
            if (target == null) return transform.forward;
            Vector3 to = target.position - transform.position;
            if (to.sqrMagnitude < 1f) return transform.forward;
            return to.normalized;
        }
    }

    // =====================================================================
    // Mod 运行时
    // =====================================================================
    public class AamSystem : MonoBehaviour
    {
        /// <summary>当前运行时实例（加载器在每次场景切换时会重新引导，用它挡住重复初始化）。</summary>
        public static AamSystem Live;

        private const BindingFlags BFS = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private IMachineApi _api;

        // ---- 发射音效 ----
        private AudioSource _launchSrc;
        private AudioClip _launchClip;
        private bool _launchReady;

        // ---- 机炮音效（叠加播放池）----
        private AudioSource[] _gunSfxPool;
        private AudioClip _gunSfxClip;
        private bool _gunSfxReady;
        private int _gunSfxPoolIdx = 0;
        private const int GUN_SFX_POOL_SIZE = 6;  // 6个AudioSource叠加播放，模拟机炮连射的密集音效

        // ---- 热诱弹弹射音效（连抛短池，避免互相截断）----
        private AudioClip _flareSfxClip;
        private AudioSource[] _flareSfxPool;
        private bool _flareSfxReady;
        private int _flareSfxIdx = 0;
        private const int FLARE_SFX_POOL_SIZE = 3;  // 连抛 (burst 2~3 枚) 时各用独立音源，互不截断

        // ---- 配置 ----
        private string cfgCargoName = "PL-15";
        private float cfgPrice = 12000f;
        private float cfgWeight = 10f;
        private int cfgCargoSpace = 60;
        private bool cfgCombat = true;
        private int cfgGiveOnStart = 4;
        private string cfgFireKey = "F";   // 已废弃：F 键发射移除，保留字段避免旧配置解析报错（不再读取使用）
        private string cfgDesignFile = "PL-15.planedesign";

        // ---- AI 飞机（靶机 / 敌机）----
        // 用户要求：AI 操控的靶机在气动与操控上"和玩家操控难度无异"，
        // 所以它用的就是游戏原版飞机（PlaneContainer + PlaneController + 原版部件），
        // 不是自研的简化飞行体。这些参数只决定"什么时候、在哪里、放几架"。
        // ⛔ 以下 4 个"随机刷 AI"配置已废弃（保留仅为兼容旧配置文件，读进来也不会再用）：
        //    aiEnabled / aiSpawnMinDelay / aiSpawnMaxDelay / aiMaxAlive / aiHostileChance
        // AAM 不再自行生成任何 AI 飞机（见 StepAiAircraft）。
        private bool cfgAiEnabled = true;          // 已废弃（不再生效）
        private string cfgAiDesign = "F-22.planedesign";
        private float cfgAiSpawnMin = 30f;         // 已废弃
        private float cfgAiSpawnMax = 80f;         // 已废弃
        private int cfgAiMaxAlive = 2;             // 已废弃
        // 出航速度 / 作战高度 / 雷达 / 开火距离 改由"性格"决定（见 AiPersonalities）。
        // 下面两个是**全局基准**：性格里的雷达距离会按 (本值 / 9000) 整体缩放，
        // 锥角按 (本值 / 35) 缩放，所以保留性格之间的相对差异，又能一键盘调。
        // 注：这两个现在只对"自测/其他来源生成的 AI"起作用（AAM 自己不再刷机）。
        private float cfgAiRadar = 9000f;          // 基准火控雷达距离
        private float cfgAiCone = 35f;             // 基准雷达锥半角（度）
        private float cfgAiHostileChance = 0.65f;  // 已废弃
        private bool cfgAiDamagePlayer = true;     // 导弹命中玩家是否造成致命伤害（整机击落 + 计分板上报）
        // 性格（人格）开关：空数组 = 全部性格都上；也可以只留几种，例如 ["VIPER","FALCON"]
        private string[] cfgAiPersonalities = new string[0];
        private float cfgAiSpeedScale = 1.0f;      // 统一缩放所有性格的巡航速度（不用重编就能整体调快慢）
        // ---- AI 空战强化（2026-09-15）----
        // 旧行为：AI 用玩家当前选中的弹（默认 PL-15，中距）、无锁定记忆、够不着也放炮，
        // 于是"AI 只敢近战、玩家打超视距、AI 摸不到玩家"。见 BanditAircraft.DoCombat。
        private string cfgAiMissile = "R-37";      // aiMissileName：AI 用的空空导弹（远程弹）
        private float cfgAiLockMemory = 6f;        // aiLockMemory：锁定记忆（s）
        private bool cfgAiKinGate = true;          // aiKinematicGate：只在导弹够得着时才开火
        private bool cfgAiAutoPersona = true;      // aiAutoPersona：外部生成的 AI 自动抽一个人格

        // 导弹规避（2026-09-13）：AI 飞机对来袭导弹做 beam + 俯冲规避。见 AiAircraft.EvadeCfg。
        private bool cfgEvadeEnabled = true;
        private float cfgEvadeRange = 2600f;
        private float cfgEvadeTTC = 11f;
        private float cfgEvadeMissDist = 220f;
        private float cfgEvadeHold = 1.6f;
        private float cfgEvadeScan = 0.15f;
        private float cfgEvadeBeamMix = 0.85f;
        private float cfgEvadeDropAlt = 600f;
        private float cfgEvadeAgl = 300f;

        // ---- 红外系统（2026-09-14）----
        // 期望值 = 油门(0~100) × cfgIrThrottleK + 速度(m/s) × cfgIrSpeedK，夹在 0~200。
        // 实际值按非线性规律逼近：升温指数逼近（差值越大变化越快），
        // 降温走 cos 在 [π/2, π] 的趋势（先快后慢、速率封顶），所以收油门不会瞬间掉下来。
        private bool cfgIrEnabled = true;
        private float cfgIrThrottleK = 1.2f;
        private float cfgIrSpeedK = 0.1f;
        private float cfgIrHeatK = 0.55f;
        private float cfgIrCoolRate = 26f;
        private float cfgIrCoolRef = 60f;
        private bool cfgIrLog = false;          // 诊断：定期打一条飞机的红外期望/实际

        // ---- 红外导引头：全向锁定 + 缩圈 IRCCM ----
        private bool cfgSeekerEnabled = true;
        private float cfgSeekerFov = 30f;       // 视场半角（度）
        private float cfgSeekerRange = 9000f;
        private bool cfgIrcmEnabled = true;
        private float cfgIrcmReact = 0.35f;     // 识别诱饵的反应延迟（s）
        private float cfgGateWide = 18f;        // 宽圈
        private float cfgGateNarrow = 2.5f;     // 窄圈
        private float cfgGateShrink = 1.5f;     // 收圈耗时（s）
        private float cfgIrcmDecoyPenalty = 0.85f;
        private float cfgIrcmSpeedFrac = 0.55f;  // IRCCM 运动学判别：诱饵速度 < 真目标×本值 就踢掉

        // ---- 热诱弹 ----
        private bool cfgFlareEnabled = true;
        private string cfgFlareKey = "X";
        private string cfgFlareDesign = "decoy flare.planedesign";
        private float cfgFlarePrice = 400f;
        private float cfgFlareWeight = 0.2f;
        private int cfgFlareSpace = 1;
        private int cfgFlareGive = 16;
        private int cfgFlareBurst = 2;          // 一次抛几枚
        private float cfgFlareBurstGap = 0.12f; // 连抛间隔（s）
        private float cfgFlareCooldown = 0.5f;
        private float cfgFlareDrag = 0.85f;     // 阻力系数（1/s）
        private float cfgFlareEjectDown = 20f;
        private float cfgFlareEjectBack = 12f;
        private float cfgFlareEjectSide = 9f;
        private int cfgFlareLights = 6;         // 同时点亮的火光上限（其余只留粒子火焰）
        private bool cfgFlareTest = false;      // 自测：热诱弹 / IRCCM 专项场景
        private bool cfgPlayerHitTest = false;  // 自测：朝玩家打一枚真弹，验证坠毁 + 计分板上报

        // ---- AI 自卫抛热诱弹（下发到 AiAircraft 的静态字段）----
        private bool cfgAiFlareEnabled = true;
        private int cfgAiFlareStock = 6;        // 每架 AI 携带量
        private int cfgAiFlareBurst = 2;        // 一次抛几枚
        private float cfgAiFlareCooldown = 2.5f;// 两次投放之间的冷却（s）
        private float cfgAiFlareDelay = 0.6f;   // 开始规避后多久才反应过来抛（s）

        private float cfgBoostTime = 2.5f;
        private float cfgBoostThrust = 320f;
        private float cfgSustainThrust = 45f;
        private float cfgDragK = 0.00009f;
        private float cfgMaxSpeed = 1250f;
        private float cfgMaxG = 40f;
        private float cfgTurnRate = 55f;
        private float cfgLifetime = 120f;
        private float cfgProximity = 25f;
        private float cfgNavConstant = 4f;
        private float cfgTargetRange = 40000f;

        // ---- 地形避障（见 TerrainAvoid）----
        private bool cfgAvoidEnabled = true;
        private float cfgAvoidLookTime = 1.6f;
        private float cfgAvoidLookTurn = 1.0f;
        private float cfgAvoidMinLook = 250f;
        private float cfgAvoidMaxLook = 3600f;
        private float cfgAvoidClearance = 40f;
        private float cfgAvoidStrength = 1f;
        private float cfgAvoidPnCut = 0.85f;
        private float cfgAvoidInterval = 0.05f;
        private float cfgAvoidSpeedStepFrac = 0.03f;   // avoidSpeedStepFrac：每拍前进量占视距的比例
        private float cfgAvoidMaxInterval = 0.30f;     // avoidMaxInterval：扫描间隔上限（秒）
        private float cfgAvoidThreatFast = 0.10f;      // avoidThreatFast：规避中的扫描间隔上限（秒）
        private float cfgAvoidExtraG = 1.35f;
        private float cfgAvoidBrake = 0.4f;
        private float cfgAvoidAbsFloor = 30f;
        private float cfgAvoidWarmup = 0.35f;
        private float cfgAvoidNose = 4f;
        private bool cfgAvoidImpactDetonate = true;
        private float cfgAvoidProfileSpan = 300f;
        private float cfgAvoidProfileMargin = 20f;
        private float cfgAvoidTerminalRange = 1600f;  // avoidTerminalRange：目标优先，终端让位距离（m）
        private float cfgAvoidTerminalMin = 0.30f;    // avoidTerminalMin：终端段保留的避障权重下限
        private bool cfgAvoidTest = false;   // avoidTest：避障专项自测（低空尾追靶机）
        private bool cfgEvadeTest = false;   // evadeTest：AI 规避专项自测（中空尾追，看 AI 会不会 beam + 俯冲）

        private bool cfgAutoTest = false;
        private bool cfgAutoEnterFlyMode = false;
        private bool cfgCapture = false;
        private string cfgShotDir = "";
        private float cfgTestDroneSpeed = 95f;

        // ---- 运行时 ----
        private CargoType _missileType;
        private Texture2D _icon;
        private PlaneContainer _plane;
        private int _planeId;
        private bool _gaveThisPlane;
        private readonly List<MissileController> _live = new List<MissileController>();
        private readonly List<AiAircraft> _ai = new List<AiAircraft>();
        private float _aiTimer;
        private int _aiSeq;
        private bool _aiSeeded;
        private bool _airportsInjected;
        private float _injectTimer;
        private float _resolveT;      // ResolvePlane 节流（菜单里 Instance 访问贵）

        // ---- 多弹型 ----
        private readonly List<MissileSpec> _specs = new List<MissileSpec>();
        private readonly List<CargoType> _missileTypes = new List<CargoType>();
        private MissileSpec _currentSpec;   // 当前生效弹型

        // ---- 弹药显示 ----
        // 这里原来挂着一个右下角的独立浮窗（"PL-15 xN" 大字号），会挡住飞行视野，已删除。
        // 现在弹药（类型 + 数量）统一并入 BattleHold 的战斗货仓面板：
        //   见 BattleHold.cs -> RefreshBody()，战斗货物那一行用 CombatItemFontSize 大字显示。
        // 本 Mod 只负责数量变化时刷新面板（BattleHoldApi.RaiseChanged 由 BattleHold 自己发）。

        // ---- BattleHold / BattleCore 反射 ----
        private bool _bhChecked, _bhOk;
        private MethodInfo _bhMarkByName, _bhMarkByType, _bhCount, _bhAdd, _bhRemove, _bhContains;
        private bool _bcChecked, _bcOk;
        private MethodInfo _bcPost;

        // ---- 自测 ----
        private bool _testStarted;
        private float _testT;
        private int _shotStage;
        private bool _revertChecked;   // 回退分支验证：装货后再生成一架 AI，验证 ActivateFlyMode 副作用被回退
        private float _lastRmbDown = -10f;
        private int _selIndex;   // 玩家 R 键选中的弹种索引
        private bool cfgAiPersonaTest = false;   // 性格验证自测（aiPersonaTest）
        private bool _personaTestDone;
        // ---- 自测：AI 超视距交战（aiCombatTest）----
        private bool cfgAiCombatTest = false;
        private float cfgAiCombatDist = 9000f;   // AI 生成在玩家正前方多远
        private float cfgAiCombatAlt = 0f;       // 相对玩家的高度差
        private string cfgAiCombatPersona = "SENTINEL";
        private int cfgAiCombatSeconds = 30;
        private bool _combatTestDone;
        private float _combatSpawnAt;
        private AiAircraft _combatAi;
        private float _combatLogAt;
        private int _combatLogN;
        private bool _combatProbeDone;              // 探针弹是否已发射
        private bool _combatEvadeSeen;              // 是否观察到 AI 进入规避
        private int _combatShotsAtEvade;            // 首次观察到规避时的发射数
        private int _combatShotsDuringEvade;        // 规避期间观察到的最大发射数
        private float cfgTestObserve = 140f;     // 自测观察窗口（秒），testObserveSeconds
        // 自测时若游戏停在主菜单（PlaneContainer 还不存在），直接加载一个 mod 存档进游戏，
        // 等价于点加载器的 "Mod Saves" 按钮。见 TryLoadSaveAndEnter()。
        private bool cfgTestAutoLoadSave = true;  // testAutoLoadSave
        private string cfgTestSaveName = "AutoSave 0"; // testSaveName（留空=取最近修改的存档）
        private bool cfgTestAmmo = false;        // testAmmo：弹药模型 + MSDM 拦截弹专项自测

        // ---- 机炮（右键点射 / 长按连射）----
        private bool cfgGunEnabled = true;
        private float cfgGunRps = 6f;            // 射速：发/秒
        private float cfgGunHoldDelay = 0.18f;   // 按住超过这么久才转入连射（短按 = 点射 1 发）
        private float cfgGunMuzzle = 815f;       // 初速 m/s 回退值（弹种表 1584kt ≈ 2.4 马赫）
        private float cfgGunLife = 3.0f;         // 子弹寿命 s（预计算航迹长度）
        private float cfgGunStep = 0.08f;        // 航迹预计算步长 s
        private float cfgGunDrag = 0.00002f;     // 简单空气阻力系数（v * (1 - drag*|v|)）
        private float cfgGunSpread = 0.35f;      // 散布角（度）
        private int cfgGunMaxLive = 240;         // 同屏最多子弹数（保险丝）
        private float cfgGunHitRadius = 14f;     // 命中判定球半径 m
        private float cfgBulletScale = 4f;       // 子弹模型放大倍数（0.3m 的原模型看不见）
        private int _ammoStage = -1;
        private int _ammoIdx;
        private float _ammoT;
        private float _ammoLogT;
        private MissileController _ammoThreat;
        private MissileController _ammoMsdm;
        private int _gunBurstN;
        private float _gunBurstT;
        private float _gunBurstFps;
        private int _gunBurstFpsN;
        private bool _gunShotDone;
        private Transform _gunPtTgt;
        private Vector3 _gunPtLast;
        private Vector3 _gunPtVel;
        private int _gunPtN;
        private float _gunPtAcc;
        private float _gunPtT;
        private bool _gunPtShot;
        private int _ammoShotN;
        private float _ammoShotNext;

        public void Init(IMachineApi api)
        {
            _api = api;
            Live = this;
            LoadConfig();
            CreateMissileCargo();
            CreateFlareCargo();
            CheckBattleHold();
            CheckBattleCore();
            MarkCombat();
            MissileController.ProbeExplosion();   // 探测原版爆炸结构（导弹爆炸改用原版材质）
            LoadLaunchSfx();                      // 加载空空导弹发射音效
            LoadGunSfx();                         // 加载机炮发射音效
            LoadFlareSfx();                       // 加载热诱弹弹射音效
            _api.Log("MachineAAM ready | cargo=" + cfgCargoName + " weight=" + cfgWeight
                     + " space=" + cfgCargoSpace + " combat=" + cfgCombat
                     + " design=" + cfgDesignFile);
            _api.Log("AAM: AI personas available = " + AiPersonalities.IdsCsv()
                     + " | enabled = " + (cfgAiPersonalities != null && cfgAiPersonalities.Length > 0
                        ? string.Join(",", cfgAiPersonalities) : "(all)"));
            MissileController.TryGetGameSmokeRenderer();   // 主菜单先探一次（探不到也不算数，进飞行场景会重试）
        }

        // =================================================================
        // 发射音效：mods/MachineAAM/audio/launch.wav，导弹离架时播放
        // =================================================================
        private void LoadLaunchSfx()
        {
            try
            {
                string path = Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), "audio", "launch.wav");
                if (!File.Exists(path)) { _api.Log("AAM: no launch sfx at " + path); return; }
                _launchClip = AamWavUtil.Decode(path);
                if (_launchClip == null) { _api.Log("AAM: launch sfx decode failed"); return; }
                var go = new GameObject("AAM_LaunchSfx");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _launchSrc = go.AddComponent<AudioSource>();
                _launchSrc.spatialBlend = 0f;
                _launchSrc.volume = 1f;
                _launchSrc.playOnAwake = false;
                _launchReady = true;
                _api.Log("AAM: launch sfx loaded len=" + _launchClip.length.ToString("F2") + "s");
            }
            catch (Exception e) { _api.Log("AAM: launch sfx error " + e.Message); }
        }

        private void PlayLaunchSfx()
        {
            if (!_launchReady || _launchSrc == null || _launchClip == null) return;
            try
            {
                if (_launchSrc.isPlaying) _launchSrc.Stop();
                _launchSrc.clip = _launchClip;
                _launchSrc.Play();
            }
            catch { }
        }

        // =================================================================
        // 机炮音效：mods/MachineAAM/audio/gun.mp3，机炮连射时节流播放
        // =================================================================
        private void LoadGunSfx()
        {
            try
            {
                string path = Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), "audio", "gun.mp3");
                if (!File.Exists(path)) { _api.Log("AAM: no gun sfx at " + path); return; }
                StartCoroutine(LoadGunSfxCoroutine(path));
            }
            catch (Exception e) { _api.Log("AAM: gun sfx load error " + e.Message); }
        }

        private IEnumerator LoadGunSfxCoroutine(string path)
        {
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace("\\", "/"), AudioType.MPEG))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _api.Log("AAM: gun sfx load failed: " + req.error);
                    yield break;
                }
                _gunSfxClip = DownloadHandlerAudioClip.GetContent(req);
                if (_gunSfxClip == null) { _api.Log("AAM: gun sfx clip null"); yield break; }
                
                // 创建AudioSource池，支持叠加播放
                _gunSfxPool = new AudioSource[GUN_SFX_POOL_SIZE];
                var go = new GameObject("AAM_GunSfxPool");
                UnityEngine.Object.DontDestroyOnLoad(go);
                for (int i = 0; i < GUN_SFX_POOL_SIZE; i++)
                {
                    _gunSfxPool[i] = go.AddComponent<AudioSource>();
                    _gunSfxPool[i].spatialBlend = 0f;
                    _gunSfxPool[i].volume = 0.6f;
                    _gunSfxPool[i].playOnAwake = false;
                }
                _gunSfxReady = true;
                _api.Log("AAM: gun sfx loaded len=" + _gunSfxClip.length.ToString("F2") + "s pool=" + GUN_SFX_POOL_SIZE);
            }
        }

        private void PlayGunSfx()
        {
            if (!_gunSfxReady || _gunSfxPool == null || _gunSfxClip == null) return;
            try
            {
                // 从池中取下一个AudioSource，叠加播放（不停止当前播放）
                AudioSource src = _gunSfxPool[_gunSfxPoolIdx];
                _gunSfxPoolIdx = (_gunSfxPoolIdx + 1) % GUN_SFX_POOL_SIZE;
                if (src.isPlaying) src.Stop();  // 如果这个槽位还在播放，停止它（池只有6个，连射时会循环复用）
                src.clip = _gunSfxClip;
                src.Play();
            }
            catch { }
        }

        // =================================================================
        // 热诱弹弹射音效：mods/MachineAAM/audio/flare.wav，每次弹射播放一次
        // =================================================================
        private void LoadFlareSfx()
        {
            try
            {
                string path = Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), "audio", "flare.wav");
                if (!File.Exists(path)) { _api.Log("AAM: no flare sfx at " + path); return; }
                _flareSfxClip = AamWavUtil.Decode(path);
                if (_flareSfxClip == null) { _api.Log("AAM: flare sfx decode failed"); return; }
                _flareSfxPool = new AudioSource[FLARE_SFX_POOL_SIZE];
                var go = new GameObject("AAM_FlareSfxPool");
                UnityEngine.Object.DontDestroyOnLoad(go);
                for (int i = 0; i < FLARE_SFX_POOL_SIZE; i++)
                {
                    _flareSfxPool[i] = go.AddComponent<AudioSource>();
                    _flareSfxPool[i].spatialBlend = 0f;   // 2D，不随距离衰减（与导弹/机炮音效一致）
                    _flareSfxPool[i].volume = 0.9f;
                    _flareSfxPool[i].playOnAwake = false;
                }
                _flareSfxReady = true;
                _api.Log("AAM: flare sfx loaded len=" + _flareSfxClip.length.ToString("F2") + "s");
            }
            catch (Exception e) { _api.Log("AAM: flare sfx error " + e.Message); }
        }

        /// <summary>播放一次热诱弹弹射音效（玩家与 AI 共用）。连抛时经短池轮流，互不截断。</summary>
        public void PlayFlareSfx()
        {
            if (!_flareSfxReady || _flareSfxPool == null || _flareSfxClip == null) return;
            try
            {
                AudioSource src = _flareSfxPool[_flareSfxIdx];
                _flareSfxIdx = (_flareSfxIdx + 1) % FLARE_SFX_POOL_SIZE;
                if (src.isPlaying) src.Stop();
                src.clip = _flareSfxClip;
                src.Play();
            }
            catch { }
        }

        // =================================================================
        // 配置
        // =================================================================
        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), "aam_config.json");
                if (!File.Exists(path)) { _api.Log("AAM: no aam_config.json, using defaults"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("AAM: bad config json"); return; }

                cfgCargoName = root.GetString("cargoName", cfgCargoName);
                cfgPrice = (float)root.GetNumber("price", cfgPrice);
                cfgWeight = (float)root.GetNumber("weight", cfgWeight);
                cfgCargoSpace = (int)root.GetNumber("cargoSpace", cfgCargoSpace);
                cfgCombat = root.GetBool("combatCargo", cfgCombat);
                cfgGiveOnStart = (int)root.GetNumber("giveOnStart", cfgGiveOnStart);
                cfgDesignFile = root.GetString("designFile", cfgDesignFile);

                cfgAiEnabled = root.GetBool("aiEnabled", cfgAiEnabled);
                cfgAiDesign = root.GetString("aiDesignFile", cfgAiDesign);
                cfgAiSpawnMin = (float)root.GetNumber("aiSpawnMinDelay", cfgAiSpawnMin);
                cfgAiSpawnMax = (float)root.GetNumber("aiSpawnMaxDelay", cfgAiSpawnMax);
                cfgAiMaxAlive = (int)root.GetNumber("aiMaxAlive", cfgAiMaxAlive);
                cfgAiRadar = (float)root.GetNumber("aiRadarRange", cfgAiRadar);
                cfgAiCone = (float)root.GetNumber("aiRadarCone", cfgAiCone);
                cfgAiHostileChance = (float)root.GetNumber("aiHostileChance", cfgAiHostileChance);
                cfgAiDamagePlayer = root.GetBool("aiDamagePlayer", cfgAiDamagePlayer);
                string[] pers = root.GetStringArray("aiPersonalities");
                if (pers != null) cfgAiPersonalities = pers;
                cfgAiSpeedScale = (float)root.GetNumber("aiSpeedScale", cfgAiSpeedScale);
                cfgAiMissile = root.GetString("aiMissileName", cfgAiMissile);
                cfgAiLockMemory = (float)root.GetNumber("aiLockMemory", cfgAiLockMemory);
                cfgAiKinGate = root.GetBool("aiKinematicGate", cfgAiKinGate);
                cfgAiAutoPersona = root.GetBool("aiAutoPersona", cfgAiAutoPersona);

                cfgEvadeEnabled = root.GetBool("evadeEnabled", cfgEvadeEnabled);
                cfgEvadeRange = (float)root.GetNumber("evadeRange", cfgEvadeRange);
                cfgEvadeTTC = (float)root.GetNumber("evadeTTC", cfgEvadeTTC);
                cfgEvadeMissDist = (float)root.GetNumber("evadeMissDist", cfgEvadeMissDist);
                cfgEvadeHold = (float)root.GetNumber("evadeHold", cfgEvadeHold);
                cfgEvadeScan = (float)root.GetNumber("evadeScan", cfgEvadeScan);
                cfgEvadeBeamMix = (float)root.GetNumber("evadeBeamMix", cfgEvadeBeamMix);
                cfgEvadeDropAlt = (float)root.GetNumber("evadeDropAlt", cfgEvadeDropAlt);
                cfgEvadeAgl = (float)root.GetNumber("evadeAgl", cfgEvadeAgl);

                cfgBoostTime = (float)root.GetNumber("boostTime", cfgBoostTime);
                cfgBoostThrust = (float)root.GetNumber("boostThrust", cfgBoostThrust);
                cfgSustainThrust = (float)root.GetNumber("sustainThrust", cfgSustainThrust);
                cfgDragK = (float)root.GetNumber("dragK", cfgDragK);
                cfgMaxSpeed = (float)root.GetNumber("maxSpeed", cfgMaxSpeed);
                cfgMaxG = (float)root.GetNumber("maxG", cfgMaxG);
                cfgTurnRate = (float)root.GetNumber("turnRate", cfgTurnRate);
                cfgLifetime = (float)root.GetNumber("lifetime", cfgLifetime);
                cfgProximity = (float)root.GetNumber("proximity", cfgProximity);
                cfgNavConstant = (float)root.GetNumber("navConstant", cfgNavConstant);
                cfgTargetRange = (float)root.GetNumber("targetRange", cfgTargetRange);

                cfgAvoidEnabled = root.GetBool("avoidEnabled", cfgAvoidEnabled);
                cfgAvoidLookTime = (float)root.GetNumber("avoidLookTime", cfgAvoidLookTime);
                cfgAvoidLookTurn = (float)root.GetNumber("avoidLookTurn", cfgAvoidLookTurn);
                cfgAvoidMinLook = (float)root.GetNumber("avoidMinLook", cfgAvoidMinLook);
                cfgAvoidMaxLook = (float)root.GetNumber("avoidMaxLook", cfgAvoidMaxLook);
                cfgAvoidClearance = (float)root.GetNumber("avoidClearance", cfgAvoidClearance);
                cfgAvoidStrength = (float)root.GetNumber("avoidStrength", cfgAvoidStrength);
                cfgAvoidPnCut = (float)root.GetNumber("avoidPnCut", cfgAvoidPnCut);
                cfgAvoidInterval = (float)root.GetNumber("avoidInterval", cfgAvoidInterval);
                cfgAvoidSpeedStepFrac = (float)root.GetNumber("avoidSpeedStepFrac", cfgAvoidSpeedStepFrac);
                cfgAvoidMaxInterval = (float)root.GetNumber("avoidMaxInterval", cfgAvoidMaxInterval);
                cfgAvoidThreatFast = (float)root.GetNumber("avoidThreatFast", cfgAvoidThreatFast);
                cfgAvoidExtraG = (float)root.GetNumber("avoidExtraG", cfgAvoidExtraG);
                cfgAvoidBrake = (float)root.GetNumber("avoidBrake", cfgAvoidBrake);
                cfgAvoidAbsFloor = (float)root.GetNumber("avoidAbsFloor", cfgAvoidAbsFloor);
                cfgAvoidWarmup = (float)root.GetNumber("avoidWarmup", cfgAvoidWarmup);
                cfgAvoidNose = (float)root.GetNumber("avoidNose", cfgAvoidNose);
                cfgAvoidImpactDetonate = root.GetBool("avoidImpactDetonate", cfgAvoidImpactDetonate);
                cfgAvoidProfileSpan = (float)root.GetNumber("avoidProfileSpan", cfgAvoidProfileSpan);
                cfgAvoidProfileMargin = (float)root.GetNumber("avoidProfileMargin", cfgAvoidProfileMargin);
                cfgAvoidTerminalRange = (float)root.GetNumber("avoidTerminalRange", cfgAvoidTerminalRange);
                cfgAvoidTerminalMin = (float)root.GetNumber("avoidTerminalMin", cfgAvoidTerminalMin);
                cfgAvoidTest = root.GetBool("avoidTest", cfgAvoidTest);
                cfgEvadeTest = root.GetBool("evadeTest", cfgEvadeTest);

                // 红外系统
                cfgIrEnabled = root.GetBool("irEnabled", cfgIrEnabled);
                cfgIrThrottleK = (float)root.GetNumber("irThrottleK", cfgIrThrottleK);
                cfgIrSpeedK = (float)root.GetNumber("irSpeedK", cfgIrSpeedK);
                cfgIrHeatK = (float)root.GetNumber("irHeatK", cfgIrHeatK);
                cfgIrCoolRate = (float)root.GetNumber("irCoolRate", cfgIrCoolRate);
                cfgIrCoolRef = (float)root.GetNumber("irCoolRef", cfgIrCoolRef);
                cfgIrLog = root.GetBool("irLog", cfgIrLog);
                // 红外导引头（全向锁定 + 缩圈 IRCCM）
                cfgSeekerEnabled = root.GetBool("seekerEnabled", cfgSeekerEnabled);
                cfgSeekerFov = (float)root.GetNumber("seekerFov", cfgSeekerFov);
                cfgSeekerRange = (float)root.GetNumber("seekerRange", cfgSeekerRange);
                cfgIrcmEnabled = root.GetBool("ircmEnabled", cfgIrcmEnabled);
                cfgIrcmReact = (float)root.GetNumber("ircmReact", cfgIrcmReact);
                cfgGateWide = (float)root.GetNumber("gateWide", cfgGateWide);
                cfgGateNarrow = (float)root.GetNumber("gateNarrow", cfgGateNarrow);
                cfgGateShrink = (float)root.GetNumber("gateShrink", cfgGateShrink);
                cfgIrcmDecoyPenalty = (float)root.GetNumber("ircmDecoyPenalty", cfgIrcmDecoyPenalty);
                cfgIrcmSpeedFrac = (float)root.GetNumber("ircmSpeedFrac", cfgIrcmSpeedFrac);
                // 热诱弹
                cfgFlareEnabled = root.GetBool("flareEnabled", cfgFlareEnabled);
                cfgFlareKey = root.GetString("flareKey", cfgFlareKey);
                cfgFlareDesign = root.GetString("flareDesign", cfgFlareDesign);
                cfgFlarePrice = (float)root.GetNumber("flarePrice", cfgFlarePrice);
                cfgFlareWeight = (float)root.GetNumber("flareWeight", cfgFlareWeight);
                cfgFlareSpace = (int)root.GetNumber("flareSpace", cfgFlareSpace);
                cfgFlareGive = (int)root.GetNumber("flareGive", cfgFlareGive);
                cfgFlareBurst = (int)root.GetNumber("flareBurst", cfgFlareBurst);
                cfgFlareBurstGap = (float)root.GetNumber("flareBurstGap", cfgFlareBurstGap);
                cfgFlareCooldown = (float)root.GetNumber("flareCooldown", cfgFlareCooldown);
                cfgFlareDrag = (float)root.GetNumber("flareDrag", cfgFlareDrag);
                cfgFlareEjectDown = (float)root.GetNumber("flareEjectDown", cfgFlareEjectDown);
                cfgFlareEjectBack = (float)root.GetNumber("flareEjectBack", cfgFlareEjectBack);
                cfgFlareEjectSide = (float)root.GetNumber("flareEjectSide", cfgFlareEjectSide);
                cfgFlareLights = (int)root.GetNumber("flareLights", cfgFlareLights);
                cfgFlareTest = root.GetBool("flareTest", cfgFlareTest);
                cfgPlayerHitTest = root.GetBool("playerHitTest", cfgPlayerHitTest);
                cfgAiFlareEnabled = root.GetBool("aiFlareEnabled", cfgAiFlareEnabled);
                cfgAiFlareStock = (int)root.GetNumber("aiFlareStock", cfgAiFlareStock);
                cfgAiFlareBurst = (int)root.GetNumber("aiFlareBurst", cfgAiFlareBurst);
                cfgAiFlareCooldown = (float)root.GetNumber("aiFlareCooldown", cfgAiFlareCooldown);
                cfgAiFlareDelay = (float)root.GetNumber("aiFlareDelay", cfgAiFlareDelay);

                cfgAutoTest = root.GetBool("autoTest", cfgAutoTest);
                cfgAutoEnterFlyMode = root.GetBool("autoEnterFlyMode", cfgAutoEnterFlyMode);
                cfgCapture = root.GetBool("captureScreenshots", cfgCapture);
                cfgAiPersonaTest = root.GetBool("aiPersonaTest", cfgAiPersonaTest);
                cfgAiCombatTest = root.GetBool("aiCombatTest", cfgAiCombatTest);
                cfgAiCombatDist = (float)root.GetNumber("aiCombatDist", cfgAiCombatDist);
                cfgAiCombatAlt = (float)root.GetNumber("aiCombatAlt", cfgAiCombatAlt);
                cfgAiCombatPersona = root.GetString("aiCombatPersona", cfgAiCombatPersona);
                cfgAiCombatSeconds = (int)root.GetNumber("aiCombatSeconds", cfgAiCombatSeconds);
                cfgTestObserve = (float)root.GetNumber("testObserveSeconds", cfgTestObserve);
                cfgTestAutoLoadSave = root.GetBool("testAutoLoadSave", cfgTestAutoLoadSave);
                cfgTestSaveName = root.GetString("testSaveName", cfgTestSaveName);
                cfgTestAmmo = root.GetBool("testAmmo", cfgTestAmmo);
                cfgGunEnabled = root.GetBool("gunEnabled", cfgGunEnabled);
                cfgGunRps = (float)root.GetNumber("gunRoundsPerSecond", cfgGunRps);
                cfgGunHoldDelay = (float)root.GetNumber("gunHoldDelay", cfgGunHoldDelay);
                cfgGunMuzzle = (float)root.GetNumber("gunMuzzleSpeed", cfgGunMuzzle);
                cfgGunLife = (float)root.GetNumber("gunBulletLife", cfgGunLife);
                cfgGunDrag = (float)root.GetNumber("gunDrag", cfgGunDrag);
                cfgGunSpread = (float)root.GetNumber("gunSpreadDeg", cfgGunSpread);
                cfgGunMaxLive = (int)root.GetNumber("gunMaxLive", cfgGunMaxLive);
                cfgGunHitRadius = (float)root.GetNumber("gunHitRadius", cfgGunHitRadius);
                cfgBulletScale = (float)root.GetNumber("gunBulletScale", cfgBulletScale);
                cfgShotDir = root.GetString("shotDir", cfgShotDir);
                cfgTestDroneSpeed = (float)root.GetNumber("testDroneSpeed", cfgTestDroneSpeed);

                // ---- 静态下发统一放这里：必须在上面所有 GetXxx 读完之后执行，
                // 否则下发的是字段默认值，配置文件里的 ir* / flare* / evade* 全都白写。----
                // AI 飞机规避（静态共享，所有 AI 共用一份）
                AiAircraft.EvadeCfg.Enabled = cfgEvadeEnabled;
                AiAircraft.EvadeCfg.Range = cfgEvadeRange;
                AiAircraft.EvadeCfg.TTC = cfgEvadeTTC;
                AiAircraft.EvadeCfg.MissDist = cfgEvadeMissDist;
                AiAircraft.EvadeCfg.Hold = cfgEvadeHold;
                AiAircraft.EvadeCfg.Scan = cfgEvadeScan;
                AiAircraft.EvadeCfg.BeamMix = cfgEvadeBeamMix;
                AiAircraft.EvadeCfg.DropAlt = cfgEvadeDropAlt;
                AiAircraft.EvadeCfg.Agl = cfgEvadeAgl;

                // 红外系统：公式系数与逼近参数（静态共享）
                IrSignature.Enabled = cfgIrEnabled;
                AiAircraft.AiIrLog = cfgIrLog;      // irLog 同时打开 AI 遥测里的红外字段
                IrSignature.ThrottleK = cfgIrThrottleK;
                IrSignature.SpeedK = cfgIrSpeedK;
                IrSignature.HeatK = cfgIrHeatK;
                IrSignature.CoolRate = cfgIrCoolRate;
                IrSignature.CoolRef = cfgIrCoolRef;

                // 热诱弹：抛放与飞行参数
                IrFlare.DragK = cfgFlareDrag;
                IrFlare.EjectDown = cfgFlareEjectDown;
                IrFlare.EjectBack = cfgFlareEjectBack;
                IrFlare.EjectSide = cfgFlareEjectSide;
                IrFlare.LightBudget = cfgFlareLights;

                // AI 自卫抛饵（静态共享，所有 AI 共用一份；改配置即可调强度）
                AiAircraft.FlareEnabled = cfgAiFlareEnabled;
                AiAircraft.FlareStock = cfgAiFlareStock;
                AiAircraft.FlareBurst = cfgAiFlareBurst;
                AiAircraft.FlareCooldown = cfgAiFlareCooldown;
                AiAircraft.FlareDelay = cfgAiFlareDelay;

                // AI 空战强化（2026-09-15）：锁定记忆 / 发射可行性门 / 自动人格
                BanditAircraft.LockMemory = cfgAiLockMemory;
                BanditAircraft.KinematicGate = cfgAiKinGate;
                AiAircraft.AutoPersona = cfgAiAutoPersona;
                AiAircraft.AutoPersonaSpeedScale = cfgAiSpeedScale;

                _api.Log("AAM: config loaded");
            }
            catch (Exception e) { _api.Log("AAM: config error " + e.Message); }
        }

        private string DesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgDesignFile); }
        }

        /// <summary>某个弹种自己的模型文件路径（未指定时回落到全局 designFile）。</summary>
        private string DesignPathFor(MissileSpec spec)
        {
            string f = (spec != null && !string.IsNullOrEmpty(spec.Design)) ? spec.Design : cfgDesignFile;
            return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), f);
        }

        /// <summary>按型号名找弹种规格（找不到返回 null）。</summary>
        private MissileSpec SpecByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _specs.Count; i++)
                if (string.Equals(_specs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _specs[i];
            return null;
        }

        // =================================================================
        // 货物
        // =================================================================
        private void DefineSpecs()
        {
            // 速度单位：节（1 马赫 ≈ 660 节）。用户 2026-09-16 最新参数（含扭矩、最大过载、惯性阻力、加速度、跟踪速率）：
            //   PL-15   15s / 2.85马赫(1881节) / 质量4  / 大小45 / 扭矩60 / 最大过载53 / 阻力100 / 加速度0.57马赫/s / 跟踪速率52°/s
            //   PL-10   11s / 2.00马赫(1320节) / 质量2  / 大小40 / 扭矩82 / 最大过载63 / 阻力50  / 加速度0.97马赫/s / 跟踪速率96°/s
            //   PL-17   25s / 2.67马赫(1762节) / 质量6  / 大小100/ 扭矩53 / 最大过载52 / 阻力90  / 加速度0.31马赫/s / 跟踪速率33°/s
            //   AIM-9X  10s / 2.15马赫(1419节) / 质量2  / 大小40 / 扭矩86 / 最大过载64 / 阻力83  / 加速度0.90马赫/s / 跟踪速率97°/s
            //   R-77    17s / 2.65马赫(1749节) / 质量4  / 大小45 / 扭矩72 / 最大过载56 / 阻力160 / 加速度0.71马赫/s / 跟踪速率56°/s
            //   MSDM    8s  / 3.20马赫(2112节) / 质量1  / 大小35 / 扭矩98 / 最大过载65 / 阻力200 / 加速度0.99马赫/s / 跟踪速率119°/s （拦截弹）
            //   R-37    23s / 2.90马赫(1914节) / 质量6  / 大小120/ 扭矩58 / 最大过载48 / 阻力105 / 加速度0.42马赫/s / 跟踪速率31°/s
            //   AIM-424 18s / 2.70马赫(1782节) / 质量5  / 大小80 / 扭矩68 / 最大过载55 / 阻力95  / 加速度0.49马赫/s / 跟踪速率45°/s
            //   Gun     6.3s/ 2.40马赫(1584节) / 质量0.025 / 大小1 （机炮子弹）
            // Design 为空则回落到 aam_config.json 的 designFile。
            // BattleRating 推导（1.0~12.0，类 WarThunder）：
            //   effRange = MaxSpeed * Lifetime (节·秒 ≈ 米级有效射程)
            //   BR = effRange/3000 + 速度档奖励( (MaxSpeed-1200)/200*0.1, 上限 0.3 )；Gun 按近战角色手动 2.0
            _specs.Clear();
            _specs.Add(new MissileSpec { Name = "PL-15", Price = 12000f, Weight = 4f, Space = 45, Lifetime = 15f, MaxSpeed = 1881f, TurnRateDeg = 60f, MaxG = 53f, CoastDrag = 100f, Acceleration = 0.57f, TrackRate = 52f, Design = "PL-15.planedesign", RangeCat = MissileRangeCategory.Medium, BattleRating = 7.2f });
            _specs.Add(new MissileSpec { Name = "PL-10", Price = 6000f, Weight = 2f, Space = 40, Lifetime = 11f, MaxSpeed = 1320f, TurnRateDeg = 82f, MaxG = 63f, CoastDrag = 50f, Acceleration = 0.97f, TrackRate = 96f, Design = "PL-15.planedesign", RangeCat = MissileRangeCategory.Short, BattleRating = 4.2f });
            _specs.Add(new MissileSpec { Name = "PL-17", Price = 18000f, Weight = 6f, Space = 100, Lifetime = 25f, MaxSpeed = 1762f, TurnRateDeg = 53f, MaxG = 52f, CoastDrag = 90f, Acceleration = 0.31f, TrackRate = 33f, Design = "PL-15.planedesign", RangeCat = MissileRangeCategory.Long, BattleRating = 10.5f });
            _specs.Add(new MissileSpec { Name = "R-77", Price = 12000f, Weight = 4f, Space = 45, Lifetime = 17f, MaxSpeed = 1749f, TurnRateDeg = 72f, MaxG = 56f, CoastDrag = 160f, Acceleration = 0.71f, TrackRate = 56f, Design = "R-77.planedesign", RangeCat = MissileRangeCategory.Medium, BattleRating = 7.0f });
            _specs.Add(new MissileSpec { Name = "AIM-9X", Price = 8000f, Weight = 2f, Space = 40, Lifetime = 10f, MaxSpeed = 1419f, TurnRateDeg = 86f, MaxG = 64f, CoastDrag = 83f, Acceleration = 0.90f, TrackRate = 97f, Design = "AIM-9.planedesign", RangeCat = MissileRangeCategory.Short, BattleRating = 4.0f });
            _specs.Add(new MissileSpec { Name = "MSDM", Price = 15000f, Weight = 1f, Space = 35, Lifetime = 8f, MaxSpeed = 2112f, TurnRateDeg = 98f, MaxG = 65f, CoastDrag = 200f, Acceleration = 0.99f, TrackRate = 119f, Design = "MSDM.planedesign", AntiMissile = true, BattleRating = 4.5f });
            _specs.Add(new MissileSpec { Name = "R-37", Price = 20000f, Weight = 6f, Space = 120, Lifetime = 23f, MaxSpeed = 1914f, TurnRateDeg = 58f, MaxG = 48f, CoastDrag = 105f, Acceleration = 0.42f, TrackRate = 31f, Design = "R-37.planedesign", RangeCat = MissileRangeCategory.Long, BattleRating = 9.8f });
            _specs.Add(new MissileSpec { Name = "AIM-424", Price = 16000f, Weight = 5f, Space = 80, Lifetime = 18f, MaxSpeed = 1782f, TurnRateDeg = 68f, MaxG = 55f, CoastDrag = 95f, Acceleration = 0.49f, TrackRate = 45f, Design = "AIM-424.planedesign", RangeCat = MissileRangeCategory.Medium, BattleRating = 8.0f });
            _specs.Add(new MissileSpec { Name = "Gun", Price = 100f, Weight = 0.025f, Space = 1, Lifetime = 6.3f, MaxSpeed = 1584f, TurnRateDeg = 50f, MaxG = 40f, CoastDrag = 100f, Acceleration = 0.8f, TrackRate = 60f, Design = "Bullet.planedesign", BattleRating = 2.0f });
        }

        private void CreateMissileCargo()
        {
            try
            {
                DefineSpecs();
                _missileTypes.Clear();
                foreach (var spec in _specs)
                {
                    var ct = ScriptableObject.CreateInstance<CargoType>();
                    ct.cargoName = spec.Name;
                    ct.basePrice = spec.Price;
                    ct.weight = spec.Weight;
                    ct.cargoSpace = spec.Space;
                    ct.fragile = false;
                    ct.expires = false;
                    ct.icon = MakeIcon(64);
                    UnityEngine.Object.DontDestroyOnLoad(ct);
                    _missileTypes.Add(ct);
                    _api.Log("AAM: cargo created [" + spec.Name + "] weight=" + spec.Weight + " space=" + spec.Space);
                }
                // 兼容旧字段：默认当前弹型 = 第一个
                if (_missileTypes.Count > 0)
                {
                    _missileType = _missileTypes[0];
                    _currentSpec = _specs[0];
                }
            }
            catch (Exception e) { _api.Log("AAM: create cargo failed " + e.Message); }
        }

        // =================================================================
        // 热诱弹（2026-09-14）
        // 走出一条和空空导弹完全不同的路：不点火、不制导，纯抛射 + 下坠，
        // 靠 210 的红外值（高于飞机上限 200）把红外导引头抢过去。
        // =================================================================
        private CargoType _flareType;
        private float _flareCd;          // 投放冷却
        private int _flarePending;       // 本次还剩几枚待抛
        private float _flareGapT;        // 连抛计时
        private IrSignature _playerIr;
        private float _irLogT;

        // 跨 mod 调用 VoiceAlerts 走反射（本工程约定：不加编译期引用，缺失时静默降级）
        private static MethodInfo _vaPlay;
        private static bool _vaProbed;

        private static void PlayVoiceClip(string clip, float cooldown)
        {
            try
            {
                if (!_vaProbed)
                {
                    _vaProbed = true;
                    Type vt = Type.GetType("VoiceAlertsMod.VoiceAlertsApi, VoiceAlerts");
                    if (vt == null)
                    {
                        // ⚠ 本机加载器加载的 mod 程序集，GetName().Name 与 "VoiceAlerts" 对不上，
                        //   所以按"类型全名"扫所有已加载程序集。
                        Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                        for (int i = 0; i < asms.Length; i++)
                        {
                            try { vt = asms[i].GetType("VoiceAlertsMod.VoiceAlertsApi"); } catch { vt = null; }
                            if (vt != null) break;
                        }
                    }
                    if (vt != null) _vaPlay = vt.GetMethod("Play",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new Type[] { typeof(string), typeof(float) }, null);
                }
                if (_vaPlay != null) _vaPlay.Invoke(null, new object[] { clip, cooldown });
            }
            catch { }
        }

        private string FlareDesignPath()
        {
            return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgFlareDesign);
        }

        private void CreateFlareCargo()
        {
            if (!cfgFlareEnabled) return;
            try
            {
                if (_flareType == null)
                {
                    _flareType = ScriptableObject.CreateInstance<CargoType>();
                    _flareType.cargoName = "Flare";
                    _flareType.basePrice = cfgFlarePrice;
                    _flareType.weight = cfgFlareWeight;
                    _flareType.cargoSpace = cfgFlareSpace;
                    _flareType.fragile = false;
                    _flareType.expires = false;
                    UnityEngine.Object.DontDestroyOnLoad(_flareType);
                }
                if (!_bhChecked) CheckBattleHold();
                if (_bhMarkByType != null) _bhMarkByType.Invoke(null, new object[] { _flareType });
                else if (_bhMarkByName != null) _bhMarkByName.Invoke(null, new object[] { "Flare" });
                _api.Log("AAM: flare cargo ready (design=" + cfgFlareDesign + ")");
            }
            catch (Exception e) { _api.Log("AAM: create flare cargo failed " + e.Message); }
        }

        public int FlareCount()
        {
            if (_flareType == null) return 0;
            if (!_bhChecked) CheckBattleHold();
            if (_bhCount == null) return 0;
            try { return (int)_bhCount.Invoke(null, new object[] { _flareType }); }
            catch { return 0; }
        }

        private bool FlareTake(int n)
        {
            if (_flareType == null || _bhRemove == null) return false;
            try { return (bool)_bhRemove.Invoke(null, new object[] { _flareType, n }); }
            catch { return false; }
        }

        private bool FlareGive(int n)
        {
            if (_flareType == null || _bhAdd == null) return false;
            try { return (bool)_bhAdd.Invoke(null, new object[] { _flareType, n }); }
            catch { return false; }
        }

        /// <summary>抛热诱弹：一次 cfgFlareBurst 枚，按 cfgFlareBurstGap 秒间隔连抛。</summary>
        public bool DispenseFlare()
        {
            if (!cfgFlareEnabled) return false;
            if (_plane == null || !_plane.FlightModeInitialized) return false;
            if (_flareCd > 0f) return false;
            int have = FlareCount();
            if (have <= 0) { Flash("NO FLARE", 1.2f); return false; }
            _flarePending = Mathf.Min(cfgFlareBurst, have);
            _flareGapT = 0f;
            _flareCd = cfgFlareCooldown;
            _api.Log("AAM: dispensing " + _flarePending + " flare(s), stock=" + have);
            // 播放"发射干扰弹"语音（仅玩家，AI 不播报；1s 冷却避免连抛重复）
            PlayVoiceClip("flare", 1f);
            return true;
        }

        /// <summary>
        /// 造一个热诱弹模型实例（玩家与 AI 共用）。模型文件是 decoy flare.planedesign。
        /// 拿不到就退回占位体 —— 红外与特效照常工作，功能不受影响。
        /// </summary>
        public GameObject BuildFlareModel()
        {
            try
            {
                string path = FlareDesignPath();
                GameObject model = null;
                try
                {
                    model = DesignModel.BuildWithGameLoader(path, "AAM_Flare", _api, false, false, true);
                    if (model == null) model = DesignModel.BuildFallback(path, "AAM_Flare", _api);
                }
                catch { }
                if (model == null)
                {
                    model = new GameObject("AAM_Flare");
                    _api.Log("AAM: flare model unavailable, using placeholder");
                }
                try
                {
                    Collider[] cols = model.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < cols.Length; i++) cols[i].enabled = false;
                }
                catch { }
                return model;
            }
            catch (Exception e) { _api.Log("AAM: build flare model failed " + e.Message); return null; }
        }

        /// <summary>真正抛出单枚热诱弹（由 Update 按间隔调用）。</summary>
        private void SpawnOneFlare()
        {
            try
            {
                if (_plane == null) return;
                GameObject model = BuildFlareModel();
                if (model == null) return;
                Vector3 vel = _plane.GetVelocity();
                var fl = IrFlare.Dispense(model, _plane.transform, vel);
                if (fl != null) { FlareTake(1); PlayFlareSfx(); }   // 热诱弹弹射音效（玩家）
                else Destroy(model);
            }
            catch (Exception e) { _api.Log("AAM: spawn flare failed " + e.Message); }
        }

        /// <summary>每帧：投放冷却、连抛间隔、玩家机红外源。</summary>
        private void FlareTick(float dt)
        {
            if (_flareCd > 0f) _flareCd -= dt;
            if (_flarePending > 0)
            {
                _flareGapT -= dt;
                if (_flareGapT <= 0f)
                {
                    SpawnOneFlare();
                    _flarePending--;
                    _flareGapT = cfgFlareBurstGap;
                }
            }
        }

        /// <summary>
        /// 玩家机的红外源：期望值 = 油门(0~100)×ThrottleK + 速度×SpeedK，
        /// 实际值由 IrSignature.Step 做非线性逼近。
        /// </summary>
        private void PlayerIrTick(float dt)
        {
            if (!cfgIrEnabled || _plane == null) return;
            try
            {
                if (_playerIr == null || _playerIr.gameObject != _plane.gameObject)
                    _playerIr = IrSignature.Ensure(_plane.gameObject);
                if (_playerIr == null) return;

                float thr = PlayerThrottlePct();
                Vector3 v = _plane.GetVelocity();
                _playerIr.Vel = v;
                _playerIr.Feed(thr, v.magnitude, dt);

                if (cfgIrLog)
                {
                    _irLogT -= dt;
                    if (_irLogT <= 0f)
                    {
                        _irLogT = 2f;
                        _api.Log("AAM IR: thr=" + thr.ToString("F0") + " spd=" + v.magnitude.ToString("F0")
                                 + " exp=" + _playerIr.Expected.ToString("F1")
                                 + " act=" + _playerIr.Actual.ToString("F1")
                                 + " flares=" + FlareCount() + " srcs=" + IrRegistry.Count);
                    }
                }
            }
            catch { }
        }

        /// <summary>玩家油门 0~100：读引擎实际的 currentThrottle（0~1）取平均。</summary>
        private float PlayerThrottlePct()
        {
            try
            {
                if (_plane == null) return 0f;
                if (_playerEngines == null || _playerEngines.Length == 0)
                    _playerEngines = _plane.GetComponentsInChildren<Engine>(true);
                if (_playerEngines == null || _playerEngines.Length == 0) return 0f;
                float sum = 0f;
                for (int i = 0; i < _playerEngines.Length; i++)
                {
                    if (_playerEngines[i] == null) continue;
                    sum += _playerEngines[i].currentThrottle;
                }
                return Mathf.Clamp(sum / _playerEngines.Length, 0f, 1f) * 100f;
            }
            catch { return 0f; }
        }
        private Engine[] _playerEngines;

        private Texture2D MakeIcon(int size)
        {
            if (_icon != null) return _icon;
            // 优先从外部图片文件加载（mods/MachineAAM/icons/missile.png）
            Texture2D fileIcon = LoadIconFromFile("missile.png");
            if (fileIcon != null)
            {
                Machine.Core.Log.Info("[AAM] icon loaded from file missile.png (" + fileIcon.width + "x" + fileIcon.height + ")");
                _icon = fileIcon;
                return _icon;
            }
            // 兜底：代码生成图标
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - size * 0.5f) / (size * 0.5f);
                    float ny = (y - size * 0.5f) / (size * 0.5f);
                    Color col = new Color(0.10f, 0.12f, 0.15f, 1f);
                    // 弹体：竖直细长
                    if (Mathf.Abs(nx) < 0.10f && ny > -0.72f && ny < 0.62f) col = new Color(0.86f, 0.87f, 0.90f, 1f);
                    // 弹头
                    if (ny > 0.62f && ny < 0.90f && Mathf.Abs(nx) < (0.90f - ny) * 1.1f) col = new Color(0.92f, 0.30f, 0.22f, 1f);
                    // 尾翼
                    if (ny > -0.90f && ny < -0.55f && Mathf.Abs(nx) > 0.10f && Mathf.Abs(nx) < 0.34f) col = new Color(0.70f, 0.72f, 0.76f, 1f);
                    // 中部小翼
                    if (ny > 0.05f && ny < 0.30f && Mathf.Abs(nx) > 0.10f && Mathf.Abs(nx) < 0.26f) col = new Color(0.70f, 0.72f, 0.76f, 1f);
                    tex.SetPixel(x, y, col);
                }
            }
            tex.Apply();
            _icon = tex;
            return tex;
        }

        /// <summary>从 mods/MachineAAM/icons/ 目录加载PNG图标文件，失败返回null。</summary>
        private Texture2D LoadIconFromFile(string fileName)
        {
            try
            {
                string iconDir = Path.Combine(_api.GetModsDirectory(), "MachineAAM", "icons");
                string path = Path.Combine(iconDir, fileName);
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(data))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    return tex;
                }
                return null;
            }
            catch (Exception e)
            {
                Machine.Core.Log.Info("[AAM] load icon failed " + fileName + " - " + e.Message);
                return null;
            }
        }

        /// <summary>把全部导弹型号挂到各机场的货物表里（与 Radar 部件的做法一致）。</summary>
        private bool InjectAirports()
        {
            if (_missileTypes.Count == 0) return false;
            try
            {
                AirportManager am = AirportManager.Instance;
                if (am == null || am.airports == null) return false;
                int n = 0;
                for (int i = 0; i < am.airports.Count; i++)
                {
                    Airport ap = am.airports[i];
                    if (ap == null) continue;
                    CargoType[] arr = null;
                    try { arr = ap.cargoType; } catch { }
                    if (arr == null)
                    {
                        FieldInfo f = typeof(Airport).GetField("cargoType",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) arr = f.GetValue(ap) as CargoType[];
                    }
                    if (arr == null) continue;

                    bool changed = false;
                    var list = new List<CargoType>(arr);
                    foreach (var mt in _missileTypes)
                    {
                        if (!list.Contains(mt)) { list.Add(mt); changed = true; }
                    }
                    if (!changed) continue;

                    var na = list.ToArray();
                    try { ap.cargoType = na; }
                    catch
                    {
                        FieldInfo f = typeof(Airport).GetField("cargoType",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) f.SetValue(ap, na);
                    }
                    n++;
                }
                _api.Log("AAM: missile cargo (" + _missileTypes.Count + " types) added to " + n + " airports");
                return true;
            }
            catch (Exception e) { _api.Log("AAM: inject airports failed " + e.Message); return false; }
        }

        // =================================================================
        // BattleHold / BattleCore
        // =================================================================
        private static Type FindModType(string asmName, string typeName)
        {
            Type t = Type.GetType(typeName + ", " + asmName);
            if (t != null) return t;
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    if (asms[i].GetName().Name == asmName) { t = asms[i].GetType(typeName); if (t != null) return t; }
                }
            }
            catch { }
            return null;
        }

        private void CheckBattleHold()
        {
            if (_bhChecked) return;
            _bhChecked = true;
            try
            {
                Type t = FindModType("BattleHold", "Machine.BattleHold.BattleHoldApi");
                if (t == null) { _api.Log("AAM: BattleHold NOT available (degraded)"); return; }
                _bhMarkByType = t.GetMethod("MarkCombat", new Type[] { typeof(CargoType) });
                _bhMarkByName = t.GetMethod("MarkCombat", new Type[] { typeof(string) });
                _bhCount = t.GetMethod("Count", new Type[] { typeof(CargoType) });
                _bhAdd = t.GetMethod("Add", new Type[] { typeof(CargoType), typeof(int) });
                _bhRemove = t.GetMethod("Remove", new Type[] { typeof(CargoType), typeof(int) });
                _bhContains = t.GetMethod("Contains", new Type[] { typeof(CargoType) });
                _bhOk = _bhCount != null && _bhAdd != null && _bhRemove != null;
                _api.Log("AAM: BattleHold available=" + _bhOk);
            }
            catch (Exception e) { _api.Log("AAM: BattleHold probe failed " + e.Message); }
        }

        private void CheckBattleCore()
        {
            if (_bcChecked) return;
            _bcChecked = true;
            try
            {
                Type t = FindModType("BattleCore", "Machine.BattleCore.Core");
                if (t == null) { _api.Log("AAM: BattleCore NOT available (degraded)"); return; }
                _bcPost = t.GetMethod("Post", new Type[] { typeof(string), typeof(string), typeof(string), typeof(int) });
                _bcOk = _bcPost != null;
                _api.Log("AAM: BattleCore available=" + _bcOk);
            }
            catch (Exception e) { _api.Log("AAM: BattleCore probe failed " + e.Message); }
        }

        private void MarkCombat()
        {
            if (!cfgCombat || _missileTypes.Count == 0) return;
            if (!_bhChecked) CheckBattleHold();
            try
            {
                foreach (var mt in _missileTypes)
                {
                    if (_bhMarkByType != null) _bhMarkByType.Invoke(null, new object[] { mt });
                    else if (_bhMarkByName != null) _bhMarkByName.Invoke(null, new object[] { mt.cargoName });
                }
                _api.Log("AAM: all missile types marked as combat cargo (" + _missileTypes.Count + ")");
            }
            catch (Exception e) { _api.Log("AAM: mark combat failed " + e.Message); }
        }

        /// <summary>当前弹型：玩家用 R 键选中的型号（_selIndex）。</summary>
        public MissileSpec CurrentSpec()
        {
            if (_specs.Count == 0) return null;
            int idx = _selIndex;
            if (idx < 0 || idx >= _specs.Count) idx = 0;
            if (idx < _missileTypes.Count) _missileType = _missileTypes[idx];
            _currentSpec = _specs[idx];
            return _currentSpec;
        }

        /// <summary>R 键 / 鼠标滚轮循环切换发射弹种。</summary>
        private void HandleMissileSelect()
        {
            try
            {
                if (_specs.Count == 0) return;
                // R 键：下一个
                if (UnityEngine.Input.GetKeyDown(KeyCode.R))
                {
                    SelectMissile(_selIndex + 1);
                }
                // 鼠标滚轮：向上=下一个，向下=上一个
                float scroll = UnityEngine.Input.mouseScrollDelta.y;
                if (scroll > 0.1f)
                {
                    SelectMissile(_selIndex + 1);
                }
                else if (scroll < -0.1f)
                {
                    SelectMissile(_selIndex - 1);
                }
            }
            catch { }
        }

        /// <summary>选中指定弹种（供 R 键与战斗货仓面板点击调用）。</summary>
        public void SelectMissile(int idx)
        {
            if (_specs.Count == 0) return;
            int n = ((idx % _specs.Count) + _specs.Count) % _specs.Count;
            _selIndex = n;
            CurrentSpec();
            _api.Log("AAM: selected " + _currentSpec.Name + " (" + _selIndex + "/" + _specs.Count + ")");
        }

        /// <summary>静态版：战斗货仓面板反射调用。</summary>
        public static void SelectMissileStatic(int idx)
        {
            var sys = Live;
            if (sys != null) sys.SelectMissile(idx);
        }

        /// <summary>导弹仓按钮信息（供战斗货仓面板点击区与 HUD）：每行 "名|索引|是否选中|数量"，只含库存>0 的弹种。</summary>
        public static string MissileButtonLines()
        {
            try
            {
                var sys = Live;
                if (sys == null || sys._specs.Count == 0 || sys._missileTypes.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sys._specs.Count; i++)
                {
                    int c = 0;
                    try { c = sys.CombatCountOf(sys._missileTypes[i]); } catch { }
                    if (c <= 0) continue;
                    sb.Append(UiFactory.Safe(sys._specs[i].Name)).Append('|').Append(i).Append('|')
                      .Append(i == sys._selIndex ? "1" : "0").Append('|').Append(c).Append('\n');
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        public int CombatCount()
        {
            if (_missileTypes.Count == 0) return 0;
            if (!_bhChecked) CheckBattleHold();
            if (_bhCount == null) return 0;
            var spec = CurrentSpec();
            if (spec == null || _missileType == null) return 0;
            try { return (int)_bhCount.Invoke(null, new object[] { _missileType }); }
            catch { return 0; }
        }

        private bool CombatTake(int n)
        {
            if (_bhRemove == null || _missileType == null) return false;
            try { return (bool)_bhRemove.Invoke(null, new object[] { _missileType, n }); }
            catch { return false; }
        }

        private bool CombatGive(int n)
        {
            if (_bhAdd == null || _missileType == null) return false;
            try { return (bool)_bhAdd.Invoke(null, new object[] { _missileType, n }); }
            catch { return false; }
        }

        private void PostCore(string key, string val, int sev)
        {
            if (!_bcChecked) CheckBattleCore();
            if (!_bcOk || _bcPost == null) return;
            try { _bcPost.Invoke(null, new object[] { "AAM", key, val, sev }); } catch { }
        }

        /// <summary>导弹结果上报（供导弹实体反射调用）：HIT/MISS 显示在战斗部。</summary>
        public void PostMissileResult(string text, int sev)
        {
            PostCore("Weapon", text, sev);
        }

        /// <summary>当前场景所有在飞导弹 Transform（优先注册表缓存，零全场景扫描；兜底反射扫描）。</summary>
        public static List<Transform> GetLiveMissiles()
        {
            var list = new List<Transform>();
            try
            {
                // 注册表缓存：导弹 OnEnable/OnDisable 自动登记/移除，直接读即可
                var reg = MissileController.RegistrySnapshot();
                if (reg.Count > 0)
                {
                    for (int i = 0; i < reg.Count; i++)
                    {
                        var m = reg[i];
                        if (m != null && m.transform != null) list.Add(m.transform);
                    }
                    return list;
                }
                // 兜底：注册表为空（如导弹在注册表机制前生成）时全场景扫描一次
                var arr = UnityEngine.Object.FindObjectsOfType<MissileController>();
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i];
                    if (m == null) continue;
                    Transform t = m.transform;
                    if (t != null) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        /// <summary>导弹发射时通知雷达系统播报 Fox 1（反射调用 RadarApi.NotifyMissileLaunch，前置缺失自动降级）。</summary>
        private void TryNotifyLaunch()
        {
            try
            {
                var t = Type.GetType("Machine.Radar.RadarApi, Radar");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "Radar") { t = asm.GetType("Machine.Radar.RadarApi"); break; }
                    }
                }
                if (t == null) return;
                var m = t.GetMethod("NotifyMissileLaunch", Type.EmptyTypes);
                if (m != null) m.Invoke(null, null);
            }
            catch { }
        }

        // =================================================================
        // 弹药显示（已并入 BattleHold 战斗货仓面板）
        // =================================================================
        // 这里曾经有一个右下角的独立浮窗（"PL-15 xN" 大字号），会挡住飞行视野，已移除。
        // 弹药（类型 + 数量）现在显示在 BattleHold 的战斗货仓面板里，且用大字号：
        //   BattleHold.cs -> RefreshBody() / CombatItemFontSize。
        // 数量变化时 BattleHold 自己会 RaiseChanged 刷新面板，本 Mod 不需要推送。

        private string _flashLast;
        private float _flashMuteUntil;

        /// <summary>装填 / 发射提示。以前打在浮窗上，现在只写日志（面板由 BattleHold 刷新）。</summary>
        private void Flash(string label, float seconds)
        {
            if (string.IsNullOrEmpty(label)) return;
            if (_flashLast == label && Time.time < _flashMuteUntil) return;
            _flashLast = label;
            _flashMuteUntil = Time.time + seconds;
            _api.Log("AAM: " + label);
        }

        /// <summary>弹种选择 HUD：信息已移入战斗货仓面板（BattleHold），此处只保留 R 键切换的日志提示。</summary>
        private void OnGUI()
        {
        }

        /// <summary>导弹仓清单（供战斗货仓面板显示）：只显示玩家装备（库存>0）的型号，当前选中金色高亮。</summary>
        public static string MissileInventoryLine()
        {
            try
            {
                var sys = Live;
                if (sys == null || sys._specs.Count == 0 || sys._missileTypes.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sys._specs.Count; i++)
                {
                    int c = 0;
                    try { c = sys.CombatCountOf(sys._missileTypes[i]); } catch { }
                    if (c <= 0) continue;   // 未装备的导弹不显示
                    if (i == sys._selIndex)
                        sb.Append("<color=#FFC800><b>  > ").Append(sys._specs[i].Name).Append(" x").Append(c).Append("</b></color>\n");
                    else
                        sb.Append("  ").Append(sys._specs[i].Name).Append(" x").Append(c).Append("\n");
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>指定导弹型号在战斗货仓中的数量。</summary>
        private int CombatCountOf(CargoType type)
        {
            if (!_bhChecked) CheckBattleHold();
            if (_bhCount == null) return 0;
            try { return (int)_bhCount.Invoke(null, new object[] { type }); } catch { return 0; }
        }

        // =================================================================
        // /kill 指令系统（2026-09-15）
        // 用法：
        //   /kill           - 杀死玩家自己（@s）
        //   /kill @s        - 杀死玩家自己
        //   /kill @a        - 杀死所有玩家
        //   /kill @e        - 杀死所有实体（玩家+AI+导弹）
        //   /kill @ai       - 杀死所有AI（包括导弹）
        //   /kill @bot      - 杀死所有AI飞机（不包括导弹）
        //   /kill @missile  - 杀死所有导弹（包括拦截弹和诱饵弹）
        //   /kill [名称]    - 杀死指定名称的目标（AI飞机或导弹）
        // =================================================================
        private void CheckKillCommand()
        {
            try
            {
                string cmd = Machine.Core.ChatBox.LastCommand;
                if (string.IsNullOrEmpty(cmd)) return;
                if (!cmd.StartsWith("/kill", System.StringComparison.OrdinalIgnoreCase)) return;

                // 清空指令，避免重复处理
                Machine.Core.ChatBox.LastCommand = null;

                string arg = cmd.Length > 5 ? cmd.Substring(5).Trim() : "";
                string target = string.IsNullOrEmpty(arg) ? "@s" : arg;

                _api.Log("AAM: /kill command received, target=" + target);

                switch (target.ToLowerInvariant())
                {
                    case "@s":
                        KillPlayerSelf();
                        break;
                    case "@a":
                        KillAllPlayers();
                        break;
                    case "@e":
                        KillAllEntities();
                        break;
                    case "@ai":
                        KillAllAi(true);
                        break;
                    case "@bot":
                        KillAllAi(false);
                        break;
                    case "@missile":
                        KillAllMissiles();
                        break;
                    default:
                        KillByName(target);
                        break;
                }
            }
            catch (Exception e) { _api.Log("AAM: /kill command failed: " + e.Message); }
        }

        /// <summary>杀死玩家自己。</summary>
        private void KillPlayerSelf()
        {
            try
            {
                if (_plane == null) { _api.Log("AAM: /kill @s - no player plane"); return; }
                // 获取玩家机型和阵营（用反射，避免编译时依赖游戏类型）
                string playerModel = "PlayerAircraft";
                int playerFaction = 0;
                try
                {
                    // 尝试获取阵营ID
                    var allComps = _plane.GetComponents<Component>();
                    for (int i = 0; i < allComps.Length; i++)
                    {
                        var c = allComps[i];
                        if (c == null) continue;
                        var t = c.GetType();
                        // FactionSyncMarker
                        if (t.Name == "FactionSyncMarker")
                        {
                            var prop = t.GetField("FactionId");
                            if (prop != null) playerFaction = (int)prop.GetValue(c);
                        }
                        // DesignController
                        if (t.Name == "DesignController")
                        {
                            var prop = t.GetField("designName");
                            if (prop != null)
                            {
                                string dn = (string)prop.GetValue(c);
                                if (!string.IsNullOrEmpty(dn)) playerModel = dn;
                            }
                        }
                    }
                }
                catch { }

                var pctrl = _plane.GetComponent<PlaneController>();
                if (pctrl != null)
                {
                    pctrl.ExplodePlane();
                    _api.Log("AAM: /kill @s - player exploded via PlaneController.ExplodePlane");
                }
                else
                {
                    var pe = _plane.GetComponent<PartExploder>();
                    if (pe != null) pe.ExplodePlane();
                    _api.Log("AAM: /kill @s - player exploded via PartExploder.ExplodePlane");
                }

                // 魔法击杀播报（FeedMagicKill在AiAircraft类中）
                AiAircraft.FeedMagicKill(playerModel, "You", playerFaction);
            }
            catch (Exception e) { _api.Log("AAM: /kill @s failed: " + e.Message); }
        }

        /// <summary>杀死所有玩家（目前只有本地玩家，联机后扩展）。</summary>
        private void KillAllPlayers()
        {
            _api.Log("AAM: /kill @a - killing all players");
            KillPlayerSelf();
        }

        /// <summary>杀死所有实体（玩家+AI+导弹）。</summary>
        private void KillAllEntities()
        {
            _api.Log("AAM: /kill @e - killing all entities");
            KillPlayerSelf();
            KillAllAi(true);
        }

        /// <summary>杀死所有AI。</summary>
        /// <param name="includeMissiles">是否包括导弹</param>
        private void KillAllAi(bool includeMissiles)
        {
            try
            {
                int killed = 0;
                // 杀死AI飞机
                var ais = UnityEngine.Object.FindObjectsByType<AiAircraft>(UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < ais.Length; i++)
                {
                    if (ais[i] == null) continue;
                    try
                    {
                        // 记录AI信息用于播报
                        string aiModel = ais[i].ModelName;
                        string aiCall = ais[i].Callsign;
                        int aiFaction = ais[i].FactionId;
                        ais[i].Despawn();
                        killed++;
                        // 逐个播报魔法击杀
                        AiAircraft.FeedMagicKill(aiModel, aiCall, aiFaction);
                    }
                    catch { }
                }
                _api.Log("AAM: /kill @ai/@bot - killed " + killed + " AI aircraft");

                // 杀死导弹
                if (includeMissiles)
                {
                    KillAllMissiles();
                }
            }
            catch (Exception e) { _api.Log("AAM: /kill @ai failed: " + e.Message); }
        }

        /// <summary>杀死所有导弹（包括拦截弹和诱饵弹）。</summary>
        private void KillAllMissiles()
        {
            try
            {
                int killed = 0;
                var mcs = UnityEngine.Object.FindObjectsByType<MissileController>(UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < mcs.Length; i++)
                {
                    if (mcs[i] == null) continue;
                    try
                    {
                        UnityEngine.Object.Destroy(mcs[i].gameObject);
                        killed++;
                    }
                    catch { }
                }
                _api.Log("AAM: /kill @missile - killed " + killed + " missiles");

                // 批量清除实体播报（导弹不属于AI/玩家，用"清除N个实体"格式）
                if (killed > 0) AiAircraft.FeedClearEntities(killed);
            }
            catch (Exception e) { _api.Log("AAM: /kill @missile failed: " + e.Message); }
        }

        /// <summary>按名称杀死目标（AI飞机或导弹）。</summary>
        private void KillByName(string name)
        {
            try
            {
                bool found = false;
                // 先找AI飞机
                var ais = UnityEngine.Object.FindObjectsByType<AiAircraft>(UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < ais.Length; i++)
                {
                    if (ais[i] == null) continue;
                    if (ais[i].Callsign.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        // 记录AI信息用于播报
                        string aiModel = ais[i].ModelName;
                        string aiCall = ais[i].Callsign;
                        int aiFaction = ais[i].FactionId;
                        ais[i].Despawn();
                        _api.Log("AAM: /kill " + name + " - AI aircraft killed");
                        // 魔法击杀播报
                        AiAircraft.FeedMagicKill(aiModel, aiCall, aiFaction);
                        found = true;
                        break;
                    }
                }

                // 再找导弹
                if (!found)
                {
                    int killed = 0;
                    var mcs = UnityEngine.Object.FindObjectsByType<MissileController>(UnityEngine.FindObjectsSortMode.None);
                    for (int i = 0; i < mcs.Length; i++)
                    {
                        if (mcs[i] == null) continue;
                        if (mcs[i].SpecName.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            UnityEngine.Object.Destroy(mcs[i].gameObject);
                            _api.Log("AAM: /kill " + name + " - missile killed");
                            killed++;
                            found = true;
                            // 按名称杀导弹只杀第一个匹配的
                            break;
                        }
                    }
                    // 导弹不属于AI/玩家，用"清除N个实体"格式
                    if (killed > 0) AiAircraft.FeedClearEntities(killed);
                }

                if (!found)
                {
                    _api.Log("AAM: /kill " + name + " - target not found");
                }
            }
            catch (Exception e) { _api.Log("AAM: /kill by name failed: " + e.Message); }
        }

        // =================================================================
        // 主循环
        // =================================================================
        private void Update()
        {
            try
            {
                // 纯净模式守卫：原版档不注入导弹/不生成 AI/不处理发射
                if (Machine.Mod.MachineState.PureMode) return;
                CheckKillCommand();
                if (!_airportsInjected)
                {
                    _injectTimer -= Time.deltaTime;
                    if (_injectTimer <= 0f) { _injectTimer = 2f; if (InjectAirports()) _airportsInjected = true; }
                }
                ResolvePlane();
                HandleGiveOnStart();
                HandleMissileSelect();
                HandleMissileInput();
                HandleGunInput();
                HandleFlareInput();
                PlayerIrTick(Time.deltaTime);
                FlareTick(Time.deltaTime);
                StepFlareTest(Time.deltaTime);
                StepPlayerHitTest(Time.deltaTime);
                // 玩家被击落时阵营系统可能还没建好（延迟初始化）→ 每帧补报挂起的那几笔
                MissileController.FlushPendingKills();
                StepAiAircraft();
                StepPersonaTest();
                StepAiCombatTest();
                CleanupLive();
                HandleAutoTest();
                StepAmmoTest();
            }
            catch (Exception e)
            {
                _api.Log("AAM Update ERROR: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
            }
        }

        private void ResolvePlane()
        {
            // ★ 节流：游戏 Singleton 在 m_Instance 为 null 时会走全场景 FindFirstObjectByType，
            //   主菜单里每次访问 = 一次全场景扫描（2~4ms）。_plane 为空（菜单/加载中）时 0.25s 查一次；
            //   已解析（飞行中）时 Instance 访问是便宜的（游戏单例已缓存），保持每帧跟随。
            if (_plane == null)
            {
                _resolveT -= Time.deltaTime;
                if (_resolveT > 0f) return;
                _resolveT = 0.25f;
            }
            PlaneContainer pc = PlaneContainer.Instance;
            if (pc == null) { _plane = null; return; }
            int id = pc.GetInstanceID();
            if (_plane == null || id != _planeId)
            {
                _plane = pc;
                _planeId = id;
                _gaveThisPlane = false;
                _api.Log("AAM: player plane resolved (id=" + id + ")");
            }
        }

        private void HandleGiveOnStart()
        {
            if (_plane == null || _gaveThisPlane) return;
            if (!_plane.FlightModeInitialized) return;
            _gaveThisPlane = true;
            // 进入飞行时提示一次操作键位（全部为鼠标操作，文案必须 ASCII：TMP 无中文字形）
            Flash("MSL = RMB x2    GUN = LMB x2 + HOLD    SWITCH = R/WHEEL    FLARE = " + cfgFlareKey, 4f);
            if (!cfgCombat) return;
            if (!_bhChecked) CheckBattleHold();
            if (cfgGiveOnStart > 0)
            {
                int have = CombatCount();
                if (have < cfgGiveOnStart)
                {
                    bool ok = CombatGive(cfgGiveOnStart - have);
                    _api.Log("AAM: giveOnStart " + (cfgGiveOnStart - have) + " -> ok=" + ok + " now=" + CombatCount());
                }
            }
            // 热诱弹：单独一份开局补给（默认 16 枚），不受 giveOnStart 控制
            if (cfgFlareEnabled && cfgFlareGive > 0)
            {
                int fh = FlareCount();
                if (fh < cfgFlareGive)
                {
                    bool fok = FlareGive(cfgFlareGive - fh);
                    _api.Log("AAM: giveOnStart flare " + (cfgFlareGive - fh) + " -> ok=" + fok + " now=" + FlareCount());
                }
            }
        }

        /// <summary>查询玩家阵营（FactionApi.PlayerFaction，缺失时 -1）。</summary>
        private int GetPlayerFactionId()
        {
            try
            {
                var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "FactionSystem") { t = asm.GetType("Machine.Faction.FactionApi"); break; }
                    }
                }
                if (t == null) return -1;
                var p = t.GetProperty("PlayerFaction", BindingFlags.Public | BindingFlags.Static);
                if (p != null) return (int)p.GetValue(null, null);
            }
            catch { }
            return -1;
        }

        /// <summary>战斗货仓里指定弹种增减（机炮用，避免为了打炮改玩家选中的弹种）。</summary>
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

        // ---------- 输入：右键双击=导弹 1 枚 / 左键双击+按住=机炮连射 ----------
        private const float DblClickWindow = 0.32f;   // 两次按下的最大间隔（秒）
        private float _lmbLastDown = -99f;
        private float _rmbLastDown = -99f;
        private bool _gunBurst;
        private float _gunAcc;
        private float _gunEmptyT = -99f;

        /// <summary>右键双击发射 1 枚导弹；再发需要再双击一次。单击不做任何事。</summary>
        private void HandleMissileInput()
        {
            if (_plane == null || !_plane.FlightModeInitialized) return;
            bool pressed = false;
            try { pressed = UnityEngine.Input.GetMouseButtonDown(1); } catch { return; }
            if (!pressed) return;
            float now = Time.time;
            if (now - _rmbLastDown <= DblClickWindow)
            {
                _rmbLastDown = -99f;          // 防止三连击连发两枚
                TryFire();
            }
            else
            {
                _rmbLastDown = now;
            }
        }

        /// <summary>左键双击后进入连射，只要不抬起左键就一直按射速打；抬起即停。</summary>
        /// <summary>
        /// 热诱弹 / IRCCM 专项自测（flareTest）：每 2s 在靶机位置抛一枚热诱弹，
        /// 并打印在飞导弹的导引头状态（ircm / events / rejects）。
        /// </summary>
        private void StepFlareTest(float dt)
        {
            if (!cfgFlareTest) return;
            if (_plane == null || !_plane.FlightModeInitialized) return;
            _flareTestNext -= dt;
            if (_flareTestNext > 0f) return;
            _flareTestNext = 2f;
            try
            {
                AiAircraft ai = UnityEngine.Object.FindObjectOfType<AiAircraft>();
                if (ai == null) { _api.Log("FLARE TEST: no AI target yet"); return; }

                // 一次性：跑一遍玩家侧抛饵路径（和 X 键完全同一个 DispenseFlare），
                // 验证按键链路 + 库存扣减真的能从 16 掉下来。A/B 两轮各做一次，对称、不污染判据。
                if (!_flareTestPlayerDone)
                {
                    _flareTestPlayerDone = true;
                    int before = FlareCount();
                    bool ok = DispenseFlare();
                    _api.Log("FLARE TEST: player path stock=" + before + " dispense=" + ok
                             + " burst=" + _flarePending + " cd=" + _flareCd.ToString("F2"));
                }

                GameObject model = null;
                try
                {
                    model = DesignModel.BuildWithGameLoader(FlareDesignPath(), "AAM_FlareTest", _api, false, false, true);
                    if (model == null) model = DesignModel.BuildFallback(FlareDesignPath(), "AAM_FlareTest", _api);
                }
                catch { }
                if (model == null) model = new GameObject("AAM_FlareTest");
                try
                {
                    Collider[] cols = model.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < cols.Length; i++) cols[i].enabled = false;
                }
                catch { }

                Vector3 carrierVel = (ai.Rb != null) ? ai.Rb.linearVelocity : Vector3.zero;
                var fl = IrFlare.Dispense(model, ai.transform, carrierVel);
                _api.Log("FLARE TEST: dispensed ir=" + (fl != null ? fl.Ir.ToString("F0") : "null")
                         + " tgtIr=" + (ai.IrActual >= 0f ? ai.IrActual.ToString("F1") : "?")
                         + " srcs=" + IrRegistry.Count
                         + " lights=" + IrFlare.LitCount + "/" + IrFlare.LightBudget
                         + " playerFlares=" + FlareCount());
                if (fl == null) Destroy(model);

                var live = MissileController.RegistrySnapshot();
                for (int i = 0; i < live.Count; i++)
                {
                    var m = live[i];
                    if (m == null) continue;
                    _api.Log("FLARE TEST: msl " + m.SpecName + " ircm=" + m.IrcmActive
                             + " events=" + m.IrcmEvents + " rejects=" + m.DecoyRejects
                             + " seeker=" + m.IrSeeker);
                }
            }
            catch (Exception e) { _api.Log("FLARE TEST error " + e.Message); }
        }
        private float _flareTestNext;
        private bool _flareTestPlayerDone;   // 专项自测里"玩家抛饵"只做一次（X 键走的同一条路径）

        // ---- 专项自测：玩家被真弹命中 ----
        private float _playerHitT;
        private bool _playerHitFired;
        private float _playerHitLogT;

        /// <summary>
        /// 自测（playerHitTest）：朝玩家打一枚**真弹**（不像 testAmmo 那样 InterceptOnly），
        /// 用来验证"命中玩家 = 整机坠毁 + 计分板上报阵亡"这条路径。
        /// 判据（日志）：`player destroyed by missile - plane exploded (crash)` +
        /// `player shot down - scoreboard=True`。
        /// </summary>
        private void StepPlayerHitTest(float dt)
        {
            if (!cfgPlayerHitTest) return;
            if (_plane == null || !_plane.FlightModeInitialized) { RequestFlyMode(); return; }

            _playerHitT += dt;
            if (!_playerHitFired && _playerHitT > 3f)
            {
                _playerHitFired = true;
                try
                {
                    Transform t = _plane.transform;
                    Vector3 fwd = t.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    fwd = fwd.normalized;
                    // 1800m 外、450 m/s 迎头 —— 飞行约 4s，越过 StrikeTarget 的 2s 出生保护
                    Vector3 spawn = t.position + fwd * 1800f + Vector3.up * 200f;
                    Vector3 vel = (t.position - spawn).normalized * 450f;

                    MissileSpec sp = SpecByName("PL-15");
                    GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "HITTEST", _api, false, false, true);
                    if (model == null) model = DesignModel.BuildFallback(DesignPathFor(sp), "HITTEST", _api);
                    if (model == null) { _api.Log("HIT TEST: model build failed"); return; }

                    var tr = new TargetRef();
                    tr.T = t;
                    tr.UseFixed = false;
                    tr.Name = "Player";
                    MissileController mc = MissileController.Spawn(model, spawn, vel, tr, null);
                    ApplyMissileConfig(mc, sp);
                    mc.ShooterModel = "TEST-BANDIT";
                    mc.ShooterCall = "BANDIT-1";
                    mc.FactionId = -1;
                    // 关键：**不**设 InterceptOnly —— 要的就是它真把玩家打下来。
                    // 关掉地形避障：自测时玩家在地面附近，避障会在最后关头把弹拉起来，打不中。
                    mc.Avoid.Enabled = false;
                    // Spawn() 里是 enabled=false（等调用方配置完再启用）。忘了这一句的后果很隐蔽：
                    // 组件不跑 Update → 弹停在原地不飞、也**不会进注册表**（OnEnable 才 Add），
                    // 表现为 live 数不增长、永远打不到人。另外三处调用点都有这一句，这里不能漏。
                    mc.enabled = true;
                    _api.Log("HIT TEST: live missile spawned at " + spawn.ToString("F0")
                             + " dist=1800 speed=" + Mathf.RoundToInt(vel.magnitude)
                             + " -> expect player crash + scoreboard");
                }
                catch (Exception e) { _api.Log("HIT TEST: spawn failed " + e.Message); }
            }

            if (!_playerHitFired) return;
            _playerHitLogT -= dt;
            if (_playerHitLogT > 0f) return;
            _playerHitLogT = 1f;
            try
            {
                PlaneContainer pc = PlaneContainer.Instance;
                PlaneController pctrl = (pc != null) ? pc.GetComponent<PlaneController>() : null;
                // 顺带盯住这枚测试弹本身：它是否还在、飞到哪里、当前锁的是谁。
                // 自测框架早段会放一架巡航机，若导引头被它吸走（tgt 从 Player 变成别的），
                // 这里能一眼看出来。
                string probe = "";
                List<MissileController> live = MissileController.RegistrySnapshot();
                for (int i = 0; i < live.Count; i++)
                {
                    MissileController m2 = live[i];
                    if (m2 == null || m2.ShooterCall != "BANDIT-1") continue;
                    probe = " testPos=" + m2.Body.Pos.ToString("F0")
                            + " testTgt=" + (m2.Target != null ? m2.Target.Name : "none")
                            + " testSpd=" + Mathf.RoundToInt(m2.Body.Vel.magnitude);
                    break;
                }
                _api.Log("HIT TEST: t=" + _playerHitT.ToString("F1") + "s planeActive="
                         + (pc != null ? pc.gameObject.activeSelf.ToString() : "?")
                         + " exploded=" + (pctrl != null ? pctrl.Exploded.ToString() : "?")
                         + " live=" + live.Count + probe);
            }
            catch (Exception e) { _api.Log("HIT TEST: probe failed " + e.Message); }
        }

        /// <summary>热诱弹投放键（默认 X，配置 flareKey）。</summary>
        private void HandleFlareInput()
        {
            if (!cfgFlareEnabled) return;
            try
            {
                KeyCode kc;
                if (!TryParseKeyCode(cfgFlareKey, out kc)) return;
                if (UnityEngine.Input.GetKeyDown(kc)) DispenseFlare();
            }
            catch { }
        }

        /// <summary>把配置里的按键名（"X" / "left shift" 等）解析成 KeyCode。</summary>
        private static bool TryParseKeyCode(string name, out KeyCode kc)
        {
            kc = KeyCode.None;
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                kc = (KeyCode)System.Enum.Parse(typeof(KeyCode), name.Trim(), true);
                return true;
            }
            catch { return false; }
        }

        private void HandleGunInput()
        {
            if (!cfgGunEnabled) return;
            if (_plane == null || !_plane.FlightModeInitialized) return;

            bool lmb = false, down = false, up = false;
            try
            {
                lmb = UnityEngine.Input.GetMouseButton(0);
                down = UnityEngine.Input.GetMouseButtonDown(0);
                up = UnityEngine.Input.GetMouseButtonUp(0);
            }
            catch { return; }

            if (up || !lmb) { _gunBurst = false; _gunAcc = 0f; return; }

            if (down)
            {
                float now = Time.time;
                if (!_gunBurst && now - _lmbLastDown <= DblClickWindow)
                {
                    _gunBurst = true;         // 双击成立：第二击按住期间连射
                    _gunAcc = 0.999f;         // 立即先打 1 发
                }
                _lmbLastDown = now;
            }
            if (!_gunBurst) return;

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
                GameObject m = DesignModel.BuildWithGameLoader(p, "AAM_BulletProto", _api, false, false, true);
                if (m == null) m = DesignModel.BuildFallback(p, "AAM_BulletProto", _api);
                if (m == null) { _api.Log("AAM: bullet model unavailable " + p); return null; }
                // 曳光弹配色：整发亮黄橙，空战里肉眼可见（原型统一改，克隆体共享材质，零额外开销）
                try
                {
                    Renderer[] rr = m.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < rr.Length; i++)
                    {
                        if (rr[i] == null) continue;
                        Material mat = rr[i].sharedMaterial;
                        if (mat == null) { mat = new Material(Shader.Find("Sprites/Default")); rr[i].sharedMaterial = mat; }
                        if (mat.HasProperty("_Color")) mat.color = new Color(1f, 0.78f, 0.25f, 1f);
                    }
                }
                catch (Exception ce) { _api.Log("AAM: bullet tint failed " + ce.Message); }
                // 曳光拖尾：TrailRenderer 挂在原型上，克隆体自动继承。
                // 白热->橙->透明的渐变亮线，驾驶员看弹道修瞄准；0.25s ≈ 200m 弹道余辉。
                // 注意实例会被 cfgBulletScale 放大（×3），所以局部宽度取 0.05 -> 实际约 0.15m。
                try
                {
                    TrailRenderer tr = m.AddComponent<TrailRenderer>();
                    tr.time = 0.25f;
                    tr.startWidth = 0.05f;
                    tr.endWidth = 0.008f;
                    tr.minVertexDistance = 0.5f;
                    tr.numCapVertices = 2;
                    tr.alignment = LineAlignment.View;
                    tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    tr.receiveShadows = false;
                    Gradient g = new Gradient();
                    g.SetKeys(
                        new GradientColorKey[]
                        {
                            new GradientColorKey(new Color(1f, 0.97f, 0.82f), 0f),   // 白热弹头端
                            new GradientColorKey(new Color(1f, 0.72f, 0.15f), 0.35f), // 亮黄曳光
                            new GradientColorKey(new Color(1f, 0.33f, 0.05f), 1f)     // 橙红余辉
                        },
                        new GradientAlphaKey[]
                        {
                            new GradientAlphaKey(0.95f, 0f),
                            new GradientAlphaKey(0.70f, 0.40f),
                            new GradientAlphaKey(0f, 1f)
                        });
                    Material tm = new Material(Shader.Find("Sprites/Default"));
                    tm.color = Color.white;
                    tr.material = tm;
                    tr.colorGradient = g;
                }
                catch (Exception te) { _api.Log("AAM: tracer trail failed " + te.Message); }
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

        /// <summary>打一发机炮（玩家路径：扣弹药、加散布、从机口射出）。</summary>
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
                PlayGunSfx();  // 机炮发射音效（节流播放）
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
        private int _gunDbgNear;   // 诊断计数（只打前几条，避免刷屏）

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
                    if (_gunDbgNear < 2)
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
                    bool friendly = MissileController.IsFriendlyTo(tt, r.FactionId);
                    if (friendly) return false;          // 友军：穿过去不打
                    MissileController.ReportGunHit(tt, b, r.ShooterModel, r.ShooterCall, r.FactionId);
                    return true;                          // 非友军命中：无论拆件成败都消耗这发弹
                }
            }
            catch { }
            return false;
        }

        // 导弹发射 = 右键双击（见 HandleMissileInput）。原 F 键发射已按需求移除（易误触）。

        /// <summary>
        /// 雷达锁定检查：未锁定敌机（或未锁定任何目标）不允许发射空空导弹。
        /// 通过 RadarApi.TryGetLocked 反射查询。
        /// </summary>
        private bool RequireRadarLock(out string lockName)
        {
            lockName = null;
            try
            {
                var t = Type.GetType("Machine.Radar.RadarApi, Radar");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "Radar") { t = asm.GetType("Machine.Radar.RadarApi"); break; }
                    }
                }
                if (t == null) return true;   // 雷达 Mod 未安装时不强制（降级）
                var m = t.GetMethod("TryGetLocked", new Type[] { typeof(string).MakeByRefType(), typeof(Vector3).MakeByRefType() });
                if (m == null) return true;
                var args = new object[] { null, Vector3.zero };
                bool ok = (bool)m.Invoke(null, args);
                if (ok && args[0] != null) lockName = (string)args[0];
                return ok;
            }
            catch { return true; }
        }

        /// <summary>智能切换弹种：锁定导弹用拦截弹，锁定飞机用空空导弹。</summary>
        private void AutoSelectMissileByTarget()
        {
            try
            {
                if (_specs.Count == 0) return;

                // 检测雷达锁定目标
                string lockName = null;
                Vector3 lockPos = Vector3.zero;
                bool hasLock = false;
                try
                {
                    var t = Type.GetType("Machine.Radar.RadarApi, Radar");
                    if (t == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "Radar") { t = asm.GetType("Machine.Radar.RadarApi"); break; }
                        }
                    }
                    if (t != null)
                    {
                        var m = t.GetMethod("TryGetLocked", new Type[] { typeof(string).MakeByRefType(), typeof(Vector3).MakeByRefType() });
                        if (m != null)
                        {
                            var args = new object[] { null, Vector3.zero };
                            hasLock = (bool)m.Invoke(null, args);
                            if (hasLock && args[0] != null) lockName = (string)args[0];
                            if (hasLock) lockPos = (Vector3)args[1];
                        }
                    }
                }
                catch { }

                // 判断锁定目标是否为导弹
                bool targetIsMissile = false;
                if (hasLock && !string.IsNullOrEmpty(lockName))
                {
                    string lower = lockName.ToLower();
                    // 导弹名称关键词：PL-、AIM-、MSDM、missile、导弹、MICA、METEOR、R-、IRIS-T
                    if (lower.Contains("pl-") || lower.Contains("aim-") || lower.Contains("msdm") ||
                        lower.Contains("missile") || lower.Contains("导弹") || lower.Contains("mica") ||
                        lower.Contains("meteor") || lower.Contains("r-7") || lower.Contains("r-2") ||
                        lower.Contains("r-3") || lower.Contains("r-5") || lower.Contains("iris-t") ||
                        lower.Contains("sidewinder") || lower.Contains("amraam") || lower.Contains("python"))
                    {
                        targetIsMissile = true;
                    }
                }

                // 如果没有雷达锁定，但有来袭导弹，也自动切换到拦截弹
                if (!targetIsMissile)
                {
                    var threat = FindIncomingMissile();
                    if (threat != null) targetIsMissile = true;
                }

                // 找到合适的弹种
                int targetIdx = -1;

                if (targetIsMissile)
                {
                    // 锁定目标是导弹：使用拦截弹（MSDM）
                    for (int i = 0; i < _specs.Count; i++)
                    {
                        if (_specs[i].AntiMissile && CombatCountOf(_missileTypes[i]) > 0)
                        { targetIdx = i; break; }
                    }
                }
                else if (hasLock)
                {
                    // 锁定目标是飞机：根据距离自动选择导弹类型
                    // 计算锁定目标与玩家的距离
                    float targetDist = float.MaxValue;
                    if (_plane != null)
                    {
                        targetDist = Vector3.Distance(_plane.transform.position, lockPos);
                    }

                    // 根据距离确定期望的射程类别
                    // > 10000m: 超远程 (Long)
                    // 3000-10000m: 中程 (Medium)
                    // < 3000m: 短程/格斗弹 (Short)
                    MissileRangeCategory desiredCat;
                    if (targetDist > 10000f) desiredCat = MissileRangeCategory.Long;
                    else if (targetDist >= 3000f) desiredCat = MissileRangeCategory.Medium;
                    else desiredCat = MissileRangeCategory.Short;

                    // 优先级：期望类别 > 次级类别 > 搭载了的最高级类别
                    // 1. 先找期望类别且有库存的导弹
                    for (int i = 0; i < _specs.Count; i++)
                    {
                        if (!_specs[i].AntiMissile && _specs[i].RangeCat == desiredCat &&
                            CombatCountOf(_missileTypes[i]) > 0)
                        { targetIdx = i; break; }
                    }

                    // 2. 如果没有期望类别，找次级类别（距离更近的类别）且有库存的导弹
                    if (targetIdx < 0)
                    {
                        MissileRangeCategory[] fallbackOrder;
                        if (desiredCat == MissileRangeCategory.Long)
                            fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Medium, MissileRangeCategory.Short };
                        else if (desiredCat == MissileRangeCategory.Medium)
                            fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Short, MissileRangeCategory.Long };
                        else
                            fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Medium, MissileRangeCategory.Long };

                        foreach (var cat in fallbackOrder)
                        {
                            for (int i = 0; i < _specs.Count; i++)
                            {
                                if (!_specs[i].AntiMissile && _specs[i].RangeCat == cat &&
                                    CombatCountOf(_missileTypes[i]) > 0)
                                { targetIdx = i; break; }
                            }
                            if (targetIdx >= 0) break;
                        }
                    }

                    // 3. 如果还是没有，找任意有库存的空空导弹（搭载了的最高级）
                    if (targetIdx < 0)
                    {
                        for (int i = 0; i < _specs.Count; i++)
                        {
                            if (!_specs[i].AntiMissile && CombatCountOf(_missileTypes[i]) > 0)
                            { targetIdx = i; break; }
                        }
                    }

                    _api.Log("AAM: auto-select by range dist=" + Mathf.RoundToInt(targetDist) + "m desired=" + desiredCat);
                }
                else
                {
                    // 没有雷达锁定：找第一个有库存的空空导弹
                    for (int i = 0; i < _specs.Count; i++)
                    {
                        if (!_specs[i].AntiMissile && CombatCountOf(_missileTypes[i]) > 0)
                        { targetIdx = i; break; }
                    }
                }

                // 如果找到了合适的弹种且不是当前选中的，切换
                if (targetIdx >= 0 && targetIdx != _selIndex)
                {
                    SelectMissile(targetIdx);
                    _api.Log("AAM: auto-selected " + _currentSpec.Name + " (target=" + (targetIsMissile ? "missile" : "aircraft") + ")");
                }
            }
            catch (Exception e) { _api.Log("AAM: auto-select failed: " + e.Message); }
        }

        /// <summary>发射一枚导弹：需雷达已锁定目标，消耗战斗货仓 1 发，从机腹挂点放出，交给 AI 制导。</summary>
        public bool TryFire(bool forceNoLock = false)
        {
            // 智能切换弹种：锁定导弹用拦截弹，锁定飞机用空空导弹
            AutoSelectMissileByTarget();

            var spec = CurrentSpec();
            if (spec == null) { Flash("<no missile>", 1.2f); return false; }
            int have = CombatCount();
            if (have <= 0)
            {
                Flash("<no " + spec.Name + ">", 1.2f);
                _api.Log("AAM: fire aborted - combat hold has 0 " + spec.Name);
                return false;
            }

            // 必须雷达锁定后才能发射（用户要求；雷达未装/未锁定时禁止发射）
            // MSDM 拦截弹例外：它锁的是"来袭导弹"而不是飞机，所以不走飞机的雷达锁定。
            string lockName = null;
            MissileController threat = null;
            if (spec.AntiMissile)
            {
                threat = FindIncomingMissile();
                if (threat == null)
                {
                    Flash("NO THREAT", 1.2f);
                    _api.Log("AAM: fire aborted - no incoming missile to intercept");
                    return false;
                }
                lockName = threat.SpecName;
            }
            else if (!forceNoLock && !RequireRadarLock(out lockName))
            {
                Flash("NO LOCK", 1.2f);
                _api.Log("AAM: fire aborted - no radar lock");
                return false;
            }

            GameObject model = null;
            string designPath = DesignPathFor(spec);   // 每个弹种用自己的模型文件
            try
            {
                model = DesignModel.BuildWithGameLoader(designPath, "AAM_" + spec.Name, _api, false, false, true);
                if (model == null)
                {
                    _api.Log("AAM: falling back to scene-clone model " + designPath);
                    model = DesignModel.BuildFallback(designPath, "AAM_" + spec.Name, _api);
                }
                if (model == null)
                {
                    _api.Log("AAM: model unavailable, aborting fire");
                    return false;
                }

                Transform t = _plane.transform;
                Vector3 spawn = t.position + t.forward * 9f - t.up * 1.2f;   // 出生点推到机头前方，避免与机体重叠
                if (spawn.y < 5f) spawn.y = 5f;    // 别把弹放到地底下
                Vector3 vel = _plane.GetVelocity();
                if (vel.magnitude < 30f) vel = t.forward * 60f;   // 低速时给一个前向初速

                // 导弹制导目标：优先用雷达锁定目标实体（用户要求——攻击锁定的目标，不能自己就近找）
                TargetRef target = null;
                if (threat != null)
                {
                    // MSDM 拦截弹：目标是那枚来袭导弹（绑它的实时速度，见 TargetRef.Msl 说明）
                    target = new TargetRef();
                    target.T = threat.transform;
                    target.Msl = threat;
                    target.UseFixed = false;
                    target.Name = threat.SpecName;
                    _api.Log("AAM: interceptor locked incoming " + threat.SpecName + " from " + threat.ShooterCall);
                }
                try
                {
                    Transform lockedTr = null;
                    var rt = Type.GetType("Machine.Radar.RadarApi, Radar");
                    if (rt == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "Radar") { rt = asm.GetType("Machine.Radar.RadarApi"); break; }
                        }
                    }
                    if (rt != null && target == null)   // 拦截弹已经有目标，不要再被雷达锁定的飞机覆盖
                    {
                        var mT = rt.GetMethod("TryGetLockedTransform", Type.EmptyTypes);
                        if (mT != null)
                        {
                            var lv = mT.Invoke(null, null) as Transform;
                            if (lv != null)
                            {
                                target = new TargetRef();
                                target.T = lv;
                                target.UseFixed = false;
                                target.Name = lockName ?? "Locked";
                            }
                        }
                    }
                }
                catch { }

                // 兜底：雷达不可用/无锁定实体时才用就近目标（正常流程不应走到这里，因为有 NO LOCK 拦截）
                // 拦截弹绝不退化成"锁飞机"——找不到导弹就宁可打空（用户要求：不可锁定并击毁敌机）。
                if (target == null && threat == null)
                    target = TargetRegistry.FindNearest(spawn, cfgTargetRange, true);
                // 计算发射距离（到目标的距离）
                float launchDist = 0f;
                if (target != null && target.Valid && target.T != null)
                    launchDist = Vector3.Distance(spawn, target.T.position);
                string targetName = (target != null && target.Valid) ? target.Name : "none";
                // 统一日志格式：与AI发射日志一致，便于统计分析
                _api.Log("AAM: You launched " + spec.Name
                         + " at target " + targetName
                         + ", dist=" + Mathf.RoundToInt(launchDist) + "m"
                         + ", speed=" + Mathf.RoundToInt(vel.magnitude)
                         + ", lock=" + (lockName ?? "none"));

                MissileController mc = MissileController.Spawn(model, spawn, vel, target, null);
                ApplyMissileConfig(mc, spec);
                mc.FactionId = GetPlayerFactionId();
                mc.ShooterModel = "Player";
                mc.ShooterCall = "You";
                // 禁用导弹模型所有碰撞体：导弹不参与物理碰撞（靠近炸引爆），
                // 避免出生时与玩家机体重叠把飞机顶坏/误判命中自己
                try
                {
                    Collider[] cols = model.GetComponentsInChildren<Collider>(true);
                    for (int ci = 0; ci < cols.Length; ci++) cols[ci].enabled = false;
                }
                catch { }
                mc.enabled = true;
                _live.Add(mc);

                PlayLaunchSfx();   // 空空导弹发射音效（audio/launch.wav）

                TryNotifyLaunch();   // 通知雷达系统播报 Fox 1（反射，降级安全）

                CombatTake(1);
                PostCore("Missiles", CombatCount().ToString(), 6);

                // 发射减重由 CombatTake → BattleHold.TryRemove 的改变量法（F3）处理，
                // 飞行中的微小漂移由 BattleHold 每帧对账（F1）纠回；此处不再额外校正
                // （旧版 ChangeMass 校正带 /15 增益且与锚点模型打架，2026-09-13 移除）。
                return true;
            }
            catch (Exception e)
            {
                _api.Log("AAM: fire failed " + e.Message);
                if (model != null) UnityEngine.Object.Destroy(model);
                return false;
            }
        }

        private void CleanupLive()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null || _live[i].Finished) _live.RemoveAt(i);
            }
        }

        /// <summary>
        /// 找一个"正在朝我们飞"的来袭导弹（MSDM 拦截弹的目标）。
        /// 条件：不是自己发的、在扫描半径内、速度矢量指向我们（接近率 > 60m/s）。取最近的一枚。
        /// </summary>
        private MissileController FindIncomingMissile()
        {
            const float scanR = 12000f;
            try
            {
                if (_plane == null) return null;
                Vector3 me = _plane.transform.position;
                MissileController best = null;
                float bestD = float.MaxValue;
                List<MissileController> all = MissileController.RegistrySnapshot();
                for (int i = 0; i < all.Count; i++)
                {
                    MissileController m = all[i];
                    if (m == null || m.Finished) continue;
                    if (m.ShooterModel == "Player") continue;     // 自己的弹不打
                    Vector3 mp = m.Body.Pos;
                    Vector3 toMe = me - mp;
                    float d = toMe.magnitude;
                    if (d > scanR || d < 30f) continue;            // 太远不理、贴脸不追
                    if (toMe.sqrMagnitude > 1f && m.Body.Speed > 20f)
                    {
                        if (Vector3.Dot(m.Body.Vel, toMe.normalized) <= 60f) continue;   // 不是在朝我们飞
                    }
                    if (d < bestD) { bestD = d; best = m; }
                }
                return best;
            }
            catch { return null; }
        }

        // =================================================================
        // 自测：不依赖玩家操作，自动配弹 -> 生成靶机 -> 发射 -> 记录弹道
        // =================================================================
        // =================================================================
        // AI 飞机：随机出现 + 火控
        // =================================================================
        /// <summary>是否允许 AI 的导弹真正打坏玩家飞机（供导弹侧读取）。</summary>
        public bool AiDamagePlayerEnabled { get { return cfgAiDamagePlayer; } }

        /// <summary>
        /// AI 用的空空导弹规格。火控的"发射可行性"判据与真正发射必须用**同一份**，
        /// 否则会出现"按 A 弹算够得着、实际打出 B 弹"的错配。
        /// </summary>
        public MissileSpec AiMissileSpec
        {
            get
            {
                MissileSpec sp = SpecByName(cfgAiMissile);
                if (sp == null) sp = SpecByName(cfgCargoName);
                return sp;
            }
        }

        /// <summary>
        /// AI 按距离自动选择导弹类型（方案2：2026-09-15）。
        /// - 距离 > 10000m：超远程弹（Long）
        /// - 距离 3000-10000m：中程弹（Medium）
        /// - 距离 < 3000m：短程/格斗弹（Short）
        /// 如果没有对应类别的导弹，使用次级或搭载了的最高级的导弹。
        /// AI 没有库存概念，可以无限发射，所以只按类别选择，不检查库存。
        /// </summary>
        public MissileSpec SelectAiMissileByDistance(float dist)
        {
            try
            {
                if (_specs.Count == 0) return AiMissileSpec;

                // 根据距离确定期望的射程类别
                MissileRangeCategory desiredCat;
                if (dist > 10000f) desiredCat = MissileRangeCategory.Long;
                else if (dist >= 3000f) desiredCat = MissileRangeCategory.Medium;
                else desiredCat = MissileRangeCategory.Short;

                // 收集所有匹配类别的导弹（排除拦截弹），然后随机选择一个
                System.Collections.Generic.List<MissileSpec> candidates = new System.Collections.Generic.List<MissileSpec>();

                // 1. 先找期望类别的导弹
                for (int i = 0; i < _specs.Count; i++)
                {
                    if (!_specs[i].AntiMissile && _specs[i].RangeCat == desiredCat)
                    {
                        candidates.Add(_specs[i]);
                    }
                }

                // 2. 如果没有期望类别，找次级类别
                if (candidates.Count == 0)
                {
                    MissileRangeCategory[] fallbackOrder;
                    if (desiredCat == MissileRangeCategory.Long)
                        fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Medium, MissileRangeCategory.Short };
                    else if (desiredCat == MissileRangeCategory.Medium)
                        fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Short, MissileRangeCategory.Long };
                    else
                        fallbackOrder = new MissileRangeCategory[] { MissileRangeCategory.Medium, MissileRangeCategory.Long };

                    foreach (var cat in fallbackOrder)
                    {
                        for (int i = 0; i < _specs.Count; i++)
                        {
                            if (!_specs[i].AntiMissile && _specs[i].RangeCat == cat)
                            {
                                candidates.Add(_specs[i]);
                            }
                        }
                        if (candidates.Count > 0) break;
                    }
                }

                // 3. 如果还是没有，找任意空空导弹
                if (candidates.Count == 0)
                {
                    for (int i = 0; i < _specs.Count; i++)
                    {
                        if (!_specs[i].AntiMissile)
                        {
                            candidates.Add(_specs[i]);
                        }
                    }
                }

                // 4. 随机选择一个候选导弹（避免AI总是用同一种导弹）
                if (candidates.Count > 0)
                {
                    int idx = UnityEngine.Random.Range(0, candidates.Count);
                    return candidates[idx];
                }
            }
            catch (Exception e) { _api.Log("AAM: AI select missile by distance failed: " + e.Message); }

            // 兜底：使用配置文件中指定的导弹
            return AiMissileSpec;
        }

        private string AiDesignPath
        {
            get { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "MachineAAM"), cfgAiDesign); }
        }

        private void StepAiAircraft()
        {
            // ⛔ 随机"野生 AI"生成器已按用户要求删除。
            // 旧逻辑：进入飞行模式后每 30~80 秒在玩家周边 2.2~3.6km 随机空域凭空刷一架
            // 敌机/靶机（aiEnabled=true 时生效）——这正是"开局 AI 数为 0、久玩却被导弹袭击"的原因。
            // 现在 AAM 只维护既有 AI 列表，绝不再自行生成任何飞机；
            // 需要飞机只能来自：① 自测开关（aiPersonaTest/testAmmo）② 游戏本体或其他 Mod 的生成器。
            for (int i = _ai.Count - 1; i >= 0; i--)
                if (_ai[i] == null) _ai.RemoveAt(i);
        }

        // SpawnOneAi()（随机野生 AI 生成）已整体删除 —— 见上面 StepAiAircraft 的说明。

        /// <summary>AI 飞机向目标（玩家）发射一枚空空导弹。</summary>
        public void AiLaunchAt(AiAircraft shooter, Transform target, float dist)
        {
            GameObject model = null;
            try
            {
                if (shooter == null || shooter.Container == null || target == null) return;
                // AI 按距离自动选择导弹类型（方案2：2026-09-15）
                // - 距离 > 10000m：超远程弹（Long）
                // - 距离 3000-10000m：中程弹（Medium）
                // - 距离 < 3000m：短程/格斗弹（Short）
                MissileSpec sp = SelectAiMissileByDistance(dist);
                string spName = (sp != null && !string.IsNullOrEmpty(sp.Name)) ? sp.Name : cfgCargoName;
                string path = DesignPathFor(sp);
                if (!File.Exists(path)) { _api.Log("AAM: AI launch failed - design missing"); return; }

                model = DesignModel.BuildWithGameLoader(path, "AAM_" + spName, _api, false, false, true);
                if (model == null) model = DesignModel.BuildFallback(path, "AAM_" + spName, _api);
                if (model == null) return;

                Vector3 fwd = shooter.Container.Forward;
                if (fwd.sqrMagnitude < 1e-6f) fwd = shooter.transform.forward;
                fwd = fwd.normalized;

                Vector3 spawn = shooter.transform.position + fwd * 5f - shooter.transform.up * 1.5f;
                Vector3 vel = shooter.Rb != null ? shooter.Rb.linearVelocity : fwd * 120f;
                if (vel.magnitude < 40f) vel = fwd * 120f;

                var tr = new TargetRef();
                tr.T = target;
                tr.UseFixed = false;
                tr.Name = "Player";

                MissileController mc = MissileController.Spawn(model, spawn, vel, tr, null);
                ApplyMissileConfig(mc, sp);   // AI 用 aiMissileName 指定的远程弹（默认 R-37）
                mc.FactionId = shooter != null ? shooter.FactionId : -1;
                mc.ShooterModel = shooter != null ? shooter.ModelName : "";
                mc.ShooterCall = shooter != null ? shooter.Callsign : "";
                mc.enabled = true;
                _live.Add(mc);

                Flash("BANDIT FOX", 2f);
                // 统一日志格式：包含导弹型号、发射者、目标、距离、速度等信息
                _api.Log("AAM: " + shooter.Callsign + " launched " + spName
                         + " at target Player"
                         + ", dist=" + Mathf.RoundToInt(dist) + "m"
                         + ", speed=" + Mathf.RoundToInt(vel.magnitude)
                         + ", model=" + (shooter != null ? shooter.ModelName : "unknown"));
            }
            catch (Exception e) { _api.Log("AAM: AI launch error " + e.Message); }
        }

        /// <summary>
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
        {
            mc.SpecName = spec != null ? spec.Name : cfgCargoName;
            mc.BoostTime = cfgBoostTime;
            mc.BoostThrust = cfgBoostThrust;
            mc.SustainThrust = cfgSustainThrust;
            mc.DragK = cfgDragK;
            // 速度上限：单位统一为节（1 马赫≈660 节），Body.Step 调用时再转 m/s
            mc.MaxSpeed = spec != null ? spec.MaxSpeed : cfgMaxSpeed;
            // 最大过载：使用规格表中的MaxG参数（各导弹不同），默认40G
            mc.MaxG = spec != null ? spec.MaxG : cfgMaxG;
            // 惯性巡航阻力：使用规格表中的CoastDrag参数（各导弹不同），默认100
            mc.CoastDrag = spec != null ? spec.CoastDrag : 100f;
            // 加速度：使用规格表中的Acceleration参数（马赫/秒，各导弹不同），默认0.5马赫/s
            mc.Acceleration = spec != null ? spec.Acceleration : 0.5f;
            // 跟踪速率：使用规格表中的TrackRate参数（度/秒，各导弹不同），默认60度/s
            mc.TrackRate = spec != null ? spec.TrackRate : 60f;
            mc.TurnRateDeg = spec != null ? spec.TurnRateDeg : cfgTurnRate;   // 使用规格表中的扭矩参数
            mc.Lifetime = spec != null ? spec.Lifetime : cfgLifetime;
            mc.Proximity = cfgProximity;
            mc.NavConstant = cfgNavConstant;
            // 地形避障参数（每发弹一份，运行期可各自被改写，不影响全局配置）
            mc.Avoid.Enabled = cfgAvoidEnabled;
            mc.Avoid.LookTime = cfgAvoidLookTime;
            mc.Avoid.LookTurn = cfgAvoidLookTurn;
            mc.Avoid.MinLook = cfgAvoidMinLook;
            mc.Avoid.MaxLook = cfgAvoidMaxLook;
            mc.Avoid.Clearance = cfgAvoidClearance;
            mc.Avoid.Strength = cfgAvoidStrength;
            mc.Avoid.PnCut = cfgAvoidPnCut;
            mc.Avoid.Interval = cfgAvoidInterval;
            mc.Avoid.SpeedStepFrac = cfgAvoidSpeedStepFrac;
            mc.Avoid.MaxInterval = cfgAvoidMaxInterval;
            mc.Avoid.ThreatFast = cfgAvoidThreatFast;
            mc.Avoid.ExtraG = cfgAvoidExtraG;
            mc.Avoid.Brake = cfgAvoidBrake;
            mc.Avoid.AbsFloor = cfgAvoidAbsFloor;
            mc.Avoid.Warmup = cfgAvoidWarmup;
            mc.Avoid.Nose = cfgAvoidNose;
            mc.Avoid.ImpactDetonate = cfgAvoidImpactDetonate;
            mc.Avoid.ProfileSpan = cfgAvoidProfileSpan;
            mc.Avoid.ProfileMargin = cfgAvoidProfileMargin;
            mc.Avoid.TerminalRange = cfgAvoidTerminalRange;
            mc.Avoid.TerminalMin = cfgAvoidTerminalMin;
            // 拦截弹：只认导弹目标（命中飞机不造成伤害）；对头接近率极高（两弹相加 ~800m/s，
            // 60fps 下每步就跨 16m），把引爆半径放大到 45m，避免高速穿靶。
            mc.InterceptOnly = spec != null && spec.AntiMissile;
            if (mc.InterceptOnly) mc.Proximity = Mathf.Max(cfgProximity, 45f);
            // 红外导引头：全向锁定 + 缩圈 IRCCM。
            // 拦截弹（MSDM）追的是来袭导弹而不是热源，不开红外头，免得被自己人的热诱弹带偏。
            mc.IrSeeker = cfgSeekerEnabled && !mc.InterceptOnly;
            mc.SeekerFov = cfgSeekerFov;
            mc.SeekerRange = cfgSeekerRange;
            mc.IrcmEnabled = cfgIrcmEnabled;
            mc.IrcmReact = cfgIrcmReact;
            mc.GateWide = cfgGateWide;
            mc.GateNarrow = cfgGateNarrow;
            mc.GateShrink = cfgGateShrink;
            mc.IrcmDecoyPenalty = cfgIrcmDecoyPenalty;
            mc.IrcmSpeedFrac = cfgIrcmSpeedFrac;
        }

        /// <summary>
        /// 自测用：在玩家前方 900m 放一架 **AI 驾驶的真飞机**（不是简化飞行体），
        /// 观察它能不能靠游戏自己的气动/引擎稳定飞起来。
        /// </summary>
        private void SpawnTestAiAircraft(Vector3 offset, string call, string personaId)
        {
            SpawnTestAiAircraft(offset, call, personaId, false);
        }

        /// <summary>
        /// flyAway = true 时靶机**背对玩家飞**（尾追工况）。避障专项自测要用它：
        /// 用户报的"追踪敌人途中撞墙"就是长时间低空尾追，对头拦截（默认）几秒就打完了，
        /// 根本考验不到避障。
        /// </summary>
        private void SpawnTestAiAircraft(Vector3 offset, string call, string personaId, bool flyAway)
        {
            try
            {
                string path = AiDesignPath;
                if (!File.Exists(path)) { _api.Log("AAM SELFTEST: AI design missing " + path); return; }

                Transform pt = _plane.transform;
                Vector3 pos = pt.position + offset;

                AiPersonality per = AiPersonalities.ById(personaId);
                if (per == null) per = AiPersonalities.All[0];
                // 和正式生成一致：直接生在该性格的作战高度上，免得一出生就大机动掉速
                pos.y = Mathf.Max(150f, per.Alt);
                float speed = per.Speed * cfgAiSpeedScale;

                Vector3 dir = flyAway ? (pos - pt.position) : (pt.position - pos);
                dir.y = 0f;
                if (dir.sqrMagnitude < 1f) dir = pt.forward;
                dir = dir.normalized;

                AiAircraft ai = AiAircraftFactory.Spawn(_api, path, pos, dir, speed, per.Alt, false, call);
                if (ai == null) { _api.Log("AAM SELFTEST: AI aircraft spawn FAILED [" + call + "]"); return; }
                ai.ApplyPersonality(per);
                ai.CruiseSpeed = speed;
                ai.cfgLogVerbose = true;     // 自测里打开：每 3s 一行高度/速度，方便判断性格是否生效
                _ai.Add(ai);
                _api.Log("AAM SELFTEST: AI aircraft spawned ok [" + call + "] persona=" + per.Id
                         + "(" + per.Desc + ") targetAlt=" + per.Alt.ToString("F0")
                         + " children=" + ai.transform.childCount);
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: AI spawn error " + e.Message); }
        }

        /// <summary>
        /// 性格验证自测（aiPersonaTest=true）：玩家周围每种性格各放一架（非敌对、开详细日志、关掉返航），
        /// 之后从日志里就能确认"谁在爬高、谁在贴地、谁在曲射拉距离"。
        /// </summary>
        private int _personaIdx;
        private float _personaNextAt;

        private void StepPersonaTest()
        {
            if (!cfgAiPersonaTest || _personaTestDone) return;
            if (_plane == null || !_plane.FlightModeInitialized) { RequestFlyMode(); return; }
            // 每隔 1.2s 放一架：每架是 60+ 个部件的真飞机，一帧塞 9 架会把游戏卡到个位数帧率。
            if (Time.unscaledTime < _personaNextAt) return;
            _personaNextAt = Time.unscaledTime + 1.2f;
            try
            {
                string path = AiDesignPath;
                if (!File.Exists(path)) { _api.Log("AAM PERSONA TEST: design missing " + path); _personaTestDone = true; return; }
                Transform pt = _plane.transform;
                AiPersonality[] all = AiPersonalities.All;
                if (_personaIdx >= all.Length) { _personaTestDone = true; _api.Log("AAM PERSONA TEST: all " + all.Length + " spawned"); return; }
                AiPersonality per = all[_personaIdx];
                float bearing = (360f / all.Length) * _personaIdx;
                _personaIdx++;
                Vector3 dirV = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                Vector3 pos = pt.position + dirV * 1200f;
                // 直接生成在性格作战高度上：出生就在目标高度 -> 高度误差≈0 -> 不会一出生就
                // 满舵爬升/俯冲（那个初始大机动会把速度从 300m/s 榨到 130m/s 然后失速栽地）。
                pos.y = Mathf.Max(150f, per.Alt);
                Vector3 toward = pt.position - pos;
                toward.y = 0f;
                if (toward.sqrMagnitude < 1f) toward = -dirV;
                string call = per.Id + "-T";
                float speed = per.Speed * cfgAiSpeedScale;
                AiAircraft ai = AiAircraftFactory.Spawn(_api, path, pos, toward.normalized, speed, per.Alt, false, call);
                if (ai == null) { _api.Log("AAM PERSONA TEST: spawn FAILED " + call); return; }
                ai.ApplyPersonality(per);
                ai.CruiseSpeed = speed;
                ai.cfgLogVerbose = true;      // 每 3s 一行 高度/速度/姿态 = 判据
                ai.FuelPlanEnabled = false;   // 测试期间别返航，免得压低高度干扰观察
                _ai.Add(ai);
                _api.Log("AAM PERSONA TEST: [" + call + "] " + per.Id + " " + per.Desc
                         + " targetAlt=" + per.Alt.ToString("F0") + " speed=" + speed.ToString("F0")
                         + " standoff=" + per.Standoff.ToString("F0") + " diveAlt=" + per.DiveAlt.ToString("F0"));
            }
            catch (Exception e) { _api.Log("AAM PERSONA TEST error: " + e.Message); }
        }

        // =================================================================
        // 自测：AI 超视距交战（aam_config.json 里 aiCombatTest=true）
        // =================================================================
        // 验证 2026-09-15 的火控强化真的生效。判据（全部落日志）：
        //   ① 在 cfgAiCombatDist（默认 9km）这种"超视距"距离上就能锁定并开火
        //      —— 旧行为要么够不着、要么打出去白费；
        //   ② 一轮齐射 2 发（SalvoSize=2，间隔 SalvoGap）；
        //   ③ 导弹能一路飞到玩家身上：把 aiDamagePlayer 设成 false 跑，命中会打
        //      "player hit (damage disabled)" —— 那就是"AI 打到了玩家"的铁证。
        private void StepAiCombatTest()
        {
            if (!cfgAiCombatTest || _combatTestDone) return;
            if (_plane == null || !_plane.FlightModeInitialized) { RequestFlyMode(); return; }

            if (_combatAi == null)
            {
                if (_combatSpawnAt <= 0f) _combatSpawnAt = Time.unscaledTime + 2f;
                if (Time.unscaledTime < _combatSpawnAt) return;
                try
                {
                    string path = AiDesignPath;
                    if (!File.Exists(path)) { _api.Log("AAM CTEST: design missing " + path); _combatTestDone = true; return; }
                    Transform pt = _plane.transform;
                    Vector3 fwd = pt.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    fwd = fwd.normalized;
                    Vector3 pos = pt.position + fwd * cfgAiCombatDist;
                    pos.y = Mathf.Max(400f, pt.position.y + cfgAiCombatAlt);
                    AiPersonality per = AiPersonalities.ById(cfgAiCombatPersona);
                    if (per == null) per = AiPersonalities.ById("SENTINEL");
                    float spd = per.Speed * cfgAiSpeedScale;
                    AiAircraft ai = AiAircraftFactory.Spawn(_api, path, pos, -fwd, spd, pos.y, true, "CTEST-1");
                    if (ai == null) { _api.Log("AAM CTEST: spawn FAILED"); _combatTestDone = true; return; }
                    ai.ApplyPersonality(per);
                    ai.CruiseSpeed = spd;
                    ai.FuelPlanEnabled = false;
                    _ai.Add(ai);
                    _combatAi = ai;
                    _combatLogAt = Time.unscaledTime + 1f;
                    _api.Log("AAM CTEST: hostile AI spawned dist=" + cfgAiCombatDist.ToString("F0")
                             + "m alt=" + pos.y.ToString("F0") + " persona=" + per.Id
                             + " radar=" + ai.RadarRange.ToString("F0")
                             + " cone=" + ai.RadarCone.ToString("F0")
                             + " fireFrac=" + ai.FireRangeFrac.ToString("F2")
                             + " salvo=" + ai.SalvoSize + " aiMissile="
                             + (AiMissileSpec != null ? AiMissileSpec.Name : "?"));
                }
                catch (Exception e) { _api.Log("AAM CTEST spawn error: " + e.Message); _combatTestDone = true; }
                return;
            }

            if (Time.unscaledTime < _combatLogAt) return;
            _combatLogAt = Time.unscaledTime + 1f;
            _combatLogN++;
            try
            {
                Transform pt = _plane.transform;
                float d = Vector3.Distance(_combatAi.transform.position, pt.position);
                BanditAircraft b = _combatAi as BanditAircraft;
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
        }

        // =================================================================
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
            Bounds shotB = new Bounds(); bool shotHas = false;
            try
            {
                if (ex)
                {
                    m = DesignModel.BuildWithGameLoader(p, "AMMOCHK_" + sp.Name, _api, false, false, true);
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
                        if (has)
                        {
                            info += " size=" + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2")
                                             + "x" + b.size.z.ToString("F2");
                            shotB = b; shotHas = true;
                        }
                    }
                }
            }
            catch (Exception e) { info += " build=EX " + e.Message; }
            finally
            {
                if (m != null)
                {
                    try
                    {
                        if (cfgCapture && shotHas)
                        {
                            Vector3 c = shotB.center + Vector3.up * 400f;   // 抬到空中，避开地面杂景
                            m.transform.position += Vector3.up * 400f;
                            float r = Mathf.Max(shotB.extents.x, Mathf.Max(shotB.extents.y, shotB.extents.z));
                            Vector3 dir = m.transform.forward;
                            Vector3 side = Vector3.Cross(dir, Vector3.up).normalized;
                            if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                            Vector3 cam = c - dir * r * 2.2f + side * r * 2.6f + Vector3.up * r * 0.9f;
                            string fn = "aam_model_" + sp.Name.Replace(" ", "_").Replace("/", "-") + ".png";
                            RenderAt(cam, c, fn);
                            _api.Log("AAM AMMO: model shot -> " + fn + " center=" + c + " r=" + r.ToString("F2"));
                        }
                    }
                    catch (Exception e2) { _api.Log("AAM AMMO: model shot failed " + e2.Message); }
                    UnityEngine.Object.Destroy(m);
                }
            }

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
                _ammoMsdm = null;
                for (int i = 0; i < _live.Count; i++)
                    if (_live[i] != null && _live[i].SpecName == "MSDM") { _ammoMsdm = _live[i]; break; }
                _api.Log("AAM AMMO: intercept fire issued ok=" + ok + " live=" + _live.Count
                         + " msdmMissile=" + (_ammoMsdm != null ? "yes" : "no"));
                return;
            }

            // 3) 等拦截结果
            if (_ammoStage == 3)
            {
                _ammoLogT += Time.deltaTime;
                if (cfgCapture && _ammoMsdm != null && !_ammoMsdm.Finished && _ammoT > _ammoShotNext)
                {
                    _ammoShotNext = _ammoT + 0.9f;
                    _ammoShotN++;
                    AmmoShot(_ammoShotN);
                }
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
                    if (cfgCapture) { _ammoShotN++; AmmoShot(_ammoShotN); }
                    _api.Log("AAM AMMO: intercept phase over | threatDead=" + threatDead
                             + " intercepts=" + MissileController.InterceptCount
                             + " | " + LiveMissileSummary());
                }
                return;
            }

            // 4) 收尾
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
                _gunBurstN = 0; _gunBurstT = 0f; _gunBurstFps = 0f; _gunBurstFpsN = 0; _gunShotDone = false;
                BulletRound.SpawnedCount = 0;
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
                if (cfgCapture && !_gunShotDone && _gunBurstT > 0.12f)
                {
                    _gunShotDone = true;
                    GunShot();
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

            // 7) 贴脸点射：在 AI 靶机侧后方 100m 直接朝它打 12 发（必中，验证命中判定与拆件）
            if (_ammoStage == 7)
            {
                if (_ammoT < 0.8f) return;
                _ammoStage = 8; _ammoT = 0f;
                AiAircraft tgt0 = (_ai.Count > 0) ? _ai[0] : null;
                if (tgt0 == null) { _api.Log("AAM GUN: no AI target, skip point-blank test"); return; }
                _gunPtTgt = tgt0.transform;
                _gunPtLast = _gunPtTgt.position;
                _gunPtVel = Vector3.zero;
                _gunPtN = 0; _gunPtAcc = 0f; _gunPtShot = false; _gunPtT = 0f;
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
                int realLive = 0;
                try { realLive = UnityEngine.Object.FindObjectsOfType<BulletRound>(true).Length; } catch { }
                _api.Log("AAM GUN: done. counter live=" + BulletRound.LiveCount
                         + " spawned=" + BulletRound.SpawnedCount
                         + " objectsInScene=" + realLive
                         + " gun rounds left=" + CombatCountOfType(GunCargo()));
                _api.Log("AAM SELFTEST: done. live=" + _live.Count + " ai=" + _ai.Count);
            }
        }

        /// <summary>自测用：给拦截弹（MSDM）拍近景/远景各一张。</summary>
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

        /// <summary>自测用：从机尾后方拍一张，看机炮弹流。</summary>
        private void GunShot()
        {
            try
            {
                if (_plane == null) return;
                BulletRound nb = BulletRound.Newest;
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
                }
            }
            catch (Exception e) { _api.Log("AAM GUN: shot failed " + e.Message); }
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
                GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "AMMO_THREAT", _api, false, false, true);
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
            if (_plane == null) return;          // 自测不需要"玩家已进入飞行"，物理在任何场景都能跑
            if (!_testStarted)
            {
                _testStarted = true;
                _testT = 0f;
                _shotStage = 0;
                _api.Log("AAM SELFTEST: start | partPrefabsUsable=" + DesignModel.PrefabsUsable
                         + " designExists=" + File.Exists(DesignPath)
                         + " flyMode=" + _plane.FlightModeInitialized);
                RequestFlyMode();
                CombatGive(cfgGiveOnStart > 0 ? cfgGiveOnStart : 2);
                _api.Log("AAM SELFTEST: combat hold now=" + CombatCount());
                return;
            }

            _testT += Time.deltaTime;

            if (_shotStage == 0 && _testT > 1.0f)
            {
                _shotStage = 1;
                SpawnTestDrone();
                return;
            }
            // 若已请求进入飞行模式，等它真正就绪再打（最多等 25s，海图/编辑器场景也照打）
            // 注意判据是 FlightModeInitialized 本身，不能看 _flyModeRequested：
            // StartFlyMode 会切一次场景，RequestFlyMode 里的场景切换检测会把 _flyModeRequested
            // 清回 false，于是"请求过起飞"这件事被抹掉，自测会在弹药还没发下来时就开火
            // （表现为 fire aborted - combat hold has 0 PL-15，整轮自测打不出导弹）。
            if (_shotStage == 1 && _testT > 2.5f)
            {
                if (!_plane.FlightModeInitialized && _testT < 25f) return;
                _shotStage = 2;
                _testFireT = 0f;
                // 顺便把"AI 开的真飞机"也测一遍：
                //   一架放近处给导弹当靶子（验证导弹能打飞机），
                //   一架放远处专门观察长时间巡航稳不稳。
                //   两架分别用"高空俯冲型 / 低空巡航型"，日志里能直接看出高度分层。
                //   性格自测模式下这两架和导弹都跳过，只留 7 架性格机，免得机身太多拖垮帧率。
                if (cfgAiPersonaTest)
                {
                    // 性格自测：StepPersonaTest 已经放了 7 架（每种性格各一架）；这里再打一发导弹，
                    // 顺带把"烟雾拖尾"也一起验了（否则性格自测期间看不到导弹）。
                    _api.Log("AAM SELFTEST: persona run (7 personas from StepPersonaTest + missile for smoke check)");
                }
                else
                {
                    // 玩家命中专项（playerHitTest）：**故意不放任何靶机**。
                    // 导引头是按"红外最亮点"选目标的，而自测时玩家停在地面（红外≈0），
                    // 场上只要有靶机，来袭弹就会去追靶机、根本打不到玩家。
                    // 只留玩家一个目标时，来袭弹在视场里找不到红外源 → 自动退回纯 PN 直冲玩家。
                    if (cfgPlayerHitTest)
                    {
                        _api.Log("AAM SELFTEST: player-hit run (no targets, real missile at player)");
                    }
                    // 避障专项（avoidTest）：前方 2.2km 放一架低空靶机**背对我们飞** ——
                    // 导弹要在 165m 高度上追十几秒，穿越沿途丘陵，正是"追踪途中撞墙"的工况。
                    // 命中/脱靶 + minAgl/avoid 计数 + 有没有 TERRAIN IMPACT 就是判据。
                    else if (cfgEvadeTest)
                    {
                        // 规避专项（evadeTest）：前方 3.2km 放一架**中空**AI 背对我们平飞，打一发尾追弹。
                        // ① 必须尾追不能对头：对头 900m 一两秒就打完了，规避来不及展开；
                        // ② 必须中空（SENTINEL 1600m）不能低空：低空机的规避地板就是它自己的高度，
                        //    压不出"俯冲换速度"那一段，1600m 才有 600m 的下压空间。
                        // 判据：AI 日志出现 EVADE（off 从 ≈180 收敛到 ≈90）+ wantAlt 下压 + 航向被掰 90 度。
                        _api.Log("AAM SELFTEST: evade run (3.2km tail chase, AI should beam + dive)");
                        SpawnTestAiAircraft(new Vector3(0f, 0f, 3200f), "DRONE-E", "SENTINEL", true);
                    }
                    else if (cfgFlareTest)
                    {
                        // 热诱弹 / IRCCM 专项：2.6km 放一架靶机，打一发红外弹；
                        // 之后每 2s 在靶机位置抛一枚热诱弹（模拟对方放干扰），
                        // 看导引头会不会收圈把它剔掉。
                        // 判据：日志出现 FLARE TEST（抛饵 + IR 值），
                        //       以及 msl ircm=True / rejects 递增。
                        _api.Log("AAM SELFTEST: flare run (2.6km target, IR seeker + IRCCM)");
                        SpawnTestAiAircraft(new Vector3(0f, 0f, 2600f), "DRONE-F", "SENTINEL", true);
                        _flareTestNext = 3f;
                        _flareTestPlayerDone = false;
                    }
                    else if (cfgAvoidTest)
                    {
                        _api.Log("AAM SELFTEST: avoid run (low-altitude tail chase over terrain)");
                        SpawnTestAiAircraft(new Vector3(0f, 0f, 2200f), "DRONE-A", "LOWRIDER", true);
                    }
                    else
                    {
                        // 常态自测：一架近处靶子（验导弹命中）+ 低空/俯冲两种性格（看高度分层）。
                        SpawnTestAiAircraft(new Vector3(0f, 60f, 900f), "DRONE-T", "LOWRIDER");
                        SpawnTestAiAircraft(new Vector3(1800f, -60f, 2400f), "DRONE-L", "FALCON");
                    }
                }
                // 弹药兜底：自测刚启动时 _missileTypes 还没建好，CombatGive 会静默失败
                // （日志里只剩 "combat hold now=0"）。开火前补一次。
                if (CombatCount() <= 0) CombatGive(2);
                bool ok = TryFire(forceNoLock: true);
                _api.Log("AAM SELFTEST: fire issued ok=" + ok + " live=" + _live.Count + " targets=" + TargetRegistry.Count);
                return;
            }
            if (_shotStage == 2)
            {
                // 回退分支验证：40s 时 MachineShop 自测已装好战斗货物（combatW>0），
                // 此时再生成一架 AI——若 ActivateFlyMode/ApplyCargo 副作用没被回退，
                // 玩家 massField 会无端上涨 combatW/15；被回退则日志出现 "reverted" 且质量不动。
                if (!_revertChecked && _testT > 40f)
                {
                    _revertChecked = true;
                    _api.Log("AAM SELFTEST: revert-check spawn begin (expect cargo loaded)");
                    SpawnTestAiAircraft(new Vector3(-1500f, 60f, 1200f), "DRONE-R", "LOWRIDER");
                    _api.Log("AAM SELFTEST: revert-check spawn done");
                    return;
                }
                _testFireT += Time.deltaTime;
                _logT += Time.deltaTime;
                float logEvery = (cfgAvoidTest || cfgEvadeTest || cfgFlareTest) ? 1f : 5f;   // 专项自测 1s 一条，才看得清遥测演化
                if (_logT > logEvery)   // 自测诊断日志限流：避免写文件拖慢主线程
                {
                    _logT = 0f;
                    LogLive();
                    LogAiStates();
                }
                if (cfgCapture && _shotCount < 14 && _testFireT > _shotNextAt)
                {
                    _shotCount++;
                    // 前 7 张打得密（0.7s 一张，覆盖导弹助推/巡航/引爆的烟迹），
                    // 后 7 张拉开（铺满整个观察窗口，用来拍 AI 性格的真实飞行姿态）。
                    _shotNextAt += (_shotCount <= 7) ? 0.7f : Mathf.Max(2f, cfgTestObserve / 14f);
                    Shoot("aam_test_" + _shotCount);
                    ChaseShot("aam_test_" + _shotCount);   // 跟拍/远景：导弹本体 + 整条烟迹 + 引爆后残留的烟
                }
                // 导弹打完不代表自测结束：再留一段时间专门观察 AI 飞机能不能靠游戏自己的气动
                // 稳定巡航 —— 这段时间的 "[AAM] <呼号>" 日志（高度/速度/姿态）就是判据。
                bool missileSettled = (_live.Count == 0 || _testFireT > cfgLifetime + 3f);
                if (missileSettled && _testFireT > cfgTestObserve)   // 观察窗口可配（默认 140s：够 FALCON 爬到 3000m 再俯冲）
                {
                    _shotStage = 3;
                    _api.Log("AAM SELFTEST: done. live=" + _live.Count + " ai=" + _ai.Count);
                }
            }
        }

        /// <summary>自测用：把每架 AI 的高度/速度/目标高度打出来，验证性格（尤其高度分层）是否真的生效。</summary>
        private void LogAiStates()
        {
            for (int i = 0; i < _ai.Count; i++)
            {
                AiAircraft ai = _ai[i];
                if (ai == null) continue;
                Vector3 v = (ai.Rb != null) ? ai.Rb.linearVelocity : Vector3.zero;
                // hdg = 水平航向（0=+Z 北，90=+X 东）。规避专项靠它看"航向有没有被掰到 3/9 线"。
                Vector3 vf = new Vector3(v.x, 0f, v.z);
                float hdg = (vf.sqrMagnitude > 0.01f) ? Mathf.Atan2(vf.x, vf.z) * Mathf.Rad2Deg : 0f;
                if (hdg < 0f) hdg += 360f;
                _api.Log("AAM SELFTEST AI: " + ai.Callsign
                         + " persona=" + (ai.Persona != null ? ai.Persona.Id : "-")
                         + " alt=" + Mathf.RoundToInt(ai.transform.position.y)
                         + " wantAlt=" + Mathf.RoundToInt(ai.CruiseAlt)
                         + " spd=" + Mathf.RoundToInt(v.magnitude)
                         + " vy=" + v.y.ToString("F1")
                         + " hdg=" + hdg.ToString("F0"));
            }
        }

        /// <summary>自测用：从主菜单/建造场景请求进入飞行模式（配置开关，默认关）。</summary>
        private void RequestFlyMode()
        {
            if (!cfgAutoEnterFlyMode) return;
            if (_plane != null && _plane.FlightModeInitialized) return;

            // 场景切换检测：PlaneContainer 从"无"变成"有" = 新场景加载完成，
            // 重置请求闩锁，让新场景里的"起飞"请求能重新发出去。
            bool hasPlane = (_plane != null);
            if (hasPlane && !_hadPlane) { _flyModeRequested = false; _flyReqNext = 0f; }
            _hadPlane = hasPlane;

            // ① 主菜单场景里没有飞机（PlaneContainer==null）：StartFlyMode 是空操作，
            //    必须先把一个存档"载入"起来。做法照抄 Machine.Core 的 ModSavesUI.OnLoadSave。
            if (_plane == null)
            {
                if (cfgTestAutoLoadSave && !_saveLoadIssued && Time.unscaledTime >= _flyReqNext)
                {
                    _flyReqNext = Time.unscaledTime + 2f;
                    if (TryLoadSaveAndEnter()) { _saveLoadIssued = true; _api.Log("AAM AUTOLOAD: save load issued, waiting for scene"); }
                }
                return;
            }

            // ② 已经有飞机（停机坪/机库）但还没起飞：调游戏 StartFlyMode。
            //    每个场景只调一次 —— 反复调用会反复重启场景加载，永远进不去。
            if (_flyModeRequested) return;
            if (Time.unscaledTime < _flyReqNext) return;
            _flyReqNext = Time.unscaledTime + 2f;
            try
            {
                GameManager gm = GameManager.Instance;
                if (gm == null) { _api.Log("AAM SELFTEST: GameManager not found"); return; }
                MethodInfo m = typeof(GameManager).GetMethod("StartFlyMode",
                    new Type[] { typeof(bool), typeof(bool) });
                if (m == null) { _api.Log("AAM SELFTEST: StartFlyMode not found"); return; }
                m.Invoke(gm, new object[] { false, false });
                _flyModeRequested = true;
                _api.Log("AAM SELFTEST: StartFlyMode(false,false) requested");
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: StartFlyMode failed " + e.Message); }
        }

        private float _flyReqNext;
        private bool _flyModeRequested;
        private bool _hadPlane;
        private bool _saveLoadIssued;

        /// <summary>
        /// 自测用：在主菜单里把一个 mod 存档载入进游戏（= 点加载器的 "Mod Saves" 按钮）。
        /// 配方逆向自 Machine.Core 的 ModSavesUI.OnLoadSave：
        ///   存档目录 %USERPROFILE%\AppData\LocalLow\Aviassembly\Machine_Mod\Aviassembly\SaveGames\*.plane
        ///   然后对场景里的 LoadPanel 反射调 SelectFile(存档名) + Load()，并置 ViaModSaves=true。
        /// 这是唯一能在"纯主菜单"状态下自动进游戏的办法（StartFlyMode 在菜单里不生效）。
        /// </summary>
        private bool TryLoadSaveAndEnter()
        {
            try
            {
                string dir = Path.Combine(Path.Combine(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData"), "LocalLow"),
                    Path.Combine("Aviassembly", Path.Combine("Machine_Mod", Path.Combine("Aviassembly", "SaveGames"))));
                if (!Directory.Exists(dir)) { _api.Log("AAM AUTOLOAD: save dir missing " + dir); return false; }

                string chosen = null;
                if (!string.IsNullOrEmpty(cfgTestSaveName))
                {
                    if (File.Exists(Path.Combine(dir, cfgTestSaveName + ".plane"))) chosen = cfgTestSaveName;
                }
                if (chosen == null)
                {
                    string[] files = Directory.GetFiles(dir, "*.plane");
                    if (files == null || files.Length == 0) { _api.Log("AAM AUTOLOAD: no .plane saves"); return false; }
                    System.Array.Sort(files, delegate (string a, string b)
                    { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                    chosen = Path.GetFileNameWithoutExtension(files[0]);
                }

                LoadPanel[] lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0)
                {
                    _api.Log("AAM AUTOLOAD: LoadPanel not found (not in main menu?)");
                    return false;
                }
                object lp = lps[0];
                Type ty = lp.GetType();
                MethodInfo sf = ty.GetMethod("SelectFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo ld = ty.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sf == null || ld == null)
                {
                    _api.Log("AAM AUTOLOAD: SelectFile/Load missing on " + ty.Name);
                    return false;
                }
                Machine.Mod.MachineState.ViaModSaves = true;
                Machine.Mod.MachineState.PureMode = false;
                sf.Invoke(lp, new object[] { chosen });
                ld.Invoke(lp, null);
                _api.Log("AAM AUTOLOAD: invoking LoadPanel.Load('" + chosen + "') -> entering game");
                return true;
            }
            catch (Exception e) { _api.Log("AAM AUTOLOAD failed: " + e.Message); return false; }
        }
        private float _testFireT;
        private float _logT;
        private int _shotCount;
        private float _shotNextAt = 0.5f;
        private GameObject _drone;

        private void SpawnTestDrone()
        {
            try
            {
                GameObject model = DesignModel.BuildWithGameLoader(DesignPath, "AAM_TargetDrone", _api);
                if (model == null) model = DesignModel.BuildFallback(DesignPath, "AAM_TargetDrone", _api);
                if (model == null) { _api.Log("AAM SELFTEST: drone model unavailable"); return; }

                Transform t = _plane.transform;
                Vector3 pos = t.position + t.forward * 4000f + t.right * 700f + t.up * 260f;
                Vector3 dir = Quaternion.AngleAxis(18f, Vector3.up) * t.forward;
                _drone = model;
                DroneController.Spawn(model, pos, dir, cfgTestDroneSpeed);
                _api.Log("AAM SELFTEST: drone spawned at " + pos + " dir=" + dir + " speed=" + cfgTestDroneSpeed);
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: drone spawn failed " + e.Message); }
        }

        private void LogLive()
        {
            if (_live.Count == 0) { _api.Log("AAM SELFTEST: no live missile"); return; }
            for (int i = 0; i < _live.Count; i++)
            {
                MissileController m = _live[i];
                if (m == null) continue;
                float r = -1f;
                if (m.Target != null && m.Target.Valid)
                    r = Vector3.Distance(m.Body.Pos, m.Target.Position);
                _api.Log("AAM SELFTEST: t=" + m.AliveTime.ToString("F1") + "s pos=" + m.Body.Pos
                         + " speed=" + Mathf.RoundToInt(m.Body.Speed)
                         + " alt=" + Mathf.RoundToInt(m.Body.Pos.y)
                         + " range=" + (r < 0 ? -1 : Mathf.RoundToInt(r))
                         + " minRange=" + Mathf.RoundToInt(m.MinRange)
                         + " agl=" + (m.MinAgl < 0f || float.IsInfinity(m.MinAgl) ? -1 : Mathf.RoundToInt(m.MinAgl))
                         + " threat=" + m.AvoidThreat.ToString("F2")
                         + " avoid=" + m.AvoidEvents);
            }
        }

        private void Shoot(string tag)
        {
            try
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(ResolveShotDir(), tag + ".png"));
                _api.Log("AAM SELFTEST: screenshot -> " + Path.Combine(ResolveShotDir(), tag + ".png"));
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: screenshot failed " + e.Message); }
        }

        private string ResolveShotDir()
        {
            string dir = cfgShotDir;
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(_api.GetModsDirectory(), "MachineAAM");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// 跟拍机位：用一个只渲染到 RenderTexture 的相机从斜后方拍导弹，
        /// 再 encode 成 PNG。这样能直接看到"设计文件还原出来的外形"，
        /// 而且完全不干扰主摄像机的画面。
        /// </summary>
        private Vector3 _lastMPos;
        private Vector3 _lastMDir;
        private float _lastMT = -999f;

        private void ChaseShot(string tag)
        {
            try
            {
                MissileController m = null;
                for (int i = 0; i < _live.Count; i++) if (_live[i] != null) { m = _live[i]; break; }

                Vector3 p;
                Vector3 d;
                if (m != null)
                {
                    p = m.Body.Pos;
                    d = m.Body.Dir;
                    _lastMPos = p; _lastMDir = d; _lastMT = Time.time;
                }
                else if (Time.time - _lastMT < 9f)
                {
                    // 导弹已经引爆：刚摘下来的烟团还留在原地慢慢散，继续拍它 → 正好验证"渐变与消失"
                    p = _lastMPos;
                    d = _lastMDir;
                }
                else return;

                Vector3 side = Vector3.Cross(d, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;

                // 近景看导弹本体 + 喷口火焰，远景看整条烟迹（烟团留在航迹上，只有远景才看得出"烟轨"）
                RenderAt(p - d * 17f + side * 8f + Vector3.up * 3.5f, p, tag + "_chase.png");
                RenderAt(p - d * 62f + side * 34f + Vector3.up * 14f, p, tag + "_trail.png");
                SmokeDiag();
                _api.Log("AAM SELFTEST: chase+trail shots -> " + tag + " pos=" + p);
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: chase shot failed " + e.Message); }
        }

        /// <summary>自测诊断：直接读粒子系统的真实状态（粒子数 / 是否播放 / 材质 / 世界包围盒），
        /// 用来判断"看不见烟"到底是①没发射、②没渲染、还是③太小/太淡。包围盒 size=0 就是没渲染出来。</summary>
        private void SmokeDiag()
        {
            try
            {
                MissileController m = null;
                for (int i = 0; i < _live.Count; i++) if (_live[i] != null) { m = _live[i]; break; }
                if (m == null) return;
                ParticleSystem[] pss = m.GetComponentsInChildren<ParticleSystem>(true);
                if (pss == null || pss.Length == 0) { _api.Log("AAM SMOKEDIAG: no ParticleSystem under missile"); return; }
                for (int i = 0; i < pss.Length; i++)
                {
                    ParticleSystem ps = pss[i];
                    ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                    Bounds b = (r != null) ? r.bounds : new Bounds();
                    _api.Log("AAM SMOKEDIAG: ps#" + i + " name=" + ps.gameObject.name
                        + " playing=" + ps.isPlaying + " count=" + ps.particleCount
                        + " rate=" + ps.emission.rateOverTime.constant.ToString("F1")
                        + " space=" + ps.main.simulationSpace
                        + " size=" + ps.main.startSize.constantMin.ToString("F2") + "~" + ps.main.startSize.constantMax.ToString("F2")
                        + " scale=" + ps.transform.lossyScale.ToString("F2")
                        + " rEnabled=" + (r != null && r.enabled)
                        + " rMode=" + (r != null ? r.renderMode.ToString() : "-")
                        + " mat=" + ((r != null && r.sharedMaterial != null)
                                     ? (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "sh?") : "NULL")
                        + " bndSize=" + b.size.ToString("F1") + " bndC=" + b.center.ToString("F0"));
                }
            }
            catch (Exception e) { _api.Log("AAM SMOKEDIAG: " + e.Message); }
        }

        /// <summary>用自测相机从指定位置渲染一张 PNG（自测专用，不影响玩家画面）。</summary>
        private void RenderAt(Vector3 camPos, Vector3 look, string file)
        {
            if (_testCam == null)
            {
                var go = new GameObject("AAM_TestCam");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _testCam = go.AddComponent<Camera>();
                _testCam.fieldOfView = 45f;
                _testCam.nearClipPlane = 0.4f;
                _testCam.farClipPlane = 40000f;
                _rt = new RenderTexture(1024, 576, 24);
                _testCam.targetTexture = _rt;
            }
            _testCam.transform.position = camPos;
            _testCam.transform.LookAt(look);
            _testCam.Render();

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0f, 0f, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(Path.Combine(ResolveShotDir(), file), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        private Camera _testCam;
        private RenderTexture _rt;
    }

    /// <summary>手动解析 WAV（PCM 8/16/24/32bit），用于发射音效，不依赖 UnityWebRequest。</summary>
    public static class AamWavUtil
    {
        public static AudioClip Decode(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 44) return null;
                if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF") return null;
                if (System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") return null;

                int pos = 12;
                int channels = 2, sampleRate = 44100, bits = 16;
                int dataOffset = -1, dataLen = 0;
                while (pos + 8 <= bytes.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    if (id == "fmt ")
                    {
                        if (pos + 24 <= bytes.Length)
                        {
                            short fmt = BitConverter.ToInt16(bytes, pos + 8);
                            if (fmt != 1) return null;
                            channels = BitConverter.ToInt16(bytes, pos + 10);
                            sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                            bits = BitConverter.ToInt16(bytes, pos + 22);
                        }
                    }
                    else if (id == "data")
                    {
                        dataOffset = pos + 8;
                        dataLen = size;
                        break;
                    }
                    pos += 8 + size + (size % 2);
                    if (size <= 0) break;
                }
                if (dataOffset < 0) return null;
                if (dataLen <= 0 || dataLen > bytes.Length - dataOffset) dataLen = bytes.Length - dataOffset;
                int bytesPerSample = bits / 8;
                int sampleCount = dataLen / bytesPerSample / channels;
                if (sampleCount <= 0 || channels <= 0 || channels > 8) return null;

                float[] samples = new float[sampleCount * channels];
                for (int i = 0; i < samples.Length; i++)
                {
                    int o = dataOffset + i * bytesPerSample;
                    if (o + bytesPerSample > bytes.Length) break;
                    if (bits == 16)
                    {
                        samples[i] = BitConverter.ToInt16(bytes, o) / 32768f;
                    }
                    else if (bits == 8)
                    {
                        samples[i] = (bytes[o] - 128) / 128f;
                    }
                    else if (bits == 24)
                    {
                        int v = (bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16));
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        samples[i] = v / 8388608f;
                    }
                    else if (bits == 32)
                    {
                        samples[i] = BitConverter.ToInt32(bytes, o) / 2147483648f;
                    }
                    else return null;
                }
                AudioClip clip = AudioClip.Create(Path.GetFileName(path), sampleCount, channels, sampleRate, false);
                clip.SetData(samples, 0);
                return clip;
            }
            catch (Exception e) { UnityEngine.Debug.Log("AAM WavUtil error " + path + " " + e.Message); return null; }
        }
    }
}
