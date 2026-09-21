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
        private static GameObject _cachedSmokePrefab;
        private static Material _cachedSmokeMat;
        private static bool _smokeProbed;
        private static Texture2D _fallbackPuff;

        private const BindingFlags BF_SMOKE = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>原版烟雾粒子预制体：优先 ParticleSpawner 的 Smoke 槽，退化到爆炸粒子。</summary>
        internal static GameObject TryGetGameSmokePrefab()
        {
            if (_smokeProbed) return _cachedSmokePrefab;
            _smokeProbed = true;
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
                        _cachedSmokePrefab = arr[idx];
                        Machine.Core.Log.Info("[AAM] smoke source: particlePrefabs[Smoke] = "
                            + (_cachedSmokePrefab != null ? _cachedSmokePrefab.name : "null")
                            + " (slots=" + arr.Length + " idx=" + idx + ")");
                    }
                    else Machine.Core.Log.Info("[AAM] smoke source: ParticleSpawner has no prefabs");
                }
                else Machine.Core.Log.Info("[AAM] smoke source: ParticleSpawner not in scene");
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] smoke lookup ParticleSpawner failed: " + e.Message); }

            if (_cachedSmokePrefab == null)
            {
                try
                {
                    PartExploder pe = null;
                    try { pe = PartExploder.Instance; } catch { }
                    if (pe == null)
                    {
                        UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(PartExploder));
                        if (all != null && all.Length > 0) pe = all[0] as PartExploder;
                    }
                    if (pe != null)
                    {
                        FieldInfo f = typeof(PartExploder).GetField("explosionParticlePrefab", BF_SMOKE);
                        _cachedSmokePrefab = (f != null) ? (f.GetValue(pe) as GameObject) : null;
                        Machine.Core.Log.Info("[AAM] smoke source: explosionParticlePrefab = "
                            + (_cachedSmokePrefab != null ? _cachedSmokePrefab.name : "null"));
                    }
                    else Machine.Core.Log.Info("[AAM] smoke source: PartExploder not in scene");
                }
                catch (Exception e) { Machine.Core.Log.Info("[AAM] smoke lookup PartExploder failed: " + e.Message); }
            }
            if (_cachedSmokePrefab == null)
                Machine.Core.Log.Info("[AAM] smoke source: NONE - using generated puff texture");
            return _cachedSmokePrefab;
        }

        /// <summary>在烟雾预制体里找一个带材质的粒子渲染器（只读，不克隆不改）。</summary>
        private static ParticleSystemRenderer FindSmokeSourceRenderer()
        {
            GameObject src = TryGetGameSmokePrefab();
            if (src == null) return null;
            try
            {
                ParticleSystemRenderer[] rs = src.GetComponentsInChildren<ParticleSystemRenderer>(true);
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

        /// <summary>直接引用原版烟雾材质/贴图/渲染模式；拿不到就用程序生成的柔和烟团兜底。</summary>
        private static void BindSmokeRenderer(ParticleSystemRenderer psr, string tag)
        {
            if (psr == null) return;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psr.receiveShadows = false;
            psr.sortingFudge = 2f;

            ParticleSystemRenderer sr = FindSmokeSourceRenderer();
            if (sr != null)
            {
                _cachedSmokeMat = sr.sharedMaterial;
                psr.sharedMaterial = sr.sharedMaterial;
                psr.renderMode = sr.renderMode;
                psr.alignment = sr.alignment;
                try { psr.mesh = sr.mesh; } catch { }
                try { psr.pivot = sr.pivot; } catch { }
                try { psr.trailMaterial = sr.trailMaterial; } catch { }
                Machine.Core.Log.Info("[AAM] smoke material borrowed [" + tag + "]: '"
                    + sr.sharedMaterial.name + "' shader='"
                    + (sr.sharedMaterial.shader != null ? sr.sharedMaterial.shader.name : "?")
                    + "' tex='" + (sr.sharedMaterial.mainTexture != null ? sr.sharedMaterial.mainTexture.name : "none")
                    + "' mode=" + sr.renderMode);
                return;
            }

            Shader sh = Shader.Find("Particles/Standard Unlit");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null)
            {
                Material m = new Material(sh);
                m.mainTexture = MakeFallbackPuffTexture();
                psr.sharedMaterial = m;
                psr.renderMode = ParticleSystemRenderMode.Billboard;
                psr.alignment = ParticleSystemRenderSpace.View;
                Machine.Core.Log.Info("[AAM] smoke material fallback [" + tag + "]: shader=" + sh.name);
            }
        }

        /// <summary>给 TrailRenderer 贴上原版烟雾贴图（拿不到就用程序生成烟团），让它像烟带而不是色线。</summary>
        private static void ApplySmokeTextureToTrail(TrailRenderer tr)
        {
            if (tr == null || tr.material == null) return;
            try
            {
                Texture tex = null;
                ParticleSystemRenderer sr = FindSmokeSourceRenderer();
                if (sr != null && sr.sharedMaterial != null) tex = sr.sharedMaterial.mainTexture;
                tr.material.mainTexture = (tex != null) ? tex : (Texture)MakeFallbackPuffTexture();
                tr.textureMode = LineTextureMode.Stretch;
                tr.numCapVertices = 6;
                tr.numCornerVertices = 2;
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] trail smoke texture failed: " + e.Message); }
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

        /// <summary>
        /// 导弹烟轨：世界空间烟团，沿航迹规则喷出 → 变大 → 变灰 → 消失。
        /// </summary>
        private static void BuildSmokeTrail(GameObject root, Transform anchor)
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

                ParticleSystem.MainModule main = ps.main;
                main.duration = 6f;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.8f);   // 活得够久才能长大再消散
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.0f, 3.0f);
                main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 2.6f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28318f);
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.97f, 0.96f, 0.93f, 0.90f),
                    new Color(0.76f, 0.75f, 0.74f, 0.70f));
                main.simulationSpace = ParticleSystemSimulationSpace.World;        // ① 烟留在航迹上
                main.scalingMode = ParticleSystemScalingMode.Local;                // 不受模型缩放干扰
                main.gravityModifier = -0.008f;                                    // 极轻微上浮
                main.maxParticles = 600;
                try { main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Custom; } catch { }

                // ② 明确不继承发射器速度（否则烟会被高速弹体拖着走）
                ParticleSystem.InheritVelocityModule inh = ps.inheritVelocity;
                inh.enabled = true;
                inh.mode = ParticleSystemInheritVelocityMode.Initial;
                inh.curve = new ParticleSystem.MinMaxCurve(0f);

                // ① 规则喷出
                ParticleSystem.EmissionModule em = ps.emission;
                em.enabled = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(78f);

                ParticleSystem.ShapeModule shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 16f;
                shape.radius = 0.10f;
                shape.radiusThickness = 1f;
                shape.rotation = new Vector3(0f, 180f, 0f);                        // 朝机尾方向喷

                // 变大
                ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0.00f, 0.18f),
                    new Keyframe(0.16f, 0.52f),
                    new Keyframe(0.52f, 0.92f),
                    new Keyframe(1.00f, 1.25f)));

                // 渐变 + 消失
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient g = new Gradient();
                g.SetKeys(
                    new GradientColorKey[]{
                        new GradientColorKey(new Color(1.00f, 0.98f, 0.93f), 0.00f),
                        new GradientColorKey(new Color(0.90f, 0.88f, 0.85f), 0.14f),
                        new GradientColorKey(new Color(0.72f, 0.71f, 0.70f), 0.48f),
                        new GradientColorKey(new Color(0.56f, 0.56f, 0.57f), 0.80f),
                        new GradientColorKey(new Color(0.48f, 0.48f, 0.50f), 1.00f)},
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

                ps.Play(true);
                Machine.Core.Log.Info("[AAM] smoke trail built on " + root.name);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] smoke trail failed: " + e.Message); }
        }
