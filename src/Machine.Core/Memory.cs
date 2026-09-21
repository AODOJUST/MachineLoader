using System;
using System.Collections.Generic;
using System.IO;

namespace Machine.Core
{
    /// <summary>
    /// Machine 记忆系统：持久化玩家信息与通用设置（键位、画面布局等键值对）。
    /// 数据文件：Machine/profile.json
    ///   { "playerName": "...", "uid": "11位数字", "rid": "5位数字(管理员)",
    ///     "oid": "原始开发者ID", "playTimeSeconds": 12345, "settings": { ... } }
    /// 玩家名字一经确认即锁定保存在此，供联机与其他 mod 读取。
    /// </summary>
    public class MachineMemory
    {
        private string _path;
        private string _playerName = "";
        private string _uid = "";
        private string _rid = "";
        private string _oid = "";
        private bool _useAltUID = false;   // 原始开发者一人多号：使用 10 位固定测试 UID
        private long _playTimeSeconds = 0;
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();

        public string PlayerName
        {
            get { return _playerName; }
            set { _playerName = value ?? ""; Save(); }
        }

        /// <summary>普通用户 ID：11 位阿拉伯数字。锁定后自动生成。</summary>
        public string UID { get { return _uid; } set { _uid = value ?? ""; Save(); } }

        /// <summary>管理员 ID：5 位阿拉伯数字。空字符串表示非管理员。</summary>
        public string RID { get { return _rid; } set { _rid = value ?? ""; Save(); } }

        /// <summary>原始开发者 ID。空字符串表示非原始开发者。</summary>
        public string OID { get { return _oid; } set { _oid = value ?? ""; Save(); } }

        /// <summary>
        /// 原始开发者一人多号：10 位固定测试 UID（普通 UID 为 11 位）。
        /// 由 OID 派生：首位固定 1，后 9 位为 OID 数字补零。例如 OID=001 → 1000000001。
        /// </summary>
        public string AltUID
        {
            get
            {
                if (_oid.Length == 0) return "";
                // 提取 OID 中的数字
                var num = new System.Text.StringBuilder();
                foreach (char ch in _oid) if (ch >= '0' && ch <= '9') num.Append(ch);
                string digits = num.ToString();
                if (digits.Length == 0) digits = "0";
                // 10 位：首位 1 + 后 9 位（OID 数字补零到 9 位）
                return "1" + digits.PadLeft(9, '0').Substring(0, 9);
            }
        }

        /// <summary>是否使用测试 UID（一人多号）。仅原始开发者可用。</summary>
        public bool UseAltUID
        {
            get { return _useAltUID && _oid.Length > 0; }
            set { _useAltUID = value; Save(); }
        }

        /// <summary>当前生效的 UID（测试模式返回 AltUID，否则返回普通 UID）。</summary>
        public string EffectiveUID
        {
            get { return UseAltUID ? AltUID : _uid; }
        }

        /// <summary>mod 版累计游玩时间（秒）。</summary>
        public long PlayTimeSeconds { get { return _playTimeSeconds; } set { _playTimeSeconds = value; Save(); } }

        public bool IsAdmin { get { return _rid.Length > 0; } }
        public bool IsOriginalDeveloper { get { return _oid.Length > 0; } }
        /// <summary>管理员及以上权限（管理员或原始开发者）。</summary>
        public bool HasAdminRights { get { return IsAdmin || IsOriginalDeveloper; } }

