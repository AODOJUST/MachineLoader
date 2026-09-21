        private static void AddTrail(GameObject root, Transform dummy)
        {
            try
            {
                if (root == null) { Machine.Core.Log.Info("[AAM] trail root null"); return; }
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh == null) { Machine.Core.Log.Info("[AAM] trail shader not found"); return; }

                // 取尾部锚点：优先用调用方传入的 dummy；否则在 root 局部坐标 -Z 方向 1.6m 处造一个
                Transform anchor = dummy;
                if (anchor == null)
                {
                    GameObject aGo = new GameObject("AAM_TrailAnchor");
                    aGo.transform.SetParent(root.transform, false);
                    aGo.transform.localPosition = new Vector3(0f, 0f, -1.6f);
                    anchor = aGo.transform;
                }

                // ----- 1) 尾焰：只有 0.4 秒的短羽状火焰（长条会变成"一条线"，所以刻意做短） -----
                GameObject coreGo = new GameObject("AAM_Flame");
                coreGo.transform.SetParent(root.transform, false);
                coreGo.transform.localPosition = anchor.localPosition;
                TrailRenderer core = coreGo.AddComponent<TrailRenderer>();
                core.time = 0.40f;
                core.widthCurve = new AnimationCurve(new Keyframe(0f, 0.36f), new Keyframe(0.6f, 0.16f), new Keyframe(1f, 0.02f));
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
                BuildSmokeTrail(root, anchor);

                Machine.Core.Log.Info("[AAM] trail attached to " + root.name);
            }
            catch (Exception e) { Machine.Core.Log.Info("[AAM] trail failed: " + e); }
        }

