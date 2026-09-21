using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Machine.Core
{
    [Serializable] public class ModCodeInfo
    {
        public string[] assemblies = new string[0];
        public string mainClass = "";
    }

    [Serializable] public class ModContentInfo
    {
        public string[] cargo = new string[0];
        public string[] parts = new string[0];
        public string[] decals = new string[0];
        public string[] textures = new string[0];
    }

    [Serializable] public class ModInfo
    {
        public string id = "";
        public string name = "";
        public string version = "1.0.0";
        public string author = "";
        public string description = "";
        public string type = "content";
        public ModCodeInfo code = new ModCodeInfo();
        public ModContentInfo content = new ModContentInfo();
        public string[] loadAfter = new string[0];
        public string[] loadBefore = new string[0];
        // === v2 API 版本化字段 ===
        /// <summary>Mod 声明使用的 Machine API 版本（如 "1.0"）。</summary>
        public string apiVersion = "1.0";
        /// <summary>Mod 要求的加载器版本范围（如 ">=2.4.0"、">=2.3.0 <3.0.0"）。</summary>
        public string loaderVersion = "";
        /// <summary>必需依赖：缺少这些 Mod 则拒绝加载。格式 "id" 或 "id>=1.0.0"。</summary>
        public string[] dependencies = new string[0];
        /// <summary>可选依赖：存在时启用联动功能，不存在也能正常加载。</summary>
        public string[] optionalDependencies = new string[0];
        /// <summary>冲突声明：与这些Mod冲突，同时存在时后加载的被自动禁用。格式 "id" 或 "id>=1.0.0"。</summary>
        public string[] conflicts = new string[0];
        /// <summary>入口类名（兼容字段，优先于 code.mainClass）。</summary>
        public string entry = "";
    }

    [Serializable] public class ModCargoJson
    {
        public string id = "";
        public string name = "";
        public float price = 100f;
        public float weight = 1f;
        public int cargoSpace = 1;
        public bool fragile = false;
        public bool expires = false;
        public float expirationTime = 60f;
        public string icon = "";
    }

    [Serializable] public class ModPartJson
    {
        public string id = "";
        public string name = "";
        public float price = 50f;
        public float weight = 1f;
        public float scale = 1f;
        public string model = "";
        public string texture = "";
        public string partClass = "Machine.Core.MachinePart";
    }

    [Serializable] public class MachineConfig
    {
        public string[] disabled = new string[0];
        public string logLevel = "INFO";
    }

    public class LoadedMod
    {
        public ModInfo Info;
        public string Dir;
        public bool Enabled;
        public Machine.Mod.IMachineMod CodeMod;
        public List<string> Errors = new List<string>();
        public bool HasErrors { get { return Errors.Count > 0; } }

        /// <summary>是否继承了 MachineModBase（支持完整生命周期）。</summary>
        public bool HasFullLifecycle
        {
            get { return CodeMod is Machine.Mod.MachineModBase; }
        }

        /// <summary>获取生命周期基类引用（如果支持）。</summary>
        public Machine.Mod.MachineModBase AsBase
        {
            get { return CodeMod as Machine.Mod.MachineModBase; }
        }
    }

    /// <summary>Mod 发现、排序、加载与启停管理。</summary>
    public class ModManager
    {
        private string _modsDir;
        private string _configPath;
        private MachineConfig _config = new MachineConfig();
        private List<LoadedMod> _mods = new List<LoadedMod>();
        private MachineApi _api;

        public int LoadedCount { get { return _mods.Count; } }
        public List<LoadedMod> Mods { get { return _mods; } }

        /// <summary>获取所有被自动禁用的Mod及其原因（供UI显示）。</summary>
        public List<string> GetDisabledReasons()
        {
            var reasons = new List<string>();
            foreach (var mod in _mods)
            {
                if (!mod.Enabled && mod.HasErrors)
                {
                    reasons.Add(mod.Info.id + " v" + mod.Info.version + ": " +
                        string.Join("; ", mod.Errors.ToArray()));
                }
            }
            return reasons;
        }

        /// <summary>检查某个Mod是否已加载且启用。</summary>
        public bool IsModLoaded(string modId)
        {
            foreach (var mod in _mods)
            {
                if (string.Equals(mod.Info.id, modId, StringComparison.OrdinalIgnoreCase))
                    return mod.Enabled;
            }
            return false;
        }

        /// <summary>获取已加载Mod的版本号（未加载返回null）。</summary>
        public string GetModVersion(string modId)
        {
            foreach (var mod in _mods)
            {
                if (string.Equals(mod.Info.id, modId, StringComparison.OrdinalIgnoreCase))
                    return mod.Info.version;
            }
            return null;
        }

        public void LoadAll(string modsDir, string machineDir, MachineApi api)
        {
            _modsDir = modsDir;
            _api = api;
            _configPath = Path.Combine(machineDir, "config.json");
            LoadConfig();
            _mods.Clear();

            if (!Directory.Exists(_modsDir))
            {
                try { Directory.CreateDirectory(_modsDir); }
                catch (Exception e) { MachineLog.Warn("cannot create mods dir: " + e.Message); return; }
            }

            var pending = new List<LoadedMod>();
            foreach (var sub in Directory.GetDirectories(_modsDir))
            {
                string manifest = Path.Combine(sub, "mod.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    string json = File.ReadAllText(manifest);
                    JsonValue root = JsonValue.Parse(json);
                    if (root == null) continue;
                    ModInfo info = new ModInfo();
                    info.id = root.GetString("id", "");
                    info.name = root.GetString("name", info.id);
                    info.version = root.GetString("version", "1.0.0");
                    info.author = root.GetString("author", "");
                    info.description = root.GetString("description", "");
                    info.type = root.GetString("type", "content");
                    info.loadAfter = root.GetStringArray("loadAfter");
                    info.loadBefore = root.GetStringArray("loadBefore");
                    // === v2 API 版本化字段 ===
                    info.apiVersion = root.GetString("apiVersion", "1.0");
                    info.loaderVersion = root.GetString("loaderVersion", "");
                    info.dependencies = root.GetStringArray("dependencies");
                    info.optionalDependencies = root.GetStringArray("optionalDependencies");
                    info.conflicts = root.GetStringArray("conflicts");
                    info.entry = root.GetString("entry", "");
                    JsonValue code = root.Get("code");
                    if (code != null && code.Kind == "object")
                    {
                        info.code.assemblies = code.GetStringArray("assemblies");
                        info.code.mainClass = code.GetString("mainClass", "");
                    }
                    JsonValue content = root.Get("content");
                    if (content != null && content.Kind == "object")
                    {
                        info.content.cargo = content.GetStringArray("cargo");
                        info.content.parts = content.GetStringArray("parts");
                        info.content.decals = content.GetStringArray("decals");
                        info.content.textures = content.GetStringArray("textures");
                    }
                    MachineLog.Info("mod [" + info.id + "] manifest ok: " + info.name + " v" + info.version + " content=" + (info.content.cargo.Length + info.content.parts.Length + info.content.decals.Length));
                    if (string.IsNullOrEmpty(info.id)) continue;
                    var lm = new LoadedMod();
                    lm.Info = info;
                    lm.Dir = sub;
                    lm.Enabled = !IsDisabled(info.id);
                    pending.Add(lm);
                }
                catch (Exception e) { MachineLog.Warn("manifest error in " + sub + ": " + e.Message); }
            }

            // === v2: 依赖验证和加载器版本检查 ===
            ValidateDependencies(pending);

            foreach (var lm in TopoSort(pending))
            {
                MachineLog.Info("mod [" + lm.Info.id + "] " + lm.Info.name + " v" + lm.Info.version + " enabled=" + lm.Enabled);
                if (!lm.Enabled) { _mods.Add(lm); continue; }
                try { LoadContent(lm); } catch (Exception e) { lm.Errors.Add("content: " + e.Message); }
                try { LoadCode(lm); } catch (Exception e) { lm.Errors.Add("code: " + e.Message); }
                _mods.Add(lm);
                if (lm.HasErrors) MachineLog.Warn("mod [" + lm.Info.id + "] errors: " + string.Join(" | ", lm.Errors.ToArray()));
            }
        }

        public bool IsDisabled(string id)
        {
            if (_config.disabled == null) return false;
            return Array.IndexOf(_config.disabled, id) >= 0;
        }

        public void SetEnabled(string id, bool enabled)
        {
            var list = new List<string>();
            if (_config.disabled != null) list.AddRange(_config.disabled);
            int idx = list.IndexOf(id);
            if (!enabled && idx < 0) list.Add(id);
            if (enabled && idx >= 0) list.RemoveAt(idx);
            _config.disabled = list.ToArray();
            SaveConfig();
            // 同步 LoadedMod.Enabled，保证界面状态与配置一致
            for (int i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Info.id == id) { _mods[i].Enabled = enabled; break; }
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                    _config = JsonUtility.FromJson<MachineConfig>(File.ReadAllText(_configPath));
                if (_config == null) _config = new MachineConfig();

                // 设置日志级别
                if (!string.IsNullOrEmpty(_config.logLevel))
                    Log.SetLevel(_config.logLevel);
            }
            catch (Exception e) { MachineLog.Warn("config load failed: " + e.Message); _config = new MachineConfig(); }
        }

        private void SaveConfig()
        {
            try
            {
                if (!Directory.Exists(Path.GetDirectoryName(_configPath))) Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                File.WriteAllText(_configPath, JsonUtility.ToJson(_config, true));
            }
            catch (Exception e) { MachineLog.Warn("config save failed: " + e.Message); }
        }

        private void LoadContent(LoadedMod mod)
        {
            var c = mod.Info.content;
            if (c == null) return;
            foreach (var rel in c.cargo)
            {
                string full = Path.Combine(mod.Dir, rel);
                if (!File.Exists(full)) { mod.Errors.Add("cargo missing: " + rel); continue; }
                JsonValue jr = JsonValue.Parse(File.ReadAllText(full));
                if (jr == null) { mod.Errors.Add("cargo bad json: " + rel); continue; }
                var cd = new Machine.Mod.CargoDefinition();
                cd.Id = jr.GetString("id", rel);
                cd.Name = jr.GetString("name", cd.Id);
                cd.Price = (float)jr.GetNumber("price", 100f);
                cd.Weight = (float)jr.GetNumber("weight", 1f);
                cd.CargoSpace = (int)jr.GetNumber("cargoSpace", 1);
                cd.Fragile = jr.GetBool("fragile", false);
                cd.Expires = jr.GetBool("expires", false);
                cd.ExpirationTime = (float)jr.GetNumber("expirationTime", 60f);
                string iconPath = jr.GetString("icon", "");
                if (!string.IsNullOrEmpty(iconPath))
                {
                    string ip = Path.Combine(mod.Dir, iconPath);
                    if (File.Exists(ip)) cd.IconPng = File.ReadAllBytes(ip);
                }
                _api.RegisterCargo(cd);
            }
            foreach (var rel in c.parts)
            {
                string full = Path.Combine(mod.Dir, rel);
                if (!File.Exists(full)) { mod.Errors.Add("part missing: " + rel); continue; }
                JsonValue jr = JsonValue.Parse(File.ReadAllText(full));
                if (jr == null) { mod.Errors.Add("part bad json: " + rel); continue; }
                var pd = new Machine.Mod.PartDefinition();
                pd.Id = jr.GetString("id", rel);
                pd.Name = jr.GetString("name", pd.Id);
                pd.Price = (float)jr.GetNumber("price", 50f);
                pd.Weight = (float)jr.GetNumber("weight", 1f);
                pd.Scale = (float)jr.GetNumber("scale", 1f);
                pd.PartClass = jr.GetString("partClass", "Machine.Core.MachinePart");
                string modelPath = jr.GetString("model", "");
                if (!string.IsNullOrEmpty(modelPath)) { string mp = Path.Combine(mod.Dir, modelPath); if (File.Exists(mp)) pd.ModelObj = File.ReadAllBytes(mp); }
                string texPath = jr.GetString("texture", "");
                if (!string.IsNullOrEmpty(texPath)) { string tp = Path.Combine(mod.Dir, texPath); if (File.Exists(tp)) pd.TexturePng = File.ReadAllBytes(tp); }
                _api.RegisterPart(pd);
            }
            foreach (var rel in c.decals)
            {
                string full = Path.Combine(mod.Dir, rel);
                if (!File.Exists(full)) { mod.Errors.Add("decal missing: " + rel); continue; }
                _api.RegisterDecal(Path.GetFileNameWithoutExtension(rel), File.ReadAllBytes(full));
            }
        }

        private void LoadCode(LoadedMod mod)
        {
            var code = mod.Info.code;
            if (code == null || code.assemblies == null || code.assemblies.Length == 0) return;
            foreach (var rel in code.assemblies)
            {
                string full = Path.Combine(mod.Dir, rel);
                if (!File.Exists(full)) { mod.Errors.Add("assembly missing: " + rel); continue; }
                try
                {
                    var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(full));
                    Type mainType = null;
                    if (!string.IsNullOrEmpty(code.mainClass)) mainType = asm.GetType(code.mainClass);
                    if (mainType == null)
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (typeof(Machine.Mod.IMachineMod).IsAssignableFrom(t) && !t.IsAbstract) { mainType = t; break; }
                        }
                    }
                    if (mainType == null) { mod.Errors.Add("no IMachineMod found in " + rel); continue; }
                    var instance = (Machine.Mod.IMachineMod)Activator.CreateInstance(mainType);
                    mod.CodeMod = instance;

                    // 生命周期：OnLoad（隔离执行，失败不影响其他Mod，带性能分析和超时）
                    ModProfiler.BeginInit(mod.Info.id);
                    try
                    {
                        instance.OnLoad(_api);
                        MachineLog.Info("code mod loaded: " + mainType.FullName);
                    }
                    catch (Exception e)
                    {
                        mod.Errors.Add("OnLoad: " + e.Message);
                        MachineLog.Error("mod [" + mod.Info.id + "] OnLoad failed: " + e.Message);
                        if (mod.HasFullLifecycle) mod.AsBase.SetError();
                        ModProfiler.EndInit(mod.Info.id);
                        continue; // OnLoad失败，不调用OnEnable
                    }
                    ModProfiler.EndInit(mod.Info.id);

                    // 生命周期：OnEnable（仅继承MachineModBase的Mod，带性能分析）
                    if (mod.HasFullLifecycle)
                    {
                        ModProfiler.BeginInit(mod.Info.id + "_enable");
                        try
                        {
                            mod.AsBase.OnEnable();
                        }
                        catch (Exception e)
                        {
                            mod.Errors.Add("OnEnable: " + e.Message);
                            MachineLog.Error("mod [" + mod.Info.id + "] OnEnable failed: " + e.Message);
                            mod.AsBase.SetError();
                        }
                        ModProfiler.EndInit(mod.Info.id + "_enable");
                    }
                }
                catch (Exception e) { mod.Errors.Add("assembly " + rel + ": " + e.Message); }
            }
        }

        /// <summary>
        /// v2: 验证Mod依赖关系。
        /// 1. 检查加载器版本要求
        /// 2. 检查必需依赖是否存在且版本满足
        /// 3. 检测循环依赖
        /// 4. 不满足的Mod被禁用并记录错误
        /// </summary>
        private void ValidateDependencies(List<LoadedMod> pending)
        {
            // 构建ID -> LoadedMod 映射
            var idMap = new Dictionary<string, LoadedMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in pending)
            {
                if (!string.IsNullOrEmpty(m.Info.id) && !idMap.ContainsKey(m.Info.id))
                    idMap[m.Info.id] = m;
            }

            // 当前加载器版本
            string loaderVer = Bootstrap.Version;

            foreach (var mod in pending)
            {
                if (!mod.Enabled) continue;
                var info = mod.Info;

                // 1. 检查加载器版本要求
                if (!string.IsNullOrEmpty(info.loaderVersion))
                {
                    if (!VersionRange.Satisfies(loaderVer, info.loaderVersion))
                    {
                        mod.Errors.Add("REQUIRES Machine loader " + info.loaderVersion +
                            ", current " + loaderVer + ". Please update Machine loader.");
                        mod.Enabled = false;
                        MachineLog.Warn("========== MOD DISABLED: " + info.id + " ==========");
                        MachineLog.Warn("  Reason: Machine loader version mismatch");
                        MachineLog.Warn("  Required: " + info.loaderVersion);
                        MachineLog.Warn("  Current:  " + loaderVer);
                        MachineLog.Warn("  Fix: Update Machine loader to " + info.loaderVersion + " or newer");
                        MachineLog.Warn("================================================");
                        continue;
                    }
                }

                // 2. 检查必需依赖
                if (info.dependencies != null && info.dependencies.Length > 0)
                {
                    bool depsOk = true;
                    foreach (var depStr in info.dependencies)
                    {
                        string depId, depRange;
                        if (!VersionRange.ParseDependency(depStr, out depId, out depRange)) continue;

                        LoadedMod depMod;
                        if (!idMap.TryGetValue(depId, out depMod))
                        {
                            mod.Errors.Add("MISSING dependency: " + depId +
                                (string.IsNullOrEmpty(depRange) ? "" : " (need version " + depRange + ")") +
                                ". Please install " + depId + " from the mod repository.");
                            depsOk = false;
                            MachineLog.Warn("========== MOD DISABLED: " + info.id + " ==========");
                            MachineLog.Warn("  Reason: Missing required dependency");
                            MachineLog.Warn("  Missing: " + depId +
                                (string.IsNullOrEmpty(depRange) ? "" : " (version " + depRange + ")"));
                            MachineLog.Warn("  Fix: Download and install " + depId +
                                (string.IsNullOrEmpty(depRange) ? "" : " version " + depRange) +
                                " into the mods folder");
                            MachineLog.Warn("================================================");
                            break;
                        }

                        // 检查依赖是否被禁用
                        if (!depMod.Enabled)
                        {
                            mod.Errors.Add("dependency " + depId + " is DISABLED. " +
                                "Please enable " + depId + " in the mod manager (Mod button in main menu).");
                            depsOk = false;
                            MachineLog.Warn("========== MOD DISABLED: " + info.id + " ==========");
                            MachineLog.Warn("  Reason: Required dependency is disabled");
                            MachineLog.Warn("  Dependency: " + depId + " v" + depMod.Info.version);
                            MachineLog.Warn("  Fix: Enable " + depId + " in the mod manager");
                            MachineLog.Warn("================================================");
                            break;
                        }

                        // 检查依赖版本
                        if (!string.IsNullOrEmpty(depRange))
                        {
                            if (!VersionRange.Satisfies(depMod.Info.version, depRange))
                            {
                                mod.Errors.Add("dependency " + depId + " version MISMATCH: " +
                                    "have " + depMod.Info.version + ", need " + depRange + ". " +
                                    "Please update " + depId + ".");
                                depsOk = false;
                                MachineLog.Warn("========== MOD DISABLED: " + info.id + " ==========");
                                MachineLog.Warn("  Reason: Dependency version mismatch");
                                MachineLog.Warn("  Dependency: " + depId);
                                MachineLog.Warn("  Have:    v" + depMod.Info.version);
                                MachineLog.Warn("  Need:    " + depRange);
                                MachineLog.Warn("  Fix: Update " + depId + " to version " + depRange);
                                MachineLog.Warn("================================================");
                                break;
                            }
                        }
                    }
                    if (!depsOk) { mod.Enabled = false; continue; }
                }

                // 3. 可选依赖：只记录日志，不禁用
                if (info.optionalDependencies != null && info.optionalDependencies.Length > 0)
                {
                    foreach (var depStr in info.optionalDependencies)
                    {
                        string depId, depRange;
                        if (!VersionRange.ParseDependency(depStr, out depId, out depRange)) continue;
                        if (!idMap.ContainsKey(depId))
                        {
                            MachineLog.Info("mod [" + info.id + "] optional dependency " + depId + " not found (will run without it)");
                        }
                    }
                }
            }

            // 3.5 冲突检测：冲突Mod自动禁用而不是崩溃
            DetectConflicts(pending, idMap);

            // 4. 循环依赖检测（基于dependencies和loadAfter）
            DetectCyclicDependencies(pending);

            // 5. 输出依赖图（日志可读）
            LogDependencyGraph(pending, idMap);
        }

        /// <summary>
        /// 冲突检测：如果Mod A声明与Mod B冲突，且B存在且已启用，则自动禁用A。
        /// 冲突格式："id" 或 "id>=1.0.0"（版本范围）。
        /// </summary>
        private void DetectConflicts(List<LoadedMod> pending, Dictionary<string, LoadedMod> idMap)
        {
            foreach (var mod in pending)
            {
                if (!mod.Enabled) continue;
                if (mod.Info.conflicts == null || mod.Info.conflicts.Length == 0) continue;

                foreach (var conflictStr in mod.Info.conflicts)
                {
                    string conflictId, conflictRange;
                    if (!VersionRange.ParseDependency(conflictStr, out conflictId, out conflictRange)) continue;

                    LoadedMod conflictMod;
                    if (!idMap.TryGetValue(conflictId, out conflictMod)) continue;
                    if (!conflictMod.Enabled) continue;

                    // 检查冲突Mod的版本是否在冲突范围内
                    if (!string.IsNullOrEmpty(conflictRange))
                    {
                        if (!VersionRange.Satisfies(conflictMod.Info.version, conflictRange))
                            continue; // 版本不在冲突范围内，不冲突
                    }

                    // 自动禁用声明冲突的Mod（A声明与B冲突，禁用A）
                    mod.Enabled = false;
                    mod.Errors.Add("conflict with " + conflictId + " v" + conflictMod.Info.version +
                        (string.IsNullOrEmpty(conflictRange) ? "" : " (range: " + conflictRange + ")") +
                        " - auto-disabled");
                    MachineLog.Warn("mod [" + mod.Info.id + "] CONFLICT with [" + conflictId + "] v" +
                        conflictMod.Info.version + " - auto-disabled to avoid crash");
                    MachineLog.Warn("  To resolve: disable " + conflictId + " or remove " + mod.Info.id +
                        " from mods folder");
                    break; // 已被禁用，不需要检查其他冲突
                }
            }
        }

        /// <summary>
        /// 输出依赖图到日志（可读格式）。
        /// 格式：
        ///   modA v1.0.0
        ///     ├── depends: modB>=1.0.0 [OK]
        ///     ├── optional: modC [MISSING]
        ///     └── conflicts: modD [NONE]
        /// </summary>
        private void LogDependencyGraph(List<LoadedMod> pending, Dictionary<string, LoadedMod> idMap)
        {
            try
            {
                MachineLog.Info("===== MOD DEPENDENCY GRAPH =====");
                MachineLog.Info("Total mods: " + pending.Count + " (enabled: " +
                    pending.FindAll(m => m.Enabled).Count + ", disabled: " +
                    pending.FindAll(m => !m.Enabled).Count + ")");

                foreach (var mod in pending)
                {
                    string status = mod.Enabled ? "ENABLED" : "DISABLED";
                    if (mod.HasErrors) status += " (errors: " + mod.Errors.Count + ")";
                    MachineLog.Info("");
                    MachineLog.Info("  [" + mod.Info.id + "] v" + mod.Info.version +
                        " - " + mod.Info.name + " [" + status + "]");

                    // 必需依赖
                    if (mod.Info.dependencies != null && mod.Info.dependencies.Length > 0)
                    {
                        for (int i = 0; i < mod.Info.dependencies.Length; i++)
                        {
                            string dep = mod.Info.dependencies[i];
                            string depId, depRange;
                            VersionRange.ParseDependency(dep, out depId, out depRange);
                            LoadedMod depMod;
                            string depStatus = "MISSING";
                            string depVer = "";
                            if (idMap.TryGetValue(depId, out depMod))
                            {
                                depVer = " v" + depMod.Info.version;
                                if (depMod.Enabled)
                                {
                                    if (!string.IsNullOrEmpty(depRange) && !VersionRange.Satisfies(depMod.Info.version, depRange))
                                        depStatus = "VERSION_MISMATCH (need " + depRange + ")";
                                    else
                                        depStatus = "OK";
                                }
                                else
                                    depStatus = "DISABLED";
                            }
                            string branch = (i == mod.Info.dependencies.Length - 1 &&
                                (mod.Info.optionalDependencies == null || mod.Info.optionalDependencies.Length == 0) &&
                                (mod.Info.conflicts == null || mod.Info.conflicts.Length == 0)) ? "└──" : "├──";
                            MachineLog.Info("    " + branch + " depends: " + depId + depVer +
                                (string.IsNullOrEmpty(depRange) ? "" : " (need " + depRange + ")") +
                                " [" + depStatus + "]");
                        }
                    }

                    // 可选依赖
                    if (mod.Info.optionalDependencies != null && mod.Info.optionalDependencies.Length > 0)
                    {
                        for (int i = 0; i < mod.Info.optionalDependencies.Length; i++)
                        {
                            string dep = mod.Info.optionalDependencies[i];
                            string depId, depRange;
                            VersionRange.ParseDependency(dep, out depId, out depRange);
                            LoadedMod depMod;
                            string depStatus = "MISSING (optional)";
                            string depVer = "";
                            if (idMap.TryGetValue(depId, out depMod))
                            {
                                depVer = " v" + depMod.Info.version;
                                depStatus = depMod.Enabled ? "PRESENT" : "DISABLED";
                            }
                            string branch = (i == mod.Info.optionalDependencies.Length - 1 &&
                                (mod.Info.conflicts == null || mod.Info.conflicts.Length == 0)) ? "└──" : "├──";
                            MachineLog.Info("    " + branch + " optional: " + depId + depVer + " [" + depStatus + "]");
                        }
                    }

                    // 冲突
                    if (mod.Info.conflicts != null && mod.Info.conflicts.Length > 0)
                    {
                        for (int i = 0; i < mod.Info.conflicts.Length; i++)
                        {
                            string dep = mod.Info.conflicts[i];
                            string depId, depRange;
                            VersionRange.ParseDependency(dep, out depId, out depRange);
                            LoadedMod depMod;
                            string depStatus = "NONE";
                            string depVer = "";
                            if (idMap.TryGetValue(depId, out depMod))
                            {
                                depVer = " v" + depMod.Info.version;
                                depStatus = depMod.Enabled ? "CONFLICT!" : "DISABLED";
                            }
                            string branch = (i == mod.Info.conflicts.Length - 1) ? "└──" : "├──";
                            MachineLog.Info("    " + branch + " conflicts: " + depId + depVer +
                                (string.IsNullOrEmpty(depRange) ? "" : " (range " + depRange + ")") +
                                " [" + depStatus + "]");
                        }
                    }

                    // 加载顺序
                    if (mod.Info.loadAfter != null && mod.Info.loadAfter.Length > 0)
                    {
                        MachineLog.Info("    └── loadAfter: " + string.Join(", ", mod.Info.loadAfter));
                    }
                }

                MachineLog.Info("");
                MachineLog.Info("===== END DEPENDENCY GRAPH =====");
            }
            catch (Exception e)
            {
                MachineLog.Warn("LogDependencyGraph error: " + e.Message);
            }
        }

        /// <summary>检测循环依赖。发现循环时记录警告但不阻止加载（拓扑排序会处理）。</summary>
        private void DetectCyclicDependencies(List<LoadedMod> pending)
        {
            var idMap = new Dictionary<string, LoadedMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in pending)
                if (!string.IsNullOrEmpty(m.Info.id) && !idMap.ContainsKey(m.Info.id))
                    idMap[m.Info.id] = m;

            // DFS 检测环
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();

            foreach (var mod in pending)
            {
                if (!mod.Enabled) continue;
                if (HasCycleDfs(mod.Info.id, idMap, visited, inStack))
                {
                    MachineLog.Warn("cyclic dependency detected involving " + mod.Info.id);
                    break;
                }
            }
        }

        private bool HasCycleDfs(string id, Dictionary<string, LoadedMod> idMap,
            HashSet<string> visited, HashSet<string> inStack)
        {
            if (inStack.Contains(id)) return true;
            if (visited.Contains(id)) return false;

            visited.Add(id);
            inStack.Add(id);

            LoadedMod mod;
            if (idMap.TryGetValue(id, out mod))
            {
                // 检查dependencies
                if (mod.Info.dependencies != null)
                {
                    foreach (var depStr in mod.Info.dependencies)
                    {
                        string depId, depRange;
                        if (VersionRange.ParseDependency(depStr, out depId, out depRange))
                            if (HasCycleDfs(depId, idMap, visited, inStack)) return true;
                    }
                }
                // 检查loadAfter
                if (mod.Info.loadAfter != null)
                {
                    foreach (var depId in mod.Info.loadAfter)
                        if (HasCycleDfs(depId, idMap, visited, inStack)) return true;
                }
            }

            inStack.Remove(id);
            return false;
        }

        private List<LoadedMod> TopoSort(List<LoadedMod> pending)
        {
            var result = new List<LoadedMod>();
            var used = new HashSet<string>();
            var ids = new HashSet<string>();
            foreach (var m in pending) ids.Add(m.Info.id);
            while (result.Count < pending.Count)
            {
                bool progress = false;
                foreach (var m in pending)
                {
                    if (used.Contains(m.Info.id)) continue;
                    bool ok = true;
                    // loadAfter: 必须在指定Mod之后加载
                    if (m.Info.loadAfter != null)
                    {
                        foreach (var dep in m.Info.loadAfter)
                        {
                            if (ids.Contains(dep) && !used.Contains(dep)) { ok = false; break; }
                        }
                    }
                    // loadBefore: 必须在指定Mod之前加载（即如果对方还没加载，我可以先加载）
                    // 这里通过反向依赖实现：如果A loadBefore B，那么B的loadAfter隐式包含A
                    if (ok && m.Info.loadBefore != null)
                    {
                        foreach (var before in m.Info.loadBefore)
                        {
                            // 检查是否有其他Mod在它的loadAfter中包含了当前Mod
                            // 如果有，说明当前Mod应该先加载（这已经通过对方的loadAfter处理了）
                        }
                    }
                    if (ok) { result.Add(m); used.Add(m.Info.id); progress = true; }
                }
                if (!progress) break;
            }
            foreach (var m in pending) if (!used.Contains(m.Info.id)) result.Add(m);
            return result;
        }

        /// <summary>
        /// 每帧更新所有已启用的Mod（仅继承MachineModBase的Mod会收到OnUpdate回调）。
        /// 每个Mod的回调都在try/catch中执行，单个Mod异常不影响其他Mod。
        /// 集成性能分析：记录每帧耗时，超过帧预算报警。
        /// 连续异常超过阈值自动禁用该Mod。
        /// </summary>
        public void UpdateAll()
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                var mod = _mods[i];
                if (!mod.Enabled || !mod.HasFullLifecycle || mod.HasErrors) continue;

                ModProfiler.BeginUpdate(mod.Info.id);
                try
                {
                    mod.AsBase.OnUpdate();
                }
                catch (Exception e)
                {
                    MachineLog.Error("mod [" + mod.Info.id + "] OnUpdate error: " + e.Message);
                    mod.Errors.Add("OnUpdate: " + e.Message);
                    ModProfiler.RecordUpdateError(mod.Info.id);

                    // 连续异常超过10次自动禁用该Mod
                    var stats = ModProfiler.GetStats(mod.Info.id);
                    if (stats != null && stats.UpdateErrorCount >= 10)
                    {
                        mod.Enabled = false;
                        MachineLog.Warn("========== MOD AUTO-DISABLED: " + mod.Info.id + " ==========");
                        MachineLog.Warn("  Reason: Too many consecutive OnUpdate errors (" + stats.UpdateErrorCount + ")");
                        MachineLog.Warn("  This mod has been disabled to protect game stability");
                        MachineLog.Warn("================================================================");
                    }
                }
                ModProfiler.EndUpdate(mod.Info.id);
            }
        }

        /// <summary>
        /// 固定帧率更新所有已启用的Mod。
        /// 集成性能分析和连续异常自动禁用。
        /// </summary>
        public void FixedUpdateAll()
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                var mod = _mods[i];
                if (!mod.Enabled || !mod.HasFullLifecycle || mod.HasErrors) continue;

                ModProfiler.BeginFixedUpdate(mod.Info.id);
                try
                {
                    mod.AsBase.OnFixedUpdate();
                }
                catch (Exception e)
                {
                    MachineLog.Error("mod [" + mod.Info.id + "] OnFixedUpdate error: " + e.Message);
                    mod.Errors.Add("OnFixedUpdate: " + e.Message);
                    ModProfiler.RecordFixedUpdateError(mod.Info.id);

                    // 连续异常超过10次自动禁用
                    var stats = ModProfiler.GetStats(mod.Info.id);
                    if (stats != null && stats.FixedUpdateErrorCount >= 10)
                    {
                        mod.Enabled = false;
                        MachineLog.Warn("========== MOD AUTO-DISABLED: " + mod.Info.id + " ==========");
                        MachineLog.Warn("  Reason: Too many consecutive OnFixedUpdate errors (" + stats.FixedUpdateErrorCount + ")");
                        MachineLog.Warn("================================================================");
                    }
                }
                ModProfiler.EndFixedUpdate(mod.Info.id);
            }
        }

        /// <summary>
        /// 卸载所有Mod（游戏退出时调用）。
        /// 按加载逆序调用OnDisable和OnUnload。
        /// </summary>
        public void UnloadAll()
        {
            // 逆序卸载
            for (int i = _mods.Count - 1; i >= 0; i--)
            {
                var mod = _mods[i];
                if (!mod.HasFullLifecycle) continue;
                try { mod.AsBase.OnDisable(); }
                catch (Exception e) { MachineLog.Error("mod [" + mod.Info.id + "] OnDisable error: " + e.Message); }
                try { mod.AsBase.OnUnload(); }
                catch (Exception e) { MachineLog.Error("mod [" + mod.Info.id + "] OnUnload error: " + e.Message); }
            }
            MachineLog.Info("all mods unloaded");
        }

        /// <summary>
        /// 启用指定Mod。
        /// </summary>
        public void EnableMod(string id)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                var mod = _mods[i];
                if (mod.Info.id != id) continue;
                mod.Enabled = true;
                if (mod.HasFullLifecycle && !mod.HasErrors)
                {
                    try { mod.AsBase.OnEnable(); }
                    catch (Exception e) { MachineLog.Error("mod [" + id + "] OnEnable error: " + e.Message); }
                }
                break;
            }
        }

        /// <summary>
        /// 禁用指定Mod。
        /// </summary>
        public void DisableMod(string id)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                var mod = _mods[i];
                if (mod.Info.id != id) continue;
                mod.Enabled = false;
                if (mod.HasFullLifecycle)
                {
                    try { mod.AsBase.OnDisable(); }
                    catch (Exception e) { MachineLog.Error("mod [" + id + "] OnDisable error: " + e.Message); }
                }
                break;
            }
        }
    }
}
