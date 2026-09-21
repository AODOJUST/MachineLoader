using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Machine.Core
{
    /// <summary>
    /// Machine 用户名单：加密存储所有已知玩家的名称、UID、IP。
    /// 数据文件：Machine/users.dat（AES-256 加密 + Base64）。
    /// 只有管理员权限（RID）及以上（OID）的玩家才能编辑此名单。
    /// 普通用户只能读取自己的记录。
    /// </summary>
    public class UserRegistry
    {
        public class UserEntry
        {
            public string Name = "";
            public string UID = "";
            public string IP = "";
            public long FirstSeen = 0;   // Unix 秒
            public long LastSeen = 0;    // Unix 秒
        }

        private readonly string _path;
        private readonly List<UserEntry> _users = new List<UserEntry>();
        private static readonly byte[] KEY = Encoding.UTF8.GetBytes("MachineLoaderUserRegistryKey2026"); // 32 bytes (AES-256)
        private static readonly byte[] IV = Encoding.UTF8.GetBytes("Mach1neL0ader!IV"); // 16 bytes

        public UserRegistry(string machineDir)
        {
            _path = Path.Combine(machineDir, "users.dat");
            Load();
        }

        /// <summary>当前操作者是否有编辑权限（管理员 RID 或原始开发者 OID）。</summary>
        public static bool CanEdit(MachineMemory mem)
        {
            return mem != null && mem.HasAdminRights;
        }

        public IReadOnlyList<UserEntry> AllUsers { get { return _users.AsReadOnly(); } }

        public UserEntry FindByUID(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            for (int i = 0; i < _users.Count; i++)
                if (_users[i].UID == uid) return _users[i];
            return null;
        }

        public UserEntry FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _users.Count; i++)
                if (string.Equals(_users[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _users[i];
            return null;
        }

        /// <summary>添加或更新用户记录。需要编辑权限。</summary>
        public bool Upsert(MachineMemory editor, string name, string uid, string ip)
        {
            if (!CanEdit(editor)) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var existing = FindByUID(uid);
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(name)) existing.Name = name;
                if (!string.IsNullOrEmpty(ip)) existing.IP = ip;
                existing.LastSeen = now;
            }
            else
            {
                _users.Add(new UserEntry
                {
                    Name = name ?? "",
                    UID = uid ?? "",
                    IP = ip ?? "",
                    FirstSeen = now,
                    LastSeen = now
                });
            }
            Save();
            return true;
        }

        /// <summary>删除用户记录。需要编辑权限。</summary>
        public bool Remove(MachineMemory editor, string uid)
        {
            if (!CanEdit(editor)) return false;
            for (int i = _users.Count - 1; i >= 0; i--)
            {
                if (_users[i].UID == uid) { _users.RemoveAt(i); Save(); return true; }
            }
            return false;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string b64 = File.ReadAllText(_path).Trim();
                if (b64.Length == 0) return;
                byte[] cipher = Convert.FromBase64String(b64);
                string json = Decrypt(cipher);
                if (string.IsNullOrEmpty(json)) return;
                var jv = JsonValue.Parse(json);
                if (jv == null || jv.Kind != "array" || jv.Arr == null) return;
                foreach (var item in jv.Arr)
                {
                    if (item == null || item.Kind != "object") continue;
                    _users.Add(new UserEntry
                    {
                        Name = item.GetString("name", ""),
                        UID = item.GetString("uid", ""),
                        IP = item.GetString("ip", ""),
                        FirstSeen = (long)item.GetNumber("firstSeen", 0),
                        LastSeen = (long)item.GetNumber("lastSeen", 0)
                    });
                }
                MachineLog.Info("UserRegistry loaded: " + _users.Count + " user(s)");
            }
            catch (Exception e) { MachineLog.Warn("UserRegistry load failed: " + e.Message); }
        }

        private void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < _users.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var u = _users[i];
                    sb.Append("{\"name\":\"").Append(Escape(u.Name)).Append("\",")
                      .Append("\"uid\":\"").Append(Escape(u.UID)).Append("\",")
                      .Append("\"ip\":\"").Append(Escape(u.IP)).Append("\",")
                      .Append("\"firstSeen\":").Append(u.FirstSeen).Append(",")
                      .Append("\"lastSeen\":").Append(u.LastSeen).Append('}');
                }
                sb.Append(']');
                byte[] cipher = Encrypt(sb.ToString());
                File.WriteAllText(_path, Convert.ToBase64String(cipher));
            }
            catch (Exception e) { MachineLog.Warn("UserRegistry save failed: " + e.Message); }
        }

        private static byte[] Encrypt(string plain)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = KEY;
                aes.IV = IV;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(plain);
                        cs.Write(bytes, 0, bytes.Length);
                    }
                    return ms.ToArray();
                }
            }
        }

        private static string Decrypt(byte[] cipher)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = KEY;
                aes.IV = IV;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var ms = new MemoryStream(cipher))
                using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
