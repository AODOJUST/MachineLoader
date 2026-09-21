# -*- coding: utf-8 -*-
"""Apply the 2026-09-14 AAM patch batch (missile colour / flare eject / flare vfx / trail life)."""
import io, sys

SRC = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(SRC, encoding='utf-8').read()
orig = s
report = []

def rep(old, new, n=1, tag=''):
    global s
    c = s.count(old)
    if c != n:
        report.append('FAIL [%s] expected %d got %d' % (tag, n, c))
        return False
    s = s.replace(old, new)
    report.append('ok   [%s] x%d' % (tag, n))
    return True

ok = True

# ---------------------------------------------------------------- 1. flare eject power
ok &= rep(
"""        public static float Gravity = 9.81f;
        public static float DragK = 0.85f;     // 指数阻力系数 (1/s)：约 0.8s 速度减半
        public static float EjectDown = 20f;   // 向下冲量 (m/s)
        public static float EjectBack = 12f;   // 向后冲量 (m/s)
        public static float EjectSide = 9f;    // 侧向散布 (m/s)""",
"""        public static float Gravity = 9.81f;
        public static float DragK = 0.50f;     // 指数阻力系数 (1/s)：约 1.4s 速度减半（旧值 0.85/0.8s 太软）
        public static float EjectDown = 24f;   // 向下冲量 (m/s)
        public static float EjectBack = 26f;   // 向后冲量 (m/s)
        public static float EjectSide = 15f;   // 侧向散布 (m/s)""", 1, 'flare-eject-defaults')

# ---------------------------------------------------------------- 2. flare spawn offset
ok &= rep(
"""                Vector3 pos = carrier.position + down * 1.6f + back * 3.2f;""",
"""                Vector3 pos = carrier.position + down * 1.8f + back * 3.8f;""", 1, 'flare-spawn-offset')

# ---------------------------------------------------------------- 3. flare burn lifetime + core look
ok &= rep(
"""                main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.45f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 1.1f);
                main.startColor = new Color(1f, 0.92f, 0.62f, 1f);""",
"""                main.startLifetime = new ParticleSystem.MinMaxCurve(0.36f, 0.95f);   // 尾迹寿命 x2
                main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.42f, 1.25f);
                main.startColor = new Color(1f, 0.94f, 0.70f, 1f);
                main.gravityModifier = 0.05f;""", 1, 'flare-fire-main')

ok &= rep(
"""                em.rateOverTime = new ParticleSystem.MinMaxCurve(230f);""",
"""                em.rateOverTime = new ParticleSystem.MinMaxCurve(260f);""", 1, 'flare-fire-rate')

ok &= rep(
"""                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 0.98f, 0.85f), 0.00f),
                        new GradientColorKey(new Color(1.00f, 0.72f, 0.25f), 0.25f),
                        new GradientColorKey(new Color(0.95f, 0.35f, 0.08f), 0.60f),
                        new GradientColorKey(new Color(0.35f, 0.10f, 0.02f), 1.00f)},
                    new GradientAlphaKey[]{
                        new GradientAlphaKey(0.95f, 0.00f),
                        new GradientAlphaKey(0.85f, 0.40f),
                        new GradientAlphaKey(0.35f, 0.75f),
                        new GradientAlphaKey(0.00f, 1.00f)});""",
"""                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 0.99f, 0.92f), 0.00f),
                        new GradientColorKey(new Color(1.00f, 0.86f, 0.42f), 0.18f),
                        new GradientColorKey(new Color(1.00f, 0.62f, 0.16f), 0.42f),
                        new GradientColorKey(new Color(0.92f, 0.32f, 0.08f), 0.68f),
                        new GradientColorKey(new Color(0.45f, 0.44f, 0.43f), 1.00f)},
                    new GradientAlphaKey[]{
                        new GradientAlphaKey(1.00f, 0.00f),
                        new GradientAlphaKey(0.95f, 0.35f),
                        new GradientAlphaKey(0.55f, 0.72f),
                        new GradientAlphaKey(0.00f, 1.00f)});""", 1, 'flare-fire-gradient')

