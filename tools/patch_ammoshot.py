# -*- coding: utf-8 -*-
# CheckOneAmmoModel: 销毁前给每个弹种拍一张模型特写
import io

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
with io.open(SRC, "r", encoding="utf-8", newline="") as f:
    s = f.read()
NL = "\r\n" if "\r\n" in s else "\n"
reps = []

# 1) try 外声明 bounds 变量
reps.append((
"""            string info = "exists=" + ex;
            GameObject m = null;
            try
""",
"""            string info = "exists=" + ex;
            GameObject m = null;
            Bounds shotB = new Bounds(); bool shotHas = false;
            try
"""))

# 2) try 内记录 bounds（复用循环算出的 b/has）
reps.append((
"""                        info += " build=ok parts=" + m.transform.childCount + " renderers=" + rr.Length;
                        if (has) info += " size=" + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2")
                                             + "x" + b.size.z.ToString("F2");
""",
"""                        info += " build=ok parts=" + m.transform.childCount + " renderers=" + rr.Length;
                        if (has)
                        {
                            info += " size=" + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2")
                                             + "x" + b.size.z.ToString("F2");
                            shotB = b; shotHas = true;
                        }
"""))

# 3) finally：销毁前拍特写（模型抬到空中干净背景，正侧后方 3/4 视角）
reps.append((
"""            catch (Exception e) { info += " build=EX " + e.Message; }
            finally { if (m != null) UnityEngine.Object.Destroy(m); }
""",
"""            catch (Exception e) { info += " build=EX " + e.Message; }
            finally
            {
                if (m != null)
                {
                    try
                    {
                        if (cfgCapture && shotHas)
                        {
                            Vector3 c = shotB.center + Vector3.up * 400f;   // 抬到空中，避开地面杂景
                            m.transform.position += Vector3.up * 400f;
                            float r = Mathf.Max(shotB.extents.x, Mathf.Max(shotB.extents.y, shotB.extents.z));
                            Vector3 dir = m.transform.forward;
                            Vector3 side = Vector3.Cross(dir, Vector3.up).normalized;
                            if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                            Vector3 cam = c - dir * r * 2.2f + side * r * 2.6f + Vector3.up * r * 0.9f;
                            string fn = "aam_model_" + sp.Name.Replace(" ", "_").Replace("/", "-") + ".png";
                            RenderAt(cam, c, fn);
                            _api.Log("AAM AMMO: model shot -> " + fn + " center=" + c + " r=" + r.ToString("F2"));
                        }
                    }
                    catch (Exception e2) { _api.Log("AAM AMMO: model shot failed " + e2.Message); }
                    UnityEngine.Object.Destroy(m);
                }
            }
"""))

ok = 0
for old, new in reps:
    o = old.replace("\n", NL); n = new.replace("\n", NL)
    c = s.count(o)
    if c != 1:
        print("!! MATCH FAIL count=%d anchor:\n%s\n---" % (c, o[:120])); continue
    s = s.replace(o, n, 1); ok += 1

print("applied %d / %d" % (ok, len(reps)))
if ok == len(reps):
    with io.open(SRC, "w", encoding="utf-8", newline="") as f: f.write(s)
    print("WROTE", len(s), "chars")
else:
    print("NOT WRITTEN")
