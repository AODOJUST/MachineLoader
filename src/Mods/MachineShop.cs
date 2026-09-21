using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Machine.Core;
using Machine.Mod;

namespace MachineShopMod
{
    /// <summary>
    /// Machine自己的装货界面mod入口。
    /// </summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.shop"; } }

        public void OnLoad(IMachineApi api)
        {
            if (MachineShopSystem.Live != null)
            {
                api.Log("MachineShop: runtime already alive, skipping duplicate init");
                return;
            }
            api.Log("MachineShop loading...");
            var go = new GameObject("Machine.Shop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MachineShopSystem>().Init(api);
        }
    }

    /// <summary>
    /// Machine自己的装货界面系统。
    /// 创建独特的装货按钮和窗口，用于装载mod物品（雷达、导弹等）。
    /// 窗口使用白色圆角风格，与游戏UI统一。
    /// </summary>
    public class MachineShopSystem : MonoBehaviour
    {
        public static MachineShopSystem Live;   // 切场景重跑 OnLoad 的防重守卫
        private IMachineApi _api;
        private bool _initialized;

        // UI元素
        private GameObject _shopButton;       // 装货按钮（屏幕下方）
        private GameObject _shopWindow;       // 装货大窗口
        private bool _shopWindowOpen;
        private bool _buttonCreated;

        // 类别
        public enum CargoCategory { All, Radar, AESA, Ammo, Countermeasure, FuelTank }
        private CargoCategory _currentCategory = CargoCategory.All;
        private string[] _categoryNames = { "All", "Radar", "AESA", "Ammo", "Countermeasure", "Fuel Tank" };

        // 可装载物品列表
        private List<ShopItem> _allItems = new List<ShopItem>();
        private List<ShopItem> _filteredItems = new List<ShopItem>();

        // 窗口拖动
        private bool _isDragging;
        private Vector2 _dragOffset;
        private Vector2 _windowPosition = new Vector2(0.5f, 0.5f);

        // 自测（machineshop_config.json）
        private bool cfgAutoTest;
        private bool cfgCaptureShots;
        private float cfgAutoOpenDelay = 30f;
        private string cfgShotDir = "";
        private int _shotIdx;
        private bool _synced;   // 是否已用真实注册的 CargoType 校正过重量/体积

        public void Init(IMachineApi api)
        {
            _api = api;
            Live = this;
            LoadConfig();
            LoadShopItems();
            _api.Log("MachineShop: initialized (autoTest=" + cfgAutoTest + ", shots=" + cfgCaptureShots + ")");
            if (cfgAutoTest) StartCoroutine(SelfTest());
        }

        private string ConfigPath
        {
            get { return Path.Combine(_api.GetModsDirectory(), "MachineShop", "machineshop_config.json"); }
        }

        private void LoadConfig()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) { _api.Log("MachineShop: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("MachineShop: bad config json"); return; }
                cfgAutoTest = root.GetBool("autoTest", cfgAutoTest);
                cfgCaptureShots = root.GetBool("captureScreenshots", cfgCaptureShots);
                cfgAutoOpenDelay = (float)root.GetNumber("autoOpenDelaySec", cfgAutoOpenDelay);
                cfgShotDir = root.GetString("shotDir", cfgShotDir);
            }
            catch (Exception e) { _api.Log("MachineShop: config error " + e.Message); }
        }

        private string ShotDir()
        {
            if (!string.IsNullOrEmpty(cfgShotDir)) return cfgShotDir;
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Machine", "logs", "machineshop_shots");
        }