# ---------------------------------------------------------------- 4. flare fields
ok &= rep(
"""        private ParticleSystem _fire;
        private Light _light;
        private float _ir = PeakIr;""",
"""        private ParticleSystem _fire;
        private ParticleSystem _smoke;
        private Light _light;
        private float _ir = PeakIr;
        private bool _dying;""", 1, 'flare-fields')

# ---------------------------------------------------------------- 5. flare death: keep smoke alive
ok &= rep(
"""            float dt = Time.fixedDeltaTime;
            Age += dt;
            if (Age >= LifeTime) { Destroy(gameObject); return; }""",
"""            float dt = Time.fixedDeltaTime;
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
            }""", 1, 'flare-death')

# ---------------------------------------------------------------- 6. hook up the new smoke system
ok &= rep(
"""                ps.Play(true);
                _fire = ps;

                AcquireLight();""",
"""                ps.Play(true);
                _fire = ps;

                BuildFlareSmoke();   // 火焰之外再挂一条世界空间烟迹（寿命长得多 -> 尾迹更久）
                AcquireLight();""", 1, 'flare-call-smoke')

# ---------------------------------------------------------------- 7. update vfx incl. smoke
ok &= rep(
"""                float k = Mathf.Clamp01(_ir / PeakIr);
                if (_light != null) _light.intensity = 3.2f * k;
                if (_fire != null)
                {
                    var em = _fire.emission;
                    em.rateOverTime = new ParticleSystem.MinMaxCurve(230f * k);
                }""",
"""                float k = Mathf.Clamp01(_ir / PeakIr);
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
                }""", 1, 'flare-update-vfx')

# ---------------------------------------------------------------- 8. new BuildFlareSmoke method
ok &= rep(
"""        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_dying) return;""",
"""        /// <summary>
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
            if (_dying) return;""", 1, 'flare-smoke-method')

# ---------------------------------------------------------------- 9. missile smoke trail: life x2, whiter
ok &= rep(
"""                main.startLifetime = new ParticleSystem.MinMaxCurve(3.0f, 5.2f);   // 活得够久才能长大再消散""",
"""                main.startLifetime = new ParticleSystem.MinMaxCurve(6.0f, 10.4f);  // 尾迹寿命 x2（用户要求维持更久）""", 1, 'smoke-life')

ok &= rep(
"""                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.98f, 0.97f, 0.95f, 0.95f),
                    new Color(0.80f, 0.79f, 0.78f, 0.80f));""",
"""                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(1.00f, 0.995f, 0.99f, 0.96f),
                    new Color(0.93f, 0.93f, 0.94f, 0.86f));""", 1, 'smoke-startcolor')

ok &= rep(
"""                main.maxParticles = 1200;""",
"""                main.maxParticles = 2400;   // 寿命翻倍后同时存活粒子也翻倍，不抬上限会提前淘汰旧粒子、把尾迹截断""", 1, 'smoke-maxparticles')

ok &= rep(
"""                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 0.98f, 0.93f), 0.00f),
                        new GradientColorKey(new Color(0.90f, 0.88f, 0.85f), 0.14f),
                        new GradientColorKey(new Color(0.72f, 0.71f, 0.70f), 0.48f),
                        new GradientColorKey(new Color(0.56f, 0.56f, 0.57f), 0.80f),
                        new GradientColorKey(new Color(0.48f, 0.48f, 0.50f), 1.00f)},""",
"""                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 1.00f, 0.99f), 0.00f),
                        new GradientColorKey(new Color(0.98f, 0.98f, 0.98f), 0.14f),
                        new GradientColorKey(new Color(0.94f, 0.94f, 0.95f), 0.48f),
                        new GradientColorKey(new Color(0.87f, 0.87f, 0.89f), 0.80f),
                        new GradientColorKey(new Color(0.79f, 0.79f, 0.82f), 1.00f)},""", 1, 'smoke-gradient')

