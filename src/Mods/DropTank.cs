using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Machine.Mod;
using Machine.Core;

namespace Machine.DropTank
{
    /// <summary>
    /// 副油箱（Drop Tank）：一种和普通货物同质的 CargoType —— 在机场商店购买、装进货仓、占用容量与重量。
    ///   每个副油箱：+10 油量（fuelCapacity / refrenceFuelCapacity / 现油量），自身 weight=3、cargoSpace=80。
    /// 实现要点：
    ///   - 货物用 ScriptableObject.CreateInstance 新建并 DontDestroyOnLoad，再注入每个机场的 cargoType 数组
    ///     （做法与 Radar 完全一致；场景切换后机场对象重建，所以每 2s 幂等重注）。
    ///   - 油量加成不直接对抗宿主：只读"当前油箱数"，把加成烘进 fuelCapacity / refrenceFuelCapacity，
    ///     靠"外部写入检测"（ActivateFlyMode / PartExploder 会重写 fuelCapacity）自动恢复。
    ///   - 纯数值部分抽成 FuelLink 结构体，可脱离 Unity 单测（tools/droptank_sim.cs）。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.droptank"; } }

        public void OnLoad(IMachineApi api)
        {
            // 加载器每次切场景都会重新引导所有 mod，只保留一个系统实例
            if (DropTankSystem.Live != null) return;
            api.Log("DropTank loading...");
            GameObject go = new GameObject("Machine.DropTank");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<DropTankSystem>().Init(api);
        }
    }

    /// <summary>副油箱对外 API（其他 mod 可反射调用）。</summary>
    public static class DropTankApi
    {
        private static DropTankSystem _sys;
        internal static void Bind(DropTankSystem sys) { _sys = sys; }
        public static bool Available { get { return _sys != null; } }
        /// <summary>机上副油箱数量。</summary>
        public static int TankCount { get { return _sys != null ? _sys.TankCount : 0; } }
        /// <summary>副油箱提供的额外油量上限。</summary>
        public static float FuelBonus { get { return _sys != null ? _sys.FuelBonus : 0f; } }
        /// <summary>货物类型本体（可拿去调 CargoInventory.GetCargoCount 等）。</summary>
        public static CargoType CargoType { get { return _sys != null ? _sys.TankCargo : null; } }
    }

    /// <summary>
    /// 副油箱 -> 油量的外挂记账。纯数值结构体（不引用 UnityEngine），可在游戏外单测。
    ///
    /// 为什么这么绕：fuelCapacity 只有两个宿主写入者 —— PlaneContainer.ActivateFlyMode（进飞行时从
    /// 部件重算）和 PartExploder.HandlePartRemoval（油箱部件被打掉）。宿主一重写，我们烘进去的加成
    /// 就没了，而下一次Tick 若还按"上次烘了多少"去减，就会把新基数算错（加成静默丢失）。
    /// 所以：观测到的 Cap 与 LastCap 不一致 => 认定宿主重写过 => Applied 归零，按新基数重新挂加成。
    /// </summary>
    public struct FuelLink
    {
        public float Cap;         // 当前 fuelCapacity
        public float Fuel;        // 当前 fuel
        public float RefCap;      // 当前 refrenceFuelCapacity（HUD 油量条按它归一化）
        public float Applied;     // 已烘进 Cap 的加成
        public float AppliedRef;  // 已烘进 RefCap 的加成
        public float LastCap;     // 上次观测/写入的 Cap（外部写入检测）
        public float LastRef;
        public int PaidTanks;     // 已经"给过油"的油箱数（防止每次进飞行都白送一箱油）

        public static FuelLink New(float cap, float fuel, float refCap)
        {
            FuelLink s = new FuelLink();
            s.Cap = cap;
            s.Fuel = fuel;
            s.RefCap = refCap;
            s.Applied = 0f;
            s.AppliedRef = 0f;
            s.LastCap = cap;
            s.LastRef = refCap;
            s.PaidTanks = 0;
            return s;
        }

        /// <summary>把 tanks 个副油箱的油量加成收敛进状态。返回是否有改动。</summary>
        public static bool Apply(ref FuelLink s, int tanks, float per, bool grant)
        {
            if (tanks < 0) tanks = 0;
            float bonus = (per > 0f) ? (tanks * per) : 0f;

            // 宿主（ActivateFlyMode / PartExploder / 加油）在我们背后改过容量 -> 加成已被抹掉
            if (Math.Abs(s.Cap - s.LastCap) > 0.001f) s.Applied = 0f;
            if (Math.Abs(s.RefCap - s.LastRef) > 0.001f) s.AppliedRef = 0f;

            float wantCap = s.Cap - s.Applied + bonus;
            float wantRef = s.RefCap - s.AppliedRef + bonus;
            bool changed = false;

            if (Math.Abs(wantCap - s.Cap) > 0.001f)
            {
                float d = wantCap - s.Cap;
                s.Cap = wantCap;
                // 只有"新装上了油箱"才补油；宿主重置后重新挂上加成时不补（否则反复起降能刷油）
                if (d > 0f && grant && tanks > s.PaidTanks)
                    s.Fuel = Math.Min(s.Fuel + (tanks - s.PaidTanks) * per, wantCap);
                changed = true;
            }
            // 容量没变也要夹一次：宿主重写容量后现油量可能残留在旧容量之上（丢油箱 = 顺带丢它的油）
            if (s.Fuel > wantCap) { s.Fuel = wantCap; changed = true; }
            if (s.Fuel < 0f) { s.Fuel = 0f; changed = true; }
            if (Math.Abs(wantRef - s.RefCap) > 0.001f)
            {
                s.RefCap = wantRef;
                changed = true;
            }

            s.Applied = bonus;
            s.AppliedRef = bonus;
            s.LastCap = s.Cap;
            s.LastRef = s.RefCap;
            s.PaidTanks = tanks;
            return changed;
        }
    }

    public class DropTankSystem : MonoBehaviour
    {
        public static DropTankSystem Live;

        private IMachineApi _api;
        private CargoType _tank;

        // ---------------- 配置 ----------------
        private string cfgCargoName = "Drop Tank";
        private float cfgFuelPerTank = 10f;
        private float cfgWeight = 3f;
        private int cfgCargoSpace = 80;
        private float cfgPrice = 1500f;
        private bool cfgGrantFuel = true;      // 装上油箱时立刻给满这箱油
        private bool cfgCombatCargo = true;    // 走 BattleHold 战斗货仓（与雷达同质）
        private bool cfgDebug = false;
        private string cfgTestSaveName = "";

        // ---------------- 状态 ----------------
        private float _injectT = 0.5f;
        private float _tickT = 0.25f;
        private bool _marked;
        private string _airportDiagSig;

        private bool _linked;
        private int _pcId;
        private FuelLink _link;
        private float _lastLogT;

        public int TankCount { get; private set; }
        public float FuelBonus { get { return TankCount * cfgFuelPerTank; } }
        public CargoType TankCargo { get { return _tank; } }

        public void Init(IMachineApi api)
        {
            _api = api;
            Live = this;
            DropTankApi.Bind(this);
            LoadConfig();
            CreateCargo();
            SceneManager.sceneLoaded += OnSceneLoaded;
            _api.Log("DropTank: ready (" + cfgCargoName + " +" + cfgFuelPerTank.ToString("F0")
                     + " fuel each, w=" + cfgWeight.ToString("F1") + " size=" + cfgCargoSpace
                     + " price=" + cfgPrice.ToString("F0") + ")");
        }

        private void OnDestroy()
        {
            if (Live == this) Live = null;
        }

        // ---------------- 配置 ----------------

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(Path.Combine(_api.GetModsDirectory(), "DropTank"), "droptank_config.json");
                if (!File.Exists(path)) { _api.Log("DropTank: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("DropTank: bad config json"); return; }
                cfgCargoName = root.GetString("cargoName", cfgCargoName);
                cfgFuelPerTank = (float)root.GetNumber("fuelPerTank", cfgFuelPerTank);
                cfgWeight = (float)root.GetNumber("weight", cfgWeight);
                cfgCargoSpace = (int)root.GetNumber("cargoSpace", cfgCargoSpace);
                cfgPrice = (float)root.GetNumber("price", cfgPrice);
                cfgGrantFuel = root.GetBool("grantFuelWhenLoaded", cfgGrantFuel);
                cfgCombatCargo = root.GetBool("combatCargo", cfgCombatCargo);
                cfgDebug = root.GetBool("debug", cfgDebug);
                cfgTestSaveName = root.GetString("testSaveName", cfgTestSaveName);
                if (cfgFuelPerTank < 0f) cfgFuelPerTank = 0f;
                if (cfgWeight < 0f) cfgWeight = 0f;
                if (cfgCargoSpace < 1) cfgCargoSpace = 1;
                _api.Log("DropTank: config loaded");
            }
            catch (Exception e) { _api.Log("DropTank: config error " + e.Message); }
        }

        // ---------------- 货物 ----------------

        private void CreateCargo()
        {
            try
            {
                _tank = ScriptableObject.CreateInstance<CargoType>();
                _tank.name = "DropTank";
                _tank.cargoName = cfgCargoName;
                _tank.basePrice = cfgPrice;
                _tank.weight = cfgWeight;
                _tank.cargoSpace = cfgCargoSpace;
                _tank.fragile = false;
                _tank.expires = false;
                _tank.icon = MakeTankIcon();
                UnityEngine.Object.DontDestroyOnLoad(_tank);
                _api.Log("DropTank: cargo created '" + cfgCargoName + "' w=" + cfgWeight
                         + " size=" + cfgCargoSpace + " fuel=+" + cfgFuelPerTank.ToString("F0"));
            }
            catch (Exception e) { _api.Log("DropTank: create cargo failed " + e.Message); }
        }

        /// <summary>代码生成的副油箱图标（细长胶囊 + 中部油带 + 尾翼）。</summary>
        private Texture2D MakeTankIcon()
        {
            Texture2D tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            Color bg = new Color(0.05f, 0.07f, 0.10f, 0.85f);
            Color body = new Color(0.74f, 0.78f, 0.84f, 1f);
            Color edge = new Color(0.16f, 0.20f, 0.26f, 1f);
            Color fuel = new Color(0.95f, 0.70f, 0.22f, 1f);
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    float fx = (x - 31.5f) / 26f;
                    float fy = (y - 31.5f) / 13f;
                    float d = fx * fx + fy * fy;
                    Color c = bg;
                    if (d <= 1f)
                    {
                        c = body;
                        if (d > 0.86f) c = edge;                              // 轮廓
                        else if (Mathf.Abs(fx) < 0.16f) c = fuel;             // 中部油带
                        if (fx < -0.74f) c = edge;                            // 头部锥
                        if (fx > 0.78f && Mathf.Abs(fy) > 0.52f) c = edge;    // 尾翼
                    }
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>把货物塞进每座机场的 cargoType 数组（幂等；场景切换后机场是新建的，所以要重注）。</summary>
        private void InjectAirports()
        {
            if (_tank == null) return;
            try
            {
                AirportManager am = AirportManager.Instance;
                if (am == null || am.airports == null) return;

                int injected = 0;
                for (int i = 0; i < am.airports.Count; i++)
                {
                    Airport ap = am.airports[i];
                    if (ap == null) continue;
                    CargoType[] arr = null;
                    try { arr = ap.cargoType; }
                    catch
                    {
                        FieldInfo f = typeof(Airport).GetField("cargoType",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) arr = f.GetValue(ap) as CargoType[];
                    }
                    if (arr == null) continue;

                    bool has = false;
                    for (int k = 0; k < arr.Length; k++) { if (arr[k] == _tank) { has = true; break; } }
                    if (has) continue;

                    List<CargoType> list = new List<CargoType>(arr);
                    list.Add(_tank);
                    CargoType[] na = list.ToArray();
                    try { ap.cargoType = na; }
                    catch
                    {
                        FieldInfo f = typeof(Airport).GetField("cargoType",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) f.SetValue(ap, na);
                    }
                    injected++;
                }

                string sig = am.airports.Count + "/" + injected;
                if (sig != _airportDiagSig)
                {
                    _airportDiagSig = sig;
                    _api.Log("DropTank: airports=" + am.airports.Count + " injected=" + injected);
                }
            }
            catch (Exception e) { _api.Log("DropTank: inject airports failed " + e.Message); }
        }

        /// <summary>标成战斗货物（与雷达同质 -> 走 BattleHold 战斗货仓）。跨 mod 一律反射，缺了就降级。</summary>
        private void TryMarkCombat()
        {
            if (_marked || _tank == null) return;
            try
            {
                Type t = Type.GetType("Machine.BattleHold.BattleHoldApi, BattleHold");
                if (t == null)
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm == null) continue;
                        try { Type x = asm.GetType("Machine.BattleHold.BattleHoldApi"); if (x != null) { t = x; break; } }
                        catch { }
                    }
                }
                if (t == null) return;   // BattleHold 没装：下次再试（可能还没加载完）
                MethodInfo m = t.GetMethod("MarkCombat", BindingFlags.Public | BindingFlags.Static, null,
                                           new Type[] { typeof(string) }, null);
                if (m == null) { _marked = true; return; }
                m.Invoke(null, new object[] { cfgCargoName });
                _marked = true;
                _api.Log("DropTank: marked as combat cargo via BattleHold");
            }
            catch (Exception e) { _api.Log("DropTank: mark combat failed " + e.Message); }
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            // 场景一换机场对象就是新的，下一拍立刻重注，不用等 2s
            _injectT = 0f;
            _airportDiagSig = null;
        }

        // ---------------- 每帧 ----------------

        private void Update()
        {
            // 自测（flag 文件门控）：必须在 PureMode 守卫之前
            CheckSelfTest();
            // 纯净模式：原版档不注入副油箱
            if (MachineState.PureMode) return;

            _injectT -= Time.unscaledDeltaTime;
            if (_injectT <= 0f)
            {
                _injectT = 2f;
                InjectAirports();
                if (cfgCombatCargo) TryMarkCombat();
            }

            _tickT -= Time.unscaledDeltaTime;
            if (_tickT <= 0f)
            {
                _tickT = 0.25f;
                Tick();
            }
        }

        private void Tick()
        {
            try
            {
                PlaneContainer pc = PlaneContainer.Instance;
                if (pc == null) { _linked = false; return; }
                if (!pc.FlightModeInitialized) { _linked = false; return; }

                int id = pc.GetInstanceID();
                if (!_linked || id != _pcId)
                {
                    _linked = true;
                    _pcId = id;
                    _link = FuelLink.New(pc.fuelCapacity, pc.fuel, pc.refrenceFuelCapacity);
                }

                TankCount = CountTanks();
                _link.Cap = pc.fuelCapacity;
                _link.Fuel = pc.fuel;
                _link.RefCap = pc.refrenceFuelCapacity;

                if (FuelLink.Apply(ref _link, TankCount, cfgFuelPerTank, cfgGrantFuel))
                {
                    pc.fuelCapacity = _link.Cap;
                    pc.fuel = _link.Fuel;
                    pc.refrenceFuelCapacity = _link.RefCap;
                    _api.Log("DropTank: tanks=" + TankCount + " cap=" + _link.Cap.ToString("F1")
                             + " fuel=" + _link.Fuel.ToString("F1") + " ref=" + _link.RefCap.ToString("F1"));
                    _lastLogT = Time.unscaledTime;
                }
                else if (cfgDebug && Time.unscaledTime - _lastLogT > 5f)
                {
                    _lastLogT = Time.unscaledTime;
                    _api.Log("DropTank: tanks=" + TankCount + " cap=" + pc.fuelCapacity.ToString("F1")
                             + " fuel=" + pc.fuel.ToString("F1") + " ref=" + pc.refrenceFuelCapacity.ToString("F1")
                             + " bonus=" + FuelBonus.ToString("F0"));
                }
            }
            catch (Exception e) { _api.Log("DropTank: tick failed " + e.Message); }
        }

        private int CountTanks()
        {
            if (_tank == null) return 0;
            CargoInventory ci = CargoInventory.Instance;
            if (ci == null) return 0;
            try { return ci.GetCargoCount(_tank); }
            catch { return 0; }
        }

        // ---------------- 自测（flag 文件门控） ----------------

        private bool _testChecked;
        private bool _testRunning;
        private string _flagSaveName = "";

        private string TestFlagPath()
        {
            try { return Path.Combine(Path.Combine(_api.GetModsDirectory(), "DropTank"), "_droptank_test.flag"); }
            catch { return ""; }
        }

        private void CheckSelfTest()
        {
            if (_testChecked || _testRunning) return;
            if (_api == null) return;
            string flag = TestFlagPath();
            if (string.IsNullOrEmpty(flag)) { _testChecked = true; return; }
            if (!File.Exists(flag)) { _testChecked = true; return; }   // 正常游玩：一帧即退出
            _testRunning = true;
            try
            {
                string c = File.ReadAllText(flag).Trim();
                if (c.Length > 0) _flagSaveName = c;   // 启动器把存档名写在 flag 内容里
            }
            catch { }
            StartCoroutine(RunSelfTest());
        }

        private IEnumerator RunSelfTest()
        {
            _api.Log("DropTank TEST: start");
            int pass = 0, total = 0;

            // 1) 纯数值状态机（与离线 sim 同一份代码）
            try
            {
                FuelLink s = FuelLink.New(100f, 100f, 100f);

                total++; if (!FuelLink.Apply(ref s, 0, 10f, true) && s.Cap == 100f) pass++;
                else _api.Log("DropTank TEST: A cap=" + s.Cap);

                total++; if (FuelLink.Apply(ref s, 1, 10f, true) && s.Cap == 110f && s.Fuel == 110f && s.RefCap == 110f) pass++;
                else _api.Log("DropTank TEST: B cap=" + s.Cap + " fuel=" + s.Fuel + " ref=" + s.RefCap);

                total++; if (FuelLink.Apply(ref s, 3, 10f, true) && s.Cap == 130f && s.Fuel == 130f) pass++;
                else _api.Log("DropTank TEST: C cap=" + s.Cap + " fuel=" + s.Fuel);

                s.Fuel = 20f;                                        // 烧油（宿主行为）
                total++; if (!FuelLink.Apply(ref s, 3, 10f, true)) pass++;

                total++; if (FuelLink.Apply(ref s, 1, 10f, true) && s.Cap == 110f && s.Fuel == 20f) pass++;
                else _api.Log("DropTank TEST: E cap=" + s.Cap + " fuel=" + s.Fuel);

                s.Cap = 100f; s.RefCap = 100f;                       // 宿主 ActivateFlyMode 重写
                total++; if (FuelLink.Apply(ref s, 1, 10f, true) && s.Cap == 110f && s.RefCap == 110f && s.Fuel == 20f) pass++;
                else _api.Log("DropTank TEST: F cap=" + s.Cap + " ref=" + s.RefCap + " fuel=" + s.Fuel);

                total++; if (FuelLink.Apply(ref s, 0, 10f, true) && s.Cap == 100f && s.RefCap == 100f && s.Fuel == 20f) pass++;
                else _api.Log("DropTank TEST: G cap=" + s.Cap + " fuel=" + s.Fuel);

                FuelLink z = FuelLink.New(100f, 0f, 100f);
                total++; if (FuelLink.Apply(ref z, 1, 10f, true) && z.Cap == 110f && z.Fuel == 10f) pass++;
                else _api.Log("DropTank TEST: H cap=" + z.Cap + " fuel=" + z.Fuel);

                _api.Log("DropTank TEST: logic " + pass + "/" + total);
            }
            catch (Exception e) { _api.Log("DropTank TEST: logic threw " + e.Message); }

            // 2) 实机：需要飞机 + 货仓。主菜单里没有，先按 3.1 配方载入一个存档。
            float waited = 0f;
            while (PlaneContainer.Instance == null && waited < 60f)
            {
                if (waited > 2f && !_loadIssued) { _loadIssued = true; TryLoadSaveAndEnter(); }
                yield return new WaitForSeconds(2f);
                waited += 2f;
            }

            PlaneContainer pc = PlaneContainer.Instance;
            CargoInventory ci = CargoInventory.Instance;
            if (pc == null || ci == null || _tank == null)
            {
                _api.Log("DropTank TEST: live=SKIPPED (no plane/inventory/cargo)");
                FinishSelfTest();
                yield break;
            }

            Dictionary<CargoType, int> dict = RawCargoDict(ci);
            if (dict == null)
            {
                _api.Log("DropTank TEST: live=SKIPPED (cargo dict not reachable)");
                FinishSelfTest();
                yield break;
            }

            int original = TanksOf(ci);
            float cap0 = CapOf(pc), ref0 = RefOf(pc), fuel0 = FuelOf(pc);
            _api.Log("DropTank TEST: base tanks=" + original + " cap=" + cap0.ToString("F1")
                     + " fuel=" + fuel0.ToString("F1") + " ref=" + ref0.ToString("F1"));

            // 直接往货仓字典里塞 2 个（AddCargo 有"必须在机场"的守卫，自测不保证在机场）
            SetCount(dict, original + 2);
            yield return new WaitForSeconds(1.5f);
            float cap2 = CapOf(pc), fuel2 = FuelOf(pc), ref2 = RefOf(pc);
            int n2 = TanksOf(ci);
            bool step2 = n2 == original + 2 && Invariant(cap2, ref2, cap0, ref0, n2);
            _api.Log("DropTank TEST: +2 -> tanks=" + n2 + " cap=" + cap2.ToString("F1")
                     + " fuel=" + fuel2.ToString("F1") + " ref=" + ref2.ToString("F1")
                     + " expect cap=" + (cap0 + n2 * cfgFuelPerTank).ToString("F1")
                     + " step2 ok=" + step2);

            // 卸掉：原版 UI 卸不掉战斗货物（BattleHold 会把它补回来），所以断言"自洽"而不是"归零"
            SetCount(dict, original);
            yield return new WaitForSeconds(1.5f);
            int n3 = TanksOf(ci);
            float cap3 = CapOf(pc), ref3 = RefOf(pc);
            bool step3 = Invariant(cap3, ref3, cap0, ref0, n3);
            _api.Log("DropTank TEST: remove -> tanks=" + n3 + " cap=" + cap3.ToString("F1")
                     + " ref=" + ref3.ToString("F1") + " expect cap="
                     + (cap0 + n3 * cfgFuelPerTank).ToString("F1")
                     + ((n3 > original) ? " (cargo restored by BattleHold)" : "")
                     + " step3 ok=" + step3);

            // 模拟宿主 ActivateFlyMode 重写容量：加成必须自己回来，且不能白送油
            float fuelBefore = FuelOf(pc);
            pc.fuelCapacity = cap0;
            pc.refrenceFuelCapacity = ref0;
            yield return new WaitForSeconds(1.5f);
            int n4 = TanksOf(ci);
            float cap4 = CapOf(pc), ref4 = RefOf(pc), fuel4 = FuelOf(pc);
            bool step4 = Invariant(cap4, ref4, cap0, ref0, n4) && fuel4 <= fuelBefore + 0.01f;
            _api.Log("DropTank TEST: host-reset -> tanks=" + n4 + " cap=" + cap4.ToString("F1")
                     + " ref=" + ref4.ToString("F1") + " fuel=" + fuel4.ToString("F1")
                     + " (was " + fuelBefore.ToString("F1") + ")"
                     + " step4 ok=" + step4);

            _api.Log("DropTank TEST: live ok=" + (step2 && step3 && step4));
            FinishSelfTest();
        }

        /// <summary>自洽判据：容量 = 基线 + 机上油箱数 × 每箱油量。</summary>
        private bool Invariant(float cap, float refCap, float cap0, float ref0, int tanks)
        {
            float d = tanks * cfgFuelPerTank;
            return Mathf.Abs(cap - (cap0 + d)) < 0.05f && Mathf.Abs(refCap - (ref0 + d)) < 0.05f;
        }

        private void SetCount(Dictionary<CargoType, int> dict, int n)
        {
            if (dict == null || _tank == null) return;
            try { if (n > 0) dict[_tank] = n; else dict.Remove(_tank); }
            catch { }
        }

        private static float CapOf(PlaneContainer pc) { try { return pc.fuelCapacity; } catch { return 0f; } }
        private static float FuelOf(PlaneContainer pc) { try { return pc.fuel; } catch { return 0f; } }
        private static float RefOf(PlaneContainer pc) { try { return pc.refrenceFuelCapacity; } catch { return 0f; } }
        private int TanksOf(CargoInventory ci)
        {
            if (_tank == null || ci == null) return 0;
            try { return ci.GetCargoCount(_tank); } catch { return 0; }
        }

        private bool _loadIssued;

        private void FinishSelfTest()
        {
            try
            {
                string flag = TestFlagPath();
                if (!string.IsNullOrEmpty(flag) && File.Exists(flag)) File.Delete(flag);
            }
            catch { }
            _testChecked = true;
            _api.Log("DROPTANK SELFTEST DONE");
        }

        private static Dictionary<CargoType, int> RawCargoDict(CargoInventory ci)
        {
            try
            {
                FieldInfo f = typeof(CargoInventory).GetField("currentCargo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return null;
                return f.GetValue(ci) as Dictionary<CargoType, int>;
            }
            catch { return null; }
        }

        /// <summary>自测用：在主菜单里载入一个 mod 存档（配方同 FactionSystem.TryLoadSaveAndEnter）。</summary>
        private bool TryLoadSaveAndEnter()
        {
            try
            {
                string dir = Path.Combine(Path.Combine(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"), "LocalLow"),
                    Path.Combine("Aviassembly", Path.Combine("Machine_Mod", Path.Combine("Aviassembly", "SaveGames"))));
                if (!Directory.Exists(dir)) { _api.Log("DropTank AUTOLOAD: save dir missing"); return false; }

                string want = !string.IsNullOrEmpty(_flagSaveName) ? _flagSaveName : cfgTestSaveName;
                string chosen = null;
                if (!string.IsNullOrEmpty(want) && File.Exists(Path.Combine(dir, want + ".plane")))
                    chosen = want;
                if (chosen == null)
                {
                    string[] files = Directory.GetFiles(dir, "*.plane");
                    if (files == null || files.Length == 0) { _api.Log("DropTank AUTOLOAD: no .plane saves"); return false; }
                    Array.Sort(files, delegate (string a, string b)
                    { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                    chosen = Path.GetFileNameWithoutExtension(files[0]);
                }

                LoadPanel[] lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0) { _api.Log("DropTank AUTOLOAD: LoadPanel not found"); return false; }
                Type ty = lps[0].GetType();
                MethodInfo sf = ty.GetMethod("SelectFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo ld = ty.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sf == null || ld == null) { _api.Log("DropTank AUTOLOAD: SelectFile/Load missing"); return false; }
                MachineState.ViaModSaves = true;
                MachineState.PureMode = false;
                sf.Invoke(lps[0], new object[] { chosen });
                ld.Invoke(lps[0], null);
                _api.Log("DropTank AUTOLOAD: LoadPanel.Load('" + chosen + "') issued");
                return true;
            }
            catch (Exception e) { _api.Log("DropTank AUTOLOAD failed: " + e.Message); return false; }
        }
    }
}
