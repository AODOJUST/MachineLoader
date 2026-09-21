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
                _api.Log("AAM SELFTEST: chase+trail shots -> " + tag + " pos=" + p);
            }
            catch (Exception e) { _api.Log("AAM SELFTEST: chase shot failed " + e.Message); }
        }