# ---------------------------------------------------------------- 10. smoke detacher afterlife
ok &= rep(
"""        public float Afterlife = 6f;      // 脱离后再留几秒，够粒子自己散完""",
"""        public float Afterlife = 14f;     // 脱离后再留几秒，够粒子自己散完（粒子寿命已加倍到 10.4s）""", 1, 'smoke-afterlife')

# ---------------------------------------------------------------- 11. ammo material: fields
ok &= rep(
"""        private static int _matApplied, _matMissed;           // 本次建模命中/未命中计数""",
"""        private static int _matApplied, _matMissed;           // 本次建模命中/未命中计数
        // 弹药（导弹 / 机炮 / 热诱弹）专用：与场景无关的中性灰白材质。
        // 按部件名缓存，避免每发一枚导弹都新建一批 Material。
        private static readonly Dictionary<string, Material> _ammoMatCache
            = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static readonly HashSet<string> _ammoMatLogged = new HashSet<string>();
        private static readonly Color AmmoTint = new Color(0.88f, 0.89f, 0.91f, 1f);""", 1, 'ammo-fields')

# ---------------------------------------------------------------- 12. ammo material: FirstMeshRenderer
ok &= rep(
"""        /// <summary>建立材质来源缓存。按"场景 + 玩家飞机"失效：飞行中反复发射导弹不会重建，""",
"""        /// <summary>
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

        /// <summary>建立材质来源缓存。按"场景 + 玩家飞机"失效：飞行中反复发射导弹不会重建，""", 1, 'first-mesh-renderer')

# ---------------------------------------------------------------- 13. material cache: clear ammo cache
ok &= rep(
"""                _matCache.Clear();
                _matSrcCache.Clear();
                _scenePartGo.Clear();
                _matFallback = null;""",
"""                _matCache.Clear();
                _matSrcCache.Clear();
                _scenePartGo.Clear();
                _ammoMatCache.Clear();     // 上一场景的材质对象已随场景失效
                _matFallback = null;""", 1, 'clear-ammo-cache')

# ---------------------------------------------------------------- 14. material cache: prefer mesh renderer
ok &= rep(
"""                        Renderer sr = null;
                        try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { }""",
"""                        Renderer sr = null;
                        try { sr = FirstMeshRenderer(bp.gameObject); } catch { }
                        if (sr == null) { try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { } }""",
2, 'cache-mesh-renderer')

# ---------------------------------------------------------------- 15. ApplyDesignMaterials
ok &= rep(
"""        private static void ApplyDesignMaterials(GameObject partGo, string partName, IMachineApi api)
        {
            if (partGo == null) return;
            try
            {
                Renderer pr = partGo.GetComponentInChildren<Renderer>(true);
                if (pr == null) return;

                string want = partName.Replace("(Clone)", "").Trim();

                // 1) 玩家飞机上的同类部件（材质带游戏贴图与配色）—— 命中才复制涂装
                Material[] mats;
                if (_matCache.TryGetValue(want, out mats) && mats != null && mats.Length > 0)
                {""",
"""        /// <summary>
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
                    if (!hit) srcs = _matFallback;
                    Material am = AmmoMaterialFor(want, srcs, api);
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
                {""", 1, 'apply-design-materials-head')

