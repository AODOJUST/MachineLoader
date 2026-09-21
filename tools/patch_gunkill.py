# -*- coding: utf-8 -*-
# 机炮对 AI 的杀伤改为累积损伤（ExplodePart 对 AI 机抛 NRE，见 09-12 日记）：
# 命中 8 发走导弹同款整机击毁路径（ExplodePlane + Despawn + KillFeed）。
# 同时去掉 gun hit try 刷屏日志。
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"

# --- 整段替换 ReportGunHit（从方法头到 FeedKill 之前）---
START = """        /// <summary>
        /// 机炮命中：只炸掉离弹着点最近的那一个部件（不像导弹那样直接引爆整机），
        /// 并在目标因此坠毁时登记击杀。由 AamSystem 的子弹命中检测调用。
        /// </summary>
        internal static bool ReportGunHit(Transform root, Vector3 at, string kModel, string kCall, int kFaction)
        {"""
END = """        internal static void FeedKill(string kModel, string kCall, int kFaction, string vModel, string vCall, int vFaction)"""

NEW = """        // ---- 机炮对 AI 飞机：累积损伤 ----
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
                    ai.Despawn();
                    Machine.Core.Log.Info("[AAM] AI aircraft destroyed by gun: " + ai.Callsign);
                    FeedKill(kModel, kCall, kFaction, ai.ModelName, ai.Callsign, victimFaction);
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

"""

i0 = s.find(START.replace("\n", NL))
i1 = s.find(END.replace("\n", NL))
if i0 < 0 or i1 < 0 or i1 <= i0:
    print("!! anchor not found", i0, i1)
else:
    s = s[:i0] + NEW.replace("\n", NL) + s[i1:]
    # 去掉 bullet passed 诊断的大部分噪音（保留前 2 条）
    s = s.replace("if (_gunDbgNear < 8)".replace("\n", NL), "if (_gunDbgNear < 2)".replace("\n", NL))
    with io.open(SRC, "w", encoding="utf-8", newline="") as f:
        f.write(s)
    print("OK, wrote", len(s), "chars")
