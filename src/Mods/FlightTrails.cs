using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Machine.Mod;
using Machine.Core;

namespace FlightTrailsMod
{
    /// <summary>航迹显示 Mod：地图上以虚线显示最近两段飞行的航迹，颜色随速度蓝->红（1.2马赫全红），悬停变实线加方向箭头。机场/基地位置在地图标注。</summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.flighttrails"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("FlightTrails loading...");
            var go = new GameObject("Machine.FlightTrails");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FlightTrailSystem>().Init(api);
        }
    }

    public class TrailConfig
    {
        public float maxSpeed = 793f;         // 颜色归一上限（节 knots），1.2 马赫 ≈ 793 knots
        public float saturation = 0.38f;      // 航迹饱和度（低）
        public float brightness = 0.85f;      // 航迹明度
        public float arrowSaturation = 0.20f; // 悬停箭头饱和度（更低）
        public int lineWidth = 9;             // 航迹线宽（纹理像素）
        public int hoverWidth = 14;           // 悬停加粗宽度
        public bool autoColor = true;         // true=速度配色, false=固定色
        public float fixedR = 0.35f, fixedG = 0.55f, fixedB = 0.85f; // 固定色
        public int keepSegments = 2;          // 保留最近几段飞行
        public float sampleInterval = 0.6f;   // 航迹采样间隔（秒）
        public float baseClearRadius = 250f;  // 判定回到基地/机场的半径（m）
        public bool showAirportMarks = true;  // 地图上标注机场与基地
    }

    public class TrailPoint
    {
        public Vector3 World;
        public float Speed;
        public TrailPoint(Vector3 w, float s) { World = w; Speed = s; }
    }

    public class FlightSegment
    {
        public List<TrailPoint> Points = new List<TrailPoint>();
    }

    public class FlightTrailSystem : MonoBehaviour
    {
        private IMachineApi _api;
        private TrailConfig _cfg = new TrailConfig();
        private List<FlightSegment> _segments = new List<FlightSegment>();
        private FlightSegment _current;
        private bool _airborne;
        private float _sampleTimer;
        private float _redrawTimer;
        private PlaneContainer _plane;
        private PlaneContainer _lastPlaneRef;
        private Map _map;
        private AirportManager _airportMgr;
        private Image _trailImage;
        private Texture2D _trailTex;
        private int _texSize = 1024;
        private int _hovered = -1;
        private bool _uiBuilt;
        private int _diagCount;
        private GameObject _clearBtn;
        private float _airportScanTimer;
        private List<Vector3> _airportPositions = new List<Vector3>();
        private List<string> _airportNames = new List<string>();
        private Vector3 _basePosition;
        private bool _baseKnown;
        private float _lastAirportDist = float.MaxValue;

        public void Init(IMachineApi api)
        {
            _api = api;
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                string dir = Path.Combine(_api.GetModsDirectory(), "FlightTrails");
                string path = Path.Combine(dir, "trails_config.json");
                if (!File.Exists(path)) { _api.Log("FlightTrails: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("FlightTrails: bad config json"); return; }
                _cfg.maxSpeed = (float)root.GetNumber("maxSpeed", _cfg.maxSpeed);
                _cfg.saturation = (float)root.GetNumber("saturation", _cfg.saturation);
                _cfg.brightness = (float)root.GetNumber("brightness", _cfg.brightness);
                _cfg.arrowSaturation = (float)root.GetNumber("arrowSaturation", _cfg.arrowSaturation);
                _cfg.lineWidth = (int)root.GetNumber("lineWidth", _cfg.lineWidth);
                _cfg.hoverWidth = (int)root.GetNumber("hoverWidth", _cfg.hoverWidth);
                _cfg.autoColor = root.GetBool("autoColor", _cfg.autoColor);
                _cfg.fixedR = (float)root.GetNumber("fixedColorR", _cfg.fixedR);
                _cfg.fixedG = (float)root.GetNumber("fixedColorG", _cfg.fixedG);
                _cfg.fixedB = (float)root.GetNumber("fixedColorB", _cfg.fixedB);
                _cfg.keepSegments = Mathf.Clamp((int)root.GetNumber("keepSegments", _cfg.keepSegments), 1, 8);
                _cfg.sampleInterval = (float)root.GetNumber("sampleInterval", _cfg.sampleInterval);
                _cfg.baseClearRadius = (float)root.GetNumber("baseClearRadius", _cfg.baseClearRadius);
                _cfg.showAirportMarks = root.GetBool("showAirportMarks", _cfg.showAirportMarks);
                if (_cfg.sampleInterval < 0.2f) _cfg.sampleInterval = 0.2f;
                _api.Log("FlightTrails: config loaded (maxSpeed=" + _cfg.maxSpeed + " keep=" + _cfg.keepSegments + ")");
            }
            catch (Exception e) { _api.Log("FlightTrails: config error " + e.Message); }
        }

        private float _findT = 0f;   // 场景查找限流：避免主菜单每帧全场景扫描

        private void Update()
        {
            // 纯净模式守卫：原版档不绘制航迹
            if (Machine.Mod.MachineState.PureMode) return;
            // 编辑器守卫：未进入飞行模式不记录/不绘制（起飞后 FlightModeInitialized=true 自动恢复，降落不退出飞行模式）
            if (!Machine.Mod.MachineState.InFlight()) return;
            if (_plane == null || _map == null || _airportMgr == null)
            {
                _findT -= Time.unscaledDeltaTime;
                if (_findT > 0f) return;
                _findT = 1f;   // 每秒最多查找一次（FindFirstObjectByType 全场景扫描很贵）
                bool inGame = false;
                try { inGame = PlaneContainer.Instance != null || AirportManager.Instance != null; } catch { }
                if (!inGame) return;   // 主菜单/非游戏场景不扫描
                if (_plane == null) _plane = (PlaneContainer)UnityEngine.Object.FindFirstObjectByType(typeof(PlaneContainer));
                if (_map == null) _map = (Map)UnityEngine.Object.FindFirstObjectByType(typeof(Map));
                if (_airportMgr == null) _airportMgr = (AirportManager)UnityEngine.Object.FindFirstObjectByType(typeof(AirportManager));
                if (_plane == null && _map == null && _airportMgr == null) return;
            }

            // 置换飞机检测：PlaneContainer 实例变化 → 清除航迹
            if (_plane != null && _lastPlaneRef != null && _plane != _lastPlaneRef)
            {
                _api.Log("FlightTrails: plane replaced, trails cleared");
                ClearTrails(false);
            }
            if (_plane != null) _lastPlaneRef = _plane;

            SampleFlight();
            ScanAirports();

            if (_map == null || _map.mapTransform == null) return;
            // UI 只要地图组件可用就构建（幂等）；对象被地图重建销毁后也要重建
            if (!_uiBuilt || _trailImage == null) BuildUI();
            bool mapOpen = _map.mapTransform.gameObject.activeInHierarchy;
            if (!mapOpen) { _hovered = -1; return; }

            int hover = DetectHover();
            if (hover != _hovered) { _hovered = hover; Redraw(); }
            _redrawTimer -= Time.deltaTime;
            if (_redrawTimer <= 0f)
            {
                _redrawTimer = 0.3f;
                Redraw();
            }
        }

        private void ScanAirports()
        {
            _airportScanTimer -= Time.deltaTime;
            if (_airportScanTimer > 0f) return;
            _airportScanTimer = 3f;
            _airportPositions.Clear();
            _airportNames.Clear();
            _baseKnown = false;
            try
            {
                if (_airportMgr != null && _airportMgr.airports != null)
                {
                    foreach (var a in _airportMgr.airports)
                    {
                        if (a == null) continue;
                        Vector3 p = a.position;
                        _airportPositions.Add(p);
                        _airportNames.Add(a.airportName ?? a.id ?? "?");
                        if (a.IsBaseAirport)
                        {
                            _basePosition = p;
                            _baseKnown = true;
                        }
                    }
                }
                // 兜底：反射读 Map.airportIconInstances（internal 字段）
                if (_airportPositions.Count == 0 && _map != null)
                {
                    var fi = typeof(Map).GetField("airportIconInstances", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (fi != null)
                    {
                        var list = fi.GetValue(_map) as System.Collections.IEnumerable;
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                if (item == null) continue;
                                var ap = item.GetType().GetField("airport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                var airport = ap != null ? ap.GetValue(item) : null;
                                if (airport == null) continue;
                                var posProp = airport.GetType().GetProperty("position");
                                var nameField = airport.GetType().GetField("airportName");
                                string nm = nameField != null && nameField.GetValue(airport) != null ? nameField.GetValue(airport).ToString() : "?";
                                if (posProp != null)
                                {
                                    Vector3 p = (Vector3)posProp.GetValue(airport, null);
                                    _airportPositions.Add(p);
                                    _airportNames.Add(nm);
                                    var isBaseField = airport.GetType().GetField("IsBaseAirport");
                                    if (isBaseField != null && (bool)isBaseField.GetValue(airport)) { _basePosition = p; _baseKnown = true; }
                                }
                            }
                        }
                    }
                }
                _api.Log("FlightTrails: airports=" + _airportPositions.Count + " baseKnown=" + _baseKnown + ( _baseKnown ? (" base=" + _basePosition) : ""));
            }
            catch (Exception e) { _api.Log("FlightTrails: scan airports error " + e.Message); }
        }

        private void SampleFlight()
        {
            if (_plane == null) return;
            Transform t = _plane.transform;
            float speed = _plane.GetVelocityMagintude();
            float h = t.position.y;
            bool grounded = h < 4f && speed < 6f;

            if (!_airborne && !grounded)
            {
                _airborne = true;
                _current = new FlightSegment();
                _api.Log("FlightTrails: takeoff, new segment started");
            }
            else if (_airborne && grounded)
            {
                _airborne = false;
                if (_current != null && _current.Points.Count >= 3)
                {
                    _segments.Add(_current);
                    while (_segments.Count > _cfg.keepSegments) _segments.RemoveAt(0);
                    _api.Log("FlightTrails: landing, segments kept=" + _segments.Count);
                }
                _current = null;
                // 降落时判定是否回到基地/传送基地 → 清除航迹
                CheckLandingClear(t.position);
            }

            if (_airborne && _current != null)
            {
                _sampleTimer -= Time.deltaTime;
                if (_sampleTimer <= 0f)
                {
                    _sampleTimer = _cfg.sampleInterval;
                    _current.Points.Add(new TrailPoint(t.position, speed));
                }
            }
        }

        /// <summary>降落时：回到当前（非基地）机场不清除航迹；回到传送基地（IsBaseAirport）才清除。</summary>
        private void CheckLandingClear(Vector3 planePos)
        {
            try
            {
                float nearBase = _baseKnown ? Vector3.Distance(planePos, _basePosition) : float.MaxValue;
                if (_baseKnown && nearBase < _cfg.baseClearRadius)
                {
                    _api.Log("FlightTrails: landed at base airport, trails cleared");
                    ClearTrails(true);
                    return;
                }
                // 非基地机场：不清除（仅记日志）
                float nearest = float.MaxValue;
                foreach (var p in _airportPositions)
                {
                    float d = Vector3.Distance(planePos, p);
                    if (d < nearest) nearest = d;
                }
                _lastAirportDist = nearest;
                if (nearest < _cfg.baseClearRadius)
                {
                    _api.Log("FlightTrails: landed at regular airport (not base), trails kept, dist=" + Mathf.RoundToInt(nearest));
                }
                else
                {
                    _api.Log("FlightTrails: landed away from airports, trails kept, nearest=" + Mathf.RoundToInt(nearest));
                }
            }
            catch (Exception e) { _api.Log("FlightTrails: landing check error " + e.Message); }
        }

        private void BuildUI()
        {
            _uiBuilt = true;
            try
            {
                // 对象仍有效则跳过（幂等），避免重复创建
                if (_trailImage != null && _trailImage.gameObject != null && _trailImage.gameObject.activeInHierarchy)
                    return;
                RectTransform mapRt = (RectTransform)_map.mapTransform;
                Transform parent = _map.iconParent != null ? _map.iconParent : mapRt;
                Rect mRect = mapRt.rect;
                Rect iRect = ((RectTransform)parent).rect;
                _api.Log("FlightTrails: mapRect=" + mRect + " iconRect=" + iRect);

                var imgGo = new GameObject("Machine.TrailTexture", typeof(RectTransform), typeof(Image));
                imgGo.transform.SetParent(parent, false);
                imgGo.transform.SetAsFirstSibling();
                var rt = (RectTransform)imgGo.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                var img = imgGo.GetComponent<Image>();
                img.color = Color.white;
                img.raycastTarget = false;
                _trailImage = img;

                // 清除航迹按钮（地图右上角）；按钮仍有效则不重复创建
                if (_clearBtn == null || !_clearBtn.gameObject.activeInHierarchy)
                {
                    var btnGo = new GameObject("Machine.ClearTrails", typeof(RectTransform), typeof(Image), typeof(Button));
                    btnGo.transform.SetParent(mapRt, false);
                    var brt = (RectTransform)btnGo.transform;
                    brt.anchorMin = new Vector2(1f, 1f);
                    brt.anchorMax = new Vector2(1f, 1f);
                    brt.pivot = new Vector2(1f, 1f);
                    brt.anchoredPosition = new Vector2(-14f, -14f);
                    brt.sizeDelta = new Vector2(150f, 38f);
                    var bimg = btnGo.GetComponent<Image>();
                    bimg.sprite = UiFactory.RoundedSprite();
                    bimg.type = Image.Type.Sliced;
                    bimg.color = new Color(1f, 1f, 1f, 0.95f);
                    var btn = btnGo.GetComponent<Button>();
                    btn.targetGraphic = bimg;
                    btn.onClick.AddListener(delegate () { ClearTrails(true); });
                    var label = UiFactory.NewText(btnGo.transform, "Clear Trails", 15, new Color(0.13f, 0.14f, 0.17f, 1f), UiFactory.HarvestFont());
                    UiFactory.Stretch((RectTransform)label.transform);
                    _clearBtn = btnGo;
                }

                _api.Log("FlightTrails: map UI built");
            }
            catch (Exception e) { _api.Log("FlightTrails: build UI failed " + e.Message); }
        }

        private void ClearTrails(bool log)
        {
            _segments.Clear();
            _current = null;
            _airborne = false;
            _hovered = -1;
            if (_trailTex != null)
            {
                var clear = new Color(0f, 0f, 0f, 0f);
                var px = _trailTex.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] = clear;
                _trailTex.SetPixels(px);
                _trailTex.Apply();
            }
            if (_trailImage != null) _trailImage.sprite = null;
            if (log) _api.Log("FlightTrails: trails cleared");
        }

        private int DetectHover()
        {
            try
            {
                if (_segments.Count == 0) return -1;
                RectTransform iconRt = (RectTransform)(_map.iconParent != null ? _map.iconParent : _map.mapTransform);
                Vector2 mouseLocal;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(iconRt, Input.mousePosition, null, out mouseLocal)) return -1;
                Rect ir = iconRt.rect;
                float mx = ir.width > 1f ? (mouseLocal.x - ir.xMin) / ir.width : 0.5f;
                float my = ir.height > 1f ? (mouseLocal.y - ir.yMin) / ir.height : 0.5f;
                float mtx = mx * _texSize, mty = my * _texSize;
                for (int s = 0; s < _segments.Count; s++)
                {
                    FlightSegment seg = _segments[s];
                    for (int i = 0; i < seg.Points.Count; i++)
                    {
                        Vector2 t = ToTex(seg.Points[i].World);
                        float dx = t.x - mtx, dy = t.y - mty;
                        if (dx * dx + dy * dy < 26f * 26f) return s;
                    }
                }
            }
            catch { }
            return -1;
        }

        private void Redraw()
        {
            try
            {
                Rect rect = ((RectTransform)_map.mapTransform).rect;
                if (rect.width <= 1f || rect.height <= 1f) return;
                if (_trailTex == null)
                {
                    _trailTex = new Texture2D(_texSize, _texSize, TextureFormat.RGBA32, false);
                    _trailTex.hideFlags = HideFlags.DontSave;
                }
                var clear = new Color(0f, 0f, 0f, 0f);
                var px = _trailTex.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] = clear;
                _trailTex.SetPixels(px);

                // 机场/基地标记
                if (_cfg.showAirportMarks) DrawAirportMarks();

                for (int s = 0; s < _segments.Count; s++)
                {
                    FlightSegment seg = _segments[s];
                    bool hover = (s == _hovered);
                    int width = hover ? _cfg.hoverWidth : _cfg.lineWidth;
                    for (int i = 1; i < seg.Points.Count; i++)
                    {
                        TrailPoint a = seg.Points[i - 1];
                        TrailPoint b = seg.Points[i];
                        Color col = SegmentColor(a.Speed);
                        Vector2 la = ToTex(a.World);
                        Vector2 lb = ToTex(b.World);
                        DrawLine(la, lb, col, width, !hover);
                    }
                    if (hover && seg.Points.Count >= 2)
                    {
                        // 悬停段：中点画方向箭头
                        TrailPoint a = seg.Points[seg.Points.Count / 2 - 1];
                        TrailPoint b = seg.Points[seg.Points.Count / 2];
                        Vector2 la = ToTex(a.World);
                        Vector2 lb = ToTex(b.World);
                        Color ac = ArrowColor();
                        DrawArrow(la, lb, ac, 9);
                    }
                }
                // 当前飞行段实时绘制（虚线，落地后自动并入历史段）
                if (_airborne && _current != null && _current.Points.Count >= 2)
                {
                    for (int i = 1; i < _current.Points.Count; i++)
                    {
                        TrailPoint a = _current.Points[i - 1];
                        TrailPoint b = _current.Points[i];
                        Color col = SegmentColor(a.Speed);
                        Vector2 la = ToTex(a.World);
                        Vector2 lb = ToTex(b.World);
                        DrawLine(la, lb, col, _cfg.lineWidth, true);
                    }
                }
                _trailTex.Apply();
                if (_trailImage != null)
                {
                    Sprite spr = Sprite.Create(_trailTex, new Rect(0f, 0f, _texSize, _texSize), new Vector2(0.5f, 0.5f));
                    _trailImage.sprite = spr;
                }
                // 诊断：确认 UI 对象可见性与首个航迹点映射
                if (_diagCount++ % 60 == 0)
                {
                    string vis = _trailImage == null ? "null" : ("active=" + _trailImage.gameObject.activeInHierarchy + " canvas=" + (_trailImage.canvas != null ? "yes" : "null"));
                    string pts = "seg=" + _segments.Count + " cur=" + (_current != null ? _current.Points.Count : 0);
                    string map = "pt0=" + (_segments.Count > 0 && _segments[0].Points.Count > 0 ? ToTex(_segments[0].Points[0].World).ToString("F0") : "none");
                    string planePos = _plane != null ? _plane.transform.position.ToString("F0") : "none";
                    _api.Log("FlightTrails diag: " + vis + " " + pts + " " + map + " plane=" + planePos);
                }
            }
            catch (Exception e) { _api.Log("FlightTrails: redraw failed " + e.Message); }
        }

        private void DrawAirportMarks()
        {
            for (int i = 0; i < _airportPositions.Count; i++)
            {
                Vector2 t = ToTex(_airportPositions[i]);
                bool isBase = _baseKnown && Vector3.Distance(_airportPositions[i], _basePosition) < 1f;
                // 基地：白圈；普通机场：青圈
                Color col = isBase ? new Color(1f, 1f, 1f, 0.9f) : new Color(0.35f, 0.85f, 1f, 0.75f);
                FillDot(Mathf.RoundToInt(t.x), Mathf.RoundToInt(t.y), col, 14);
                FillDot(Mathf.RoundToInt(t.x), Mathf.RoundToInt(t.y), new Color(0f, 0f, 0f, 0.85f), 5);
            }
        }

        private Vector2 ToTex(Vector3 world)
        {
            // 优先反射调用游戏自己的 GetInterpolator，保证与机场图标完全一致
            try
            {
                var mi = typeof(Map).GetMethod("GetInterpolator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi != null)
                {
                    Vector2 interp = (Vector2)mi.Invoke(_map, new object[] { world });
                    if (interp.x >= -0.2f && interp.y >= -0.2f && interp.x <= 1.2f && interp.y <= 1.2f)
                        return new Vector2(interp.x * _texSize, interp.y * _texSize);
                }
            }
            catch { }
            // 兜底公式（与 Map.GetInterpolator 同）
            float zoom = MapZoom();
            if (zoom < 0.01f) zoom = 1f;
            Vector2 off = MapOffset();
            float ix = world.x / zoom + off.x + 0.5f;
            float iy = world.z / zoom + off.y + 0.5f;
            return new Vector2(ix * _texSize, iy * _texSize);
        }

        private float MapZoom()
        {
            try
            {
                var f = typeof(Map).GetField("zoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (float)f.GetValue(_map);
            }
            catch { }
            return 1f;
        }

        private Vector2 MapOffset()
        {
            try
            {
                var f = typeof(Map).GetField("offset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (Vector2)f.GetValue(_map);
            }
            catch { }
            return Vector2.zero;
        }

        private Color SegmentColor(float speed)
        {
            if (!_cfg.autoColor) return new Color(_cfg.fixedR, _cfg.fixedG, _cfg.fixedB, 1f);
            float t = Mathf.Clamp01(speed / Mathf.Max(1f, _cfg.maxSpeed));
            float hue = (1f - t) * 240f / 360f;
            return Color.HSVToRGB(hue, _cfg.saturation, _cfg.brightness);
        }

        private Color ArrowColor()
        {
            return Color.HSVToRGB(0.6f, _cfg.arrowSaturation, _cfg.brightness);
        }

        private void DrawLine(Vector2 a, Vector2 b, Color col, int width, bool dash)
        {
            float dx = b.x - a.x, dy = b.y - a.y;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
            float sx = dx / steps, sy = dy / steps;
            float x = a.x, y = a.y;
            for (int i = 0; i <= steps; i++)
            {
                if (!dash || (i % 30) < 26)
                {
                    FillDot(Mathf.RoundToInt(x), Mathf.RoundToInt(y), col, width);
                }
                x += sx; y += sy;
            }
        }

        private void DrawArrow(Vector2 a, Vector2 b, Color col, int size)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 0.001f) return;
            d.Normalize();
            Vector2 n = new Vector2(-d.y, d.x);
            Vector2 tip = b;
            Vector2 baseP = b - d * size;
            Vector2 p1 = baseP + n * (size * 0.45f);
            Vector2 p2 = baseP - n * (size * 0.45f);
            DrawLine(p1, tip, col, 3, false);
            DrawLine(p2, tip, col, 3, false);
            DrawLine(p1, p2, col, 3, false);
            FillDot(Mathf.RoundToInt(tip.x), Mathf.RoundToInt(tip.y), col, 4);
        }

        private void FillDot(int cx, int cy, Color col, int width)
        {
            int r = width / 2;
            for (int y = -r; y <= r; y++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y <= r * r)
                    {
                        int tx = cx + x, ty = cy + y;
                        if (tx >= 0 && ty >= 0 && tx < _texSize && ty < _texSize)
                            _trailTex.SetPixel(tx, ty, col);
                    }
                }
            }
        }
    }
}