        private void Shot(string tag)
        {
            if (!cfgCaptureShots) return;
            try
            {
                string dir = ShotDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _shotIdx++;
                string file = string.Format("{0:00}_{1}.png", _shotIdx, tag);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, file));
                _api.Log("MachineShop: shot -> " + Path.Combine(dir, file));
            }
            catch (Exception e) { _api.Log("MachineShop: shot failed " + e.Message); }
        }

        /// <summary>自测：自动开窗 + 几何/渲染诊断 + 截图（用于定位“窗口内没有货品”）。</summary>
        private IEnumerator SelfTest()
        {
            _api.Log("MachineShop SELFTEST: start");
            Shot("00_boot");
            yield return new WaitForSeconds(8f);

            // 自动进游戏（照抄 MachineAAM 自测配方）：
            //   主菜单载入最近的 mod 存档 -> 停机坪出现飞机 -> StartFlyMode -> 等飞行初始化
            //   装货链路与质量探针都需要真实飞机 + CargoInventory，纯主菜单无法验证
            bool flyRequested = false;
            int waited = 0;
            while (waited < 150)
            {
                PlaneContainer pc = null;
                try { pc = PlaneContainer.Instance; } catch { }
                if (pc == null)
                {
                    if (!_saveLoadIssued && TryLoadSaveAndEnter()) _saveLoadIssued = true;
                }
                else if (!pc.FlightModeInitialized)
                {
                    if (!flyRequested)
                    {
                        try
                        {
                            GameManager gm = GameManager.Instance;
                            MethodInfo m = typeof(GameManager).GetMethod("StartFlyMode",
                                new Type[] { typeof(bool), typeof(bool) });
                            if (gm != null && m != null)
                            {
                                m.Invoke(gm, new object[] { false, false });
                                flyRequested = true;
                                _api.Log("MachineShop SELFTEST: StartFlyMode requested");
                            }
                        }
                        catch (Exception e) { _api.Log("MachineShop SELFTEST: StartFlyMode failed " + e.Message); }
                    }
                }
                else break; // 飞行模式就绪
                yield return new WaitForSeconds(2f);
                waited += 2;
                if (waited % 20 == 0) _api.Log("MachineShop SELFTEST: waiting for flight... " + waited + "s");
            }

            bool flightReady = false;
            try { flightReady = PlaneContainer.Instance != null && PlaneContainer.Instance.FlightModeInitialized; } catch { }
            _api.Log("MachineShop SELFTEST: flight ready=" + flightReady);

            // 复现用户流程：逐件装填 MSDM（每件间隔 8s），观察质量分解与滞后增长
            //（yield 不能放在带 catch 的 try 里 —— CS1626，各步骤内部已自行捕获异常）
            ClearCombatViaApi();
            yield return new WaitForSeconds(1f);
            _api.Log("MSDMDiag: start mass0=" + ReadPlaneMass().ToString("F2"));
            for (int i = 0; i < 6; i++)
            {
                LoadItem("MSDM");
                _api.Log("MSDMDiag: load#" + (i + 1) + " mass=" + ReadPlaneMass().ToString("F2"));
                yield return new WaitForSeconds(8f);
                _api.Log("MSDMDiag: settle#" + (i + 1) + " mass=" + ReadPlaneMass().ToString("F2"));
            }
            for (int t = 0; t < 6; t++)
            {
                yield return new WaitForSeconds(10f);
                _api.Log("MSDMDiag: idle" + ((t + 1) * 10) + "s mass=" + ReadPlaneMass().ToString("F2"));
            }
            ClearCombatViaApi();
            _api.Log("MSDMDiag: cleared mass=" + ReadPlaneMass().ToString("F2"));

            // 保证自测看到的是完整列表（期间用户可能手动点过类别抽屉）
            _currentCategory = CargoCategory.All;
            _synced = false;
            if (_shopWindow != null) CloseShopWindow();
            if (_shopWindow == null) OpenShopWindow();
            yield return new WaitForSeconds(1.5f);
            DumpDiag("open+1.5s");
            Shot("shop_open");
            yield return new WaitForSeconds(2.0f);
            Shot("shop_late");

            // 质量诊断实验：用游戏原版 AddCargo 逐件装不同货物，量出游戏真实的质量换算
            float m3 = -1f, mk = -1f;
            try
            {
                float m0 = ReadPlaneMass();
                _api.Log("ShopMassDiag: mass0 = " + m0.ToString("F2"));

                object pl15 = FindCargoTypeByName("PL-15");
                DumpCargoTypeFields(pl15);

                // 基准序列：PL-15 x2 + Radar Mk5 + Gun（各自 weight 差异大，能看出换算规律）
                float mA = GameAddCargoMass(pl15);
                _api.Log("ShopMassDiag: +PL-15#1 (w=9) -> " + mA.ToString("F3") + " delta=" + (mA - m0).ToString("F3"));
                float mB = GameAddCargoMass(pl15);
                _api.Log("ShopMassDiag: +PL-15#2 (w=9) -> " + mB.ToString("F3") + " delta=" + (mB - mA).ToString("F3"));
                object radar5 = FindCargoTypeByName("Radar Mk5");
                float mC = GameAddCargoMass(radar5);
                _api.Log("ShopMassDiag: +RadarMk5 (w=5) -> " + mC.ToString("F3") + " delta=" + (mC - mB).ToString("F3"));
                object gun = FindCargoTypeByName("Gun");
                float mD = GameAddCargoMass(gun);
                _api.Log("ShopMassDiag: +Gun (w=0.025) -> " + mD.ToString("F3") + " delta=" + (mD - mC).ToString("F3"));

                // 游戏原版普通货物（非 mod 注册）也测一件：AirportManager.allCargoTypes 第一个
                try
                {
                    var amType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                        .FirstOrDefault(t => t.Name == "AirportManager");
                    var amInst = UnityEngine.Object.FindFirstObjectByType(amType) as UnityEngine.Object;
                    var allProp = amType.GetField("allCargoTypes");
                    CargoType[] all = allProp != null ? allProp.GetValue(amInst) as CargoType[] : null;
                    if (all != null)
                    {
                        foreach (CargoType t in all)
                        {
                            if (t == null) continue;
                            float me = GameAddCargoMass(t);
                            _api.Log("ShopMassDiag: +vanilla '" + t.cargoName + "' (w=" + t.weight
                                     + ") -> " + me.ToString("F3") + " delta=" + (me - mD).ToString("F3"));
                            break;
                        }
                        string names = "";
                        foreach (CargoType t in all) names += (t != null ? t.cargoName + "(w=" + t.weight + ") " : "null ");
                        _api.Log("ShopMassDiag: vanilla cargos: " + names);
                    }
                }
                catch (Exception e) { _api.Log("ShopMassDiag vanilla probe error " + e.Message); }

                m3 = mD;
            }
            catch (Exception e) { _api.Log("ShopMassDiag error " + e.Message); }

            // 修复验证：战斗货仓 TryAdd 装 8×PL-15 + Radar Mk5，期望总增量 ≈ (8×9+5)/15 ≈ 5.13
            try
            {
                ClearCombatViaApi();
                float v0 = ReadPlaneMass();
                for (int i = 0; i < 8; i++) LoadItem("PL-15");
                LoadItem("Radar Mk5");
                float v1 = ReadPlaneMass();
                _api.Log("ShopMassDiag: TryAdd 8xPL-15+RadarMk5 -> " + v1.ToString("F2")
                         + " total-delta=" + (v1 - v0).ToString("F2") + " (expect ~5.13)");
                ClearCombatViaApi();
                float v2 = ReadPlaneMass();
                _api.Log("ShopMassDiag: after final ClearCombat -> " + v2.ToString("F2")
                         + " delta=" + (v2 - v1).ToString("F2"));
            }
            catch (Exception e) { _api.Log("ShopMassDiag verify error " + e.Message); }

            // 观察游戏是否事后改写质量（yield 在 try 外）
            for (int t = 0; t < 3; t++)
            {
                yield return new WaitForSeconds(2f);
                _api.Log("ShopMassDiag: +" + (2 * (t + 1)) + "s mass=" + ReadPlaneMass().ToString("F2"));
            }
            _api.Log("MachineShop SELFTEST: done");
        }

        /// <summary>反射 dump 一个 CargoType 的全部字段与属性（定位游戏真正使用的质量字段）。</summary>
        private void DumpCargoTypeFields(object ct)
        {
            try
            {
                if (ct == null) { _api.Log("CargoTypeDump: null"); return; }
                var ty = ct.GetType();
                var sb = new System.Text.StringBuilder("CargoTypeDump [" + ty.Name + "]: ");
                foreach (var f in ty.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object v = null;
                    try { v = f.GetValue(ct); } catch { }
                    sb.Append(f.Name).Append('=').Append(v == null ? "null" : v.ToString()).Append("; ");
                }
                foreach (var p in ty.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v = null;
                    try { v = p.GetValue(ct, null); } catch { }
                    sb.Append('[').Append(p.Name).Append('=').Append(v == null ? "null" : v.ToString()).Append("]; ");
                }
                _api.Log(sb.ToString());
            }
            catch (Exception e) { _api.Log("CargoTypeDump failed " + e.Message); }
        }

        /// <summary>通过游戏原版 CargoInventory.AddCargo 装一件货物，返回装后飞机质量（失败返回 -1）。</summary>
        private float GameAddCargoMass(object cargoType)
        {
            try
            {
                if (cargoType == null) return -1f;
                var ciType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "CargoInventory");
                if (ciType == null) return -1f;
                var ci = UnityEngine.Object.FindFirstObjectByType(ciType) as UnityEngine.Object;
                if (ci == null) return -1f;
                var add = ciType.GetMethod("AddCargo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(CargoType) }, null);
                if (add == null) return -1f;
                add.Invoke(ci, new object[] { (CargoType)cargoType });
                return ReadPlaneMass();
            }
            catch { return -1f; }
        }

        /// <summary>通过 BattleHoldApi.Clear() 清空战斗货仓（诊断用）。</summary>
        private void ClearCombatViaApi()
        {
            try
            {
                var bhApiType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "BattleHoldApi");
                var clear = bhApiType != null ? bhApiType.GetMethod("Clear", Type.EmptyTypes) : null;
                if (clear != null) clear.Invoke(null, null);
            }
            catch (Exception e) { _api.Log("MachineShop: ClearCombatViaApi failed " + e.Message); }
        }

        private bool _saveLoadIssued;

        /// <summary>自测用：在主菜单载入最近的 mod 存档进游戏（配方照抄 MachineAAM.TryLoadSaveAndEnter）。
        /// 这是唯一能在“纯主菜单”状态下自动进游戏的办法（StartFlyMode 在菜单里不生效）。</summary>
        private bool TryLoadSaveAndEnter()
        {
            try
            {
                string dir = Path.Combine(Path.Combine(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData"), "LocalLow"),
                    Path.Combine("Aviassembly", Path.Combine("Machine_Mod", Path.Combine("Aviassembly", "SaveGames"))));
                if (!Directory.Exists(dir)) { _api.Log("MachineShop AUTOLOAD: save dir missing " + dir); return false; }
                string[] files = Directory.GetFiles(dir, "*.plane");
                if (files == null || files.Length == 0) { _api.Log("MachineShop AUTOLOAD: no .plane saves"); return false; }
                System.Array.Sort(files, delegate (string a, string b)
                { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                string chosen = Path.GetFileNameWithoutExtension(files[0]);

                LoadPanel[] lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0) { _api.Log("MachineShop AUTOLOAD: LoadPanel not found (not in main menu?)"); return false; }
                Type ty = lps[0].GetType();
                MethodInfo sf = ty.GetMethod("SelectFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo ld = ty.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sf == null || ld == null) { _api.Log("MachineShop AUTOLOAD: SelectFile/Load missing on " + ty.Name); return false; }
                MachineState.ViaModSaves = true;
                MachineState.PureMode = false;
                sf.Invoke(lps[0], new object[] { chosen });
                ld.Invoke(lps[0], null);
                _api.Log("MachineShop AUTOLOAD: LoadPanel.Load('" + chosen + "') issued");
                return true;
            }
            catch (Exception e) { _api.Log("MachineShop AUTOLOAD failed: " + e.Message); return false; }
        }

        /// <summary>读取当前飞机质量（自测用）。</summary>
        private float ReadPlaneMass()
        {
            try
            {
                PlaneContainer pc = PlaneContainer.Instance;
                if (pc == null) return -1f;
                return pc.GetMass();
            }
            catch { return -1f; }
        }

        /// <summary>把关键节点的运行时几何与渲染状态写进日志（定位“创建了但看不见”）。</summary>
        private void DumpDiag(string tag)
        {
            try
            {
                _api.Log("MachineShop DIAG ==== " + tag + " ====");
                var canvasGo = GameObject.Find("MachineShopCanvas");
                DumpNode("Canvas", canvasGo != null ? canvasGo.transform : null);
                if (_shopWindow != null)
                {
                    DumpNode("Window", _shopWindow.transform);
                    DumpNode("TitleBar", _shopWindow.transform.Find("TitleBar"));
                    DumpNode("CategoryPanel", _shopWindow.transform.Find("CategoryPanel"));
                    var sv = _shopWindow.transform.Find("ItemScrollView");
                    DumpNode("ItemScrollView", sv);
                    if (sv != null)
                    {
                        var vp = sv.Find("Viewport");
                        DumpNode("Viewport", vp);
                        if (vp != null)
                        {
                            var ct = vp.Find("Content");
                            DumpNode("Content", ct);
                            if (ct != null)
                            {
                                DumpNode("Item_0", ct.GetChild(0));
                                DumpNode("Item_1", ct.GetChild(1));
                                DumpNode("Item_0/Name", ct.GetChild(0).Find("Name"));
                            }
                        }
                    }
                    DumpNode("BattleHoldPanel", _shopWindow.transform.Find("BattleHoldPanel"));
                }
                else _api.Log("MachineShop DIAG window is NULL");
            }
            catch (Exception e) { _api.Log("MachineShop DIAG error " + e.Message); }
        }

        private void DumpNode(string label, Transform t)
        {
            if (t == null) { _api.Log("  " + label + " : null/missing"); return; }
            try
            {
                var rt = t as RectTransform;
                string geo;
                if (rt != null)
                {
                    var c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    geo = "size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")
                          + " anchoredPos=" + rt.anchoredPosition.ToString("F0")
                          + " corners=(" + c[0].x.ToString("F0") + "," + c[0].y.ToString("F0") + ")-("
                          + c[2].x.ToString("F0") + "," + c[2].y.ToString("F0") + ")";
                }
                else geo = "no RectTransform";

                var g = t.GetComponent<Graphic>();
                string gr = "";
                if (g != null)
                {
                    var cr = g.canvasRenderer;
                    gr = " graphic=" + g.GetType().Name
                         + " enabled=" + g.enabled
                         + " col=" + g.color.ToString("F2")
                         + " cull=" + (cr != null ? cr.cull.ToString() : "?")
                         + " cAlpha=" + (cr != null ? cr.GetAlpha().ToString("F2") : "?")
                         + " mat=" + (g.material != null && g.material.shader != null ? g.material.shader.name : "NULL");
                }
                var tmp = t.GetComponent<TMPro.TMP_Text>();
                string tx = "";
                if (tmp != null) tx = " TMP font=" + (tmp.font != null ? tmp.font.name : "NULL") + " text='" + tmp.text + "'";

                var mask = t.GetComponent<Mask>();
                string mk = mask != null ? " MASK(show=" + mask.showMaskGraphic + ")" : "";
                var rm = t.GetComponent<RectMask2D>();
                if (rm != null) mk += " RECTMASK2D";

                _api.Log("  " + label + " act=" + t.gameObject.activeInHierarchy + " " + geo + gr + tx + mk);
            }
            catch (Exception e) { _api.Log("  " + label + " dump error " + e.Message); }
        }

        /// <summary>加载可装载的mod物品列表。</summary>
        private void LoadShopItems()
        {
            _allItems.Clear();
            
            // 普通雷达（名称与Radar mod一致）
            _allItems.Add(new ShopItem { name = "Radar Mk1", category = CargoCategory.Radar, weight = 0.1f, size = 15f, description = "Basic radar, range 750m" });
            _allItems.Add(new ShopItem { name = "Radar Mk2", category = CargoCategory.Radar, weight = 0.1f, size = 20f, description = "Tier 2 radar, range 4500m" });
            _allItems.Add(new ShopItem { name = "Radar Mk3", category = CargoCategory.Radar, weight = 0.2f, size = 15f, description = "Tier 3 radar, range 6800m" });
            _allItems.Add(new ShopItem { name = "Radar Mk4", category = CargoCategory.Radar, weight = 1.0f, size = 18f, description = "Tier 4 radar, range 8100m" });
            _allItems.Add(new ShopItem { name = "Radar Mk5", category = CargoCategory.Radar, weight = 2.0f, size = 18f, description = "Tier 5 radar, range 10000m" });
            
            // 有源相控阵雷达
            _allItems.Add(new ShopItem { name = "AESA Mk1", category = CargoCategory.AESA, weight = 2.0f, size = 15f, description = "AESA tier 1, 360 scan 5200m" });
            _allItems.Add(new ShopItem { name = "AESA Mk2", category = CargoCategory.AESA, weight = 5.0f, size = 10f, description = "AESA tier 2, 360 scan 7000m" });
            _allItems.Add(new ShopItem { name = "AESA Mk3", category = CargoCategory.AESA, weight = 8.0f, size = 8f, description = "AESA tier 3, 360 scan 8600m" });
            _allItems.Add(new ShopItem { name = "AESA Mk4", category = CargoCategory.AESA, weight = 12.0f, size = 8f, description = "AESA tier 4, 360 scan 10000m" });
            _allItems.Add(new ShopItem { name = "AESA Mk5", category = CargoCategory.AESA, weight = 50.0f, size = 200f, description = "AESA tier 5, 360 scan 21000m" });

            // 弹药（name 必须与 AAM mod 注册的 CargoType 名严格一致，否则 SyncFromRegisteredCargo 匹配不上）
            // weight/size 只是界面兜底值，开窗时会被 SyncFromRegisteredCargo 用真实 CargoType 覆盖。
            _allItems.Add(new ShopItem { name = "PL-15", category = CargoCategory.Ammo, weight = 9f, size = 45f, description = "PL-15 AAM, 15s, Mach 2.05" });
            _allItems.Add(new ShopItem { name = "PL-10", category = CargoCategory.Ammo, weight = 4f, size = 40f, description = "PL-10 AAM, 10s, Mach 1.80" });
            _allItems.Add(new ShopItem { name = "PL-17", category = CargoCategory.Ammo, weight = 20f, size = 100f, description = "PL-17 AAM, 25s, Mach 1.97" });
            _allItems.Add(new ShopItem { name = "R-77", category = CargoCategory.Ammo, weight = 9f, size = 45f, description = "R-77 AAM, 18s, Mach 1.75" });
            _allItems.Add(new ShopItem { name = "AIM-9X", category = CargoCategory.Ammo, weight = 2f, size = 40f, description = "AIM-9X AAM, 10s, Mach 2.15" });
            _allItems.Add(new ShopItem { name = "Gun", category = CargoCategory.Ammo, weight = 0.025f, size = 1f, description = "Gun rounds, 6.3s, Mach 2.40" });
            _allItems.Add(new ShopItem { name = "MSDM", category = CargoCategory.Ammo, weight = 3f, size = 35f, description = "Anti-missile interceptor, 8s, Mach 2.20" });
            _allItems.Add(new ShopItem { name = "R-37", category = CargoCategory.Ammo, weight = 20f, size = 110f, description = "R-37 AAM, 23s, Mach 2.07" });
            _allItems.Add(new ShopItem { name = "AIM-424", category = CargoCategory.Ammo, weight = 18f, size = 80f, description = "AIM-424 AAM, 18s, Mach 1.89" });

            // 对抗措施（热诱弹；name 必须 = AAM mod 注册的 CargoType 名 "Flare"）
            _allItems.Add(new ShopItem { name = "Flare", category = CargoCategory.Countermeasure, weight = 0.2f, size = 1f, description = "Decoy flare, IR 210, burns out in 12s" });

            // 副油箱（name 必须 = DropTank mod 注册的 CargoType 名 "Drop Tank"；
            // weight/size 只是界面兜底值，开窗时会被 SyncFromRegisteredCargo 用真实 CargoType 覆盖）
            _allItems.Add(new ShopItem { name = "Drop Tank", category = CargoCategory.FuelTank, weight = 3f, size = 80f, description = "External drop tank, +10 fuel per tank" });

            FilterItems();
            _api.Log("MachineShop: loaded " + _allItems.Count + " shop items");
        }

        /// <summary>根据当前类别过滤物品。</summary>
        private void FilterItems()
        {
            _filteredItems.Clear();
            foreach (var item in _allItems)
            {
                if (_currentCategory == CargoCategory.All || item.category == _currentCategory)
                {
                    _filteredItems.Add(item);
                }
            }
        }

        /// <summary>
        /// 用 Radar / MachineAAM 真正注册出来的 CargoType 校正列表里的重量与体积，
        /// 避免界面上显示的数字和实际装载的货物对不上（注册是延后的，所以第一次开窗时才做）。
        /// </summary>
        private void SyncFromRegisteredCargo()
        {
            try
            {
                int hit = 0;
                foreach (var it in _allItems)
                {
                    object ct = FindCargoTypeByName(it.name);
                    if (ct == null) continue;
                    var t = ct.GetType();
                    var wf = t.GetField("weight", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var sf = t.GetField("cargoSpace", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (wf != null && wf.FieldType == typeof(float)) it.weight = (float)wf.GetValue(ct);
                    if (sf != null && sf.FieldType == typeof(float)) it.size = (float)sf.GetValue(ct);
                    hit++;
                }
                _api.Log("MachineShop: synced " + hit + "/" + _allItems.Count + " items with real CargoTypes");
            }
            catch (Exception e) { _api.Log("MachineShop: sync error " + e.Message); }
        }

        void Update()
        {
            // 纯净模式守卫
            if (MachineState.PureMode) return;
            
            // 创建装货按钮（只创建一次）
            if (!_buttonCreated)
            {
                CreateShopButton();
            }
            
            // 更新按钮显示/隐藏
            UpdateShopButton();
            
            // 更新窗口拖动
            UpdateWindowDrag();
        }

        /// <summary>创建装货按钮（屏幕右上角，独立Canvas，最高渲染顺序）。</summary>
        private void CreateShopButton()
        {
            try
            {
                // 创建独立的Canvas，确保渲染顺序最高
                var canvasGo = new GameObject("MachineShopCanvas");
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9999; // 最高渲染顺序
                canvasGo.AddComponent<CanvasScaler>();
                canvasGo.AddComponent<GraphicRaycaster>();
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);

                _shopButton = new GameObject("MachineShopButton");
                _shopButton.transform.SetParent(canvasGo.transform, false);
                var rt = _shopButton.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.85f, 0.9f);
                rt.anchorMax = new Vector2(0.85f, 0.9f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(200f, 60f);
                rt.anchoredPosition = Vector2.zero;

                var img = _shopButton.AddComponent<Image>();
                img.sprite = UiFactory.RoundedSprite();  // 游戏同款圆角
                img.type = Image.Type.Sliced;
                img.color = new Color(1f, 1f, 1f, 0.95f); // 白色背景

                var btn = _shopButton.AddComponent<Button>();
                var colors = btn.colors;
                colors.normalColor = new Color(1f, 1f, 1f, 0.95f);
                colors.highlightedColor = new Color(0.9f, 0.95f, 1f, 1f);
                colors.pressedColor = new Color(0.8f, 0.9f, 1f, 1f);
                btn.colors = colors;
                btn.onClick.AddListener(ToggleShopWindow);

                var textGo = new GameObject("Text");
                textGo.transform.SetParent(_shopButton.transform, false);
                var textRt = textGo.AddComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = Vector2.zero;
                textRt.offsetMax = Vector2.zero;
                var text = textGo.AddComponent<TMPro.TextMeshProUGUI>();
                text.text = "Mod Cargo";
                text.fontSize = 24;
                text.alignment = TMPro.TextAlignmentOptions.Center;
                text.color = new Color(0.1f, 0.1f, 0.1f, 1f); // 黑色文字

                _shopButton.SetActive(true); // 直接显示
                _buttonCreated = true;
                _api.Log("MachineShop: shop button created at top-right, canvas sortingOrder=9999, active=true");
            }
            catch (Exception e) { _api.Log("MachineShop: create button error " + e.Message + "\n" + e.StackTrace); }
        }

        /// <summary>更新装货按钮的显示/隐藏。仿照原版逻辑：仅在飞机停在地面且低速时显示。</summary>
        private void UpdateShopButton()
        {
            if (!_buttonCreated || _shopButton == null) return;
            try
            {
                PlaneContainer pc = null;
                try { pc = PlaneContainer.Instance; } catch { }
                
                bool shouldShow = false;
                if (pc != null && pc.FlightModeInitialized)
                {
                    // 条件1：飞机在地面上
                    bool grounded = false;
                    try { grounded = pc.IsGrounded(); } catch { }
                    
                    // 条件2：飞机速度很低（< 15节，约7.7 m/s）
                    float speed = 0f;
                    try { speed = pc.GetVelocity().magnitude; } catch { }
                    bool lowSpeed = speed < 8f; // 约15节
                    
                    shouldShow = grounded && lowSpeed;
                }
                
                // 如果装货窗口已打开，保持按钮显示（方便玩家关闭窗口）
                if (_shopWindowOpen) shouldShow = true;
                
                if (shouldShow && !_shopButton.activeSelf)
                {
                    _shopButton.SetActive(true);
                }
                else if (!shouldShow && _shopButton.activeSelf)
                {
                    _shopButton.SetActive(false);
                    // 如果按钮隐藏时装货窗口还开着，自动关闭
                    if (_shopWindowOpen) CloseShopWindow();
                }
            }
            catch (Exception e) { _api.Log("MachineShop: UpdateShopButton error " + e.Message); }
        }

        /// <summary>切换装货窗口的打开/关闭。</summary>
        private void ToggleShopWindow()
        {
            if (_shopWindowOpen) CloseShopWindow();
            else OpenShopWindow();
        }

        /// <summary>打开装货大窗口。</summary>
        private void OpenShopWindow()
        {
            if (_shopWindow != null) return;
            try
            {
                // 使用按钮的独立Canvas，确保渲染顺序最高
                var shopCanvas = GameObject.Find("MachineShopCanvas");
                if (shopCanvas == null)
                {
                    _api.Log("MachineShop: ERROR - MachineShopCanvas not found, cannot open window");
                    return;
                }

                _shopWindow = new GameObject("MachineShopWindow");
                _shopWindow.transform.SetParent(shopCanvas.transform, false);
                var winRt = _shopWindow.AddComponent<RectTransform>();
                winRt.anchorMin = _windowPosition;
                winRt.anchorMax = _windowPosition;
                winRt.pivot = new Vector2(0.5f, 0.5f);
                winRt.sizeDelta = new Vector2(700f, 500f);
                winRt.anchoredPosition = Vector2.zero;

                var winImg = _shopWindow.AddComponent<Image>();
                winImg.sprite = UiFactory.RoundedSprite();   // 游戏同款白色圆角
                winImg.type = Image.Type.Sliced;
                winImg.color = new Color(1f, 1f, 1f, 1f); // 完全不透明，防止其他UI透过来
                winImg.raycastTarget = true;

                // 窗口标题栏（可拖动区域）
                var titleBar = new GameObject("TitleBar");
                titleBar.transform.SetParent(_shopWindow.transform, false);
                var titleRt = titleBar.AddComponent<RectTransform>();
                titleRt.anchorMin = new Vector2(0, 1);
                titleRt.anchorMax = new Vector2(1, 1);
                titleRt.pivot = new Vector2(0.5f, 1);
                titleRt.sizeDelta = new Vector2(0, 45f);
                titleRt.anchoredPosition = Vector2.zero;
                var titleImg = titleBar.AddComponent<Image>();
                titleImg.color = new Color(0.95f, 0.97f, 1f, 1f);
                titleImg.raycastTarget = true;

                // 标题文本
                var titleTextGo = new GameObject("Title");
                titleTextGo.transform.SetParent(titleBar.transform, false);
                var titleTextRt = titleTextGo.AddComponent<RectTransform>();
                titleTextRt.anchorMin = new Vector2(0, 0);
                titleTextRt.anchorMax = new Vector2(1, 1);
                titleTextRt.offsetMin = new Vector2(20, 0);
                titleTextRt.offsetMax = new Vector2(-60, 0);
                var titleText = titleTextGo.AddComponent<TMPro.TextMeshProUGUI>();
                titleText.text = "Machine Cargo Warehouse";
                titleText.fontSize = 20;
                titleText.alignment = TMPro.TextAlignmentOptions.Left;
                titleText.color = new Color(0.1f, 0.1f, 0.1f, 1f);

                // 关闭按钮
                var closeButton = new GameObject("CloseButton");
                closeButton.transform.SetParent(titleBar.transform, false);
                var closeRt = closeButton.AddComponent<RectTransform>();
                closeRt.anchorMin = new Vector2(1, 0.5f);
                closeRt.anchorMax = new Vector2(1, 0.5f);
                closeRt.pivot = new Vector2(1, 0.5f);
                closeRt.sizeDelta = new Vector2(35f, 35f);
                closeRt.anchoredPosition = new Vector2(-10, 0);
                var closeImg = closeButton.AddComponent<Image>();
                closeImg.color = new Color(0.9f, 0.3f, 0.3f, 0.9f);
                var closeBtn = closeButton.AddComponent<Button>();
                closeBtn.onClick.AddListener(CloseShopWindow);
                var closeTextGo = new GameObject("Text");
                closeTextGo.transform.SetParent(closeButton.transform, false);
                var closeTextRt = closeTextGo.AddComponent<RectTransform>();
                closeTextRt.anchorMin = Vector2.zero;
                closeTextRt.anchorMax = Vector2.one;
                closeTextRt.offsetMin = Vector2.zero;
                closeTextRt.offsetMax = Vector2.zero;
                var closeText = closeTextGo.AddComponent<TMPro.TextMeshProUGUI>();
                closeText.text = "×";
                closeText.fontSize = 24;
                closeText.alignment = TMPro.TextAlignmentOptions.Center;
                closeText.color = Color.white;

                // 类别抽屉面板（左侧）
                CreateCategoryPanel();

                // 过滤物品（修复：打开窗口前必须先过滤，否则_filteredItems为空）
                if (!_synced) { _synced = true; SyncFromRegisteredCargo(); }
                FilterItems();
                _api.Log("MachineShop: filteredItems=" + _filteredItems.Count + ", allItems=" + _allItems.Count);

                // 物品滚动视图（中间）
                CreateItemScrollView();

                // 战斗仓库信息面板（右上角）
                CreateBattleHoldPanel();

                _shopWindowOpen = true;
                _api.Log("MachineShop: shop window opened, filteredItems=" + _filteredItems.Count + ", allItems=" + _allItems.Count);
            }
            catch (Exception e) { _api.Log("MachineShop: open window error " + e.Message + "\n" + e.StackTrace); }
        }

        /// <summary>创建类别抽屉面板（左侧）。</summary>
        private void CreateCategoryPanel()
        {
            try
            {
                var categoryPanel = new GameObject("CategoryPanel");
                categoryPanel.transform.SetParent(_shopWindow.transform, false);
                var catRt = categoryPanel.AddComponent<RectTransform>();
                catRt.anchorMin = new Vector2(0, 0);
                catRt.anchorMax = new Vector2(0, 1);
                catRt.pivot = new Vector2(0, 1);  // 从顶部开始
                catRt.sizeDelta = new Vector2(140f, -100f);  // 高度减去100，给标题栏留空间
                catRt.anchoredPosition = new Vector2(10f, -55f);  // 从标题栏下方开始

                var catImg = categoryPanel.AddComponent<Image>();
                catImg.color = new Color(0.97f, 0.98f, 1f, 0.8f);

                for (int i = 0; i < _categoryNames.Length; i++)
                {
                    var catBtn = new GameObject("CatBtn_" + i);
                    catBtn.transform.SetParent(categoryPanel.transform, false);
                    var btnRt = catBtn.AddComponent<RectTransform>();
                    btnRt.anchorMin = new Vector2(0.5f, 1);
                    btnRt.anchorMax = new Vector2(0.5f, 1);
                    btnRt.pivot = new Vector2(0.5f, 1);
                    btnRt.sizeDelta = new Vector2(120f, 40f);
                    btnRt.anchoredPosition = new Vector2(0, -10f - i * 45f);

                    var btnImg = catBtn.AddComponent<Image>();
                    btnImg.color = (i == (int)_currentCategory) ? new Color(0.8f, 0.9f, 1f, 1f) : new Color(1f, 1f, 1f, 0.9f);

                    var btn = catBtn.AddComponent<Button>();
                    int catIndex = i;
                    btn.onClick.AddListener(() => SelectCategory(catIndex));

                    var btnTextGo = new GameObject("Text");
                    btnTextGo.transform.SetParent(catBtn.transform, false);
                    var btnTextRt = btnTextGo.AddComponent<RectTransform>();
                    btnTextRt.anchorMin = Vector2.zero;
                    btnTextRt.anchorMax = Vector2.one;
                    btnTextRt.offsetMin = Vector2.zero;
                    btnTextRt.offsetMax = Vector2.zero;
                    var btnText = btnTextGo.AddComponent<TMPro.TextMeshProUGUI>();
                    btnText.text = _categoryNames[i];
                    btnText.fontSize = 16;
                    btnText.alignment = TMPro.TextAlignmentOptions.Center;
                    btnText.color = new Color(0.1f, 0.1f, 0.1f, 1f);
                }
            }
            catch (Exception e) { _api.Log("MachineShop: create category panel error " + e.Message); }
        }

        /// <summary>选择类别。不关闭重开窗口，只刷新物品列表，避免窗口位置重置。</summary>
        private void SelectCategory(int index)
        {
            _currentCategory = (CargoCategory)index;
            FilterItems();
            // 只刷新物品滚动视图，不关闭重开窗口
            RefreshItemScrollView();
            // 刷新类别按钮的选中状态
            RefreshCategoryButtons();
        }

        /// <summary>刷新物品滚动视图（不重建整个窗口）。</summary>
        private void RefreshItemScrollView()
        {
            try
            {
                if (_shopWindow == null) return;
                var oldScroll = _shopWindow.transform.Find("ItemScrollView");
                if (oldScroll != null) GameObject.Destroy(oldScroll.gameObject);
                CreateItemScrollView();
            }
            catch (Exception e) { _api.Log("MachineShop: refresh item scroll error " + e.Message); }
        }

        /// <summary>刷新类别按钮的选中状态。</summary>
        private void RefreshCategoryButtons()
        {
            try
            {
                if (_shopWindow == null) return;
                var catPanel = _shopWindow.transform.Find("CategoryPanel");
                if (catPanel == null) return;
                for (int i = 0; i < catPanel.childCount; i++)
                {
                    var btn = catPanel.GetChild(i);
                    var img = btn.GetComponent<Image>();
                    if (img != null)
                    {
                        img.color = (i == (int)_currentCategory)
                            ? new Color(0.8f, 0.9f, 1f, 1f)
                            : new Color(1f, 1f, 1f, 0.9f);
                    }
                }
            }
            catch (Exception e) { _api.Log("MachineShop: refresh category buttons error " + e.Message); }
        }

        /// <summary>创建物品滚动视图（中间区域）。</summary>
        private void CreateItemScrollView()
        {
            try
            {
                var itemScrollView = new GameObject("ItemScrollView");
                itemScrollView.transform.SetParent(_shopWindow.transform, false);
                var scrollRt = itemScrollView.AddComponent<RectTransform>();
                scrollRt.anchorMin = new Vector2(0, 0);
                scrollRt.anchorMax = new Vector2(1, 1);
                scrollRt.pivot = new Vector2(0.5f, 0.5f);
                scrollRt.sizeDelta = new Vector2(-170f, -155f);  // 左160给类别面板，右10边距；上55给标题栏，下100给战斗仓库
                scrollRt.anchoredPosition = new Vector2(75f, 22.5f);  // 中心点偏移

                var scrollImg = itemScrollView.AddComponent<Image>();
                scrollImg.color = new Color(0.98f, 0.99f, 1f, 1f); // 不透明背景，防止其他UI透过来

                var scrollRect = itemScrollView.AddComponent<ScrollRect>();
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.scrollSensitivity = 50f;

                // 视口
                var viewport = new GameObject("Viewport");
                viewport.transform.SetParent(itemScrollView.transform, false);
                var viewRt = viewport.AddComponent<RectTransform>();
                viewRt.anchorMin = Vector2.zero;
                viewRt.anchorMax = Vector2.one;
                viewRt.offsetMin = new Vector2(10, 10);
                viewRt.offsetMax = new Vector2(-10, -10);
                // ⚠ 关键修复（2026-09-12）：视口必须用 RectMask2D 裁剪。
                // 原来这里用的是 Mask（模板缓冲）+ 一个 alpha=0 的 Image，
                // 结果整个视口内的子物体被 100% 裁掉：Item 的 size/anchoredPosition/
                // 颜色/alpha/font/cull 全部正常，但一个像素都不画 —— 这就是
                // “窗口能打开、里面却没有货品”的真正原因（和货物数据无关，所以从数据侧怎么查都查不出来）。
                // RectMask2D 按矩形裁剪、不依赖模板缓冲，也是本工程其它滚动列表（UI.cs / OnlineUI.cs）的做法。
                viewport.AddComponent<RectMask2D>();
                scrollRect.viewport = viewRt;

                // 内容
                var content = new GameObject("Content");
                content.transform.SetParent(viewport.transform, false);
                var contentRt = content.AddComponent<RectTransform>();
                contentRt.anchorMin = new Vector2(0, 1);
                contentRt.anchorMax = new Vector2(1, 1);
                contentRt.pivot = new Vector2(0.5f, 1);
                contentRt.sizeDelta = new Vector2(0, _filteredItems.Count * 70f + 20f);
                contentRt.anchoredPosition = Vector2.zero;
                scrollRect.content = contentRt;

                // 物品按钮
                _api.Log("MachineShop: creating " + _filteredItems.Count + " item buttons");
                for (int i = 0; i < _filteredItems.Count; i++)
                {
                    var item = _filteredItems[i];
                    var itemBtn = new GameObject("Item_" + i + "_" + item.name);
                    itemBtn.transform.SetParent(content.transform, false);
                    var itemRt = itemBtn.AddComponent<RectTransform>();
                    itemRt.anchorMin = new Vector2(0, 1); // 修复：从(0.5,1)改为(0,1)
                    itemRt.anchorMax = new Vector2(1, 1); // 修复：从(0.5,1)改为(1,1)
                    itemRt.pivot = new Vector2(0.5f, 1);
                    itemRt.sizeDelta = new Vector2(-20f, 60f); // 现在宽度=content宽度-20，正确！
                    itemRt.anchoredPosition = new Vector2(0, -10f - i * 70f);

                    var itemImg = itemBtn.AddComponent<Image>();
                    itemImg.sprite = UiFactory.RoundedSprite();
                    itemImg.type = Image.Type.Sliced;
                    itemImg.color = new Color(0.93f, 0.96f, 1f, 1f); // 淡蓝卡片，与纯白窗底拉开对比
                    itemImg.raycastTarget = true;

                    var btn = itemBtn.AddComponent<Button>();
                    btn.targetGraphic = itemImg;
                    string itemName = item.name;
                    btn.onClick.AddListener(() => LoadItem(itemName));

                    // 物品名称
                    var nameGo = new GameObject("Name");
                    nameGo.transform.SetParent(itemBtn.transform, false);
                    var nameRt = nameGo.AddComponent<RectTransform>();
                    nameRt.anchorMin = new Vector2(0, 1);
                    nameRt.anchorMax = new Vector2(1, 1);
                    nameRt.pivot = new Vector2(0, 1);
                    nameRt.sizeDelta = new Vector2(-100f, 25f);
                    nameRt.anchoredPosition = new Vector2(15f, -5f);
                    var nameText = nameGo.AddComponent<TMPro.TextMeshProUGUI>();
                    nameText.text = item.name;
                    nameText.fontSize = 16;
                    nameText.alignment = TMPro.TextAlignmentOptions.Left;
                    nameText.color = new Color(0.1f, 0.1f, 0.1f, 1f);

                    // 物品描述
                    var descGo = new GameObject("Desc");
                    descGo.transform.SetParent(itemBtn.transform, false);
                    var descRt = descGo.AddComponent<RectTransform>();
                    descRt.anchorMin = new Vector2(0, 0);
                    descRt.anchorMax = new Vector2(1, 0.5f);
                    descRt.pivot = new Vector2(0, 0);
                    descRt.sizeDelta = new Vector2(-100f, 0);
                    descRt.anchoredPosition = new Vector2(15f, 5f);
                    var descText = descGo.AddComponent<TMPro.TextMeshProUGUI>();
                    descText.text = item.description;
                    descText.fontSize = 12;
                    descText.alignment = TMPro.TextAlignmentOptions.Left;
                    descText.color = new Color(0.4f, 0.4f, 0.4f, 1f);

                    // 重量和大小
                    var statGo = new GameObject("Stats");
                    statGo.transform.SetParent(itemBtn.transform, false);
                    var statRt = statGo.AddComponent<RectTransform>();
                    statRt.anchorMin = new Vector2(1, 0.5f);
                    statRt.anchorMax = new Vector2(1, 0.5f);
                    statRt.pivot = new Vector2(1, 0.5f);
                    statRt.sizeDelta = new Vector2(90f, 40f);
                    statRt.anchoredPosition = new Vector2(-10f, 0);
                    var statText = statGo.AddComponent<TMPro.TextMeshProUGUI>();
                    statText.text = "W:" + item.weight + "\nS:" + item.size;
                    statText.fontSize = 11;
                    statText.alignment = TMPro.TextAlignmentOptions.Right;
                    statText.color = new Color(0.3f, 0.3f, 0.3f, 1f);
                }
            }
            catch (Exception e) { _api.Log("MachineShop: create item scroll view error " + e.Message); }
        }

        /// <summary>创建战斗仓库信息面板（右上角）。</summary>
        private void CreateBattleHoldPanel()
        {
            try
            {
                var battleHoldPanel = new GameObject("BattleHoldPanel");
                battleHoldPanel.transform.SetParent(_shopWindow.transform, false);
                var bhRt = battleHoldPanel.AddComponent<RectTransform>();
                bhRt.anchorMin = new Vector2(0, 0);
                bhRt.anchorMax = new Vector2(1, 0);
                bhRt.pivot = new Vector2(0.5f, 0);  // 底部居中
                bhRt.sizeDelta = new Vector2(-20f, 80f);  // 宽度=窗口宽度-20，高度80
                bhRt.anchoredPosition = new Vector2(0f, 10f);  // 底部偏移10

                var bhImg = battleHoldPanel.AddComponent<Image>();
                bhImg.color = new Color(0.95f, 0.97f, 1f, 0.9f);

                // 标题
                var titleGo = new GameObject("Title");
                titleGo.transform.SetParent(battleHoldPanel.transform, false);
                var titleRt = titleGo.AddComponent<RectTransform>();
                titleRt.anchorMin = new Vector2(0, 1);
                titleRt.anchorMax = new Vector2(1, 1);
                titleRt.pivot = new Vector2(0.5f, 1);
                titleRt.sizeDelta = new Vector2(0, 30f);
                titleRt.anchoredPosition = Vector2.zero;
                var titleText = titleGo.AddComponent<TMPro.TextMeshProUGUI>();
                titleText.text = "Battle Hold";
                titleText.fontSize = 16;
                titleText.alignment = TMPro.TextAlignmentOptions.Center;
                titleText.color = new Color(0.1f, 0.1f, 0.1f, 1f);

                // 弹药信息
                var ammoGo = new GameObject("AmmoInfo");
                ammoGo.transform.SetParent(battleHoldPanel.transform, false);
                var ammoRt = ammoGo.AddComponent<RectTransform>();
                ammoRt.anchorMin = new Vector2(0, 0);
                ammoRt.anchorMax = new Vector2(1, 0.85f);
                ammoRt.pivot = new Vector2(0.5f, 0.5f);
                ammoRt.sizeDelta = new Vector2(-20f, -10f);
                ammoRt.anchoredPosition = Vector2.zero;
                var ammoText = ammoGo.AddComponent<TMPro.TextMeshProUGUI>();
                ammoText.text = GetBattleHoldInfo();
                ammoText.fontSize = 13;
                ammoText.alignment = TMPro.TextAlignmentOptions.Left;
                ammoText.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            }
            catch (Exception e) { _api.Log("MachineShop: create battle hold panel error " + e.Message); }
        }

        /// <summary>获取战斗仓库信息。</summary>
        private string GetBattleHoldInfo()
        {
            try
            {
                // 查找BattleHoldApi类型（命名空间可能是Machine.BattleHold或BattleHoldMod）
                var bhType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "BattleHoldApi" || 
                                         (t.Name == "BattleHoldApi" && t.Namespace != null) ||
                                         t.FullName == "Machine.BattleHold.BattleHoldApi" ||
                                         t.FullName == "BattleHoldMod.BattleHoldApi");
                if (bhType == null)
                {
                    // 尝试查找BattleHoldSystem或Main类
                    var sysType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                        .FirstOrDefault(t => t.Name == "BattleHoldSystem" || t.FullName == "Machine.BattleHold.BattleHoldSystem");
                    if (sysType == null) return "Battle Hold\nNot installed";
                    
                    // 检查是否有实例
                    var instance = UnityEngine.Object.FindObjectOfType(sysType);
                    if (instance == null) return "Battle Hold\nNot active";
                    return "Battle Hold\nInstalled\n(active)";
                }
                
                // 检查BattleHoldApi.Available属性
                var availProp = bhType.GetProperty("Available", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (availProp != null)
                {
                    bool available = (bool)availProp.GetValue(null, null);
                    if (!available) return "Battle Hold\nNot available";
                }
                
                // 获取战斗仓库快照
                var snapshotMethod = bhType.GetMethod("Snapshot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (snapshotMethod != null)
                {
                    var snapshot = snapshotMethod.Invoke(null, null) as System.Collections.IEnumerable;
                    if (snapshot != null)
                    {
                        string info = "Battle Hold\n";
                        int count = 0;
                        foreach (var kv in snapshot)
                        {
                            if (kv == null) continue;
                            var keyProp = kv.GetType().GetProperty("Key");
                            var valProp = kv.GetType().GetProperty("Value");
                            if (keyProp != null && valProp != null)
                            {
                                var key = keyProp.GetValue(kv, null);
                                var val = valProp.GetValue(kv, null);
                                if (key != null)
                                {
                                    var nameField = key.GetType().GetField("cargoName");
                                    string name = nameField != null ? (nameField.GetValue(key) as string) : key.ToString();
                                    info += name + ": " + val + "\n";
                                    count++;
                                }
                            }
                        }
                        if (count == 0) info += "Empty";
                        return info;
                    }
                }
                
                return "Battle Hold\nInstalled\n(reading...)";
            }
            catch (Exception e) { 
                _api.Log("MachineShop: GetBattleHoldInfo error " + e.Message);
                return "Battle Hold\nRead error"; 
            }
        }

        /// <summary>装载物品到飞机。</summary>
        private void LoadItem(string itemName)
        {
            _api.Log("MachineShop: loading item " + itemName);
            try
            {
                // 1. 查找CargoType
                object cargoType = FindCargoTypeByName(itemName);
                if (cargoType == null)
                {
                    _api.Log("MachineShop: ERROR - CargoType '" + itemName + "' not found");
                    return;
                }
                _api.Log("MachineShop: CargoType found: " + itemName);

                // 2. 通过反射调用BattleHoldApi.Add方法
                var bhApiType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.FullName == "BattleHoldMod.BattleHoldApi" || t.Name == "BattleHoldApi");
                if (bhApiType == null)
                {
                    _api.Log("MachineShop: ERROR - BattleHoldApi not found");
                    return;
                }

                var addMethod = bhApiType.GetMethod("Add", new Type[] { cargoType.GetType(), typeof(int) });
                if (addMethod == null)
                {
                    // 尝试查找泛型方法或其他签名
                    var methods = bhApiType.GetMethods();
                    foreach (var m in methods)
                    {
                        if (m.Name == "Add" && m.GetParameters().Length == 2)
                        {
                            addMethod = m;
                            break;
                        }
                    }
                }
                if (addMethod == null)
                {
                    _api.Log("MachineShop: ERROR - BattleHoldApi.Add method not found");
                    return;
                }

                // 3. 调用Add方法，添加货物
                // 红外干扰弹（Flare）单次装2个，其他物品单次装1个
                int loadCount = (itemName == "Flare") ? 2 : 1;
                object result = addMethod.Invoke(null, new object[] { cargoType, loadCount });
                _api.Log("MachineShop: BattleHoldApi.Add result=" + result + ", item=" + itemName + ", count=" + loadCount);
            }
            catch (Exception e)
            {
                _api.Log("MachineShop: LoadItem error " + e.Message + "\n" + e.StackTrace);
            }
        }

        /// <summary>通过名称查找CargoType（包括游戏原版和mod注册的）。</summary>
        private object FindCargoTypeByName(string name)
        {
            try
            {
                // 方法0（最可靠，2026-09-12 加）：直接扫描所有已创建的 CargoType 实例。
                // Radar / MachineAAM 都是 ScriptableObject.CreateInstance<CargoType>() + DontDestroyOnLoad
                // 注册货物的，所以无论它是哪个 mod 注册的（雷达、AESA、导弹、机炮、拦截弹），
                // 这里都能一次拿到；不再依赖某几个 mod 内部私有字段的名字。
                var all = Resources.FindObjectsOfTypeAll(typeof(CargoType));
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var ct = all[i] as CargoType;
                        if (ct != null && ct.cargoName == name) return ct;
                    }
                }
            }
            catch (Exception e) { _api.Log("MachineShop: cargo scan error " + e.Message); }

            try
            {
                // 方法1: 查找AssetManager.allCargoTypes
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm != null)
                {
                    var amType = asm.GetType("AssetManager");
                    if (amType != null)
                    {
                        var instanceProp = amType.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (instanceProp != null)
                        {
                            var amInstance = instanceProp.GetValue(null, null);
                            if (amInstance != null)
                            {
                                var allCargoField = amType.GetField("allCargoTypes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (allCargoField != null)
                                {
                                    var arr = allCargoField.GetValue(amInstance) as System.Collections.IEnumerable;
                                    if (arr != null)
                                    {
                                        foreach (var ct in arr)
                                        {
                                            if (ct == null) continue;
                                            var cnProp = ct.GetType().GetField("cargoName");
                                            if (cnProp != null)
                                            {
                                                string cn = cnProp.GetValue(ct) as string;
                                                if (cn == name) return ct;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 方法2: 通过反射查找Radar mod中的_radarTypes列表
                var radarSysType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "RadarSystem" || (t.Namespace == "RadarMod" && t.Name == "Main"));
                if (radarSysType != null)
                {
                    var radarTypesField = radarSysType.GetField("_radarTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (radarTypesField != null)
                    {
                        var radarInstance = UnityEngine.Object.FindObjectOfType(radarSysType);
                        if (radarInstance != null)
                        {
                            var list = radarTypesField.GetValue(radarInstance) as System.Collections.IEnumerable;
                            if (list != null)
                            {
                                foreach (var ct in list)
                                {
                                    if (ct == null) continue;
                                    var cnProp = ct.GetType().GetField("cargoName");
                                    if (cnProp != null)
                                    {
                                        string cn = cnProp.GetValue(ct) as string;
                                        if (cn == name) return ct;
                                    }
                                }
                            }
                        }
                    }
                }

                // 方法3: 通过反射查找AAM mod中的导弹CargoType列表
                var aamSysType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "AAMSystem" || (t.Namespace == "MachineAAMMod" && t.Name == "Main"));
                if (aamSysType != null)
                {
                    // 尝试查找_missileTypes或类似字段
                    var fields = aamSysType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    foreach (var f in fields)
                    {
                        if (f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Length > 0)
                        {
                            var elemType = f.FieldType.GetGenericArguments()[0];
                            if (elemType.Name == "CargoType" || elemType.FullName == "CargoType")
                            {
                                var aamInstance = UnityEngine.Object.FindObjectOfType(aamSysType);
                                if (aamInstance != null)
                                {
                                    var list = f.GetValue(aamInstance) as System.Collections.IEnumerable;
                                    if (list != null)
                                    {
                                        foreach (var ct in list)
                                        {
                                            if (ct == null) continue;
                                            var cnProp = ct.GetType().GetField("cargoName");
                                            if (cnProp != null)
                                            {
                                                string cn = cnProp.GetValue(ct) as string;
                                                if (cn == name) return ct;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _api.Log("MachineShop: FindCargoTypeByName error " + e.Message);
            }
            return null;
        }

        /// <summary>关闭装货窗口。</summary>
        private void CloseShopWindow()
        {
            if (_shopWindow != null)
            {
                var rt = _shopWindow.GetComponent<RectTransform>();
                if (rt != null) _windowPosition = rt.anchorMin;
                UnityEngine.Object.Destroy(_shopWindow);
                _shopWindow = null;
            }
            _shopWindowOpen = false;
            _api.Log("MachineShop: shop window closed");
        }

        /// <summary>更新窗口拖动逻辑。</summary>
        private void UpdateWindowDrag()
        {
            if (!_shopWindowOpen || _shopWindow == null) return;
            try
            {
                if (Input.GetMouseButtonDown(0))
                {
                    var titleBar = _shopWindow.transform.Find("TitleBar");
                    if (titleBar != null)
                    {
                        var rt = titleBar.GetComponent<RectTransform>();
                        if (RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition))
                        {
                            _isDragging = true;
                            var winRt = _shopWindow.GetComponent<RectTransform>();
                            _dragOffset = new Vector2(Input.mousePosition.x, Input.mousePosition.y) - 
                                new Vector2(winRt.position.x, winRt.position.y);
                        }
                    }
                }
                if (Input.GetMouseButtonUp(0))
                {
                    _isDragging = false;
                }
                if (_isDragging)
                {
                    var winRt = _shopWindow.GetComponent<RectTransform>();
                    Vector2 newPos = new Vector2(Input.mousePosition.x, Input.mousePosition.y) - _dragOffset;
                    winRt.position = new Vector3(newPos.x, newPos.y, winRt.position.z);
                }
            }
            catch { }
        }
    }

    /// <summary>商店物品数据结构。</summary>
    public class ShopItem
    {
        public string name;
        public MachineShopSystem.CargoCategory category;
        public float weight;
        public float size;
        public string description;
    }
}