# ---------------------------------------------------------------- 16. AmmoMaterialFor method
ok &= rep(
"""        /// <summary>把源部件的涂装颜色复制到目标部件（BuildingPart.SetColor / GetCurrentColor）。</summary>""",
"""        /// <summary>
        /// 弹药的固定外观材质：照抄一个来源材质（保住游戏里的金属贴图），
        /// 但色调强制成中性灰白，且绝不返回"着色器丢失"的材质（那种会渲染成品红）。
        /// 每个部件名只建一次，之后复用。
        /// </summary>
        private static Material AmmoMaterialFor(string partName, Material[] sources, IMachineApi api)
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
                api.Log("AAM: ammo material '" + partName + "' src=" + (src != null ? src.name : "none")
                        + " shader=" + sn + " tex=" + tn + " color=0.88/0.89/0.91");
            }
            return built;
        }

        /// <summary>把源部件的涂装颜色复制到目标部件（BuildingPart.SetColor / GetCurrentColor）。</summary>""", 1, 'ammo-material-for')

# ---------------------------------------------------------------- 17. BuildWithGameLoader signature
ok &= rep(
"""        public static GameObject BuildWithGameLoader(string path, string rootName, IMachineApi api,
                                                     bool keepColliders = false, bool startInactive = false)
        {""",
"""        public static GameObject BuildWithGameLoader(string path, string rootName, IMachineApi api,
                                                     bool keepColliders = false, bool startInactive = false,
                                                     bool ammoLook = false)
        {""", 1, 'build-signature')

ok &= rep(
"""                        ApplyDesignMaterials(go, name, api);""",
"""                        ApplyDesignMaterials(go, name, api, ammoLook);""", 1, 'build-call')

# ---------------------------------------------------------------- 18. ammo call sites
ok &= rep(
"""                    model = DesignModel.BuildWithGameLoader(path, "AAM_Flare", _api);""",
"""                    model = DesignModel.BuildWithGameLoader(path, "AAM_Flare", _api, false, false, true);""", 1, 'call-flare')

ok &= rep(
"""                    model = DesignModel.BuildWithGameLoader(FlareDesignPath(), "AAM_FlareTest", _api);""",
"""                    model = DesignModel.BuildWithGameLoader(FlareDesignPath(), "AAM_FlareTest", _api, false, false, true);""", 1, 'call-flaretest')

ok &= rep(
"""                    GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "HITTEST", _api);""",
"""                    GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "HITTEST", _api, false, false, true);""", 1, 'call-hittest')

ok &= rep(
"""                GameObject m = DesignModel.BuildWithGameLoader(p, "AAM_BulletProto", _api);""",
"""                GameObject m = DesignModel.BuildWithGameLoader(p, "AAM_BulletProto", _api, false, false, true);""", 1, 'call-bullet')

ok &= rep(
"""                model = DesignModel.BuildWithGameLoader(designPath, "AAM_" + spec.Name, _api);""",
"""                model = DesignModel.BuildWithGameLoader(designPath, "AAM_" + spec.Name, _api, false, false, true);""", 1, 'call-missile')

ok &= rep(
"""                model = DesignModel.BuildWithGameLoader(path, "AAM_" + cfgCargoName, _api);""",
"""                model = DesignModel.BuildWithGameLoader(path, "AAM_" + cfgCargoName, _api, false, false, true);""", 1, 'call-cargoname')

ok &= rep(
"""                    m = DesignModel.BuildWithGameLoader(p, "AMMOCHK_" + sp.Name, _api);""",
"""                    m = DesignModel.BuildWithGameLoader(p, "AMMOCHK_" + sp.Name, _api, false, false, true);""", 1, 'call-ammochk')

ok &= rep(
"""                GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "AMMO_THREAT", _api);""",
"""                GameObject model = DesignModel.BuildWithGameLoader(DesignPathFor(sp), "AMMO_THREAT", _api, false, false, true);""", 1, 'call-threat')

io.open(SRC, 'w', encoding='utf-8', newline='').write(s)
report.append('changed=%s  bytes %d -> %d' % (s != orig, len(orig), len(s)))
io.open(r'D:\豆包的下载\Machine_Dev\_patch_report.txt', 'w', encoding='utf-8').write('\n'.join(report))
print('\n'.join(report))
print('ALL_OK' if ok else 'HAS_FAILURES')
