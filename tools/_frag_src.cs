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

            ParticleSystemRenderer sr = TryGetGameSmokeRenderer();
            if (sr != null)
            {
                psr.sharedMaterial = sr.sharedMaterial;    // 只读引用共享材质：不克隆、不修改
                psr.renderMode = sr.renderMode;
                psr.alignment = sr.alignment;
                try { psr.mesh = sr.mesh; } catch { }
                try { psr.pivot = sr.pivot; } catch { }
                try { psr.trailMaterial = sr.trailMaterial; } catch { }
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
                Machine.Core.Log.Info("[AAM] smoke material fallback [" + tag + "]: " + sh.name);
            }
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

