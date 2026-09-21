using System;
using System.Collections.Generic;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// 窗口注册表。所有可调整位置的 Machine 窗口都注册到这里。
    /// 玩家可以在设置界面调整各窗口的初始位置，位置保存到 Machine/window_positions.json。
    /// </summary>
    public static class WindowRegistry
    {
        public class WindowEntry
        {
            public string Id;           // 唯一标识
            public string DisplayName;  // 显示名称
            public GameObject Root;     // 窗口根 GameObject
            public Vector2 DefaultPos;  // 默认位置（anchoredPosition）
            public Vector2 CurrentPos;  // 当前位置
        }

        private static readonly Dictionary<string, WindowEntry> _entries = new Dictionary<string, WindowEntry>();
        private static string _configPath = "";
        private static bool _loaded = false;

        /// <summary>初始化，加载保存的窗口位置。</summary>
        public static void Init(string machineDir)
        {
            _configPath = System.IO.Path.Combine(machineDir, "window_positions.json");
            Load();
        }

        /// <summary>注册一个可调整位置的窗口。</summary>
        public static void Register(string id, string displayName, GameObject root, Vector2 defaultPos)
        {
            if (string.IsNullOrEmpty(id) || root == null) return;
            var entry = new WindowEntry
            {
                Id = id,
                DisplayName = displayName,
                Root = root,
                DefaultPos = defaultPos,
                CurrentPos = defaultPos
            };
            // 从保存的配置加载位置
            if (_loaded && _savedPositions.ContainsKey(id))
            {
                entry.CurrentPos = _savedPositions[id];
                ApplyPosition(entry);
            }
            _entries[id] = entry;
        }

        /// <summary>取消注册窗口。</summary>
        public static void Unregister(string id)
        {
            _entries.Remove(id);
        }

        /// <summary>获取所有已注册窗口。</summary>
        public static List<WindowEntry> GetAll()
        {
            var list = new List<WindowEntry>();
            foreach (var kv in _entries) list.Add(kv.Value);
            return list;
        }

        /// <summary>获取指定窗口。</summary>
        public static WindowEntry Get(string id)
        {
            WindowEntry e;
            _entries.TryGetValue(id, out e);
            return e;
        }

        /// <summary>设置窗口位置并应用。</summary>
        public static void SetPosition(string id, Vector2 pos)
        {
            WindowEntry e;
            if (!_entries.TryGetValue(id, out e)) return;
            e.CurrentPos = pos;
            ApplyPosition(e);
        }

        /// <summary>重置窗口位置到默认值。</summary>
        public static void ResetPosition(string id)
        {
            WindowEntry e;
            if (!_entries.TryGetValue(id, out e)) return;
            e.CurrentPos = e.DefaultPos;
            ApplyPosition(e);
        }

        /// <summary>重置所有窗口位置。</summary>
        public static void ResetAll()
        {
            foreach (var kv in _entries)
            {
                kv.Value.CurrentPos = kv.Value.DefaultPos;
                ApplyPosition(kv.Value);
            }
        }

        /// <summary>保存所有窗口位置到配置文件。</summary>
        public static void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (var kv in _entries)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\"").Append(kv.Key).Append("\":{")
                      .Append("\"x\":").Append(kv.Value.CurrentPos.x.ToString("F2")).Append(",")
                      .Append("\"y\":").Append(kv.Value.CurrentPos.y.ToString("F2"))
                      .Append("}");
                }
                sb.Append("}");
                System.IO.File.WriteAllText(_configPath, sb.ToString(), System.Text.Encoding.UTF8);
                MachineLog.Info("Window positions saved: " + _entries.Count + " windows");
            }
            catch (Exception e) { MachineLog.Error("Save window positions failed: " + e.Message); }
        }

        private static Dictionary<string, Vector2> _savedPositions = new Dictionary<string, Vector2>();

        private static void Load()
        {
            _savedPositions.Clear();
            try
            {
                if (!System.IO.File.Exists(_configPath)) { _loaded = true; return; }
                string json = System.IO.File.ReadAllText(_configPath);
                var jv = JsonValue.Parse(json);
                if (jv.Obj != null)
                {
                    foreach (var key in jv.Obj.Keys)
                    {
                        var node = jv.Obj[key];
                        float x = (float)node.GetNumber("x", 0);
                        float y = (float)node.GetNumber("y", 0);
                        _savedPositions[key] = new Vector2(x, y);
                    }
                }
                MachineLog.Info("Loaded " + _savedPositions.Count + " saved window positions");
            }
            catch (Exception e) { MachineLog.Error("Load window positions failed: " + e.Message); }
            _loaded = true;
        }

        private static void ApplyPosition(WindowEntry e)
        {
            if (e.Root == null) return;
            var rt = e.Root.GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = e.CurrentPos;
        }
    }
}
