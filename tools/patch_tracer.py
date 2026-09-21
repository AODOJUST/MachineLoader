# -*- coding: utf-8 -*-
# 曳光弹可见性：原型统一染色（亮黄橙），近景截图提前到 0.12s（约 85m 处）
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# 1) 原型染成曳光色（克隆体共享，不用每发实例化材质）
reps.append((
"""                m.SetActive(false);
                _bulletProto = m;
                _api.Log("AAM: bullet proto ready from " + p);""",
"""                // 曳光弹配色：整发亮黄橙，空战里肉眼可见（原型统一改，克隆体共享材质，零额外开销）
                try
                {
                    Renderer[] rr = m.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < rr.Length; i++)
                    {
                        if (rr[i] == null) continue;
                        Material mat = rr[i].sharedMaterial;
                        if (mat == null) { mat = new Material(Shader.Find("Sprites/Default")); rr[i].sharedMaterial = mat; }
                        if (mat.HasProperty("_Color")) mat.color = new Color(1f, 0.78f, 0.25f, 1f);
                    }
                }
                catch (Exception ce) { _api.Log("AAM: bullet tint failed " + ce.Message); }
                m.SetActive(false);
                _bulletProto = m;
                _api.Log("AAM: bullet proto ready from " + p);"""))

# 2) 近景截图提前到 0.12s
reps.append((
"""                if (cfgCapture && !_gunShotDone && _gunBurstT > 0.45f)""",
"""                if (cfgCapture && !_gunShotDone && _gunBurstT > 0.12f)"""))

# 3) 机位拉近：侧后 12m、高 3m，看 150m 外
reps.append((
"""                RenderAt(muzzle + f * 230f + side * 26f + Vector3.up * 6f, look, "aam_gun_burst.png");
                RenderAt(muzzle + f * 120f + side * 8f + Vector3.up * 2.5f, muzzle + f * 300f, "aam_gun_close.png");""",
"""                RenderAt(muzzle + f * 180f + side * 20f + Vector3.up * 5f, look, "aam_gun_burst.png");
                RenderAt(muzzle + f * 70f + side * 7f + Vector3.up * 2f, muzzle + f * 150f, "aam_gun_close.png");"""))

# 4) 默认放大 4 倍
reps.append((
"""        private float cfgBulletScale = 3f;""",
"""        private float cfgBulletScale = 4f;"""))


ok = 0
for old, new in reps:
    o = old.replace("\n", NL); n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d anchor:\n%s\n---" % (c, o[:130])); continue
    s = s.replace(o, n, 1); ok += 1

print("applied %d / %d" % (ok, len(reps)))
if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f: f.write(s)
    print("WROTE", len(s), "chars")
else:
    print("NOT WRITTEN")
