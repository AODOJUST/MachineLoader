# -*- coding: utf-8 -*-
import io
SRC = r'D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs'
s = io.open(SRC, encoding='utf-8').read()
rep = []

def r(old, new, n=1, tag=''):
    global s
    c = s.count(old)
    if c != n:
        rep.append('FAIL [%s] want %d got %d' % (tag, n, c))
        return
    s = s.replace(old, new)
    rep.append('ok   [%s] x%d' % (tag, n))

# 1) FirstMeshRenderer helper, inserted before PrimeMaterialCache's doc comment
r("""        /// <summary>
        /// 建立材质来源缓存。按"场景 + 玩家飞机"失效：飞行中反复发射导弹不会重建，""",
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

        /// <summary>
        /// 建立材质来源缓存。按"场景 + 玩家飞机"失效：飞行中反复发射导弹不会重建，""", 1, 'first-mesh-renderer')

# 2a) plane scan (24 spaces)
r("""                        Renderer sr = null;
                        try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { }""",
"""                        Renderer sr = null;
                        try { sr = FirstMeshRenderer(bp.gameObject); } catch { }
                        if (sr == null) { try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { } }""", 1, 'cache-mesh-renderer-plane')

# 2b) scene scan (28 spaces)
r("""                            Renderer sr = null;
                            try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { }""",
"""                            Renderer sr = null;
                            try { sr = FirstMeshRenderer(bp.gameObject); } catch { }
                            if (sr == null) { try { sr = bp.GetComponentInChildren<Renderer>(true); } catch { } }""", 1, 'cache-mesh-renderer-scene')

io.open(SRC, 'w', encoding='utf-8', newline='').write(s)
io.open(r'D:\豆包的下载\Machine_Dev\_patch_report2.txt', 'w', encoding='utf-8').write('\n'.join(rep))
print('\n'.join(rep))
print('ALL_OK' if all(x.startswith('ok') for x in rep) else 'HAS_FAILURES')