        public void Load(string machineDir)
        {
            try
            {
                _path = Path.Combine(machineDir, "profile.json");
                if (File.Exists(_path))
                {
                    var jv = JsonValue.Parse(File.ReadAllText(_path));
                    // profile.json 是玩家可手改的外部输入，逐字段校验后再采用：
                    // 非法 UID/RID/OID 会被丢弃（不合法的主管理员 ID 直接清零，
                    // 而不是带着一个"看起来像管理员"的脏值继续跑）。
                    string name = jv.GetString("playerName", "");
                    if (name.Length > 0 && !ConfigGuard.IsSafeNick(name))
                    {
                        MachineLog.Warn("profile.json: playerName 非法，已忽略");
                        name = "";
                    }
                    _playerName = name;

                    string uid = jv.GetString("uid", "");
                    if (uid.Length > 0 && !ConfigGuard.IsValidUid(uid))
                    {
                        MachineLog.Warn("profile.json: uid 非法（应为 11 位数字），将重新生成");
                        uid = "";
                    }
                    _uid = uid;

                    string rid = jv.GetString("rid", "");
                    if (!ConfigGuard.IsValidRid(rid))
                    {
                        MachineLog.Warn("profile.json: rid 非法（应为 5 位数字），已清空");
                        rid = "";
                    }
                    _rid = rid;

                    string oid = jv.GetString("oid", "");
                    if (!ConfigGuard.IsValidOid(oid))
                    {
                        MachineLog.Warn("profile.json: oid 非法（应为 1-16 位字母数字），已清空");
                        oid = "";
                    }
                    _oid = oid;

                    _useAltUID = jv.GetBool("useAltUID", false);
                    _playTimeSeconds = ConfigGuard.ClampPlayTime(jv.GetNumber("playTimeSeconds", 0));
                    var s = jv.Get("settings");
                    if (s != null && s.Kind == "object" && s.Obj != null)
                    {
                        foreach (var kv in s.Obj)
                        {
                            if (kv.Value == null) continue;
                            _settings[kv.Key] = kv.Value.Kind == "string" ? kv.Value.Str : kv.Value.ToString();
                        }
                    }
                }
                // 首次锁定昵称时自动生成 11 位 UID
                if (_playerName.Length > 0 && _uid.Length == 0)
                {
                    _uid = GenerateUID();
                    Save();
                }
                if (!File.Exists(_path)) Save();
            }
            catch (Exception e) { MachineLog.Warn("Memory load failed " + e.Message); }
        }

        /// <summary>累加游玩时间（由 Bootstrap 每帧调用，仅 mod 模式）。</summary>
        public void TickPlayTime(float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return;
            _playTimeSeconds += (long)Math.Round(deltaSeconds);
            // 每 10 秒落盘一次，避免频繁写盘
            if (_playTimeSeconds % 10 == 0) Save();
        }

        /// <summary>格式化游玩时间为 "Xh Ym"。</summary>
        public string PlayTimeFormatted
        {
            get
            {
                long h = _playTimeSeconds / 3600;
                long m = (_playTimeSeconds % 3600) / 60;
                if (h > 0) return h + "h " + m + "m";
                return m + "m";
            }
        }

        /// <summary>生成 11 位随机数字 UID（首位不为 0）。</summary>
        public static string GenerateUID()
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var sb = new System.Text.StringBuilder();
            sb.Append(rnd.Next(1, 10));
            for (int i = 1; i < 11; i++) sb.Append(rnd.Next(0, 10));
            return sb.ToString();
        }

        /// <summary>生成 5 位随机数字 RID（首位不为 0）。</summary>
        public static string GenerateRID()
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var sb = new System.Text.StringBuilder();
            sb.Append(rnd.Next(1, 10));
            for (int i = 1; i < 5; i++) sb.Append(rnd.Next(0, 10));
            return sb.ToString();
        }

        /// <summary>读取设置项（键位 / 布局等通用键值）。</summary>
        public string Get(string key, string def = "")
        {
            string v;
            return _settings.TryGetValue(key, out v) ? v : def;
        }

        /// <summary>写入设置项并立即落盘。</summary>
        public void Set(string key, string value)
        {
            _settings[key] = value ?? "";
            Save();
        }

        public void Save()
        {
            try
            {
                if (_path == null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"playerName\":\"").Append(Escape(_playerName)).Append("\"");
                if (_uid.Length > 0) sb.Append(",\"uid\":\"").Append(Escape(_uid)).Append("\"");
                if (_rid.Length > 0) sb.Append(",\"rid\":\"").Append(Escape(_rid)).Append("\"");
                if (_oid.Length > 0) sb.Append(",\"oid\":\"").Append(Escape(_oid)).Append("\"");
                if (_useAltUID) sb.Append(",\"useAltUID\":true");
                sb.Append(",\"playTimeSeconds\":").Append(_playTimeSeconds);
                sb.Append(",\"settings\":{");
                bool first = true;
                foreach (var kv in _settings)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Escape(kv.Key)).Append("\":\"").Append(Escape(kv.Value)).Append('"');
                }
                sb.Append("}}");
                File.WriteAllText(_path, sb.ToString());
            }
            catch (Exception e) { MachineLog.Warn("Memory save failed " + e.Message); }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
