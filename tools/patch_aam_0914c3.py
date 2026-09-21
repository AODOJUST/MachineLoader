# -*- coding: utf-8 -*-
import io
SRC = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(SRC, encoding='utf-8').read()
rep = []
def r(old, new, n=1, tag=''):
    global s
    c = s.count(old)
    if c != n:
        rep.append('FAIL [%s] want %d got %d' % (tag, n, c)); return
    s = s.replace(old, new); rep.append('ok   [%s] x%d' % (tag, n))

# 1) field + helper
r("""        private static readonly Color AmmoTint = new Color(0.88f, 0.89f, 0.91f, 1f);""",
"""        private static readonly Color AmmoTint = new Color(0.88f, 0.89f, 0.91f, 1f);
        private static string _matFallbackSrc = "";   // 诊断：兜底材质是从哪个场景物体上抓的""", 1, 'fallback-src-field')

r("""        /// <summary>
        /// 部件上第一个"网格"渲染器。""",
"""        /// <summary>物体在场景层级里的路径（诊断用：告诉我们"兜底材质"到底是从哪个东西上抓的）。</summary>
        private static string ScenePath(Transform t)
        {
            try
            {
                string path = t.name;
                Transform p = t.parent;
                int guard = 0;
                while (p != null && guard++ < 6) { path = p.name + "/" + path; p = p.parent; }
                return path;
            }
            catch { return "?"; }
        }

        /// <summary>
        /// 部件上第一个"网格"渲染器。""", 1, 'scene-path-helper')

# 2) record the fallback source
r("""                            if (_matFallback == null && sr.sharedMaterial != null && sr.sharedMaterial.mainTexture != null)
                                _matFallback = sr.sharedMaterials;""",
"""                            if (_matFallback == null && sr.sharedMaterial != null && sr.sharedMaterial.mainTexture != null)
                            {
                                _matFallback = sr.sharedMaterials;
                                _matFallbackSrc = ScenePath(bp.transform);
                            }""", 1, 'record-fallback-src')

r("""                _ammoMatCache.Clear();     // 上一场景的材质对象已随场景失效
                _matFallback = null;""",
"""                _ammoMatCache.Clear();     // 上一场景的材质对象已随场景失效
                _matFallback = null;
                _matFallbackSrc = "";""", 1, 'clear-fallback-src')

r("""                    api.Log("AAM: material cache primed in " + sw.ElapsedMilliseconds + "ms"
                            + " names=" + _matCache.Count + " fallback=" + (_matFallback != null));""",
"""                    api.Log("AAM: material cache primed in " + sw.ElapsedMilliseconds + "ms"
                            + " names=" + _matCache.Count + " fallback=" + (_matFallback != null)
                            + " fallbackSrc=" + (_matFallbackSrc.Length > 0 ? _matFallbackSrc : "none"));""", 1, 'log-fallback-src')

# 3) thread the source tag into AmmoMaterialFor
r("""                    Material[] srcs;
                    bool hit = _matCache.TryGetValue(want, out srcs) && srcs != null && srcs.Length > 0;
                    if (!hit) srcs = _matFallback;
                    Material am = AmmoMaterialFor(want, srcs, api);""",
"""                    Material[] srcs;
                    bool hit = _matCache.TryGetValue(want, out srcs) && srcs != null && srcs.Length > 0;
                    string srcTag = hit ? "cache" : "fallback";
                    if (!hit) srcs = _matFallback;
                    Material am = AmmoMaterialFor(want, srcs, api, srcTag);""", 1, 'ammo-src-tag')

r("""        private static Material AmmoMaterialFor(string partName, Material[] sources, IMachineApi api)
        {""",
"""        private static Material AmmoMaterialFor(string partName, Material[] sources, IMachineApi api, string srcTag)
        {""", 1, 'ammo-sig')

r("""                api.Log("AAM: ammo material '" + partName + "' src=" + (src != null ? src.name : "none")
                        + " shader=" + sn + " tex=" + tn + " color=0.88/0.89/0.91");""",
"""                api.Log("AAM: ammo material '" + partName + "' from=" + srcTag
                        + " src=" + (src != null ? src.name : "none")
                        + " srcObj=" + (_matFallbackSrc.Length > 0 ? _matFallbackSrc : "none")
                        + " shader=" + sn + " tex=" + tn + " color=0.88/0.89/0.91");""", 1, 'ammo-log')

io.open(SRC, 'w', encoding='utf-8', newline='').write(s)
io.open(r'D:\豆包的下载\Machine_Dev\_patch_report3.txt', 'w', encoding='utf-8').write('\n'.join(rep))
print('\n'.join(rep))
print('ALL_OK' if all(x.startswith('ok') for x in rep) else 'HAS_FAILURES')
