using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Machine.Core
{
    public static class MachineLog
    {
        public static void Info(string m) { Log.Info(m); }
        public static void Warn(string m) { Log.Warn(m); }
        public static void Error(string m) { Log.Error(m); }
    }

    /// <summary>PNG → Texture2D。</summary>
    public static class TextureLoader
    {
        public static Texture2D FromPng(string name, byte[] png)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.name = name;
                if (tex.LoadImage(png)) return tex;
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            catch (Exception e) { MachineLog.Warn("Texture load failed " + name + ": " + e.Message); return null; }
        }
    }

    /// <summary>极简 Wavefront OBJ 解析器（顶点/UV/面）。</summary>
    public static class ObjLoader
    {
        public static Mesh Load(string objText)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var faces = new List<int[]>();
            string[] lines = objText.Split('\n');
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("v "))
                {
                    string[] p = line.Substring(2).Trim().Split(' ');
                    verts.Add(new Vector3(F(p, 0), F(p, 1), F(p, 2)));
                }
                else if (line.StartsWith("vt "))
                {
                    string[] p = line.Substring(3).Trim().Split(' ');
                    uvs.Add(new Vector2(F(p, 0), 1f - F(p, 1)));
                }
                else if (line.StartsWith("f "))
                {
                    string[] p = line.Substring(2).Trim().Split(' ');
                    var idx = new List<int>();
                    foreach (var tok in p)
                    {
                        if (tok.Length == 0) continue;
                        string first = tok.Split('/')[0];
                        int vi;
                        if (int.TryParse(first, out vi)) idx.Add(vi > 0 ? vi - 1 : verts.Count + vi);
                    }
                    if (idx.Count >= 3) faces.Add(idx.ToArray());
                }
            }
            if (verts.Count == 0 || faces.Count == 0) return null;
            var mesh = new Mesh();
            mesh.name = "MachineObjMesh";
            var tri = new List<int>();
            for (int i = 0; i < faces.Count; i++)
            {
                int[] f = faces[i];
                for (int j = 0; j + 2 < f.Length; j++) { tri.Add(f[0]); tri.Add(f[j + 1]); tri.Add(f[j + 2]); }
            }
            mesh.vertices = verts.ToArray();
            if (uvs.Count > 0)
            {
                var mapped = new Vector2[verts.Count];
                for (int i = 0; i < faces.Count; i++)
                {
                    int[] f = faces[i];
                    for (int j = 0; j < f.Length; j++)
                        if (f[j] < mapped.Length && f[j] < uvs.Count) mapped[f[j]] = uvs[f[j]];
                }
                mesh.uv = mapped;
            }
            mesh.triangles = tri.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float F(string[] p, int i)
        {
            float v;
            if (i < p.Length && float.TryParse(p[i], out v)) return v;
            return 0f;
        }

        /// <summary>无模型时的默认方块。</summary>
        public static Mesh Box()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh m = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return m;
        }
    }

    /// <summary>货物注册：新建 CargoType 并挂进全局 + 机场。管理器未就绪时返回 null（由 Registrar 重试）。</summary>
    public static class CargoRegistry
    {
        public static CargoType Register(Machine.Mod.CargoDefinition def)
        {
            try
            {
                var am = AirportManager.Instance;
                if (am == null) return null;
                CargoType existing = FindByName(am, def.Name);
                if (existing != null) { MachineLog.Info("cargo already exists: " + def.Name); return existing; }

                var ct = ScriptableObject.CreateInstance<CargoType>();
                ct.name = def.Id;
                ct.cargoName = def.Name;
                ct.basePrice = def.Price;
                ct.weight = def.Weight;
                ct.cargoSpace = def.CargoSpace;
                ct.fragile = def.Fragile;
                ct.expires = def.Expires;
                ct.expirationTime = def.ExpirationTime;
                if (def.IconPng != null) ct.icon = TextureLoader.FromPng(def.Id + "_icon", def.IconPng);

                am.allCargoTypes = Append(am.allCargoTypes, ct);
                if (am.airports != null)
                {
                    foreach (var ap in am.airports)
                    {
                        if (ap == null) continue;
                        ap.cargoType = Append(ap.cargoType, ct);
                    }
                }
                MachineLog.Info("cargo registered: " + def.Name + " (price=" + def.Price + ", weight=" + def.Weight + ")");
                return ct;
            }
            catch (Exception e) { MachineLog.Error("cargo register failed: " + e); return null; }
        }

        private static CargoType FindByName(AirportManager am, string name)
        {
            if (am.allCargoTypes == null) return null;
            foreach (var c in am.allCargoTypes) if (c != null && c.cargoName == name) return c;
            return null;
        }

        private static CargoType[] Append(CargoType[] arr, CargoType ct)
        {
            if (arr == null) return new CargoType[] { ct };
            var copy = new CargoType[arr.Length + 1];
            Array.Copy(arr, copy, arr.Length);
            copy[arr.Length] = ct;
            return copy;
        }
    }

    /// <summary>Machine 通用部件：继承游戏 PlanePart，支持重量统计与自定义物理扩展。</summary>
    public class MachinePart : PlanePart
    {
        public override PartStat[] GetPartStats()
        {
            var s = new PartStat();
            s.statName = "重量";
            s.SetValue(weight);
            return new PartStat[] { s };
        }

        public override void UpdatePart(PlaneContainer container) { }
    }

    /// <summary>部件注册：模型 → 预制体 → 注册表 + 部件栏按钮。管理器未就绪时返回 false（由 Registrar 重试）。</summary>
    public static class PartRegistry
    {
        public static bool Register(Machine.Mod.PartDefinition def)
        {
            try
            {
                if (UnityEngine.Object.FindFirstObjectByType<PartPrefabs>() == null) return false;
                var list = PartPrefabs.GetAllPrefabs();
                if (list == null) return false;
                var bar = UnityEngine.Object.FindFirstObjectByType<PartBar>();
                if (bar == null) return false;

                GameObject prefab = BuildPrefab(def);
                foreach (var g in list)
                {
                    if (g != null && g.name == prefab.name)
                    {
                        MachineLog.Info("part already exists: " + def.Name);
                        UnityEngine.Object.Destroy(prefab);
                        return true;
                    }
                }
                list.Add(prefab);
                MachineLog.Info("part registered: " + def.Name + " (weight=" + def.Weight + ")");
                SpawnBarButton(bar, prefab);
                return true;
            }
            catch (Exception e) { MachineLog.Error("part register failed: " + e); return false; }
        }

        private static void SpawnBarButton(PartBar bar, GameObject prefab)
        {
            try
            {
                if (bar.buttonPrefab == null || bar.placer == null) return;
                var go = (GameObject)UnityEngine.Object.Instantiate(bar.buttonPrefab, bar.transform);
                go.transform.localScale = Vector3.one;
                var pb = go.GetComponent<PartButton>();
                if (pb != null) pb.Init(bar.placer, prefab, IconGenerator.Instance);
            }
            catch (Exception e) { MachineLog.Warn("part bar button spawn failed: " + e.Message); }
        }

        public static GameObject BuildPrefab(Machine.Mod.PartDefinition def)
        {
            var go = new GameObject(def.Name);
            Mesh mesh = null;
            if (def.ModelObj != null && def.ModelObj.Length > 0)
            {
                try { mesh = ObjLoader.Load(System.Text.Encoding.UTF8.GetString(def.ModelObj)); } catch { mesh = null; }
            }
            if (mesh == null) mesh = ObjLoader.Box();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = CreateMaterial(def);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;

            var bp = go.AddComponent<BuildingPart>();
            bp.partName = def.Name;
            bp.price = def.Price;

            Type partType = ResolvePartClass(def.PartClass);
            var mp = (PlanePart)go.AddComponent(partType);
            mp.weight = def.Weight;

            if (Mathf.Abs(def.Scale - 1f) > 0.001f) go.transform.localScale = Vector3.one * def.Scale;
            return go;
        }

        /// <summary>在已加载的全部程序集中解析自定义部件行为类（允许 Mod 自带 PlanePart 子类）。</summary>
        private static Type ResolvePartClass(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return typeof(MachinePart);
            try
            {
                var t = Type.GetType(fullName);
                if (t != null && typeof(PlanePart).IsAssignableFrom(t)) return t;
            }
            catch { }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    var t = asm.GetType(fullName);
                    if (t != null && typeof(PlanePart).IsAssignableFrom(t)) return t;
                }
            }
            catch { }
            return typeof(MachinePart);
        }

        private static Material CreateMaterial(Machine.Mod.PartDefinition def)
        {
            Shader sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Unlit/Texture");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh != null ? sh : Shader.Find("Diffuse"));
            if (def.TexturePng != null)
            {
                var tex = TextureLoader.FromPng(def.Id + "_tex", def.TexturePng);
                if (tex != null)
                {
                    if (mat.HasProperty("_MainTex")) mat.mainTexture = tex;
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                }
            }
            return mat;
        }
    }

    /// <summary>贴花/预设材质注册。管理器未就绪时返回 false（由 Registrar 重试）。</summary>
    public static class DecalRegistry
    {
        public static bool Register(string name, byte[] png)
        {
            try
            {
                var pp = UnityEngine.Object.FindFirstObjectByType<PartPrefabs>();
                if (pp == null) return false;
                var tex = TextureLoader.FromPng(name, png);
                if (tex == null) return false;
                var list = new List<Texture2D>();
                if (pp.decals != null) list.AddRange(pp.decals);
                foreach (var d in list) if (d != null && d.name == name) return true;
                list.Add(tex);
                pp.decals = list.ToArray();
                MachineLog.Info("decal registered: " + name);
                return true;
            }
            catch (Exception e) { MachineLog.Warn("decal register failed " + name + ": " + e.Message); return false; }
        }
    }

    /// <summary>内容注册队列：管理器所在场景加载后自动完成注册（每帧重试，幂等）。</summary>
    public class Registrar
    {
        private class DecalItem
        {
            public string Name;
            public byte[] Png;
        }

        private List<Machine.Mod.CargoDefinition> _cargos = new List<Machine.Mod.CargoDefinition>();
        private List<Machine.Mod.PartDefinition> _parts = new List<Machine.Mod.PartDefinition>();
        private List<DecalItem> _decals = new List<DecalItem>();

        public bool HasPending
        {
            get { return _cargos.Count > 0 || _parts.Count > 0 || _decals.Count > 0; }
        }

        public void EnqueueCargo(Machine.Mod.CargoDefinition def) { _cargos.Add(def); }
        public void EnqueuePart(Machine.Mod.PartDefinition def) { _parts.Add(def); }
        public void EnqueueDecal(string name, byte[] png)
        {
            var d = new DecalItem();
            d.Name = name;
            d.Png = png;
            _decals.Add(d);
        }

        public void Update()
        {
            for (int i = _cargos.Count - 1; i >= 0; i--)
            {
                if (CargoRegistry.Register(_cargos[i]) != null) _cargos.RemoveAt(i);
            }
            for (int i = _parts.Count - 1; i >= 0; i--)
            {
                if (PartRegistry.Register(_parts[i])) _parts.RemoveAt(i);
            }
            for (int i = _decals.Count - 1; i >= 0; i--)
            {
                if (DecalRegistry.Register(_decals[i].Name, _decals[i].Png)) _decals.RemoveAt(i);
            }
        }
    }

    /// <summary>IMachineApi 的实现。</summary>
    public class MachineApi : Machine.Mod.IMachineApi
    {
        /// <summary>
        /// IMachineApi 契约版本，与 mod.json 里的 apiVersion 字段同源（Mods.cs 默认 "1.0"）。
        /// 供 Log.GetDiagnosticInfo 打印，方便排查"mod 要求的 API 和加载器提供的对不上"。
        /// </summary>
        public const string ApiVersion = "1.0";

        private MachineRuntime _rt;

        public MachineApi(MachineRuntime rt) { _rt = rt; }

        public void Log(string message) { MachineLog.Info("[mod] " + message); }

        public void RegisterCargo(Machine.Mod.CargoDefinition definition) { _rt.Registrar.EnqueueCargo(definition); }

        public void RegisterPart(Machine.Mod.PartDefinition definition) { _rt.Registrar.EnqueuePart(definition); }

        public void RegisterDecal(string decalName, byte[] pngData) { _rt.Registrar.EnqueueDecal(decalName, pngData); }

        public Texture2D LoadTexture(string name, byte[] pngData) { return TextureLoader.FromPng(name, pngData); }

        public string GetModsDirectory() { return _rt.ModsDir; }

        public void AddMainMenuButton(string text, Action onClick) { _rt.Menu.AddExtraButton(text, onClick); }

        public void OpenModManager() { _rt.OpenModManager(); }

        public GameObject CreatePartPrefab(Machine.Mod.PartDefinition definition) { return PartRegistry.BuildPrefab(definition); }
    }
}
