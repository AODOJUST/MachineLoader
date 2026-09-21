using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Machine.Mod;

namespace Machine.Faction
{
    // =====================================================================
    // 入口
    // =====================================================================
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.faction"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("FactionSystem loading...");
            var go = new GameObject("Machine.Faction");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FactionSystem>().Init(api);
        }
    }

    /// <summary>一个玩家/AI的战绩记录。</summary>
    public class PlayerStat
    {
        public string Name = "Pilot";       // 玩家名或AI呼号
        public string Model = "";            // 机型
        public bool IsAI;                    // 是否AI
        public bool Alive = true;            // 是否存活（退出/被删除的不显示）
        public int Kills;
        public int Deaths;
    }

    /// <summary>一个阵营（对应一座主岛）。</summary>
    public class FactionData
    {
        /// <summary>对战积分模式的回合基准积分（各方开局 / 重置后的值）。</summary>
        public const float DefaultBasePoints = 800000f;

        public string Name = "Faction";
        public int ContinentIndex;        // ContinentManager.continents 下标
        public Vector3 HomePos;           // 主岛基地坐标
        public Airport BaseAirport;       // 主岛基地
        public object Continent;               // ContinentData 实例（机场归属判定）
        public string ContinentTypeName = "";  // ContinentType 资产名（Airport.id 后缀，兜底判定）
        public int Kills;                 // 本阵营总击杀
        public int Deaths;                // 本阵营总阵亡
        public int AiCount = 0;           // 本阵营 AI 数量（玩家可改；默认 0，避免开局卡顿）
        public List<string> AiDesigns = new List<string>();   // 本阵营 AI 可驾驶机型池
        public int DesignCursor;          // 当前生成机型游标
        public readonly List<PlayerStat> Players = new List<PlayerStat>();  // 本阵营玩家/AI战绩

        // ---- 对战积分模式（阵营战） ----
        public float Points = DefaultBasePoints;      // 当前剩余积分
        public float BasePoints = DefaultBasePoints;  // 回合基准积分（重置用）
        public bool Eliminated;                       // 积分低于 0，本回合已落败
        public int AircraftLost;                      // 本回合被摧毁（坠毁/被击落）的飞机数
        public float LostValue;                       // 本回合累计损失造价
    }

    /// <summary>阵营公开 API（AAM / Radar 等 Mod 反射联动）。</summary>
    public static class FactionApi
    {
        private static FactionSystem _sys;

        internal static void Bind(FactionSystem sys) { _sys = sys; }
        public static bool Available { get { return _sys != null; } }

        public static int PlayerFaction { get { return _sys != null ? _sys.PlayerFactionId : 0; } }
        public static int FactionCount { get { return _sys != null ? _sys.Factions.Count : 0; } }

        /// <summary>
        /// 机场的"实际归属"（雷达据此上色）：
        ///  · 积分战 = 大陆归属（机场落在哪个主岛大陆上就归那个阵营；-1 = 中立大陆）；
        ///  · 阵地战 = **当前占领方**（无人占领时退回原始归属）→ 机场会随占点战实时变色。
        /// </summary>
        public static int AirportFaction(Airport ap)
        {
            if (_sys == null) return -1;
            try { return _sys.EffectiveAirportOwner(ap); } catch { return -1; }
        }

        /// <summary>机场的原始归属（只看大陆，不含占点结果）。</summary>
        public static int AirportNativeFaction(Airport ap)
        {
            if (_sys == null) return -1;
            try { return _sys.AirportFactionOf(ap); } catch { return -1; }
        }

        /// <summary>按坐标判定机场归属阵营（-1 = 中立/未知）。</summary>
        public static int AirportFactionAt(Vector3 pos)
        {
            if (_sys == null) return -1;
            try { return _sys.AirportFactionAt(pos); } catch { return -1; }
        }

        /// <summary>当前模式：0 = 积分战，1 = 阵地战，-1 = 阵营系统未就绪。</summary>
        public static int Mode { get { return _sys == null ? -1 : _sys.GameMode; } }

        /// <summary>阵地战本回合剩余秒数；积分战 / 未就绪 = -1。</summary>
        public static float TerritoryTimeLeft
        {
            get { return (_sys != null && _sys.TerritoryEnabled) ? _sys.TerrTimeLeft : -1f; }
        }

        /// <summary>某阵营当前占有的阵地（机场/基地）数；阵营无效返回 -1。</summary>
        public static int TerritoryOwned(int factionId)
        {
            if (_sys == null || factionId < 0 || factionId >= _sys.Factions.Count) return -1;
            try { return _sys.CountOwned(factionId); } catch { return -1; }
        }

        /// <summary>阵地总数（没建表时 -1）。</summary>
        public static int TerritoryTotal
        {
            get { return _sys == null ? -1 : _sys.Territories.Count; }
        }

        /// <summary>阵营名；越界或用不上时返回空串。</summary>
        public static string FactionName(int id)
        {
            if (_sys == null || id < 0 || id >= _sys.Factions.Count) return "";
            var fd = _sys.Factions[id];
            return fd != null ? fd.Name : "";
        }

        /// <summary>玩家所在阵营的主岛基地坐标（雷达据此把己方基地标蓝）。</summary>
        public static Vector3 PlayerHomePos
        {
            get
            {
                if (_sys == null) return Vector3.zero;
                int pf = _sys.PlayerFactionId;
                if (pf >= 0 && pf < _sys.Factions.Count) return _sys.Factions[pf].HomePos;
                return Vector3.zero;
            }
        }

        /// <summary>实体所属阵营：-1 = 未知（旧系统，按 Hostile 判定）。玩家机无标记时也视为玩家阵营。</summary>
        public static int FactionOf(GameObject go)
        {
            if (go == null) return -1;
            var mark = go.GetComponentInParent<FactionMarker>();
            if (mark != null) return mark.FactionId;
            // 玩家机特判：标记未挂载（延迟）时也按玩家阵营，避免同阵营 AI 误攻玩家
            if (_sys != null && _sys.PlayerPlane != null)
            {
                try
                {
                    if (go == _sys.PlayerPlane.gameObject || go.transform.IsChildOf(_sys.PlayerPlane.transform))
                        return _sys.PlayerFactionId;
                }
                catch { }
            }
            return -1;
        }

        /// <summary>两实体是否敌对（任意一方无阵营时按旧规则：敌机=敌对）。</summary>
        public static bool IsEnemy(GameObject a, GameObject b)
        {
            if (a == null || b == null) return false;
            int fa = FactionOf(a);
            int fb = FactionOf(b);
            if (fa >= 0 && fb >= 0) return fa != fb;
            // 旧系统兼容：带 Bandit 标记的 AI 视为对所有阵营敌对
            var ma = a.GetComponentInParent<FactionMarker>();
            var mb = b.GetComponentInParent<FactionMarker>();
            bool aBandit = ma == null && a.GetComponent("AiEntityMarker") != null && IsHostileKind(a);
            bool bBandit = mb == null && b.GetComponent("AiEntityMarker") != null && IsHostileKind(b);
            return aBandit || bBandit;
        }

        private static bool IsHostileKind(GameObject go)
        {
            try
            {
                var m = go.GetComponent("AiEntityMarker");
                if (m == null) return false;
                var f = m.GetType().GetField("Kind");
                if (f != null)
                {
                    var v = f.GetValue(m);
                    return v != null && v.ToString() == "bandit";
                }
            }
            catch { }
            return false;
        }

        /// <summary>是否玩家友军（玩家自己 / 玩家阵营 AI）。</summary>
        public static bool IsFriendlyToPlayer(GameObject go)
        {
            if (go == null) return false;
            // 玩家飞机本体
            if (_sys != null && _sys.PlayerPlane != null)
            {
                if (go == _sys.PlayerPlane.gameObject || go.transform.IsChildOf(_sys.PlayerPlane.transform)) return true;
            }
            int f = FactionOf(go);
            if (f >= 0) return f == PlayerFaction;
            return false;
        }

        /// <summary>登记击杀（计分板用）。victim 为空时视为玩家阵亡。</summary>
        public static void OnKill(int killerFaction, int victimFaction)
        {
            if (_sys == null) return;
            _sys.RegisterKill(killerFaction, victimFaction);
        }

        /// <summary>取全部带阵营标记的 AI 实体。</summary>
        public static List<GameObject> GetAllAi()
        {
            var list = new List<GameObject>();
            if (_sys == null) return list;
            var marks = UnityEngine.Object.FindObjectsOfType<FactionMarker>(true);
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i] == null) continue;
                var go = marks[i].gameObject;
                // 只统计 AI（玩家机也可能带标记，排除）
                if (go.GetComponent("AiEntityMarker") != null) list.Add(go);
            }
            return list;
        }

        public static FactionData GetFaction(int id)
        {
            if (_sys == null || id < 0 || id >= _sys.Factions.Count) return null;
            return _sys.Factions[id];
        }

        // =================================================================
        // 对战积分模式（阵营战）对外 API —— 供 MachineAAM 等 Mod 反射调用
        // =================================================================
        /// <summary>积分模式是否启用（阵营系统未加载 / 模式关闭时 false）。</summary>
        public static bool PointsMode { get { return _sys != null && _sys.PointsEnabled; } }

        /// <summary>某阵营当前积分；阵营无效时返回 -1。</summary>
        public static float PointsOf(int factionId)
        {
            if (_sys == null || factionId < 0 || factionId >= _sys.Factions.Count) return -1f;
            return _sys.Factions[factionId].Points;
        }

        /// <summary>
        /// 登记一架阵营飞机（把整机造价记到账上，战损时按这个数扣分）。
        /// 由 FactionSystem 自己在生成 AI / 发现玩家机时调用；外部 Mod 一般不需要调。
        /// </summary>
        public static void RegisterAircraft(GameObject root, int factionId, string model)
        {
            if (_sys != null) _sys.RegisterAircraft(root, factionId, model);
        }

        /// <summary>
        /// 上报"这架阵营飞机被摧毁"（坠毁或被击落）→ 从其所属阵营扣除整机造价积分。
        /// root = 飞机根物体（PlaneContainer/PlaneController 所在的那个 GameObject）。
        /// killerFaction 仅用于日志（积分只看受害方）；-1 = 无归属（坠毁）。
        /// 重复调用同一架飞机只会结算一次（内部按实例 ID 去重）。
        /// </summary>
        public static void OnAircraftLost(GameObject root, int killerFaction, string reason)
        {
            if (_sys != null) _sys.ReportAircraftLost(root, killerFaction, reason);
        }

        /// <summary>按原版口径算一架飞机的整机造价（BuildingPart.price 求和，减去作战部件）。</summary>
        public static float AircraftWorth(GameObject root)
        {
            return FactionSystem.ComputeWorth(root);
        }
    }

    /// <summary>挂在阵营实体（AI / 玩家机）上，标记阵营归属。</summary>
    public class FactionMarker : MonoBehaviour
    {
        public int FactionId;
    }

    // =====================================================================
    // 阵营系统本体
    // =====================================================================
    public class FactionSystem : MonoBehaviour
    {
        public static FactionSystem Live;
        public IMachineApi Api;

        public List<FactionData> Factions = new List<FactionData>();
        public int PlayerFactionId;
        public PlaneContainer PlayerPlane;
        private int _playerPlaneId;                // PlayerPlane 的 instanceID（换机/复位时重新解析）
        private float _planeScanT;

        // ---- 对战积分模式（阵营战） ----
        // 规则：各方开局 800000 积分；己方飞机坠毁/被击落 → 按整机造价（作战部件不计价）扣分；
        //       扣到低于 0 即该阵营落败；只剩一方还有积分时回合结束，全场积分重置为基准值。
        public bool PointsEnabled = true;          // 模式：积分战（原阵营战）
        public bool TerritoryEnabled = false;      // 模式：阵地战（抢机场占点）
        public bool PointsDebug = false;           // 登记/结算的房价明细日志
        private int _round = 1;                    // 当前回合序号
        private string _roundWinner = "";          // 最近一次胜者（显示横幅用）
        private float _roundOverT = -99f;          // 回合结束时间（横幅显示 8s）
        private int _pointsEventSeq;

        /// <summary>登记在册的"阵营飞机"（用来算造价 + 监视是否被摧毁）。</summary>
        private class AircraftRec
        {
            public Transform Tr;
            public PlaneController Pc;
            public int Faction;
            public float Worth;         // 最近一次实测造价（会随存活期间刷新，见 RefreshWorth）
            public float WorthT;        // 上次刷新造价的时刻（节流）
            public string Model = "";
            public float BornT;
            public bool Reported;      // 已结算过战损（防重复扣分）
        }
        private readonly Dictionary<int, AircraftRec> _aircraft = new Dictionary<int, AircraftRec>();
        private readonly List<int> _aircraftGone = new List<int>();
        private float _aircraftWatchT;

        // ---- Tab 面板 / 计分板 ----
        private bool _panelOpen;
        private float _panelOpenTime;
        private bool _scoreOpen = false;   // 开局默认不显示，按 Alt 开启
        private int _shotIdx;              // 自测截图序号
        private bool _scoreDrawErrLogged;
        private static bool _forceScore;   // 自测强制显示计分板
        private static int _altToggles;
        private float _lastAltToggle = -9f;
        private Rect _panelRect = new Rect(360f, 160f, 460f, 420f);
        private Rect _scoreRect = new Rect(600f, 96f, 400f, 400f);

        // ---- AI 生成 ----
        private float _aiSpawnT;
        private int _aiSeq;
        private float _retryT = 1f;
        private bool _probeLoggedFail;

        // ---- 地图友军 ----
        private object _map;
        private GameObject _friendlyLayer;
        private Texture2D _friendlyTex;
        private const int TexSize = 512;
        private float _mapT;
        private Image _friendlyImage;
        private bool _mapUiBuilt;
        private string _lastFactionProbe = "";

        // ---- 机场归属（阵营大陆） ----
        // 全部大陆都要记下来（不只前 3 个阵营的）：中立大陆（Seaport / Hermit / Prison）也得能认出来，
        // 否则它的机场会掉进"离谁近就算谁的"兜底里被错判成某阵营的友军机场。
        private readonly List<object> _continents = new List<object>();
        private readonly List<string> _continentTypeNames = new List<string>();

        // ---- 默认机型池（26 个 workshop 航模，绝对路径） ----
        private static readonly string[] _allDesigns =
        {
            @"D:\steam\steamapps\workshop\content\2660460\3761511521",  // F-14 tomcat
            @"D:\steam\steamapps\workshop\content\2660460\3795286269",  // C-17
            @"D:\steam\steamapps\workshop\content\2660460\3795431933",  // TU-160
            @"D:\steam\steamapps\workshop\content\2660460\3795912098",  // J-20
            @"D:\steam\steamapps\workshop\content\2660460\3795919950",  // C-12 PLUS
            @"D:\steam\steamapps\workshop\content\2660460\3796004703",  // B-2
            @"D:\steam\steamapps\workshop\content\2660460\3796093572",  // FA-18
            @"D:\steam\steamapps\workshop\content\2660460\3796098870",  // F-22
            @"D:\steam\steamapps\workshop\content\2660460\3796496145",  // F-35
            @"D:\steam\steamapps\workshop\content\2660460\3796547300",  // SU-33
            @"D:\steam\steamapps\workshop\content\2660460\3796583282",  // J-50
            @"D:\steam\steamapps\workshop\content\2660460\3796607146",  // J-15
            @"D:\steam\steamapps\workshop\content\2660460\3796637384",  // J-16
            @"D:\steam\steamapps\workshop\content\2660460\3796731846",  // SU-30
            @"D:\steam\steamapps\workshop\content\2660460\3796843783",  // F-14
            @"D:\steam\steamapps\workshop\content\2660460\3797226945",  // SU-27
            @"D:\steam\steamapps\workshop\content\2660460\3797268688",  // J-36
            @"D:\steam\steamapps\workshop\content\2660460\3797312006",  // SR-71
            @"D:\steam\steamapps\workshop\content\2660460\3797395773",  // F-16
            @"D:\steam\steamapps\workshop\content\2660460\3797847964",  // F-4F
            @"D:\steam\steamapps\workshop\content\2660460\3797890987",  // MIG-29
            @"D:\steam\steamapps\workshop\content\2660460\3797922303",  // GONGJI-11
            @"D:\steam\steamapps\workshop\content\2660460\3798381468",  // MQ-9
            @"D:\steam\steamapps\workshop\content\2660460\3798419990",  // WL-10
            @"D:\steam\steamapps\workshop\content\2660460\3798428144",  // PL-15
            @"D:\steam\steamapps\workshop\content\2660460\3798442585"   // J-16 COMBAT
        };

        private string DesignFileFor(string dir)
        {
            try
            {
                if (System.IO.Directory.Exists(dir))
                {
                    var files = System.IO.Directory.GetFiles(dir, "*.planedesign");
                    if (files.Length > 0) return files[0];
                }
            }
            catch { }
            return null;
        }

        public void Init(IMachineApi api)
        {
            Api = api;
            Live = this;
            FactionApi.Bind(this);
            _retryT = 1f;
            ProbeContinents();
            if (Factions.Count > 0) DistributeDesigns();
            _aiSpawnT = 6f;
            Api.Log("FactionSystem ready | factions=" + Factions.Count
                    + " player=" + (PlayerFactionId < Factions.Count ? Factions[PlayerFactionId].Name : "?"));
        }

        // =================================================================
        // 阵营初始化（三大主岛）
        // =================================================================
        private void ProbeContinents()
        {
            try
            {
                Factions.Clear();
                var cm = FindSingleton("ContinentManager");
                if (cm == null)
                {
                    if (!_probeLoggedFail) { _probeLoggedFail = true; Api.Log("Faction: ContinentManager not found (will retry silently)"); }
                    return;
                }                var fContinents = typeof(ContinentManager).GetField("continents",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fContinents == null) { Api.Log("Faction: continents field missing"); return; }
                var list = fContinents.GetValue(cm) as System.Collections.IList;
                if (list == null || list.Count == 0) { Api.Log("Faction: continents empty"); return; }

                // 先把全部大陆（含中立的 Seaport / Hermit / Prison）登记下来，机场归属判定要用
                _continents.Clear();
                _continentTypeNames.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    _continents.Add(list[i]);
                    _continentTypeNames.Add(ContinentTypeNameOf(list[i]));
                }

                int maxFactions = Mathf.Min(3, list.Count);
                for (int i = 0; i < maxFactions; i++)
                {
                    var cd = list[i];
                    if (cd == null) continue;
                    var fd = new FactionData { ContinentIndex = i };
                    fd.Continent = cd;
                    fd.ContinentTypeName = ContinentTypeNameOf(cd);
                    // 大陆名
                    try
                    {
                        var fCt = cd.GetType().GetField("continentType");
                        if (fCt != null && fCt.GetValue(cd) != null)
                        {
                            var ct = fCt.GetValue(cd);
                            var fName = ct.GetType().GetField("continentName");
                            if (fName != null && fName.GetValue(ct) != null)
                                fd.Name = fName.GetValue(ct).ToString();
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(fd.Name)) fd.Name = "Island " + (i + 1);

                    // 主岛基地
                    try
                    {
                        var fBase = cd.GetType().GetField("baseAirport");
                        if (fBase != null)
                        {
                            var ap = fBase.GetValue(cd);
                            if (ap != null)
                            {
                                fd.BaseAirport = ap as Airport;
                                var pProp = ap.GetType().GetProperty("position");
                                if (pProp != null)
                                {
                                    object pv = pProp.GetValue(ap, null);
                                    if (pv != null) fd.HomePos = (Vector3)pv;   // (0,0,0) 也是合法坐标（Spawn 主岛基地），不再误判回退
                                }
                            }
                        }
                    }
                    catch { }
                    // origin 兜底：仅当机场根本没读到时才用大陆原点
                    if (fd.BaseAirport == null)
                    {
                        try
                        {
                            var fO = cd.GetType().GetField("origin");
                            if (fO != null) fd.HomePos = (Vector3)fO.GetValue(cd);
                        }
                        catch { }
                    }
                    Factions.Add(fd);
                    Api.Log("Faction: " + fd.Name + " home=" + fd.HomePos.ToString("F0"));
                }
                if (Factions.Count == 0)
                {
                    // 兜底：无法读取时按机场数量最多的三个机场群设为主岛基地（近似）
                    FallbackFactions();
                }
            }
            catch (Exception e) { Api.Log("Faction: probe failed " + e.Message); }
        }

        private void FallbackFactions()
        {
            try
            {
                var am = (AirportManager)UnityEngine.Object.FindFirstObjectByType(typeof(AirportManager));
                if (am == null || am.airports == null) return;
                List<Airport> bases = new List<Airport>();
                foreach (var ap in am.airports)
                {
                    if (ap == null) continue;
                    try { if (ap.IsBaseAirport) bases.Add(ap); } catch { }
                }
                int n = Mathf.Min(3, bases.Count);
                for (int i = 0; i < n; i++)
                {
                    var fd = new FactionData { Name = "Faction " + (i + 1), BaseAirport = bases[i], HomePos = bases[i].position };
                    Factions.Add(fd);
                }
            }
            catch { }
        }

        private static object FindSingleton(string typeName)
        {
            try
            {
                var t = Type.GetType(typeName + ", Assembly-CSharp");
                if (t == null) return null;
                // 静态属性/字段不继承：泛型基类 Singleton<T> 的 Instance/m_Instance 在 BaseType 上
                var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p == null && t.BaseType != null)
                    p = t.BaseType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) return p.GetValue(null, null);
                var f = t.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null && t.BaseType != null)
                    f = t.BaseType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
                if (t.BaseType != null)
                {
                    var fm = t.BaseType.GetField("m_Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (fm != null) return fm.GetValue(null);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 大陆类型资产名（ContinentType.name，如 DefaultContinentType / MountainContinent）。
        /// `Airport.id` 就是 "机场预制体名 + 大陆类型资产名"，所以这个后缀本身就能当归属判据。
        /// </summary>
        private static string ContinentTypeNameOf(object cd)
        {
            try
            {
                if (cd == null) return "";
                var fCt = cd.GetType().GetField("continentType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var ct = fCt != null ? fCt.GetValue(cd) : null;
                if (ct == null) return "";
                var nP = ct.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
                if (nP != null) { var v = nP.GetValue(ct, null); if (v != null) return v.ToString(); }
                var nF = ct.GetType().GetField("name", BindingFlags.Instance | BindingFlags.Public);
                if (nF != null) { var v2 = nF.GetValue(ct); if (v2 != null) return v2.ToString(); }
            }
            catch { }
            return "";
        }

        // =================================================================
        // 机场归属（阵营大陆）
        // =================================================================
        // 规则：机场落在哪个主岛大陆上就归哪个阵营 —— 一个阵营的"友军机场"不只是它的 Base，
        // 还包括同大陆上全部普通机场（雷达上只有一个蓝点看着很奇怪）。
        // 中立大陆（Seaport / Hermit / Prison）的机场不属于任何阵营 → -1。

        /// <summary>机场所属大陆在 ContinentManager.continents 里的下标；-1 = 判不出来。</summary>
        public int AirportContinentIndex(Airport ap)
        {
            if (ap == null || _continents.Count == 0) return -1;
            Vector3 pos;
            try { pos = ap.position; } catch { return -1; }

            // ① 大陆运行时的机场清单（ContinentData.airports）——最权威，直接看它在谁的名单里
            try
            {
                for (int i = 0; i < _continents.Count; i++)
                    if (ContinentHasAirport(_continents[i], ap)) return i;
            }
            catch { }

            // ② 凸包判定：机场坐标落在哪个大陆的范围内
            int hull = ContinentIndexAt(pos);
            if (hull >= 0) return hull;

            // ③ Airport.id 后缀 = 大陆类型资产名（如 DesertBaseDesertContinentType）
            try
            {
                string aid = ap.id;
                if (!string.IsNullOrEmpty(aid))
                    for (int i = 0; i < _continentTypeNames.Count; i++)
                    {
                        string tn = _continentTypeNames[i];
                        if (!string.IsNullOrEmpty(tn) && aid.EndsWith(tn)) return i;
                    }
            }
            catch { }

            return -1;
        }

        /// <summary>机场归属阵营 id；-1 = 中立（无阵营大陆）或判不出来。</summary>
        public int AirportFactionOf(Airport ap)
        {
            return FactionOfContinent(AirportContinentIndex(ap));
        }

        /// <summary>按坐标判定的机场归属阵营 id（拿不到 Airport 对象时的兜底）。</summary>
        public int AirportFactionAt(Vector3 pos)
        {
            // 阵地战：坐标落在某座阵地的空域附近 → 直接返回当前占领方
            if (TerritoryEnabled && Territories.Count > 0)
            {
                float bestD = float.MaxValue;
                TerritoryState best = null;
                for (int i = 0; i < Territories.Count; i++)
                {
                    var st = Territories[i];
                    if (st == null) continue;
                    float d = (st.Pos - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = st; }
                }
                float reach = TerrAirRadius + 200f;
                if (best != null && bestD <= reach * reach)
                    return best.Owner >= 0 ? best.Owner : best.NativeOwner;
            }
            if (_continents.Count == 0) return -1;
            int ci = ContinentIndexAt(pos);
            if (ci >= 0) return FactionOfContinent(ci);
            // 凸包没判出来：找坐标上的那座机场，复用它的归属
            try
            {
                var am = AirportManager.Instance;
                if (am != null && am.airports != null)
                    for (int k = 0; k < am.airports.Count; k++)
                    {
                        var a = am.airports[k];
                        if (a == null) continue;
                        if (Vector3.Distance(a.position, pos) < 1f) return EffectiveAirportOwner(a);
                    }
            }
            catch { }
            return -1;
        }

        /// <summary>坐标所在的大陆下标（ContinentManager.GetCurrentContinent 凸包判定）；-1 = 不在任何大陆上/没读到。</summary>
        private int ContinentIndexAt(Vector3 pos)
        {
            try
            {
                var cm = FindSingleton("ContinentManager");
                if (cm == null) return -1;
                var m = cm.GetType().GetMethod("GetCurrentContinent",
                    BindingFlags.Instance | BindingFlags.Public);
                if (m == null) return -1;
                var cd = m.Invoke(cm, new object[] { pos });
                if (cd == null) return -1;
                for (int i = 0; i < _continents.Count; i++)
                    if (object.ReferenceEquals(_continents[i], cd)) return i;
            }
            catch { }
            return -1;
        }

        /// <summary>大陆下标 → 阵营 id（只有前 3 个大陆有阵营，其余中立）。</summary>
        private int FactionOfContinent(int ci)
        {
            if (ci < 0) return -1;
            for (int i = 0; i < Factions.Count; i++)
                if (Factions[i] != null && Factions[i].ContinentIndex == ci) return i;
            return -1;
        }

        // =================================================================
        // 阵地战（抢机场）：地图上每座机场/基地都是一座"阵地"
        // =================================================================
        // 规则（用户钦定）：
        //  · 每座阵地占领值上限 150，值属于"当前占领方"。
        //  · 敌方飞机进入空域 → 按 8/s 消耗当前占领方的占领值，归零即易手（该方变成"后占领方"）。
        //  · 占领 10/s：无主之地到场即积累；后占领方在**己方飞机在场**时按这个速度把自己的占领值涨起来
        //    （必须这样，否则"归零易手后再涨己方占领值"这一步永远走不完）。
        //  · 恢复 2/s：原始占领方（按大陆归属）无人来抢时自然恢复；空域内有己方飞机则 4/s。
        //    后占领方**没有**自然恢复（己方飞机不走就一直冻着）。
        //  · 每回合 10 分钟（可设），时间到 → **占有机场数**多的阵营胜。
        public const float TerrMaxValue = 150f;         // 每座阵地占领值上限
        public const float TerrCaptureRate = 10f;       // 占领速度 /s
        public const float TerrConsumeRate = 8f;        // 消耗速度 /s
        public const float TerrRecoverRate = 2f;        // 恢复速度 /s（原始占领方，空域内无己方飞机）
        public const float TerrRecoverRateFriend = 4f;  // 恢复速度 /s（原始占领方 + 空域内有己方飞机）

        public float PointsBase = FactionData.DefaultBasePoints;  // 积分战开局积分（面板可调）
        public float TerrRoundSeconds = 600f;                     // 阵地战每局时长（秒，面板可调）
        public float TerrAirRadius = 600f;                        // 占点空域半径（米，面板可调）
        public float TerrAirCeiling = 700f;                       // 空域高度上限（米，机场上方圆柱）

        /// <summary>一座阵地（机场/基地）的占领状态。</summary>
        public class TerritoryState
        {
            public Airport Ap;
            public string Id = "";
            public string Label = "";
            public Vector3 Pos;
            public int NativeOwner = -1;    // 原始占领方（按大陆归属）；-1 = 中立大陆（Seaport/Hermit/Prison）
            public int Owner = -1;          // 当前占领方；-1 = 无人占领
            public float Value;             // 当前占领方的占领值 0..TerrMaxValue
            public float FlipT = -99f;      // 最近一次易手时刻
        }

        public readonly List<TerritoryState> Territories = new List<TerritoryState>();
        private object _terrAm;             // 建表时的 AirportManager 实例（切场景要重建）
        private float _terrTickT;
        private float _terrLastT = -1f;
        private bool _terrReady;
        private bool _terrRoundStarted;     // 本局倒计时是否已初始化（防"没开过局就判负"）
        private readonly List<TerrCraft> _terrCraft = new List<TerrCraft>();
        private int[] _terrCount = new int[0];
        public float TerrTimeLeft;          // 本回合剩余秒数
        public int TerrFlips;               // 本回合累计易手次数（日志/自测用）

        private struct TerrCraft { public Vector3 Pos; public int Faction; }

        /// <summary>当前模式：0 = 积分战，1 = 阵地战（对外只读）。</summary>
        public int GameMode { get { return TerritoryEnabled ? 1 : 0; } }

        /// <summary>切模式（两个 bool 互斥，避免"两个模式同时生效"的怪状态）。</summary>
        public void SwitchMode(bool territory, string why)
        {
            if (territory == TerritoryEnabled) return;
            TerritoryEnabled = territory;
            PointsEnabled = !territory;
            _roundWinner = "";
            _roundOverT = -99f;
            if (territory)
            {
                BuildTerritoryList();
                BeginTerritoryRound(why);
            }
            else ResetPointsNow();
            Api.Log("Faction: mode -> " + (territory ? "TERRITORY" : "POINTS") + " (" + why + ")");
            PushPointsEvent(territory ? "阵地战 mode" : "积分战 mode");
        }

        /// <summary>积分战：开局积分（面板可调）。改完立刻按新基准重开一局。</summary>
        public void SetPointsBase(float v)
        {
            PointsBase = Mathf.Clamp(Mathf.Round(v / 50000f) * 50000f, 50000f, 20000000f);
            ResetPointsNow();
            Api.Log("Faction/points: base -> " + PointsBase.ToString("F0"));
            PushPointsEvent("开局积分 = " + Mathf.RoundToInt(PointsBase).ToString("N0"));
        }

        /// <summary>积分战：全场积分回到基准值（不动回合号）。</summary>
        public void ResetPointsNow()
        {
            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                if (fd == null) continue;
                fd.BasePoints = PointsBase;
                fd.Points = PointsBase;
                fd.Eliminated = false;
                fd.AircraftLost = 0;
                fd.LostValue = 0f;
            }
        }

        /// <summary>阵地战：每局时长（分钟，面板可调）。改完立刻按新时长重开一局。</summary>
        public void SetTerrMinutes(int minutes)
        {
            TerrRoundSeconds = Mathf.Clamp(minutes, 1, 60) * 60f;
            BeginTerritoryRound("duration changed");
            PushPointsEvent("每局 " + Mathf.RoundToInt(TerrRoundSeconds / 60f) + " 分钟");
        }

        /// <summary>阵地战：空域半径（米，面板可调）。</summary>
        public void SetTerrRadius(float meters)
        {
            TerrAirRadius = Mathf.Clamp(meters, 200f, 3000f);
            PushPointsEvent("空域半径 " + Mathf.RoundToInt(TerrAirRadius) + " m");
        }

        /// <summary>建表：把 AirportManager.airports 全部登记成阵地（换场景/机场表变了就重建）。</summary>
        private void BuildTerritoryList()
        {
            try
            {
                var am = AirportManager.Instance;
                if (am == null || am.airports == null || am.airports.Count == 0) return;
                if (_terrReady && ReferenceEquals(am, _terrAm)
                    && Territories.Count == am.airports.Count) return;

                _terrAm = am;
                Territories.Clear();
                for (int i = 0; i < am.airports.Count; i++)
                {
                    var ap = am.airports[i];
                    if (ap == null) continue;
                    var st = new TerritoryState();
                    st.Ap = ap;
                    try { st.Id = string.IsNullOrEmpty(ap.id) ? "" : ap.id; } catch { }
                    try { st.Pos = ap.position; } catch { }
                    string nm = "";
                    try { nm = ap.airportName; } catch { }
                    st.Label = string.IsNullOrEmpty(nm) ? ("Airport#" + i) : nm;
                    if (st.Label.Length > 20) st.Label = st.Label.Substring(0, 20);
                    st.NativeOwner = AirportFactionOf(ap);
                    st.Owner = st.NativeOwner;
                    st.Value = st.NativeOwner >= 0 ? TerrMaxValue : 0f;
                    Territories.Add(st);
                }
                _terrReady = Territories.Count > 0;
                int nat = 0, neu = 0;
                for (int i = 0; i < Territories.Count; i++)
                {
                    if (Territories[i].NativeOwner >= 0) nat++; else neu++;
                }
                Api.Log("Faction/terr: built " + Territories.Count + " strongpoints (native=" + nat + " neutral=" + neu + ")");
            }
            catch (Exception e) { Api.Log("Faction/terr: build failed " + e.Message); }
        }

        /// <summary>阵地全部回到原始归属（原始占领方满值 150，中立大陆 0 且无人占领）。</summary>
        public void ResetTerritories()
        {
            for (int i = 0; i < Territories.Count; i++)
            {
                var st = Territories[i];
                if (st == null) continue;
                st.Owner = st.NativeOwner;
                st.Value = st.NativeOwner >= 0 ? TerrMaxValue : 0f;
                st.FlipT = -99f;
            }
            TerrFlips = 0;
        }

        /// <summary>开一局阵地战：重置阵地 + 倒计时归位。</summary>
        public void BeginTerritoryRound(string why)
        {
            BuildTerritoryList();
            ResetTerritories();
            TerrTimeLeft = Mathf.Max(30f, TerrRoundSeconds);
            _terrRoundStarted = true;
            int[] own = OwnedCounts();
            Api.Log("Faction/terr: round " + _round + " start (" + why + ") len="
                    + Mathf.RoundToInt(TerrTimeLeft) + "s radius=" + Mathf.RoundToInt(TerrAirRadius)
                    + " owned=" + OwnedSig(own));
            PushPointsEvent("阵地战 round " + _round + " - " + Mathf.RoundToInt(TerrTimeLeft / 60f) + " min");
        }

        /// <summary>各阵营当前占有的阵地数（下标 = 阵营 id）。</summary>
        public int[] OwnedCounts()
        {
            int[] n = new int[Factions.Count];
            for (int i = 0; i < Territories.Count; i++)
            {
                var st = Territories[i];
                if (st == null || st.Owner < 0) continue;
                if (st.Owner < n.Length) n[st.Owner]++;
            }
            return n;
        }

        public int CountOwned(int factionId)
        {
            if (factionId < 0) return 0;
            int n = 0;
            for (int i = 0; i < Territories.Count; i++)
                if (Territories[i] != null && Territories[i].Owner == factionId) n++;
            return n;
        }

        private string OwnedSig(int[] own)
        {
            string s = "";
            for (int i = 0; i < own.Length; i++) s += (i > 0 ? "," : "") + FactionName(i) + ":" + own[i];
            int un = 0;
            for (int i = 0; i < Territories.Count; i++)
                if (Territories[i] != null && Territories[i].Owner < 0) un++;
            return s + ",none:" + un + "/" + Territories.Count;
        }

        /// <summary>按 Airport 对象找阵地（先按引用，再按 id 兜底）。</summary>
        private TerritoryState FindTerritory(Airport ap)
        {
            if (ap == null || Territories.Count == 0) return null;
            for (int i = 0; i < Territories.Count; i++)
                if (Territories[i] != null && ReferenceEquals(Territories[i].Ap, ap)) return Territories[i];
            string id = null;
            try { id = ap.id; } catch { }
            if (!string.IsNullOrEmpty(id))
                for (int i = 0; i < Territories.Count; i++)
                    if (Territories[i] != null && Territories[i].Id == id) return Territories[i];
            return null;
        }

        /// <summary>机场的"实际归属"：阵地战看当前占领方（无人占领时退回原始归属），积分战 = 大陆归属。</summary>
        public int EffectiveAirportOwner(Airport ap)
        {
            if (!TerritoryEnabled) return AirportFactionOf(ap);
            var st = FindTerritory(ap);
            if (st == null) return AirportFactionOf(ap);
            return st.Owner >= 0 ? st.Owner : st.NativeOwner;
        }

        /// <summary>
        /// 单座阵地推进一拍（**纯逻辑**，不碰 Unity 世界 → 自测可以直接驱动）。
        /// attacker = 空域内数量最多的"非当前占领方"阵营（-1 = 空域里没有敌人）；
        /// holderPresent = 当前占领方在空域内有飞机。返回 true 表示这一拍发生了易手。
        ///
        /// 四档速率（全部来自用户规格）：
        ///   · 占领 10/s：**无主之地**被到场方直接积累；以及**后占领方**在己方飞机在场时把自己的占领值涨起来
        ///     （否则"先消耗敌方占领值→归零→再涨己方的占领值"永远走不完：涨完那一刻自己就是 owner，
        ///      再没人把它当 attacker，数值会永远冻在 0）
        ///   · 消耗 8/s：敌方飞机在场 → 磨当前占领方的占领值，归零即易手
        ///   · 恢复 2/s：原始占领方，无人来抢（空域里一架飞机都没有）
        ///   · 恢复 4/s：原始占领方，空域里有己方飞机
        /// 后占领方**没有自然恢复**（没有己方飞机就是 0）。
        /// </summary>
        public static bool TerritoryStep(TerritoryState st, int attacker, bool holderPresent, float dt)
        {
            if (st == null || dt <= 0f) return false;
            if (attacker >= 0)
            {
                if (st.Owner >= 0 && st.Owner != attacker)
                {
                    // 有主之地：先按消耗速度磨掉当前占领方的占领值
                    st.Value -= TerrConsumeRate * dt;
                    if (st.Value <= 0f)
                    {
                        st.Value = 0f;
                        st.Owner = attacker;
                        st.FlipT = Time.time;
                        return true;
                    }
                }
                else
                {
                    // 无主之地：到场即开始积累自己的占领值
                    st.Owner = attacker;
                    st.Value = Mathf.Min(TerrMaxValue, st.Value + TerrCaptureRate * dt);
                }
            }
            else if (st.Owner >= 0 && st.Owner == st.NativeOwner)
            {
                // 原始占领方：无人来抢就自然恢复（空域内有己方飞机则翻倍）
                st.Value = Mathf.Min(TerrMaxValue,
                    st.Value + (holderPresent ? TerrRecoverRateFriend : TerrRecoverRate) * dt);
            }
            else if (st.Owner >= 0 && holderPresent)
            {
                // 后占领方：没有自然恢复，只有己方飞机在场才能按占领速度把自己的占领值涨起来
                st.Value = Mathf.Min(TerrMaxValue, st.Value + TerrCaptureRate * dt);
            }
            return false;
        }

        /// <summary>阵地战主循环：0.5s 一拍，扫描空域内飞机 → 推进每座阵地 → 回合倒计时。</summary>
        private void StepTerritory()
        {
            if (!TerritoryEnabled) return;
            try
            {
                if (!Machine.Mod.MachineState.InFlight()) { _terrLastT = -1f; return; }
                var am = AirportManager.Instance;
                if (am == null) return;
                if (!_terrReady || !ReferenceEquals(am, _terrAm)) BuildTerritoryList();
                if (Territories.Count == 0) return;

                _terrTickT -= Time.deltaTime;
                if (_terrTickT > 0f) return;
                // ⚠ 按"真实经过时间"推进，别用 Time.deltaTime（本块被节流成每 0.5s 才跑一次，
                //    用帧间隔会把速度稀释几十倍 —— Radar 那个 30s 才刷新的老坑就是这么来的）
                float dt = (_terrLastT < 0f) ? 0.5f : Mathf.Clamp(Time.time - _terrLastT, 0.01f, 1.5f);
                _terrLastT = Time.time;
                _terrTickT = 0.5f;

                // 没开过局（外部把 TerritoryEnabled 直接打开的场合）→ 先正常开一局，
                // 否则 TerrTimeLeft 是 0，第一拍就判"回合结束"（自测里踩过这个坑）
                if (!_terrRoundStarted) { BeginTerritoryRound("auto init"); return; }

                TerrTimeLeft -= dt;
                if (TerrTimeLeft <= 0f) { EvaluateTerritoryRound(); return; }

                // 一次扫描收集所有"带阵营标记"的飞机（AI + 玩家机），供全部阵地共用
                _terrCraft.Clear();
                var marks = UnityEngine.Object.FindObjectsOfType<FactionMarker>(true);
                for (int i = 0; i < marks.Length; i++)
                {
                    var m = marks[i];
                    if (m == null) continue;
                    if (m.FactionId < 0 || m.FactionId >= Factions.Count) continue;
                    var cr = new TerrCraft();
                    try { cr.Pos = m.transform.position; } catch { continue; }
                    cr.Faction = m.FactionId;
                    _terrCraft.Add(cr);
                }
                if (_terrCount.Length < Factions.Count) _terrCount = new int[Factions.Count];

                float r2 = TerrAirRadius * TerrAirRadius;
                for (int i = 0; i < Territories.Count; i++)
                {
                    var st = Territories[i];
                    if (st == null) continue;
                    for (int f = 0; f < _terrCount.Length; f++) _terrCount[f] = 0;
                    for (int c = 0; c < _terrCraft.Count; c++)
                    {
                        var cr = _terrCraft[c];
                        float dx = cr.Pos.x - st.Pos.x, dz = cr.Pos.z - st.Pos.z, dy = cr.Pos.y - st.Pos.y;
                        if (dx * dx + dz * dz > r2) continue;
                        if (dy < -80f || dy > TerrAirCeiling) continue;   // 空域 = 机场上方的圆柱
                        _terrCount[cr.Faction]++;
                    }
                    int attacker = -1, best = 0;
                    for (int f = 0; f < Factions.Count; f++)
                    {
                        if (_terrCount[f] <= 0 || f == st.Owner) continue;
                        if (_terrCount[f] > best) { best = _terrCount[f]; attacker = f; }
                    }
                    bool holderPresent = st.Owner >= 0 && _terrCount[st.Owner] > 0;
                    int prev = st.Owner;
                    bool flipped = TerritoryStep(st, attacker, holderPresent, dt);
                    if (flipped)
                    {
                        TerrFlips++;
                        Api.Log("Faction/terr: " + st.Label + " " + FactionName(prev) + " -> "
                                + FactionName(st.Owner) + " (in-airspace " + best + " craft)");
                        PushPointsEvent(FactionName(st.Owner) + " took " + st.Label, st.Owner);
                    }
                }
            }
            catch { }
        }

        /// <summary>阵地战回合结算：占有机场数多者胜（并列 = 平局），随后重置阵地开下一回合。</summary>
        private void EvaluateTerritoryRound()
        {
            try
            {
                int[] own = OwnedCounts();
                int best = -1, bestCount = 0, ties = 0;
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i] > bestCount) { bestCount = own[i]; best = i; ties = 1; }
                    else if (own[i] == bestCount) ties++;
                }
                string winner = (best >= 0 && ties == 1) ? Factions[best].Name : "";
                Api.Log("Faction/terr: ROUND " + _round + " OVER -> "
                        + (winner.Length > 0 ? winner + " WINS" : "DRAW") + " (" + bestCount + "/" + Territories.Count + ")"
                        + " owned=" + OwnedSig(own) + " flips=" + TerrFlips);
                _roundWinner = winner;
                _roundOverT = Time.time;
                _round++;
                PushPointsEvent("ROUND " + (_round - 1) + " OVER - " + (winner.Length > 0 ? winner + " WINS" : "DRAW"));
                BeginTerritoryRound("round over");
            }
            catch (Exception e) { Api.Log("Faction/terr: round eval failed " + e.Message); }
        }

        /// <summary>ContinentData.airports（List&lt;Airport&gt;）里有没有这座机场。</summary>
        private static bool ContinentHasAirport(object cd, Airport ap)
        {
            try
            {
                if (cd == null || ap == null) return false;
                var f = cd.GetType().GetField("airports",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return false;
                var list = f.GetValue(cd) as System.Collections.IList;
                if (list == null || list.Count == 0) return false;
                string aid = null;
                try { aid = ap.id; } catch { }
                Vector3 apPos = Vector3.zero;
                bool havePos = false;
                try { apPos = ap.position; havePos = true; } catch { }
                for (int k = 0; k < list.Count; k++)
                {
                    var o = list[k] as Airport;
                    if (o == null) continue;
                    if (object.ReferenceEquals(o, ap)) return true;
                    if (!string.IsNullOrEmpty(aid))
                    {
                        try { if (o.id == aid) return true; } catch { }
                    }
                    if (havePos)
                    {
                        try { if (Vector3.Distance(o.position, apPos) < 1f) return true; } catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private void DistributeDesigns()
        {
            for (int i = 0; i < Factions.Count; i++)
            {
                Factions[i].AiDesigns.Clear();
                // 均分 26 个机型（每人 ~8-9 个），保证覆盖全部
                for (int k = 0; k < _allDesigns.Length; k++)
                {
                    if (k % Factions.Count == i) Factions[i].AiDesigns.Add(_allDesigns[k]);
                }
            }
        }

        // =================================================================
        // 对战积分模式（阵营战）
        // =================================================================
        /// <summary>
        /// 作战部件（武器/弹药）关键词：命中即不计入飞机造价。
        /// 原版没有武器部件，这些都是 Mod 注册的；用户要求"飞机内部作战部件不计入价格"，
        /// 所以算造价时把带这些关键词的部件剔掉，让"飞机整体价格"= 机体 + 动力 + 航电。
        /// </summary>
        private static readonly string[] _combatKeywords =
        {
            "gun", "cannon", "missile", "rocket", "bomb", "ammo", "ammunition",
            "weapon", "turret", "launcher", "flare", "armament", "warhead"
        };

        public static bool IsCombatPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return false;
            string n = partName.ToLowerInvariant();
            for (int i = 0; i < _combatKeywords.Length; i++)
                if (n.Contains(_combatKeywords[i])) return true;
            return false;
        }

        /// <summary>
        /// 一架飞机的整机造价：与原版 `PlaneContainer.GetPlanePrice()` 同口径（BuildingPart.price 求和），
        /// 但剔掉作战部件（弹药/武器），并要求部件已放置（未放置的“幽灵”部件不计）。
        /// </summary>
        public static float ComputeWorth(GameObject root)
        {
            if (root == null) return 0f;
            float sum = 0f;
            try
            {
                var parts = root.GetComponentsInChildren<BuildingPart>(true);
                for (int i = 0; i < parts.Length; i++)
                {
                    var bp = parts[i];
                    if (bp == null) continue;
                    if (IsCombatPart(bp.partName)) continue;
                    sum += bp.price;
                }
            }
            catch { }
            return Mathf.Max(0f, sum);
        }

        /// <summary>能不能当"玩家机"：非空、未销毁、且不是 AI 机。</summary>
        private static bool IsUsablePlayerPlane(PlaneContainer pc)
        {
            if (pc == null) return false;                       // Unity 重载 ==：已销毁也算 null
            try
            {
                if (pc.GetComponent("AiEntityMarker") != null) return false;
            }
            catch { }
            return true;
        }

        /// <summary>原版口径整机价格（含作战部件），仅用于日志对照。</summary>
        private static float GamePlanePrice(GameObject root)
        {
            try
            {
                var pc = root.GetComponent<PlaneContainer>();
                if (pc == null) pc = root.GetComponentInParent<PlaneContainer>();
                if (pc == null) return -1f;
                return pc.GetPlanePrice();
            }
            catch { return -1f; }
        }

        /// <summary>
        /// 刷新一架在册飞机的造价快照（活着的期间反复调，节流 interval 秒）。
        /// ⚠ 必须在飞机**还完整**的时候刷新：玩家的机体会在原版"编辑 → 再起飞"里被复用/重建，
        /// 如果只在注册那一刻算一次，之后无论开多贵的飞机都按第一次那个数扣分
        /// （用户实测：连续 4 次死亡恒扣 -59011，贵的便宜的完全一样）。
        /// 已经在爆炸/散架时不刷新，否则会按残骸少算。
        /// </summary>
        private void RefreshWorth(AircraftRec rec, float interval)
        {
            if (rec == null || rec.Tr == null) return;
            if (interval > 0f && Time.time - rec.WorthT < interval) return;
            rec.WorthT = Time.time;
            try { if (rec.Pc != null && rec.Pc.Exploded) return; }
            catch { return; }
            float w;
            try { w = ComputeWorth(rec.Tr.gameObject); }
            catch { return; }
            // 重建/复位的那一两帧部件可能临时为空 → 别让 0 覆盖掉已知的有效值
            if (w <= 0f && rec.Worth > 0f) return;
            if (Mathf.Abs(w - rec.Worth) > 0.5f)
            {
                if (PointsDebug)
                    Api.Log("Faction/points: worth update " + rec.Model + " " + FactionName(rec.Faction)
                            + " " + rec.Worth.ToString("F0") + " -> " + w.ToString("F0"));
                rec.Worth = w;
            }
        }

        /// <summary>登记一架阵营飞机（生成 AI / 发现玩家机时调用）。同一架只登记一次。</summary>
        public void RegisterAircraft(GameObject root, int factionId, string model)
        {
            if (root == null) return;
            if (!PointsEnabled) return;
            int id = root.GetInstanceID();
            AircraftRec exist;
            if (_aircraft.TryGetValue(id, out exist))
            {
                // 玩家中途换阵营：账记在最新阵营上（已结算过的不改）
                if (!exist.Reported && exist.Faction != factionId) exist.Faction = factionId;
                // ⚠⭐ 同一架飞机被复用是很常见的（玩家在机场编辑机体后再起飞、坠毁后原机复位），
                //    所以每次看到它都要重算造价 —— 只在注册那一刻算一次会导致
                //    "开多贵的飞机都按第一次那个数扣分"。
                if (exist.Pc == null)
                {
                    try
                    {
                        exist.Pc = root.GetComponent<PlaneController>();
                        if (exist.Pc == null) exist.Pc = root.GetComponentInParent<PlaneController>();
                    }
                    catch { }
                }
                RefreshWorth(exist, 0.5f);
                return;
            }
            var rec = new AircraftRec();
            rec.Tr = root.transform;
            rec.Faction = factionId;
            rec.Model = string.IsNullOrEmpty(model) ? "Aircraft" : model;
            rec.BornT = Time.time;
            try
            {
                rec.Pc = root.GetComponent<PlaneController>();
                if (rec.Pc == null) rec.Pc = root.GetComponentInParent<PlaneController>();
            }
            catch { }
            rec.Worth = ComputeWorth(root);
            rec.WorthT = Time.time;
            _aircraft[id] = rec;
            if (PointsDebug)
                Api.Log("Faction/points: +aircraft " + rec.Model + " faction=" + FactionName(factionId)
                        + " worth=" + rec.Worth.ToString("F0")
                        + " (gamePrice=" + GamePlanePrice(root).ToString("F0") + ")");
        }

        /// <summary>上报一架阵营飞机被摧毁（坠毁 / 被击落）→ 扣其阵营积分。</summary>
        public void ReportAircraftLost(GameObject root, int killerFaction, string reason)
        {
            if (root == null) return;
            if (!PointsEnabled) return;
            int id;
            try { id = root.GetInstanceID(); } catch { return; }
            AircraftRec rec;
            if (_aircraft.TryGetValue(id, out rec)) { SettleLoss(rec, killerFaction, reason); return; }

            // ⚠ 传进来的可能只是机体的**某个部件**（机炮命中给的就是被击中的碰撞体 transform，
            //   无人机/导弹命中的也不一定是根物体）。所以先上溯到飞机根（PlaneController 所在物体），
            //   否则按部件算造价会**严重少扣**，而且登记表按根物体 ID 也对不上。
            Transform anchor = null;
            try
            {
                var pc = root.GetComponentInParent<PlaneController>();
                if (pc != null) anchor = pc.transform;
                else
                {
                    var pcon = root.GetComponentInParent<PlaneContainer>();
                    if (pcon != null) anchor = pcon.transform;
                }
            }
            catch { }
            if (anchor != null)
            {
                int aid;
                try { aid = anchor.GetInstanceID(); } catch { aid = 0; }
                if (aid != 0 && _aircraft.TryGetValue(aid, out rec)) { SettleLoss(rec, killerFaction, reason); return; }
            }

            // 再兜一层：登记表里找"以 root 为后代"的那架飞机
            foreach (var kv in _aircraft)
            {
                var r2 = kv.Value;
                if (r2 == null || r2.Tr == null || r2.Reported) continue;
                try
                {
                    if (root.transform == r2.Tr || root.transform.IsChildOf(r2.Tr))
                    { SettleLoss(r2, killerFaction, reason); return; }
                }
                catch { }
            }

            // 完全没登记过（例如 AAM 自测生成的 AI）：现算造价，按 FactionMarker 找阵营
            if (anchor == null) anchor = root.transform;
            int fid = FactionApi.FactionOf(anchor.gameObject);
            if (fid < 0) fid = FactionApi.FactionOf(root);
            if (fid < 0) return;
            var fallback = new AircraftRec();
            fallback.Tr = anchor;
            fallback.Faction = fid;
            fallback.Model = "Aircraft";
            fallback.BornT = Time.time;
            try { fallback.Pc = anchor.GetComponent<PlaneController>(); } catch { }
            fallback.Worth = ComputeWorth(anchor.gameObject);
            try { _aircraft[anchor.GetInstanceID()] = fallback; } catch { }
            SettleLoss(fallback, killerFaction, reason);
        }

        private void SettleLoss(AircraftRec rec, int killerFaction, string reason)
        {
            try
            {
                if (rec == null || rec.Reported) return;
                rec.Reported = true;
                int fid = rec.Faction;
                if (fid < 0 || fid >= Factions.Count) return;
                var fd = Factions[fid];
                // 结算前再用**还活着的机体**实测一次造价（AAM 上报击落通常发生在爆炸之前）；
                // 已经炸开/散架的用快照 —— 快照由 RefreshWorth 保证 ≤0.5s 新鲜。
                float worth = rec.Worth;
                try
                {
                    if (rec.Tr != null && (rec.Pc == null || !rec.Pc.Exploded))
                    {
                        float live = ComputeWorth(rec.Tr.gameObject);
                        if (live > 0f) worth = live;
                    }
                }
                catch { }
                rec.Worth = worth;

                fd.Points -= worth;
                fd.AircraftLost++;
                fd.LostValue += worth;

                string killer = (killerFaction >= 0 && killerFaction < Factions.Count && killerFaction != fid)
                    ? Factions[killerFaction].Name : "-";
                Api.Log("Faction/points: LOST " + rec.Model + " [" + reason + "] " + fd.Name
                        + " -" + worth.ToString("F0") + " => " + fd.Points.ToString("F0")
                        + " (killer=" + killer + ")");
                PushPointsEvent(fd.Name + " -" + Mathf.RoundToInt(worth) + "  (" + rec.Model + ")", fid);
                EvaluateRound();
            }
            catch (Exception e) { Api.Log("Faction/points: settle failed " + e.Message); }
        }

        private string FactionName(int id)
        {
            if (id >= 0 && id < Factions.Count) return Factions[id].Name;
            return "?";
        }

        /// <summary>阵营专属色（按大陆名判定）：Snow=蓝，Desert=橙红，其余（Spawn/森林）=森林绿。</summary>
        public static Color FactionColor(string name)
        {
            string n = name == null ? "" : name.ToLowerInvariant();
            if (n.Contains("snow") || n.Contains("ice") || n.Contains("arctic") || n.Contains("winter"))
                return new Color(0.20f, 0.52f, 0.92f, 1f);      // 雪原 —— 蓝
            if (n.Contains("desert") || n.Contains("sand") || n.Contains("dune"))
                return new Color(0.95f, 0.42f, 0.16f, 1f);      // 荒漠 —— 橙红
            return new Color(0.22f, 0.62f, 0.30f, 1f);          // 森林 —— 森林绿
        }

        /// <summary>阵营色（落败后转暗，便于和存活阵营区分）。</summary>
        private Color FactionColorOf(FactionData fd)
        {
            if (fd == null) return new Color(0.6f, 0.6f, 0.6f, 1f);
            Color c = FactionColor(fd.Name);
            if (fd.Eliminated) c = Color.Lerp(c, new Color(0.42f, 0.42f, 0.45f, 1f), 0.62f);
            return c;
        }

        /// <summary>回合判定：积分 &lt; 0 落败；只剩一方有积分 → 回合结束并重置全场积分。</summary>
        private void EvaluateRound()
        {
            if (!PointsEnabled || Factions.Count < 2) return;
            int alive = 0, lastAlive = -1;
            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                if (fd.Points < 0f)
                {
                    if (!fd.Eliminated)
                    {
                        fd.Eliminated = true;
                        Api.Log("Faction/points: " + fd.Name + " DEFEATED (" + fd.Points.ToString("F0") + ")");
                        PushPointsEvent(fd.Name + " DEFEATED", i);
                        ClearFactionAircraft(i);   // 淘汰即清场：把该阵营场上还在飞的 AI 收掉
                    }
                }
                else { alive++; lastAlive = i; }
            }
            if (alive > 1) return;

            // 决出胜负 → 全场积分重置为基准值，进入下一回合
            string winner = lastAlive >= 0 ? Factions[lastAlive].Name : "";
            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                fd.Points = fd.BasePoints;
                fd.Eliminated = false;
                fd.AircraftLost = 0;
                fd.LostValue = 0f;
            }
            _round++;
            _roundWinner = winner;
            _roundOverT = Time.time;
            Api.Log("Faction/points: ROUND OVER -> winner=" + (winner.Length > 0 ? winner : "none")
                    + " | all factions reset to " + ((int)Factions[0].BasePoints)
                    + " | next round = " + _round);
            PushPointsEvent("ROUND " + (_round - 1) + " OVER - " + (winner.Length > 0 ? winner + " WINS" : "DRAW"));
        }

        /// <summary>
        /// 淘汰清场：把某个阵营场上还在飞的 AI 收掉（只动 AI，绝不碰玩家飞机）。
        /// 用 Destroy 而不是 SetActive(false)，原因有两条：
        ///   ① WatchAircraft 对"Tr 已销毁"的记录只摘账、不结算（见 :1449），
        ///      所以清场不会产生额外的战损扣分；
        ///   ② CountAlive 用的是 FindObjectsOfType&lt;FactionMarker&gt;(true)，**包含 inactive** ——
        ///      若改成 SetActive(false)，这些"幽灵"会被继续算作存活，
        ///      该阵营（或后续回合）就永远补刷不出新飞机。
        /// </summary>
        private void ClearFactionAircraft(int factionId)
        {
            int n = 0;
            try
            {
                var marks = UnityEngine.Object.FindObjectsOfType<FactionMarker>(true);
                for (int i = 0; i < marks.Length; i++)
                {
                    var m = marks[i];
                    if (m == null || m.FactionId != factionId) continue;
                    if (m.gameObject.GetComponent("AiEntityMarker") == null) continue;   // 只收 AI
                    UnityEngine.Object.Destroy(m.gameObject);
                    n++;
                }
            }
            catch (Exception e) { Api.Log("Faction: clear aircraft failed " + e.Message); }
            Api.Log("Faction: " + FactionName(factionId) + " eliminated -> cleared " + n + " aircraft");
        }

        /// <summary>每 0.15s 扫一遍在册飞机：原版自行炸毁（撞地等）的靠这里兜底结算。</summary>
        private void WatchAircraft()
        {
            _aircraftWatchT -= Time.deltaTime;
            if (_aircraftWatchT > 0f) return;
            _aircraftWatchT = 0.15f;
            if (_aircraft.Count == 0) return;

            _aircraftGone.Clear();
            foreach (var kv in _aircraft)
            {
                var rec = kv.Value;
                if (rec == null) { _aircraftGone.Add(kv.Key); continue; }
                if (rec.Tr == null) { _aircraftGone.Add(kv.Key); continue; }   // Unity 已销毁（正常回收/切场景）

                bool alive = true, exploded = false;
                try
                {
                    if (rec.Pc != null) exploded = rec.Pc.Exploded;
                }
                catch { alive = false; }

                // 活着且还没结算 → 持续刷新造价快照（玩家随时会在机场改机体 / 坠毁后原机复位）
                if (!rec.Reported && !exploded) RefreshWorth(rec, 0.5f);

                if (rec.Reported)
                {
                    // 飞机被修好/复位（玩家机复用）→ 解除已结算标记，下一次战损还能记账
                    if (alive && !exploded) rec.Reported = false;
                    continue;
                }
                if (exploded) SettleLoss(rec, -1, "destroyed");
            }
            for (int i = 0; i < _aircraftGone.Count; i++) _aircraft.Remove(_aircraftGone[i]);
            // 兜底清账：切场景/长时间堆积时的泄漏保护
            if (_aircraft.Count > 240) _aircraft.Clear();
        }

        /// <summary>最近一条积分事件（战况条下方一行：出现 → 过几秒自己消失）。</summary>
        private string _pointsToast = "";
        private float _pointsToastT = -99f;
        private float _pointsToastDur = 5f;
        private int _flashFaction = -1;     // 刚发生积分变动的阵营 → 战况条该行短暂高亮
        private float _flashT = -99f;
        private float _flashDur = 2.5f;

        private void PushPointsEvent(string s) { PushPointsEvent(s, -1); }

        private void PushPointsEvent(string s, int factionId)
        {
            _pointsEventSeq++;
            _pointsToast = s;
            _pointsToastT = Time.time;
            if (factionId >= 0 && factionId < Factions.Count)
            {
                _flashFaction = factionId;
                _flashT = Time.time;
            }
        }

        // =================================================================
        // 自测（阵营积分模式）
        //   触发：mods/FactionSystem/_points_test.flag 存在（文件内容 = 存档名，可留空）
        //   1) 主菜单自动载入 mod 存档进游戏（配方同 MachineAAM / MachineShop）
        //   2) 给一个非玩家阵营生成 AI，核对 ComputeWorth（并与原版 GetPlanePrice 对照）
        //   3) 真实上报一次战损 → 核对扣分 + 落败判定
        //   4) 把其余阵营全部打到负分 → 核对"回合结束 + 全场积分重置为基准值"
        //   跑完删除 flag，避免劫持下一次启动
        // =================================================================
        private bool _testArmed;
        private bool _testRan;
        private bool _saveLoadIssued;
        private string _testSaveName = "";

        private string TestFlagPath()
        {
            try
            {
                return System.IO.Path.Combine(
                    System.IO.Path.Combine(Api.GetModsDirectory(), "FactionSystem"), "_points_test.flag");
            }
            catch { return null; }
        }

        private void CheckSelfTest()
        {
            if (_testRan || _testArmed) return;
            string flag = TestFlagPath();
            if (string.IsNullOrEmpty(flag)) return;
            try { if (!System.IO.File.Exists(flag)) return; } catch { return; }
            _testArmed = true;
            try { _testSaveName = System.IO.File.ReadAllText(flag).Trim(); } catch { }
            Api.Log("Faction/selftest: armed save='" + _testSaveName + "'");
            StartCoroutine(PointsSelfTest(flag));
        }

        // -----------------------------------------------------------------
        // 机场地图 dump（只读）：列出全部机场 + 每个阵营的基地机场与机型池。
        // 仅在 _points_test.flag 存在（自测）时输出一次，正常游玩零日志。
        // -----------------------------------------------------------------
        private bool _airDumped;
        private void TryDumpAirportMap()
        {
            if (_airDumped) return;
            string flag = TestFlagPath();
            if (string.IsNullOrEmpty(flag)) { _airDumped = true; return; }
            try { if (!System.IO.File.Exists(flag)) { _airDumped = true; return; } } catch { _airDumped = true; return; }

            System.Collections.Generic.List<Airport> aps = null;
            try { var am = AirportManager.Instance; if (am != null) aps = am.airports; } catch { }
            if (aps == null || aps.Count == 0) return;   // 机场未就绪，下一帧再看
            if (Factions.Count == 0) return;             // 阵营未就绪，下一帧再看

            _airDumped = true;
            // 大陆清单 + 每个大陆的基地机场（名字/坐标）
            try
            {
                var cm2 = FindSingleton("ContinentManager");
                if (cm2 != null)
                {
                    var fL = cm2.GetType().GetField("continents",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var lst = fL != null ? fL.GetValue(cm2) as System.Collections.IList : null;
                    Api.Log("Faction/AIRMAP: continents=" + (lst != null ? lst.Count : -1));
                    if (lst != null)
                    {
                        for (int ci = 0; ci < lst.Count; ci++)
                        {
                            var cd = lst[ci];
                            if (cd == null) continue;
                            string cname = "?";
                            var fCt2 = cd.GetType().GetField("continentType");
                            if (fCt2 != null && fCt2.GetValue(cd) != null)
                            {
                                var ct2 = fCt2.GetValue(cd);
                                var fN2 = ct2.GetType().GetField("continentName");
                                if (fN2 != null && fN2.GetValue(ct2) != null) cname = fN2.GetValue(ct2).ToString();
                            }
                            string bname = "none"; Vector3 bpos = Vector3.zero;
                            var fB2 = cd.GetType().GetField("baseAirport");
                            var ap2 = fB2 != null ? fB2.GetValue(cd) : null;
                            if (ap2 != null)
                            {
                                try { var nf = ap2.GetType().GetField("airportName"); bname = nf != null && nf.GetValue(ap2) != null ? nf.GetValue(ap2).ToString() : "(unnamed)"; } catch { }
                                try { var pp = ap2.GetType().GetProperty("position"); if (pp != null) bpos = (Vector3)pp.GetValue(ap2, null); } catch { }
                            }
                            Api.Log("Faction/AIRMAP: continent[" + ci + "] " + cname + " baseAirport=" + bname + " pos=" + bpos.ToString("F0")
                                    + (ci < Factions.Count ? (" faction=" + Factions[ci].Name) : ""));
                        }
                    }
                }
            }
            catch (Exception e2) { Api.Log("Faction/AIRMAP: continent dump failed " + e2.Message); }
            Api.Log("Faction/AIRMAP: airports=" + aps.Count + " factions=" + Factions.Count
                    + " playerFaction=" + PlayerFactionId);
            for (int i = 0; i < aps.Count; i++)
            {
                var a = aps[i];
                if (a == null) { Api.Log("Faction/AIRMAP: [" + i + "] <null>"); continue; }
                string nm = "?"; string id = "?"; Vector3 p = Vector3.zero; bool b = false;
                try { nm = string.IsNullOrEmpty(a.airportName) ? "(unnamed)" : a.airportName; } catch { }
                try { id = string.IsNullOrEmpty(a.id) ? "?" : a.id; } catch { }
                try { p = a.position; } catch { }
                try { b = a.IsBaseAirport; } catch { }
                int ownerId = AirportFactionOf(a);
                int contIdx = AirportContinentIndex(a);
                string owner = ownerId >= 0 ? (Factions[ownerId].Name + "#" + ownerId) : "neutral";
                Api.Log("Faction/AIRMAP: [" + i + "] " + nm + " id=" + id
                        + " pos=" + p.ToString("F0") + " base=" + b
                        + " continent=" + contIdx + " owner=" + owner);
            }

            // ---- 大陆机场清单：整座大陆的机场都该是同一个阵营 ----
            try
            {
                var cm3 = FindSingleton("ContinentManager");
                var fL3 = cm3 != null ? cm3.GetType().GetField("continents",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var lst3 = fL3 != null ? fL3.GetValue(cm3) as System.Collections.IList : null;
                if (lst3 != null)
                {
                    for (int ci = 0; ci < lst3.Count; ci++)
                    {
                        var cd3 = lst3[ci];
                        var fA3 = cd3 != null ? cd3.GetType().GetField("airports",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                        var al3 = fA3 != null ? fA3.GetValue(cd3) as System.Collections.IList : null;
                        string names3 = "";
                        if (al3 != null)
                        {
                            for (int k = 0; k < al3.Count && k < 40; k++)
                            {
                                var a3 = al3[k] as Airport;
                                if (a3 == null) continue;
                                names3 += (names3.Length > 0 ? "," : "") + (string.IsNullOrEmpty(a3.airportName) ? "?" : a3.airportName);
                            }
                        }
                        string ctn3 = ci < _continentTypeNames.Count ? _continentTypeNames[ci] : "?";
                        Api.Log("Faction/AIRMAP: continentAirports[" + ci + "] " + ctn3
                                + " count=" + (al3 != null ? al3.Count : -1)
                                + " faction=" + (FactionOfContinent(ci) >= 0 ? (Factions[FactionOfContinent(ci)].Name + "#" + FactionOfContinent(ci)) : "neutral")
                                + " [" + (names3.Length > 0 ? names3 : "-") + "]");
                    }
                }
            }
            catch (Exception e3) { Api.Log("Faction/AIRMAP: continent airports dump failed " + e3.Message); }

            // ---- 归属交叉核对：独立按 Airport.id 后缀算一遍，与解析器结果逐座比对 ----
            try
            {
                int ok = 0, bad = 0, undet = 0, mine = 0;
                string detail = "";
                int[] cnt = new int[Factions.Count + 1];   // [0..n-1] 各阵营，[n] = 中立
                var map = new System.Text.StringBuilder();
                for (int i = 0; i < aps.Count; i++)
                {
                    var a = aps[i];
                    if (a == null) continue;
                    int got = AirportFactionOf(a);
                    cnt[got >= 0 && got < Factions.Count ? got : Factions.Count]++;
                    if (got == PlayerFactionId) mine++;
                    map.Append('[').Append(i).Append(']')
                       .Append(string.IsNullOrEmpty(a.airportName) ? "?" : a.airportName)
                       .Append('=').Append(got < 0 ? "neutral" : Factions[got].Name).Append(' ');
                    // 独立算法：id 后缀 = 大陆类型资产名
                    int exp = -2;   // -2 = 这次算不出来，不参与比对
                    string aid2 = a.id;
                    if (!string.IsNullOrEmpty(aid2))
                        for (int ci = 0; ci < _continentTypeNames.Count; ci++)
                        {
                            string tn = _continentTypeNames[ci];
                            if (!string.IsNullOrEmpty(tn) && aid2.EndsWith(tn)) { exp = FactionOfContinent(ci); break; }
                        }
                    if (exp == -2) { undet++; continue; }
                    if (exp == got) ok++;
                    else
                    {
                        bad++;
                        if (bad <= 8)
                            detail += (detail.Length > 0 ? " | " : "")
                                      + (string.IsNullOrEmpty(a.airportName) ? "?" : a.airportName)
                                      + " id=" + aid2 + " got=" + got + " expect=" + exp;
                    }
                }
                string counts = "";
                for (int i = 0; i < Factions.Count; i++) counts += (counts.Length > 0 ? "," : "") + i + ":" + cnt[i];
                counts += ",neutral:" + cnt[Factions.Count];
                Api.Log("Faction/AIRMAP: airportFaction matched=" + ok + " mismatched=" + bad
                        + " undetermined=" + undet + " blueForPlayer=" + mine + "/" + aps.Count
                        + " counts=" + counts + " verdict=" + (bad == 0 ? "PASS" : "FAIL"));
                if (detail.Length > 0) Api.Log("Faction/AIRMAP: mismatches " + detail);
                Api.Log("Faction/AIRMAP: ownership " + map.ToString());
            }
            catch (Exception e4) { Api.Log("Faction/AIRMAP: crosscheck failed " + e4.Message); }
            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                if (fd == null) continue;
                string bn = "?";
                try { if (fd.BaseAirport != null) bn = string.IsNullOrEmpty(fd.BaseAirport.airportName) ? "(unnamed)" : fd.BaseAirport.airportName; } catch { }
                Api.Log("Faction/AIRMAP: faction[" + i + "] " + fd.Name + " home=" + fd.HomePos.ToString("F0")
                        + " baseAirport=" + bn + " aiCount=" + fd.AiCount + " designPool=" + fd.AiDesigns.Count
                        + (i == PlayerFactionId ? " (PLAYER)" : ""));
            }
        }

        private int CountRegistered(int factionId)
        {
            int n = 0;
            foreach (var kv in _aircraft)
                if (kv.Value != null && kv.Value.Faction == factionId) n++;
            return n;
        }

        private AircraftRec FirstRecOf(int factionId)
        {
            foreach (var kv in _aircraft)
                if (kv.Value != null && kv.Value.Faction == factionId && !kv.Value.Reported) return kv.Value;
            return null;
        }

        /// <summary>自测专用：不经过登记表直接记一笔战损（用来把阵营打到负分）。</summary>
        public void TestApplyLoss(int factionId, float worth, string reason)
        {
            var rec = new AircraftRec();
            rec.Faction = factionId;
            rec.Worth = worth;
            rec.Model = "TESTACFT";
            rec.BornT = Time.time;
            SettleLoss(rec, -1, reason);
        }

        private bool AllPointsAtBase()
        {
            for (int i = 0; i < Factions.Count; i++)
                if (Mathf.Abs(Factions[i].Points - Factions[i].BasePoints) > 0.5f) return false;
            return true;
        }

        /// <summary>自测截图（异步落盘，调用后要等 ≥1.2s 再切状态）。</summary>
        private void Shot(string tag)
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath),
                                          "Machine", "logs", "faction_shots");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _shotIdx++;
                string file = Path.Combine(dir, string.Format("{0:00}_{1}.png", _shotIdx, tag));
                ScreenCapture.CaptureScreenshot(file);
                Api.Log("Faction/selftest: shot -> " + file);
            }
            catch (Exception e) { Api.Log("Faction/selftest: shot failed " + e.Message); }
        }

        /// <summary>
        /// 阵地战规则的状态机自测（**纯逻辑**，不需要游戏世界，直接驱动 TerritoryStep）。
        /// 断言：8/s 消耗 → 18.75s 易手；10/s 占领 → 15s 满值；后占领方不恢复；
        ///       原始占领方 2/s（无人）/ 4/s（有己方飞机）；满值不溢出。
        /// </summary>
        private bool SelfTestTerritory()
        {
            bool consumeOk = false, captureOk = false, noRegenOk = false, neutralOk = false;
            bool regen2Ok = false, regen4Ok = false, capOk = false;
            float flipSec = -1f, capSec = -1f;
            try
            {
                // ① 有主之地（原始占领方 0 满值）被 1 架敌机压制：8/s 消耗 150 → 18.75s 易手
                var a = new TerritoryState();
                a.NativeOwner = 0; a.Owner = 0; a.Value = TerrMaxValue;
                float t = 0f; bool flipped = false;
                for (int i = 0; i < 400; i++)
                {
                    t += 0.5f;
                    if (TerritoryStep(a, 1, false, 0.5f)) { flipped = true; break; }
                }
                flipSec = t;
                consumeOk = flipped && a.Owner == 1
                            && Mathf.Abs(t - TerrMaxValue / TerrConsumeRate) <= 0.51f;

                // ② 易手后自己就是"后占领方"：己方飞机留在空域 → 按占领速度 10/s 涨满 150（15s）。
                //    这里必须传 attacker=-1 + holderPresent=true —— 真实调用点永远不会把"当前占领方"
                //    当攻击者传进来（否则这一条会走错分支，掩盖"占领值永远冻在 0"的 bug）
                float t2 = 0f;
                for (int i = 0; i < 400 && a.Value < TerrMaxValue - 0.01f; i++)
                {
                    TerritoryStep(a, -1, true, 0.5f);
                    t2 += 0.5f;
                }
                capSec = t2;
                captureOk = a.Value >= TerrMaxValue - 0.01f
                            && Mathf.Abs(t2 - TerrMaxValue / TerrCaptureRate) <= 0.51f;

                // ②b 后占领方飞机一走 → 占领值**冻住**（既不涨也不掉）
                {
                    float keepV = a.Value;
                    for (int i = 0; i < 20; i++) TerritoryStep(a, -1, false, 0.5f);
                    noRegenOk = Mathf.Abs(a.Value - keepV) < 0.01f;
                }

                // ②c 无主之地被到场方直接积累（10/s）
                var z = new TerritoryState();
                z.NativeOwner = -1; z.Owner = -1; z.Value = 0f;
                for (int i = 0; i < 20; i++) TerritoryStep(z, 1, true, 0.5f);
                neutralOk = Mathf.Abs(z.Value - 100f) < 0.01f && z.Owner == 1;

                // ④ 原始占领方：无人时 2/s，空域内有己方飞机时 4/s（各静置 10s）
                var b = new TerritoryState();
                b.NativeOwner = 2; b.Owner = 2; b.Value = 0f;
                for (int i = 0; i < 20; i++) TerritoryStep(b, -1, false, 0.5f);
                regen2Ok = Mathf.Abs(b.Value - 20f) < 0.01f;

                var c = new TerritoryState();
                c.NativeOwner = 2; c.Owner = 2; c.Value = 0f;
                for (int i = 0; i < 20; i++) TerritoryStep(c, -1, true, 0.5f);
                regen4Ok = Mathf.Abs(c.Value - 40f) < 0.01f;

                // ⑤ 满值不溢出
                var d = new TerritoryState();
                d.NativeOwner = 0; d.Owner = 0; d.Value = TerrMaxValue;
                for (int i = 0; i < 20; i++) TerritoryStep(d, -1, true, 0.5f);
                capOk = Mathf.Abs(d.Value - TerrMaxValue) < 0.01f;
            }
            catch (Exception e) { Api.Log("Faction/selftest: terr logic threw " + e.Message); }

            bool ok = consumeOk && captureOk && noRegenOk && neutralOk && regen2Ok && regen4Ok && capOk;
            Api.Log("Faction/selftest: TEST territory logic consume=" + consumeOk
                    + "(" + flipSec.ToString("F2") + "s, expect " + (TerrMaxValue / TerrConsumeRate).ToString("F2") + ")"
                    + " capture=" + captureOk + "(" + capSec.ToString("F2") + "s, expect "
                    + (TerrMaxValue / TerrCaptureRate).ToString("F2") + ")"
                    + " postCaptureFreeze=" + noRegenOk + " neutralCapture=" + neutralOk
                    + " regen2=" + regen2Ok + " regen4=" + regen4Ok
                    + " cap=" + capOk + " ok=" + ok);
            return ok;
        }

        private IEnumerator PointsSelfTest(string flag)
        {
            _testRan = true;
            Api.Log("Faction/selftest: start (pointsMode=" + PointsEnabled + ")");

            int waited = 0;
            while (Factions.Count == 0 && waited < 240) { yield return new WaitForSeconds(2f); waited += 2; }
            if (Factions.Count == 0)
            {
                Api.Log("Faction/selftest: FAIL no factions after " + waited + "s FACTION_POINTS_SELFTEST_DONE");
                FinishTest(flag); yield break;
            }
            Api.Log("Faction/selftest: factions=" + Factions.Count + " first=" + Factions[0].Name
                    + " player=" + PlayerFactionId);

            bool flyReq = false;
            waited = 0;
            while (waited < 180)
            {
                PlaneContainer pc = null;
                try { pc = PlaneContainer.Instance; } catch { }
                if (pc == null)
                {
                    if (!_saveLoadIssued && TryLoadSaveAndEnter()) _saveLoadIssued = true;
                }
                else if (!pc.FlightModeInitialized)
                {
                    if (!flyReq)
                    {
                        try
                        {
                            GameManager gm = GameManager.Instance;
                            MethodInfo m = typeof(GameManager).GetMethod("StartFlyMode",
                                new Type[] { typeof(bool), typeof(bool) });
                            if (gm != null && m != null)
                            {
                                m.Invoke(gm, new object[] { false, false });
                                flyReq = true;
                                Api.Log("Faction/selftest: StartFlyMode requested");
                            }
                        }
                        catch (Exception e) { Api.Log("Faction/selftest: StartFlyMode failed " + e.Message); }
                    }
                }
                else break;
                yield return new WaitForSeconds(2f);
                waited += 2;
                if (waited % 20 == 0) Api.Log("Faction/selftest: waiting for flight... " + waited + "s");
            }
            bool flightReady = false;
            try { flightReady = PlaneContainer.Instance != null && PlaneContainer.Instance.FlightModeInitialized; } catch { }
            Api.Log("Faction/selftest: flightReady=" + flightReady);

            // 开一次计分板 + 战况条，截图留证（新排版 / 模式按钮 / 灰描边长什么样）
            // ⚠ ScreenCapture.CaptureScreenshot 是**本帧帧末 latch**：Shot() 之后必须 yield 一帧再改状态，
            //    否则同一帧里改回去的 _scoreOpen / 模式会作用到那张图上（拍出来是"关着的"）——
            //    之前几轮"计分板间歇性不渲染"就是这么拍出来的，不是渲染 bug。
            _scoreOpen = true;
            _forceScore = true;
            yield return new WaitForSeconds(2.5f);
            Api.Log("Faction/selftest: preShot1 pure=" + Machine.Mod.MachineState.PureMode
                    + " inflight=" + Machine.Mod.MachineState.InFlight() + " scoreOpen=" + _scoreOpen);
            Shot("scoreboard_points");
            yield return null;
            yield return new WaitForSeconds(1.2f);
            _scoreOpen = false;
            _forceScore = false;
            yield return new WaitForSeconds(1.5f);
            Shot("points_bar_plain");
            yield return null;
            yield return new WaitForSeconds(1.2f);

            // ---- 阵地战：实机端到端（真建表 → 真扫描 → 真 tick）----
            // ⚠ C# 不允许在带 catch 的 try 块里 yield → 把 yield 挪到 try 外面
            bool terrLiveOk = false, terrLiveRan = false;
            bool keepTerr = TerritoryEnabled;
            float keepR = TerrAirRadius, keepTerrTime = TerrTimeLeft, keepCeil = TerrAirCeiling;
            TerritoryState pick = null;     // 敌方阵地（验证"消耗"）
            TerritoryState pickN = null;    // 中立阵地（验证"占领"）
            try
            {
                BuildTerritoryList();
                int nat = 0, neu = 0;
                for (int i = 0; i < Territories.Count; i++)
                {
                    var s0 = Territories[i];
                    if (s0 == null) continue;
                    if (s0.NativeOwner >= 0) nat++; else neu++;
                }
                Api.Log("Faction/terr: dump strongpoints=" + Territories.Count + " native=" + nat + " neutral=" + neu);
                for (int i = 0; i < Territories.Count; i++)
                {
                    var s0 = Territories[i];
                    if (s0 == null) continue;
                    Api.Log("Faction/terr:   [" + i + "] " + s0.Label
                            + " native=" + FactionName(s0.NativeOwner) + " owner=" + FactionName(s0.Owner)
                            + " value=" + s0.Value.ToString("F0") + " pos=" + s0.Pos.ToString("F0"));
                }

                Vector3 pp = Vector3.zero;
                bool haveP = false;
                try { if (PlayerPlane != null) { pp = PlayerPlane.transform.position; haveP = true; } } catch { }
                Api.Log("Faction/selftest: TEST territory live playerPos=" + pp.ToString("F0")
                        + " playerFaction=" + FactionName(PlayerFactionId));
                float bestE = float.MaxValue, bestN = float.MaxValue;
                if (haveP)
                    for (int i = 0; i < Territories.Count; i++)
                    {
                        var s0 = Territories[i];
                        if (s0 == null) continue;
                        float d = (s0.Pos - pp).sqrMagnitude;
                        if (s0.NativeOwner >= 0 && s0.NativeOwner != PlayerFactionId && s0.Owner != PlayerFactionId)
                        {
                            if (d < bestE) { bestE = d; pick = s0; }          // 有主之地 → 验证"消耗"
                        }
                        else if (s0.NativeOwner < 0 && s0.Owner < 0)
                        {
                            if (d < bestN) { bestN = d; pickN = s0; }         // 无主之地 → 验证"占领"
                        }
                    }
                if (!haveP || (pick == null && pickN == null))
                    Api.Log("Faction/selftest: TEST territory live SKIP (playerPlane=" + haveP
                            + " enemyStrongpoint=" + (pick != null ? pick.Label : "none")
                            + " neutralStrongpoint=" + (pickN != null ? pickN.Label : "none") + ")");
                else
                {
                    terrLiveRan = true;
                    TerritoryEnabled = true;                    // 只为让 StepTerritory 跑起来（PointsEnabled 不动）
                    BeginTerritoryRound("selftest live");        // 倒计时 + 阵地都归位，避免一开就判回合结束
                    TerrAirRadius = 30000f;                     // 覆盖全图 → 玩家机同时压制所有敌方阵地
                    TerrAirCeiling = 6000f;                     // 高度也不设限，免得巡航高度把它挡掉
                    _terrLastT = -1f;
                    _terrTickT = 0f;
                }
            }
            catch (Exception e) { Api.Log("Faction/selftest: territory live setup failed " + e.Message); }

            if (terrLiveRan)
            {
                float eBefore = pick != null ? pick.Value : -1f;
                int eOwnBefore = pick != null ? pick.Owner : -1;
                float nBefore = pickN != null ? pickN.Value : -1f;
                yield return new WaitForSeconds(3f);            // 6 拍：消耗 8/s → ≈24；占领 10/s → ≈30
                try
                {
                    float eAfter = pick != null ? pick.Value : -1f;
                    float nAfter = pickN != null ? pickN.Value : -1f;
                    bool myCraft = false;
                    try
                    {
                        var mk = PlayerPlane.GetComponent<FactionMarker>();
                        myCraft = (mk != null && mk.FactionId == PlayerFactionId);
                    }
                    catch { }
                    bool drainOk = (pick == null) || (eAfter < eBefore - 4f);
                    bool claimOk = (pickN == null) || (nAfter > nBefore + 4f || pickN.Owner == PlayerFactionId);
                    terrLiveOk = drainOk && claimOk;
                    Api.Log("Faction/selftest: TEST territory live drain=["
                            + (pick != null ? pick.Label + " native=" + FactionName(pick.NativeOwner) : "none")
                            + "] " + FactionName(eOwnBefore) + " " + eBefore.ToString("F0")
                            + " -> " + eAfter.ToString("F0") + " ok=" + drainOk
                            + " | claim=[" + (pickN != null ? pickN.Label : "none") + "] "
                            + nBefore.ToString("F0") + " -> " + nAfter.ToString("F0")
                            + " owner=" + (pickN != null ? FactionName(pickN.Owner) : "-") + " ok=" + claimOk
                            + " | myMarker=" + myCraft + " radius=" + Mathf.RoundToInt(TerrAirRadius)
                            + " ok=" + terrLiveOk);
                }
                catch (Exception e) { Api.Log("Faction/selftest: territory live failed " + e.Message); }
            }

            // 还原（TerrTimeLeft 给个正常值，免得紧跟着的截图块一开就判回合结束）
            TerrAirRadius = keepR;
            TerrAirCeiling = keepCeil;
            TerritoryEnabled = keepTerr;
            _terrRoundStarted = true;
            TerrTimeLeft = keepTerr ? keepTerrTime : TerrRoundSeconds;
            ResetTerritories();

            // 阵地战的计分板截图（模式按钮 + 模式设置 + 阵地列 + 分隔线）
            if (Territories.Count > 0)
            {
                bool keepT3 = TerritoryEnabled;
                TerritoryEnabled = true;
                _scoreOpen = true;
                _forceScore = true;
                _scoreOpenTime = Time.time;
                yield return new WaitForSeconds(2.5f);
                Api.Log("Faction/selftest: preShotTerr inflight=" + Machine.Mod.MachineState.InFlight()
                        + " scoreOpen=" + _scoreOpen + " terr=" + TerritoryEnabled
                        + " owned=" + OwnedSig(OwnedCounts()));
                Shot("scoreboard_territory");
                yield return null;                     // 帧末 latch：先让这一帧画完再改状态
                yield return new WaitForSeconds(1.2f);
                _scoreOpen = false;
                _forceScore = false;
                TerritoryEnabled = keepT3;
                yield return new WaitForSeconds(1.2f);
            }

            // ---- 造一架非玩家阵营的 AI，验证造价 ----
            PointsDebug = true;
            int target = (Factions.Count > 1) ? (PlayerFactionId == 0 ? 1 : 0) : 0;
            Factions[target].AiCount = Mathf.Max(Factions[target].AiCount, 1);
            Api.Log("Faction/selftest: AiCount[" + Factions[target].Name + "]=" + Factions[target].AiCount
                    + " (waiting for spawn)");

            waited = 0;
            while (FirstRecOf(target) == null && waited < 150) { yield return new WaitForSeconds(3f); waited += 3; }

            AircraftRec rec = FirstRecOf(target);
            float worth = 0f, origWorth = -1f;
            bool worthRefreshOk = false;   // 复用同一架飞机时造价会不会重算（老 bug 的回归检查）
            if (rec != null)
            {
                worth = rec.Worth;
                if (rec.Tr != null) origWorth = GamePlanePrice(rec.Tr.gameObject);
                Api.Log("Faction/selftest: aircraft=" + rec.Model + " worth=" + worth.ToString("F0")
                        + " origGetPlanePrice=" + origWorth.ToString("F0"));
            }
            else Api.Log("Faction/selftest: WARN no AI registered within " + waited + "s (spawn/design missing?)");

            // ---- 回归：同一架飞机被复用后，造价必须重新算（老 bug：恒按第一次登记的数扣分）----
            // 用户实测"开高价值飞机被击落，扣分和低价机一模一样"（连续 4 次恒扣 -59011）：
            // 根因是 rec.Worth 只在注册那一刻算一次，玩家机被原版复用/重建后从不刷新。
            try
            {
                if (PlayerPlane == null)
                    Api.Log("Faction/selftest: TEST worthRefresh SKIP (player plane not resolved)");
                else
                {
                    int pid = PlayerPlane.gameObject.GetInstanceID();
                    int pn = 0;
                    try { pn = PlayerPlane.GetComponentsInChildren<BuildingPart>(true).Length; } catch { }
                    AircraftRec pr;
                    if (!_aircraft.TryGetValue(pid, out pr) || pr == null)
                        Api.Log("Faction/selftest: TEST worthRefresh FAIL (player plane not registered, id=" + pid + ")");
                    else
                    {
                        float truth = ComputeWorth(PlayerPlane.gameObject);
                        float stale = pr.Worth;
                        pr.Worth = 1234f;      // 人为写坏成"陈旧的错误值"
                        pr.WorthT = -99f;      // 同时解除 0.5s 节流，让下一次登记立刻重算
                        RegisterAircraft(PlayerPlane.gameObject, PlayerFactionId, "Player");
                        bool ok = Mathf.Abs(pr.Worth - truth) < 1f;
                        worthRefreshOk = ok;
                        Api.Log("Faction/selftest: TEST worthRefresh playerParts=" + pn
                                + " stale=" + stale.ToString("F0") + " broken->1234"
                                + " recomputed=" + pr.Worth.ToString("F0")
                                + " truth=" + truth.ToString("F0") + " ok=" + ok);
                    }
                }
            }
            catch (Exception e) { Api.Log("Faction/selftest: TEST worthRefresh EXCEPTION " + e.Message); }

            // 登记表总览（含玩家机）—— 用来确认玩家机确实被登记进册
            {
                var sbReg = new System.Text.StringBuilder();
                sbReg.Append("Faction/selftest: registered=").Append(_aircraft.Count);
                foreach (var kv in _aircraft)
                {
                    var r = kv.Value;
                    if (r == null) continue;
                    sbReg.Append(" | ").Append(r.Model).Append("(").Append(FactionName(r.Faction))
                         .Append(" w=").Append((int)r.Worth)
                         .Append(r.Pc != null ? " pc=ok" : " pc=MISSING").Append(")");
                }
                Api.Log(sbReg.ToString());
            }

            // ---- 走 AAM 的生产路径上报战损（等价于被导弹/机炮击落）----
            // 故意传**部件 transform**（机炮命中就是给被击中的碰撞体），验证阵营系统能上溯到飞机根、
            // 按整机造价而不是单部件造价扣分。
            int roundBefore = _round;
            float p0 = Factions[target].Points;
            bool viaAam = false;
            Transform arg = null;
            if (rec != null && rec.Tr != null)
            {
                arg = rec.Tr;
                if (rec.Tr.childCount > 0) arg = rec.Tr.GetChild(0);
                Api.Log("Faction/selftest: reporting via part transform '" + arg.name
                        + "' (root='" + rec.Tr.name + "')");
                viaAam = InvokeAamReport(arg, "selftest");
                yield return new WaitForSeconds(0.5f);
            }
            if (!viaAam)
            {
                worth = worth > 0f ? worth : 5000f;
                TestApplyLoss(target, worth, "selftest-fallback");
            }
            float p1 = Factions[target].Points;
            bool booked = Mathf.Abs((p0 - worth) - p1) < 1f;
            Api.Log("Faction/selftest: bridge=" + viaAam + " worth=" + worth.ToString("F0")
                    + " points " + p0.ToString("F0") + " -> " + p1.ToString("F0")
                    + " bookedExact=" + booked);

            // 扣分后截图：积分条变短 + 事件提示
            _scoreOpen = true;
            _forceScore = true;
            yield return new WaitForSeconds(1.2f);
            Shot("points_after_loss");
            yield return null;                      // ⚠ 帧末 latch：先让这一帧画完再改状态
            yield return new WaitForSeconds(1.2f);
            _scoreOpen = false;
            _forceScore = false;
            yield return new WaitForSeconds(1.2f);

            // ---- 打到只剩一方 → 回合结束 + 全场重置 ----
            Factions[target].Points = 50f;
            TestApplyLoss(target, 1000f, "selftest-eliminate");
            Api.Log("Faction/selftest: eliminate " + Factions[target].Name
                    + " eliminatedFlag=" + Factions[target].Eliminated);

            int guard = 0;
            bool roundEnded = _round > roundBefore;
            while (!roundEnded && Factions.Count >= 2 && guard++ < 12)
            {
                bool acted = false;
                for (int i = 0; i < Factions.Count; i++)
                {
                    if (i == PlayerFactionId || Factions[i].Points < 0f) continue;
                    TestApplyLoss(i, Factions[i].Points + 1000f, "selftest-final");
                    acted = true;
                    break;
                }
                roundEnded = _round > roundBefore;
                if (!acted) break;
            }
            bool allReset = AllPointsAtBase();
            Api.Log("Faction/selftest: round " + roundBefore + "->" + _round
                    + " ended=" + roundEnded + " allReset=" + allReset
                    + " winner=" + _roundWinner);

            // 回合结束横幅（截图留证）—— ⚠ 这一张必须排在"坠毁测试"**之前**：
            //   玩家机一炸毁就进 "You Crashed" 画面，MachineState.InFlight() 变 false →
            //   OnGUI 第一行就 return，横幅/计分板一个像素都画不出来（前几轮拍空就是这么来的）。
            yield return new WaitForSeconds(1.2f);
            Api.Log("Faction/selftest: preShot2 pure=" + Machine.Mod.MachineState.PureMode
                    + " inflight=" + Machine.Mod.MachineState.InFlight() + " scoreOpen=" + _scoreOpen);
            Shot("round_over");
            yield return null;
            yield return new WaitForSeconds(1.2f);

            // ---- 坠毁兜底（原版爆炸 + 0.15s 轮询）：用玩家机验证 ----
            // 注：AI 飞机的 PlaneController.ExplodePlane() 在原版就会 NRE（爆炸粒子预制体/音效字段
            //     是 AddComponent 出来的控制器没有的 —— AAM 每次 AI 击毁日志里都有这条），
            //     所以 AI 的 Exploded 永远不为 true。AI 的坠毁靠 AAM 自己的上报路径兜，
            //     这个轮询兜底实际服务的是**玩家机**（真机 prefab 字段齐全，原版撞地/被击落都会走它）。
            bool pollOk = false;
            {
                AircraftRec prec = FirstRecOf(PlayerFactionId);
                float pw = 0f, pb = 0f, pa = 0f;
                if (prec != null && prec.Pc != null)
                {
                    pw = prec.Worth;
                    pb = Factions[PlayerFactionId].Points;
                    try { prec.Pc.ExplodePlane(); }
                    catch (Exception e) { Api.Log("Faction/selftest: player explode failed " + e.Message); }
                    yield return new WaitForSeconds(1.5f);
                    pa = Factions[PlayerFactionId].Points;
                    pollOk = Mathf.Abs((pb - pw) - pa) < 1f;
                }
                else Api.Log("Faction/selftest: WARN player aircraft not registered (poll not verifiable)");
                Api.Log("Faction/selftest: crashPoll(player) worth=" + pw.ToString("F0")
                        + " points " + pb.ToString("F0") + " -> " + pa.ToString("F0") + " ok=" + pollOk);
            }

            bool worthOk = worth > 0f;
            bool terrLogicOk = SelfTestTerritory();
            // 实机阵地检查只在真的跑起来时才算分（SKIP 会在日志里明确写出来，别当通过）
            bool terrOk = terrLogicOk && (!terrLiveRan || terrLiveOk);
            bool pass = worthOk && booked && pollOk && worthRefreshOk && terrOk
                        && (Factions.Count < 2 || (roundEnded && allReset));
            Api.Log("Faction/selftest: worthOk=" + worthOk + " lossBooked=" + booked
                    + " crashPollOk=" + pollOk + " worthRefreshOk=" + worthRefreshOk
                    + " terrLogic=" + terrLogicOk + " terrLiveRan=" + terrLiveRan + " terrLive=" + terrLiveOk
                    + " verdict=" + (pass ? "PASS" : "FAIL") + " FACTION_POINTS_SELFTEST_DONE");

            FinishTest(flag);
        }

        /// <summary>自测专用：反射调用 MachineAAM 的战损上报桥（真实击落路径用的就是它）。</summary>
        private bool InvokeAamReport(Transform root, string reason)
        {
            try
            {
                // ⚠ 本机加载器把 mod 程序集加载进来后，Assembly.GetName().Name 与 "MachineAAM"
                //    **不相等**（实测：按名字匹配一个都找不到，而 Type.GetType("X, MachineAAM") 却能解析）。
                //    所以这里按"类型全名"扫，不看程序集名。
                Type t = null;
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try { t = asms[i].GetType("Machine.AAM.MissileController"); } catch { t = null; }
                    if (t != null) break;
                }
                Api.Log("Faction/selftest: bridge type=" + (t != null ? t.FullName : "null"));
                if (t == null) { Api.Log("Faction/selftest: MachineAAM not found (skip bridge)"); return false; }
                MethodInfo m = t.GetMethod("ReportFactionAircraftLost",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Api.Log("Faction/selftest: bridge method=" + (m != null ? m.ToString() : "null"));
                if (m == null) { Api.Log("Faction/selftest: AAM bridge method missing"); return false; }
                m.Invoke(null, new object[] { root, reason });
                Api.Log("Faction/selftest: AAM bridge invoked OK");
                return true;
            }
            catch (Exception e)
            {
                Api.Log("Faction/selftest: AAM bridge failed " + e.GetType().Name + ": " + e.Message
                        + " | inner=" + (e.InnerException != null
                            ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : "-")
                        + " | stack=" + e.StackTrace);
                return false;
            }
        }

        private void FinishTest(string flag)
        {
            try { if (!string.IsNullOrEmpty(flag) && System.IO.File.Exists(flag)) System.IO.File.Delete(flag); }
            catch { }
            PointsDebug = false;
            _forceScore = false;
            _scoreOpen = false;
            for (int i = 0; i < Factions.Count; i++) Factions[i].AiCount = 0;
            Api.Log("Faction/selftest: finished, flag removed, AiCount reset");
        }

        /// <summary>自测用：在主菜单里把一个 mod 存档载入进游戏（配方同 MachineAAM.TryLoadSaveAndEnter）。</summary>
        private bool TryLoadSaveAndEnter()
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData"), "LocalLow"),
                    System.IO.Path.Combine("Aviassembly", System.IO.Path.Combine("Machine_Mod",
                        System.IO.Path.Combine("Aviassembly", "SaveGames"))));
                if (!System.IO.Directory.Exists(dir)) { Api.Log("Faction AUTOLOAD: save dir missing " + dir); return false; }

                string chosen = null;
                if (!string.IsNullOrEmpty(_testSaveName)
                    && System.IO.File.Exists(System.IO.Path.Combine(dir, _testSaveName + ".plane")))
                    chosen = _testSaveName;
                if (chosen == null)
                {
                    string[] files = System.IO.Directory.GetFiles(dir, "*.plane");
                    if (files == null || files.Length == 0) { Api.Log("Faction AUTOLOAD: no .plane saves"); return false; }
                    Array.Sort(files, delegate (string a, string b)
                    { return System.IO.File.GetLastWriteTime(b).CompareTo(System.IO.File.GetLastWriteTime(a)); });
                    chosen = System.IO.Path.GetFileNameWithoutExtension(files[0]);
                }

                LoadPanel[] lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0) { Api.Log("Faction AUTOLOAD: LoadPanel not found"); return false; }
                Type ty = lps[0].GetType();
                MethodInfo sf = ty.GetMethod("SelectFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo ld = ty.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sf == null || ld == null) { Api.Log("Faction AUTOLOAD: SelectFile/Load missing"); return false; }
                Machine.Mod.MachineState.ViaModSaves = true;
                Machine.Mod.MachineState.PureMode = false;
                sf.Invoke(lps[0], new object[] { chosen });
                ld.Invoke(lps[0], null);
                Api.Log("Faction AUTOLOAD: LoadPanel.Load('" + chosen + "') issued");
                return true;
            }
            catch (Exception e) { Api.Log("Faction AUTOLOAD failed: " + e.Message); return false; }
        }

        // =================================================================

        private void Update()
        {
            // 自测（flag 文件门控）：必须在 PureMode 守卫之前，否则主菜单阶段被挡掉
            CheckSelfTest();
            // 自测期间附带输出"机场 -> 阵营"地图（只读，正常游玩不产出）
            TryDumpAirportMap();
            // 纯净模式守卫：Load/New/Sandbox 进入的原版档，不生成 AI、不显示面板/计分板
            if (Machine.Mod.MachineState.PureMode) { _panelOpen = false; _scoreOpen = false; return; }
            // 阵营未就绪时（主菜单场景没有 ContinentManager）延迟重试，失败静默
            if (Factions.Count == 0)
            {
                _retryT -= Time.deltaTime;
                if (_retryT <= 0f)
                {
                    _retryT = 10f;
                    int before = Factions.Count;
                    ProbeContinents();
                    if (Factions.Count > before)
                    {
                        DistributeDesigns();
                        Api.Log("FactionSystem ready (deferred) | factions=" + Factions.Count);
                    }
                }
                return;
            }

            // 找玩家飞机（排除 AI 机——AI 机也是 PlaneContainer）
            _planeScanT -= Time.deltaTime;
            if (_planeScanT <= 0f)
            {
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                _planeScanT = inGame ? 0.3f : 3f;   // 主菜单/编辑器：3s 低频扫描（减少空场景 FindObjectsOfType 开销）
                // 玩家机解析：**优先 PlaneContainer.Instance**（单例就是玩家当前那架，AAM 也是这么拿的）。
                // ⚠ 原来只在 PlayerPlane == null 时 FindObjectsOfType 一次就永久锁定 —— 拿到的很可能是
                //   机场里的模板/预览容器而不是玩家真正在飞的那架；它的部件永不变化，于是
                //   "开多贵的飞机都扣同一个数"（用户实测恒扣 -59011）。这里改成每次扫描都按 instanceID
                //   重新校验，换机/复位立刻跟上。
                if (PlayerPlane == null || PlayerPlane.GetInstanceID() != _playerPlaneId)
                {
                    PlaneContainer found = null;
                    try { if (IsUsablePlayerPlane(PlaneContainer.Instance)) found = PlaneContainer.Instance; } catch { }
                    if (found == null)
                    {
                        var all = UnityEngine.Object.FindObjectsOfType<PlaneContainer>(true);
                        for (int i = 0; i < all.Length; i++)
                            if (IsUsablePlayerPlane(all[i])) { found = all[i]; break; }
                    }
                    if (found != null)
                    {
                        PlayerPlane = found;
                        _playerPlaneId = found.GetInstanceID();
                        if (PointsDebug)
                        {
                            int pn = 0;
                            try { pn = found.GetComponentsInChildren<BuildingPart>(true).Length; } catch { }
                            Api.Log("Faction/points: player plane resolved id=" + _playerPlaneId
                                    + " parts=" + pn + " worth=" + ComputeWorth(found.gameObject).ToString("F0"));
                        }
                    }
                }
                // 玩家机挂阵营标记（阵营可能切换）
                if (PlayerPlane != null)
                {
                    var mk = PlayerPlane.GetComponent<FactionMarker>();
                    if (mk == null) mk = PlayerPlane.gameObject.AddComponent<FactionMarker>();
                    mk.FactionId = PlayerFactionId;
                    // 玩家机也是"阵营飞机"：造价记到账上，坠毁/被击落同样扣分
                    RegisterAircraft(PlayerPlane.gameObject, PlayerFactionId, "Player");
                }
            }

            // 在册飞机的战损兜底（原版自行爆炸的飞机在这里结算）
            if (PointsEnabled) WatchAircraft();

            // 阵地战：占点推进 + 回合倒计时（0.5s 一拍，内部自己节流）
            StepTerritory();

            // /ai 指令开关阵营面板（通过聊天框输入 /ai 呼出）
            // Tab 键呼出已移除，改为指令呼出
            CheckAiCommand();
            // Alt 开关计分板
            try
            {
                bool altDown = false;
                var kb2 = UnityEngine.InputSystem.Keyboard.current;
                if (kb2 != null)
                    altDown = (kb2.leftAltKey.wasPressedThisFrame && kb2.leftAltKey.isPressed)
                           || (kb2.rightAltKey.wasPressedThisFrame && kb2.rightAltKey.isPressed);
                if (!altDown) altDown = UnityEngine.Input.GetKeyDown(KeyCode.LeftAlt) || UnityEngine.Input.GetKeyDown(KeyCode.RightAlt);
                // 只在游戏窗口获得焦点时响应，并做 0.4s 去抖
                // （实测：窗口失焦时 InputSystem 会误报 leftAltKey.wasPressedThisFrame，导致计分板自己乱开关）
                bool focused = true;
                try { focused = UnityEngine.Application.isFocused; } catch { }
                if (altDown && focused && Time.unscaledTime - _lastAltToggle > 0.4f)
                {
                    _lastAltToggle = Time.unscaledTime;
                    _scoreOpen = !_scoreOpen;
                    if (_scoreOpen) _scoreOpenTime = Time.time;   // 交给计分板做 0.5s 防误触
                    _altToggles++;
                    Api.Log("Faction: ALT toggled scoreboard -> " + _scoreOpen + " (n=" + _altToggles + ")");
                }
            }
            catch { }

            // AI 生成
            StepAiSpawn();

            // 地图友军标记
            UpdateFriendlyMap();
        }

        private void OnGUI()
        {
            if (Factions.Count == 0) return;
            // 主菜单/非游戏场景守卫：不绘制阵营面板与计分板（PlaneContainer 或机场存在才算游戏内）
            bool inGame = false;
            try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
            if (!inGame) return;
            // 纯净模式守卫：原版档不显示阵营面板/计分板
            if (Machine.Mod.MachineState.PureMode) return;
            // 编辑器/停机坪：未进入飞行模式不显示阵营面板/计分板
            if (!Machine.Mod.MachineState.InFlight()) return;
            if (_panelOpen) DrawPanel();
            if (_scoreOpen || _forceScore) DrawScoreboard();
            DrawPointsBanner();
        }

        // =================================================================
        // AI 生成（每阵营按 AiCount 在主岛基地附近刷）
        // =================================================================
        private void StepAiSpawn()
        {
            // 主菜单/非游戏场景守卫：不在主菜单生成 AI（避免无谓开销与主菜单出现空中 AI）
            bool inGame = false;
            try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
            if (!inGame) return;

            _aiSpawnT -= Time.deltaTime;
            if (_aiSpawnT > 0f) return;
            _aiSpawnT = 4f;

            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                // 本回合已被淘汰（积分 < 0）的阵营不再补刷。
                // ⚠ 必须有这一句：EvaluateRound 只在"存活阵营 ≤ 1"时才复位 Eliminated，
                //   所以只要还有 ≥2 个阵营活着，被淘汰方就会一直挂着 Eliminated=true ——
                //   之前少了这个判断，淘汰阵营照样一架接一架往外刷。
                if (fd.Eliminated) continue;
                int alive = CountAlive(i);
                if (alive >= fd.AiCount) continue;
                if (fd.HomePos == Vector3.zero) continue;

                string designDir = null;
                if (fd.AiDesigns.Count > 0)
                {
                    fd.DesignCursor = (fd.DesignCursor + 1) % fd.AiDesigns.Count;
                    designDir = fd.AiDesigns[fd.DesignCursor];
                }
                string designFile = designDir != null ? DesignFileFor(designDir) : null;
                if (designFile == null)
                {
                    // 回退：AAM mod 自带的 F-22
                    designFile = System.IO.Path.Combine(System.IO.Path.Combine(Api.GetModsDirectory(), "MachineAAM"), "F-22.planedesign");
                    if (!System.IO.File.Exists(designFile)) continue;
                }

                Vector3 pos = fd.HomePos + new Vector3(
                    UnityEngine.Random.Range(-350f, 350f), 0f, UnityEngine.Random.Range(-350f, 350f));   // 靠近机场陆地，减少落入海域
                try
                {
                    // 落地检测：从高空向下 Raycast 找真实地面高度，AI 生成在地面上方 3m（避免生成在地底/海底钻地）
                    UnityEngine.RaycastHit hit;
                    Vector3 probe = pos + Vector3.up * 400f;
                    if (Physics.Raycast(probe, Vector3.down, out hit, 2500f))
                        pos.y = Mathf.Max(hit.point.y, fd.HomePos.y) + 3f;
                    else pos.y = fd.HomePos.y + 5f;
                }
                catch { pos.y = fd.HomePos.y + 5f; }
                Vector3 dir = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f)).normalized;
                bool hostile = (i != PlayerFactionId);

                string callsign = fd.Name.Replace(" ", "") + "-" + (++_aiSeq);
                // speed=45：跑道式生成（贴地 + 滑跑初速），AI 全油门滑跑起飞，不再半空凭空出现
                var ai = SpawnAiAircraft(designFile, pos, dir, 45f, 500f, hostile, callsign, i);
                if (ai != null)
                {
                    // 挂阵营标记（AAM 返回的根物体）
                    var mark = ai.GetComponent<FactionMarker>();
                    if (mark == null) mark = ai.gameObject.AddComponent<FactionMarker>();
                    mark.FactionId = i;
                    // 同步 AI 的 FactionId 字段（Bandit 分支据此优先打敌方 AI 而非玩家）
                    try
                    {
                        var ff = ai.GetType().GetField("FactionId", BindingFlags.Public | BindingFlags.Instance);
                        if (ff != null) ff.SetValue(ai, i);
                    }
                    catch { }
                    Api.Log("Faction: spawned " + callsign + " -> " + fd.Name + " (hostile=" + hostile + ")");
                    // 对战积分：登记造价（战损时按其扣分）
                    RegisterAircraft(ai.gameObject, i, callsign);
                }
            }
        }

        private int CountAlive(int factionId)
        {
            int n = 0;
            var marks = UnityEngine.Object.FindObjectsOfType<FactionMarker>(true);
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i] == null || marks[i].FactionId != factionId) continue;
                if (marks[i].gameObject.GetComponent("AiEntityMarker") == null) continue;   // 只统计 AI
                n++;
            }
            return n;
        }

        /// <summary>反射调用 AAM 的 AiAircraftFactory.Spawn。</summary>
        private Component SpawnAiAircraft(string designFile, Vector3 pos, Vector3 dir, float speed, float alt, bool hostile, string callsign, int faction)
        {
            try
            {
                var t = Type.GetType("Machine.AAM.AiAircraftFactory, MachineAAM");
                if (t == null)
                {
                    // ⚠ 不要按 Assembly.GetName().Name 匹配：本机加载器加载进来的 mod 程序集，
                    //    程序集简单名与文件/Type.GetType 用的名字对不上（实测一个都匹配不到）。
                    var asms = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < asms.Length; i++)
                    {
                        try { t = asms[i].GetType("Machine.AAM.AiAircraftFactory"); } catch { t = null; }
                        if (t != null) break;
                    }
                }
                if (t == null) { Api.Log("Faction: AAM factory missing (MachineAAM not loaded?)"); return null; }
                var m = t.GetMethod("Spawn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { Api.Log("Faction: AAM factory Spawn missing"); return null; }
                var res = m.Invoke(null, new object[] { Api, designFile, pos, dir, speed, alt, hostile, callsign });
                return res as Component;
            }
            catch (Exception e) { Api.Log("Faction: spawn AI failed " + e.Message); return null; }
        }

        // =================================================================
        // 计分板
        // =================================================================
        public void RegisterKill(int killerFaction, int victimFaction)
        {
            try
            {
                if (killerFaction >= 0 && killerFaction < Factions.Count) Factions[killerFaction].Kills++;
                if (victimFaction >= 0 && victimFaction < Factions.Count) Factions[victimFaction].Deaths++;
                Api.Log("Faction: kill -> " + (killerFaction >= 0 ? Factions[killerFaction].Name : "?")
                        + " / death -> " + (victimFaction >= 0 ? Factions[victimFaction].Name : "?")
                        + " (K " + Factions[killerFaction >= 0 ? killerFaction : 0].Kills + ")");
            }
            catch { }
        }

        /// <summary>登记玩家/AI级别的击杀战绩。</summary>
        public void RegisterPlayerKill(string killerName, string killerModel, int killerFaction, bool killerAI,
                                        string victimName, string victimModel, int victimFaction, bool victimAI)
        {
            try
            {
                RegisterKill(killerFaction, victimFaction);
                // 击杀方
                if (killerFaction >= 0 && killerFaction < Factions.Count)
                {
                    var ps = GetOrCreatePlayer(Factions[killerFaction], killerName, killerModel, killerAI);
                    ps.Kills++;
                    ps.Alive = true;
                }
                // 被击杀方
                if (victimFaction >= 0 && victimFaction < Factions.Count)
                {
                    var ps = GetOrCreatePlayer(Factions[victimFaction], victimName, victimModel, victimAI);
                    ps.Deaths++;
                }
            }
            catch { }
        }

        private PlayerStat GetOrCreatePlayer(FactionData fd, string name, string model, bool isAI)
        {
            for (int i = 0; i < fd.Players.Count; i++)
            {
                if (fd.Players[i].Name == name) return fd.Players[i];
            }
            var ps = new PlayerStat { Name = name, Model = model, IsAI = isAI, Alive = true };
            fd.Players.Add(ps);
            return ps;
        }

        // =================================================================
        // 对战积分：常驻战况条 + 回合结束横幅 + 事件提示
        // =================================================================
        private Texture2D _px;
        private GUIStyle _bigStyle;

        private Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                _px.SetPixels(new Color[] { Color.white, Color.white, Color.white, Color.white });
                _px.Apply();
            }
            return _px;
        }

        /// <summary>
        /// 阵地战战况条（与积分条同一位置、同样"只有条 + 灰描边 + 阵营色填充"的样式）：
        /// 填充比例 = 该阵营占有的阵地数 / 阵地总数，右侧显示 "N / 总数"，上方一行是本回合倒计时。
        /// </summary>
        private void DrawTerritoryBanner()
        {
            try
            {
                if (Factions.Count == 0 || Territories.Count == 0) { DrawRoundBannerOnly(); return; }
                GUI.skin = BuildSkin();

                float w = Mathf.Min(560f, Screen.width - 40f);
                float x = (Screen.width - w) * 0.5f;
                float rowH = 19f;
                bool toast = Time.time - _pointsToastT < _pointsToastDur;
                float top = Screen.height - 108f - Factions.Count * rowH;

                int left = Mathf.Max(0, Mathf.CeilToInt(TerrTimeLeft));
                int mm = left / 60, ss = left - mm * 60;
                BarLabel(new Rect(x + 10f, top - 17f, w - 20f, 16f),
                         "阵地战   " + (mm < 10 ? "0" : "") + mm + ":" + (ss < 10 ? "0" : "") + ss
                         + "   阵地 " + Territories.Count + "   [" + Mathf.RoundToInt(TerrAirRadius) + "m]",
                         new Color(0.96f, 0.97f, 1f, 1f));

                int total = Mathf.Max(1, Territories.Count);
                for (int i = 0; i < Factions.Count; i++)
                {
                    var fd = Factions[i];
                    float ry = top + i * rowH;
                    bool me = (i == PlayerFactionId);
                    Color fc = FactionColorOf(fd);
                    Color tc = Color.Lerp(fc, Color.white, 0.22f);
                    float flash = (i == _flashFaction) ? Mathf.Clamp01(1f - (Time.time - _flashT) / _flashDur) : 0f;
                    if (flash > 0f) tc = Color.Lerp(tc, Color.white, flash * 0.7f);

                    BarLabel(new Rect(x + 10f, ry, 130f, 17f), (me ? "> " : "  ") + fd.Name, tc);

                    float bx = x + 142f, bw = w - 250f, by = ry + 3f, bh = 11f;
                    float ratio = Mathf.Clamp01(CountOwned(i) / (float)total);
                    Color border = new Color(0.46f, 0.48f, 0.52f, 0.95f);
                    if (flash > 0f) border = Color.Lerp(border, new Color(1f, 1f, 1f, 1f), flash);
                    GUI.color = border;
                    GUI.DrawTexture(new Rect(bx, by, bw, 1f), Px());                 // 上
                    GUI.DrawTexture(new Rect(bx, by + bh - 1f, bw, 1f), Px());       // 下
                    GUI.DrawTexture(new Rect(bx, by, 1f, bh), Px());                 // 左
                    GUI.DrawTexture(new Rect(bx + bw - 1f, by, 1f, bh), Px());       // 右
                    if (ratio > 0f)
                    {
                        GUI.color = flash > 0f ? Color.Lerp(fc, Color.white, flash * 0.45f) : fc;
                        GUI.DrawTexture(new Rect(bx + 1f, by + 1f, (bw - 2f) * ratio, bh - 2f), Px());
                    }
                    GUI.color = Color.white;

                    BarLabel(new Rect(x + w - 104f, ry, 96f, 17f), CountOwned(i) + " / " + total, tc);
                }

                if (toast)
                {
                    float ta = Mathf.Clamp01(_pointsToastDur - (Time.time - _pointsToastT));
                    BarLabel(new Rect(x + 10f, top + Factions.Count * rowH + 3f, w - 20f, 15f),
                             _pointsToast, new Color(0.90f, 0.92f, 0.96f, 1f), ta);
                }
            }
            catch { }
            DrawRoundBannerOnly();
        }

        private void DrawPointsBanner()
        {
            try
            {
                if (Factions.Count == 0) return;

                // 阵地战：换成"占有阵地数"的战况条（同样常驻）
                if (TerritoryEnabled) { DrawTerritoryBanner(); return; }

                if (!PointsEnabled) { DrawRoundBannerOnly(); return; }

                // ⭐ 积分战期间**常驻显示**：不再跟随 Alt 计分板显隐，也不再只在事件后短暂出现。
                //   用户要求"积分战实时显示、不要跟随计分板消失"；变动反馈交给两样东西：
                //   ① 变动的那一行短暂提亮 2.5s ② 下方 toast 一行字，5s 后自己消失。
                GUI.skin = BuildSkin();

                float w = Mathf.Min(560f, Screen.width - 40f);
                float x = (Screen.width - w) * 0.5f;
                float rowH = 19f;
                bool toast = Time.time - _pointsToastT < _pointsToastDur;
                // 顶部那条横带被原版按钮行/罗盘带占满 → 战况条放屏幕下方（钱币栏上方），两边都空着
                float top = Screen.height - 108f - Factions.Count * rowH;

                // 只画积分条本身，不再用整块底色面板框住（会挡住原版 HUD）
                for (int i = 0; i < Factions.Count; i++)
                {
                    var fd = Factions[i];
                    float ry = top + i * rowH;
                    bool me = (i == PlayerFactionId);
                    Color fc = FactionColorOf(fd);
                    // 文字用同一阵营色、稍微提亮（深色地面上也能看清）；落败方保持压暗
                    Color tc = fd.Eliminated ? fc : Color.Lerp(fc, Color.white, 0.22f);
                    // 刚发生积分变动的阵营：整行提亮，_flashDur 秒内线性淡出
                    float flash = (i == _flashFaction) ? Mathf.Clamp01(1f - (Time.time - _flashT) / _flashDur) : 0f;
                    if (flash > 0f) tc = Color.Lerp(tc, Color.white, flash * 0.7f);

                    BarLabel(new Rect(x + 10f, ry, 130f, 17f), (me ? "> " : "  ") + fd.Name, tc);

                    // 积分条：灰色描边 + 阵营色填充，空余部分留空
                    float bx = x + 142f, bw = w - 250f, by = ry + 3f, bh = 11f;
                    float ratio = Mathf.Clamp01(fd.Points / Mathf.Max(1f, fd.BasePoints));
                    Color border = new Color(0.46f, 0.48f, 0.52f, 0.95f);
                    if (flash > 0f) border = Color.Lerp(border, new Color(1f, 1f, 1f, 1f), flash);
                    GUI.color = border;
                    GUI.DrawTexture(new Rect(bx, by, bw, 1f), Px());                 // 上
                    GUI.DrawTexture(new Rect(bx, by + bh - 1f, bw, 1f), Px());       // 下
                    GUI.DrawTexture(new Rect(bx, by, 1f, bh), Px());                 // 左
                    GUI.DrawTexture(new Rect(bx + bw - 1f, by, 1f, bh), Px());       // 右
                    if (ratio > 0f)
                    {
                        GUI.color = flash > 0f ? Color.Lerp(fc, Color.white, flash * 0.45f) : fc;
                        GUI.DrawTexture(new Rect(bx + 1f, by + 1f, (bw - 2f) * ratio, bh - 2f), Px());
                    }
                    GUI.color = Color.white;

                    BarLabel(new Rect(x + w - 104f, ry, 96f, 17f),
                             fd.Eliminated ? "0 (OUT)" : Mathf.RoundToInt(Mathf.Max(0f, fd.Points)).ToString("N0"), tc);
                }

                if (toast)
                {
                    // 出现 → 显示 _pointsToastDur 秒 → 最后 1s 淡出消失
                    float ta = Mathf.Clamp01(_pointsToastDur - (Time.time - _pointsToastT));
                    BarLabel(new Rect(x + 10f, top + Factions.Count * rowH + 3f, w - 20f, 15f),
                             _pointsToast, new Color(0.90f, 0.92f, 0.96f, 1f), ta);
                }
            }
            catch { }
            DrawRoundBannerOnly();
        }

        /// <summary>
        /// 带黑色描边的文字：没有底板也能在天空/地面/浅色云上看清，且不吃掉阵营色。
        /// IMGUI 没有原生描边 → 把同一串字往东南西北各偏移画一遍黑字，再在正中画本色字。
        /// </summary>
        private void BarLabel(Rect r, string s, Color c) { BarLabel(r, s, c, 1f); }

        private void BarLabel(Rect r, string s, Color c, float a)
        {
            a = Mathf.Clamp01(a);
            if (a <= 0.01f) return;
            GUI.color = new Color(0f, 0f, 0f, 0.72f * a);
            GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), s);
            GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), s);
            GUI.Label(new Rect(r.x, r.y - 1f, r.width, r.height), s);
            GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), s);
            GUI.color = new Color(c.r, c.g, c.b, c.a * a);
            GUI.Label(r, s);
            GUI.color = Color.white;
        }

        /// <summary>
        /// 粗体 + 本色字面 + 黑色包边。用于回合结束横幅 —— 原来那种细字单色在天空/地面上会糊进背景。
        /// t = 包边厚度（px）；t &gt; 1.2 时走八个方向（含对角），否则只走上下左右（省绘制次数）。
        /// </summary>
        private void StrokedBoldLabel(Rect r, string s, GUIStyle st, Color fill, float a, float t)
        {
            a = Mathf.Clamp01(a);
            if (a <= 0.01f || st == null) return;
            int n = (t > 1.2f) ? 8 : 4;
            for (int i = 0; i < n; i++)
            {
                float dx = 0f, dy = 0f;
                switch (i)
                {
                    case 0: dx = -t; break;
                    case 1: dx = t; break;
                    case 2: dy = -t; break;
                    case 3: dy = t; break;
                    case 4: dx = -t; dy = -t; break;
                    case 5: dx = t; dy = -t; break;
                    case 6: dx = -t; dy = t; break;
                    case 7: dx = t; dy = t; break;
                }
                st.normal.textColor = new Color(0f, 0f, 0f, 0.94f * a);
                GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), s, st);
            }
            st.normal.textColor = new Color(fill.r, fill.g, fill.b, a);
            GUI.Label(r, s, st);
        }

        /// <summary>回合结束横幅（永远显示，与顶部战况条的显隐无关）。</summary>
        private void DrawRoundBannerOnly()
        {
            try
            {
                float dt = Time.time - _roundOverT;
                if (!(dt < 8f && _roundOverT > 0f)) return;
                if (_bigStyle == null)
                {
                    _bigStyle = new GUIStyle("label");
                    _bigStyle.fontSize = 30;
                    _bigStyle.fontStyle = FontStyle.Bold;   // 原来那种细字会糊进背景
                    _bigStyle.alignment = TextAnchor.MiddleCenter;
                }
                float a = Mathf.Clamp01(dt < 6f ? 1f : 1f - (dt - 6f) * 0.5f);

                // 白字 + 粗黑包边：无论底下是天空、雪原还是深色地面都读得出来
                _bigStyle.fontSize = 30;
                string title = "ROUND OVER - " + (_roundWinner.Length > 0 ? _roundWinner + " WINS" : "DRAW");
                StrokedBoldLabel(new Rect(0f, Screen.height * 0.30f, Screen.width, 44f),
                                 title, _bigStyle, Color.white, a, 2f);

                _bigStyle.fontSize = 15;
                string sub = TerritoryEnabled
                    ? ("strongpoints reset  -  round " + _round + "  -  "
                       + Mathf.RoundToInt(TerrRoundSeconds / 60f) + " min")
                    : ("all factions reset to " + Mathf.RoundToInt(PointsBase).ToString("N0")
                       + "  -  round " + _round);
                StrokedBoldLabel(new Rect(0f, Screen.height * 0.30f + 44f, Screen.width, 22f),
                                 sub, _bigStyle, new Color(0.90f, 0.93f, 0.98f, 1f), a, 1.5f);
                _bigStyle.fontSize = 30;
                GUI.color = Color.white;
            }
            catch { }
        }

        // ---- 计分板布局：固定列宽、数值右对齐、阵营之间画分隔线 ----
        // 老版是"阵营标题一个 360 宽的 label + Total/lost 各自缩进"，列和列对不上、
        // 窗口又固定 400x400，内容一多下部分就被裁掉 —— 这就是"下部分排版有问题"。
        private const float ScW = 470f;            // 窗口宽（内容坐标最大用到 440）
        private const float ScContW = 428f;        // 分隔线宽
        private const float ScNameX = 24f, ScNameW = 150f;
        private const float ScKX = 180f, ScNumW = 30f;
        private const float ScDX = 214f;
        private const float ScKdX = 248f, ScKdW = 44f;
        private const float ScPtsX = 296f, ScPtsW = 144f;
        private GUIStyle _scoreRight;
        private float _scoreContentH = 300f;       // 上一帧内容高度（回填窗口高度用）
        private float _scoreOpenTime = -9f;        // 刚打开 0.5s 内防误触

        /// <summary>计分板（Alt 开启）。内容见 ScoreWindow。</summary>
        private void DrawScoreboard()
        {
            try
            {
                GUI.skin = BuildSkin();
                // 窗口宽高由内容决定（高度取上一帧内容的实测高度 → 永远不裁切，也不会空一大块）
                _scoreRect.width = ScW;
                float maxH = Mathf.Max(220f, Screen.height - 150f);
                _scoreRect.height = Mathf.Clamp(_scoreContentH + 26f, 220f, maxH);
                _scoreRect.x = Mathf.Clamp(_scoreRect.x, 8f, Mathf.Max(8f, Screen.width - _scoreRect.width - 8f));
                _scoreRect.y = Mathf.Clamp(_scoreRect.y, 8f, Mathf.Max(8f, Screen.height - _scoreRect.height - 70f));
                _scoreRect = GUI.Window(98231, _scoreRect, ScoreWindow, "KD Scoreboard");
            }
            catch (Exception e)
            {
                if (!_scoreDrawErrLogged) { _scoreDrawErrLogged = true; Api.Log("Faction/scoreboard draw failed " + e.Message); }
            }
        }

        /// <summary>计分板里的 1px 分隔线（IMGUI 没有 HR）。</summary>
        private void ScLine(float x, float y, float w)
        {
            GUI.color = new Color(0.70f, 0.72f, 0.77f, 1f);
            GUI.DrawTexture(new Rect(x, y, w, 1f), Px());
            GUI.color = Color.white;
        }

        /// <summary>数值列（右对齐，位数不同也能对齐小数点）。</summary>
        private void ScNum(Rect r, string s)
        {
            GUI.Label(r, s, _scoreRight != null ? _scoreRight : GUI.skin.label);
        }

        /// <summary>计分板内容：模式按钮 + 模式设置 + 各阵营战绩 / 阵地数。</summary>
        private void ScoreWindow(int id)
        {
            try
            {
                if (_scoreRight == null)
                {
                    _scoreRight = new GUIStyle(GUI.skin.label);
                    _scoreRight.alignment = TextAnchor.MiddleRight;
                }
                bool guard = Time.time - _scoreOpenTime < 0.5f;      // 刚打开 0.5s 内防误触
                bool mouseUp = Event.current.type == EventType.MouseUp && Event.current.button == 0;
                bool click = mouseUp && !guard;
                bool terr = TerritoryEnabled;
                float y = 24f;

                // ---------- 模式按钮 ----------
                if (GUI.Button(new Rect(12f, y, 104f, 24f), terr ? "积分战" : "[ 积分战 ]") && click)
                    SwitchMode(false, "scoreboard");
                if (GUI.Button(new Rect(120f, y, 104f, 24f), terr ? "[ 阵地战 ]" : "阵地战") && click)
                    SwitchMode(true, "scoreboard");
                GUI.Label(new Rect(232f, y + 4f, 236f, 18f),
                          terr ? "mode: territory" : "mode: points");
                y += 30f;

                // ---------- 模式设置 ----------
                if (terr)
                {
                    GUI.Label(new Rect(12f, y + 4f, 76f, 18f), "每局时长");
                    if (GUI.Button(new Rect(90f, y, 26f, 22f), "-") && click)
                        SetTerrMinutes(Mathf.RoundToInt(TerrRoundSeconds / 60f) - 1);
                    GUI.Label(new Rect(120f, y + 3f, 92f, 18f), Mathf.RoundToInt(TerrRoundSeconds / 60f) + " 分钟");
                    if (GUI.Button(new Rect(214f, y, 26f, 22f), "+") && click)
                        SetTerrMinutes(Mathf.RoundToInt(TerrRoundSeconds / 60f) + 1);
                    if (GUI.Button(new Rect(254f, y, 96f, 22f), "重开本局") && click)
                        BeginTerritoryRound("scoreboard button");
                    GUI.Label(new Rect(358f, y + 4f, 110f, 18f), "countdown " + Mathf.Max(0, Mathf.CeilToInt(TerrTimeLeft)) + "s");
                    y += 26f;

                    GUI.Label(new Rect(12f, y + 4f, 76f, 18f), "空域半径");
                    if (GUI.Button(new Rect(90f, y, 26f, 22f), "-") && click) SetTerrRadius(TerrAirRadius - 100f);
                    GUI.Label(new Rect(120f, y + 3f, 92f, 18f), Mathf.RoundToInt(TerrAirRadius) + " m");
                    if (GUI.Button(new Rect(214f, y, 26f, 22f), "+") && click) SetTerrRadius(TerrAirRadius + 100f);
                    GUI.Label(new Rect(254f, y + 4f, 214f, 18f), "占点/消耗/恢复 = 10 / 8 / 2-4 per s");
                    y += 26f;
                }
                else
                {
                    GUI.Label(new Rect(12f, y + 4f, 76f, 18f), "开局积分");
                    if (GUI.Button(new Rect(90f, y, 26f, 22f), "-") && click) SetPointsBase(PointsBase - 50000f);
                    GUI.Label(new Rect(120f, y + 3f, 110f, 18f), Mathf.RoundToInt(PointsBase).ToString("N0"));
                    if (GUI.Button(new Rect(232f, y, 26f, 22f), "+") && click) SetPointsBase(PointsBase + 50000f);
                    if (GUI.Button(new Rect(270f, y, 96f, 22f), "重置回合") && click)
                    {
                        ResetPointsNow();
                        PushPointsEvent("points reset by player");
                    }
                    y += 26f;
                }
                y += 6f;
                ScLine(12f, y, ScContW);
                y += 8f;

                // ---------- 表头 ----------
                GUI.Label(new Rect(ScNameX, y, ScNameW, 18f), "Name");
                ScNum(new Rect(ScKX, y, ScNumW, 18f), "K");
                ScNum(new Rect(ScDX, y, ScNumW, 18f), "D");
                ScNum(new Rect(ScKdX, y, ScKdW, 18f), "KD");
                ScNum(new Rect(ScPtsX, y, ScPtsW, 18f), terr ? "Strongpoints" : "Points");
                y += 20f;

                int terrTotal = Mathf.Max(1, Territories.Count);
                for (int i = 0; i < Factions.Count; i++)
                {
                    var fd = Factions[i];
                    Color fc = FactionColorOf(fd);
                    // 阵营标题行：左侧色块 + 阵营名 + 战绩 + 积分/阵地数
                    GUI.color = fc;
                    GUI.DrawTexture(new Rect(12f, y + 4f, 8f, 12f), Px());
                    GUI.color = Color.white;
                    GUI.Label(new Rect(ScNameX, y, ScNameW, 18f), (i == PlayerFactionId ? "> " : "") + fd.Name);
                    ScNum(new Rect(ScKX, y, ScNumW, 18f), fd.Kills.ToString());
                    ScNum(new Rect(ScDX, y, ScNumW, 18f), fd.Deaths.ToString());
                    ScNum(new Rect(ScKdX, y, ScKdW, 18f), fd.Deaths > 0 ? (fd.Kills / (float)fd.Deaths).ToString("F2") : "-");
                    if (terr) ScNum(new Rect(ScPtsX, y, ScPtsW, 18f), CountOwned(i) + " / " + terrTotal);
                    else ScNum(new Rect(ScPtsX, y, ScPtsW, 18f),
                               fd.Eliminated ? "0 (OUT)" : Mathf.RoundToInt(Mathf.Max(0f, fd.Points)).ToString("N0"));
                    y += 19f;

                    // 阵营成员（只列存活）
                    for (int p = 0; p < fd.Players.Count; p++)
                    {
                        var ps = fd.Players[p];
                        if (!ps.Alive) continue;
                        GUI.color = new Color(0.30f, 0.31f, 0.36f, 1f);
                        GUI.Label(new Rect(ScNameX + 14f, y, ScNameW - 14f, 17f), ps.Name + (ps.IsAI ? " [AI]" : ""));
                        ScNum(new Rect(ScKX, y, ScNumW, 17f), ps.Kills.ToString());
                        ScNum(new Rect(ScDX, y, ScNumW, 17f), ps.Deaths.ToString());
                        ScNum(new Rect(ScKdX, y, ScKdW, 17f), ps.Deaths > 0 ? (ps.Kills / (float)ps.Deaths).ToString("F2") : "-");
                        GUI.color = Color.white;
                        y += 17f;
                    }

                    // 阵营小计
                    GUI.color = new Color(0.44f, 0.46f, 0.52f, 1f);
                    GUI.Label(new Rect(ScNameX + 14f, y, ScNameW - 14f, 17f), "Total");
                    ScNum(new Rect(ScKX, y, ScNumW, 17f), fd.Kills.ToString());
                    ScNum(new Rect(ScDX, y, ScNumW, 17f), fd.Deaths.ToString());
                    ScNum(new Rect(ScKdX, y, ScKdW, 17f), fd.Deaths > 0 ? (fd.Kills / (float)fd.Deaths).ToString("F2") : "-");
                    GUI.color = Color.white;
                    y += 18f;

                    // 明细行：积分战 = 战损造价；阵地战 = 原始阵地 / 占有 / 丢掉 / 抢来
                    string det;
                    if (terr)
                    {
                        int nat = 0, lost = 0, gained = 0;
                        for (int t = 0; t < Territories.Count; t++)
                        {
                            var st = Territories[t];
                            if (st == null) continue;
                            if (st.NativeOwner == i) nat++;
                            if (st.NativeOwner == i && st.Owner != i) lost++;
                            if (st.Owner == i && st.NativeOwner != i) gained++;
                        }
                        det = "native " + nat + "   owned " + CountOwned(i)
                              + "   lost " + lost + "   gained " + gained;
                    }
                    else
                    {
                        det = "lost " + fd.AircraftLost + " acft / " + Mathf.RoundToInt(fd.LostValue).ToString("N0")
                              + (fd.Eliminated ? "   [DEFEATED]" : "");
                    }
                    GUI.color = new Color(0.44f, 0.46f, 0.52f, 1f);
                    GUI.Label(new Rect(ScNameX + 14f, y, ScContW - 30f, 17f), det);
                    GUI.color = Color.white;
                    y += 19f;

                    if (i < Factions.Count - 1) { ScLine(12f, y, ScContW); y += 9f; }
                }

                y += 6f;
                ScLine(12f, y, ScContW);
                y += 7f;
                string foot = "[ALT] close    player: "
                    + (PlayerFactionId >= 0 && PlayerFactionId < Factions.Count ? Factions[PlayerFactionId].Name : "?");
                if (terr) foot += "    round " + _round + "    flips " + TerrFlips;
                GUI.Label(new Rect(12f, y, ScContW, 17f), foot);
                y += 20f;

                _scoreContentH = y;
                GUI.DragWindow(new Rect(0f, 0f, ScW - 30f, 24f));
            }
            catch { }
        }

        // =================================================================
        // Tab 阵营控制面板
        // =================================================================
        private void DrawPanel()
        {
            try
            {
                GUI.skin = BuildSkin();
                // 根据阵营数量保证最小高度，避免内容被截断；允许玩家手动拉高
                float minH = 24f + 26f + Factions.Count * 118f + 82f + 8f;
                if (_panelRect.height < minH) _panelRect.height = minH;
                _panelRect = GUI.Window(98232, _panelRect, PanelWindow, "Faction Control  [/ai]");
            }
            catch { }
        }

        private void PanelWindow(int id)
        {
            try
            {
                bool guard = Time.time - _panelOpenTime < 0.5f;   // 刚打开 0.5s 内防误触
                // 仅真实鼠标点击（MouseUp）触发按钮，挡掉焦点/系统事件造成的误触发；
                // 注意：GUI.Button 必须每事件帧都调用（含 MouseDown），否则按压状态不被记录、永远不触发
                bool mouseUp = Event.current.type == EventType.MouseUp && Event.current.button == 0;
                const float padL = 8f;        // 面板左内边距
                const float padR = 8f;        // 面板右内边距
                const float winW = 460f;      // 面板宽度（与 _panelRect 一致）
                const float boxW = winW - padL - padR; // 444
                const float btnW = 112f;      // 右侧按钮统一宽度
                const float btnR = winW - padR - btnW; // 340
                float y = 26f;
                for (int i = 0; i < Factions.Count; i++)
                {
                    var fd = Factions[i];
                    GUI.Box(new Rect(padL, y, boxW, 108f), GUIContent.none);
                    GUI.Label(new Rect(16f, y + 6f, 220f, 22f), fd.Name + (i == PlayerFactionId ? "  <you>" : ""));
                    // 选择阵营
                    if (i != PlayerFactionId && GUI.Button(new Rect(btnR, y + 4f, btnW, 24f), "Join Faction") && !guard && mouseUp)
                    {
                        PlayerFactionId = i;
                        Api.Log("Faction: player joined " + fd.Name + " (mouse)");
                    }
                    // AI 数量
                    GUI.Label(new Rect(16f, y + 34f, 78f, 20f), "AI count: " + fd.AiCount);
                    if (GUI.Button(new Rect(98f, y + 32f, 26f, 22f), "-") && !guard && mouseUp)
                    {
                        fd.AiCount = Mathf.Max(0, fd.AiCount - 1);
                    }
                    if (GUI.Button(new Rect(128f, y + 32f, 26f, 22f), "+") && !guard && mouseUp)
                    {
                        fd.AiCount = Mathf.Min(8, fd.AiCount + 1);
                    }
                    // 机型池
                    int pool = fd.AiDesigns.Count;
                    GUI.Label(new Rect(164f, y + 34f, 170f, 20f), "designs: " + pool);
                    string cur = "none";
                    if (pool > 0)
                    {
                        string d = DesignFileFor(fd.AiDesigns[Mathf.Clamp(fd.DesignCursor, 0, pool - 1)]);
                        if (d != null) cur = System.IO.Path.GetFileName(d).Replace(".planedesign", "");
                    }
                    GUI.Label(new Rect(16f, y + 60f, 320f, 20f), "next: " + cur);
                    if (pool > 0 && GUI.Button(new Rect(btnR, y + 58f, btnW, 22f), "cycle") && !guard && mouseUp)
                    {
                        fd.DesignCursor = (fd.DesignCursor + 1) % pool;
                    }
                    // 显示当前空中数量 + 对战积分
                    GUI.Label(new Rect(16f, y + 86f, boxW - 16f, 18f), "airborne: " + CountAlive(i)
                        + (PointsEnabled ? ("   points: " + Mathf.RoundToInt(Mathf.Max(0f, fd.Points))
                                           + (fd.Eliminated ? " [OUT]" : "")) : ""));
                    y += 118f;
                }
                // 模式切换（积分战 / 阵地战）+ 对应模式的重置
                if (GUI.Button(new Rect(8f, y + 2f, 110f, 22f),
                        TerritoryEnabled ? "积分战" : "[ 积分战 ]") && !guard && mouseUp)
                    SwitchMode(false, "panel");
                if (GUI.Button(new Rect(122f, y + 2f, 110f, 22f),
                        TerritoryEnabled ? "[ 阵地战 ]" : "阵地战") && !guard && mouseUp)
                    SwitchMode(true, "panel");
                if (GUI.Button(new Rect(236f, y + 2f, 140f, 22f),
                        TerritoryEnabled ? "重开本局 / Reset" : "重置回合 / Reset") && !guard && mouseUp)
                {
                    if (TerritoryEnabled) BeginTerritoryRound("panel button");
                    else
                    {
                        ResetPointsNow();
                        PushPointsEvent("points reset by player");
                        Api.Log("Faction/points: manual reset");
                    }
                }
                // 关闭按钮
                if (GUI.Button(new Rect(382f, y + 2f, 70f, 22f), "Close") && !guard && mouseUp)
                {
                    _panelOpen = false;
                    Api.Log("Faction: panel closed (button)");
                }
                GUI.Label(new Rect(12f, y + 28f, 436f, 40f),
                    "mode: " + (TerritoryEnabled ? "territory (strongpoints)" : "points")
                    + "\ntype /ai in chat to toggle this panel.  worth = airframe price (combat parts excluded).");
                GUI.DragWindow(new Rect(0f, 0f, winW, 24f));
            }
            catch { }
        }

        // =================================================================
        // /ai 指令呼出阵营面板
        // =================================================================
        private static FactionSystem _aiInstance;
        /// <summary>公共静态方法：切换阵营面板显示，供聊天框 /ai 指令调用。</summary>
        public static void TogglePanel()
        {
            if (_aiInstance == null) return;
            _aiInstance._panelOpen = !_aiInstance._panelOpen;
            _aiInstance._panelOpenTime = Time.time;
        }

        private void CheckAiCommand()
        {
            try
            {
                if (_aiInstance == null) _aiInstance = this;
                // 从 ChatBox.LastCommand 读取最近的指令
                string cmd = Machine.Core.ChatBox.LastCommand;
                if (!string.IsNullOrEmpty(cmd) && cmd.Equals("/ai", System.StringComparison.OrdinalIgnoreCase))
                {
                    Machine.Core.ChatBox.LastCommand = null;
                    TogglePanel();
                    Api.Log("Faction: panel toggled by /ai command");
                }
            }
            catch { }
        }

        // =================================================================
        // 地图友军位置（实时定位，不显示航迹）
        // =================================================================
        private void UpdateFriendlyMap()
        {
            try
            {
                _mapT -= Time.deltaTime;
                if (_mapT > 0f) return;

                if (_map == null)
                {
                    // 主菜单/非游戏场景没有 Map：低频重试，避免每 0.5s 空扫描
                    _mapT = 5f;
                    _map = UnityEngine.Object.FindFirstObjectByType(typeof(Map));
                    if (_map == null) return;
                }
                else _mapT = 0.5f;
                var map = (Map)_map;
                if (map.mapTransform == null) return;

                if (!_mapUiBuilt || _friendlyImage == null)
                {
                    BuildFriendlyLayer(map);
                    return;
                }
                bool mapOpen = map.mapTransform.gameObject.activeInHierarchy;
                if (!mapOpen) return;

                RedrawFriendly(map);
            }
            catch (Exception e)
            {
                // 低频错误不刷屏
                if (_lastFactionProbe != e.Message)
                {
                    _lastFactionProbe = e.Message;
                    Api.Log("Faction: map marker error " + e.Message);
                }
            }
        }

        private void BuildFriendlyLayer(Map map)
        {
            try
            {
                Transform parent = map.iconParent != null ? (Transform)map.iconParent : map.mapTransform;
                if (_friendlyLayer != null && _friendlyLayer.gameObject != null)
                {
                    _mapUiBuilt = true;
                    return;
                }
                var imgGo = new GameObject("Machine.FriendlyMarkers", typeof(RectTransform), typeof(Image));
                imgGo.transform.SetParent(parent, false);
                var rt = (RectTransform)imgGo.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                var img = imgGo.GetComponent<Image>();
                img.color = Color.white;
                img.raycastTarget = false;
                _friendlyImage = img;
                _friendlyLayer = imgGo;
                if (_friendlyTex == null)
                    _friendlyTex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
                _mapUiBuilt = true;
                Api.Log("Faction: friendly map layer built");
            }
            catch (Exception e) { Api.Log("Faction: build friendly layer failed " + e.Message); }
        }

        private Color[] _friendlyPx;
        private Sprite _friendlySprite;
        private string _friendlyHash = "";

        private void RedrawFriendly(Map map)
        {
            try
            {
                var marks = UnityEngine.Object.FindObjectsOfType<FactionMarker>(true);
                // 摘要哈希：友军位置/数量无变化不重绘（避免 0.5s 周期全量重绘 + 纹理上传拖慢帧率）
                string h = "";
                int drawCount = 0;
                for (int i = 0; i < marks.Length; i++)
                {
                    var mk = marks[i];
                    if (mk == null || mk.FactionId != PlayerFactionId) continue;
                    var go = mk.gameObject;
                    if (go.GetComponent("AiEntityMarker") == null) continue;
                    Vector3 wp = go.transform.position;
                    h += (int)(wp.x * 5f) + "," + (int)(wp.z * 5f) + ";";
                    drawCount++;
                }
                if (h == _friendlyHash) return;
                _friendlyHash = h;
                if (drawCount == 0) return;

                if (_friendlyPx == null) _friendlyPx = new Color[TexSize * TexSize];   // 复用数组，零每帧分配
                var px = _friendlyPx;
                var clear = new Color(0f, 0f, 0f, 0f);
                for (int i = 0; i < px.Length; i++) px[i] = clear;

                for (int i = 0; i < marks.Length; i++)
                {
                    var mk = marks[i];
                    if (mk == null || mk.FactionId != PlayerFactionId) continue;
                    var go = mk.gameObject;
                    // 只画 AI（玩家飞机由箭头/机标已有，不重复）
                    if (go.GetComponent("AiEntityMarker") == null) continue;
                    Vector3 wp = go.transform.position;
                    Vector2 t = ToMapTex(map, wp);
                    FillDot(px, (int)t.x, (int)t.y, new Color(0.35f, 0.95f, 0.45f, 0.9f), 7);
                }
                _friendlyTex.SetPixels(px);
                _friendlyTex.Apply();
                if (_friendlySprite == null)
                {
                    // Sprite 复用：引用同一纹理，纹理更新后画面自动刷新，不必每帧新建
                    _friendlySprite = Sprite.Create(_friendlyTex, new Rect(0f, 0f, TexSize, TexSize), new Vector2(0.5f, 0.5f));
                    if (_friendlyImage != null) _friendlyImage.sprite = _friendlySprite;
                }
            }
            catch (Exception e) { if (_lastFactionProbe != e.Message) { _lastFactionProbe = e.Message; Api.Log("Faction: redraw friendly failed " + e.Message); } }
        }

        private Vector2 ToMapTex(Map map, Vector3 world)
        {
            try
            {
                var mi = typeof(Map).GetMethod("GetInterpolator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null)
                {
                    Vector2 interp = (Vector2)mi.Invoke(map, new object[] { world });
                    if (interp.x >= -0.2f && interp.y >= -0.2f && interp.x <= 1.2f && interp.y <= 1.2f)
                        return new Vector2(interp.x * TexSize, interp.y * TexSize);
                }
            }
            catch { }
            return new Vector2(TexSize * 0.5f, TexSize * 0.5f);
        }

        private void FillDot(Color[] px, int cx, int cy, Color col, int r)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int tx = cx + x, ty = cy + y;
                    if (tx < 0 || tx >= TexSize || ty < 0 || ty >= TexSize) continue;
                    px[ty * TexSize + tx] = col;
                }
            }
        }

        // =================================================================
        // UI 皮肤
        // =================================================================
        private GUISkin _skin;
        private GUISkin BuildSkin()
        {
            try
            {
                if (_skin != null) return _skin;
                _skin = ScriptableObject.CreateInstance<GUISkin>();
                // 与战斗部（BattleCore）一致的浅色风格：白底面板 + 深灰文字
                _skin.window = new GUIStyle("window");
                _skin.window.normal.background = SolidTex(new Color(0.96f, 0.96f, 0.97f, 0.94f));
                _skin.window.normal.textColor = new Color(0.13f, 0.14f, 0.17f, 1f);
                _skin.window.fontSize = 15;
                _skin.box = new GUIStyle("box");
                _skin.box.normal.background = SolidTex(new Color(0.99f, 0.99f, 1f, 0.88f));
                _skin.box.normal.textColor = new Color(0.20f, 0.21f, 0.25f, 1f);
                _skin.label = new GUIStyle("label");
                _skin.label.normal.textColor = new Color(0.25f, 0.26f, 0.30f, 1f);
                _skin.label.fontSize = 13;
                _skin.button = new GUIStyle("button");
                _skin.button.normal.background = SolidTex(new Color(0.85f, 0.86f, 0.89f, 1f));
                _skin.button.normal.textColor = new Color(0.15f, 0.16f, 0.20f, 1f);
                _skin.button.hover.background = SolidTex(new Color(0.93f, 0.94f, 0.96f, 1f));
                _skin.button.hover.textColor = new Color(0.10f, 0.11f, 0.14f, 1f);
                _skin.button.active.background = SolidTex(new Color(0.72f, 0.74f, 0.78f, 1f));
                _skin.button.active.textColor = new Color(0.10f, 0.11f, 0.14f, 1f);
                return _skin;
            }
            catch { return null; }
        }

        private Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.SetPixels(new Color[] { c, c, c, c });
            t.Apply();
            return t;
        }
    }
}
