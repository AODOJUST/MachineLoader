using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Machine.Core
{
    /// <summary>
    /// 发布公钥（RSA-3072, PKCS#1 v1.5 + SHA-256）。
    /// 由 tools/sign_release.py 自动写入，请勿手工编辑。
    /// 私钥只有发布者持有；客户端只用这里的内置公钥验签，
    /// 因此 update.json / version.json 被替换也无法让客户端接受非官方 DLL。
    /// </summary>
    public static class ReleaseKey
    {
        // BEGIN MACHINE_PUBKEY
public const string ModulusHex = "91d8e329a3e546f01aacbb356e46dd7cd99e3aa8b90e91307d9da678485c91cdfe2f1989a529b3e328b43711eb32acdd32ec2180d04ae9b95e32476fff28ace3a53a1f0a1eb560f018e0bd68e16aa4b531e44abda36aa1851a7ac50f45d4fafb0cc7a1ef8b2b477421303af8cba1cd511661ced32ffe1a52c442d6897e718068a2672c9613b5a1d968bbf3052f3354f2df26187be701e68f0c1c69a376bd72c375a3e3306037eebe278a897afa221c8470a5aeb61c12f5f100553020422e6d18ac5e6592b804f4368fdc9055ab6843d8d1f659c9e11c08ccf50a7c3eff1b3132097d364c9e2bf7d21f5d7228cb040e615d71df8ccc25a9b4f26e5dfe00c363e49a8d30f2d530b70d21348af8e2c709af99d0f1b30cac5826dd53b4aece1436f979c52c45094d32bf44cab3e54041a87266dcc2a1b8f0e31bde7760ffffff2254bbd1dbda45fe6fe571748a8fbd1f815fe301277d428368aa341dbbd3c17c392d9095e8806d2297532322f20958d5fb020aa51f6f42f962d300ed348fefbfbe99";
public const string ExponentHex = "010001";
public const string FingerprintSha256 = "93c4ab8598865fc82e6b0e1bcdf9ad5cf11f767d45db1286c14eee9bad70065a";
// END MACHINE_PUBKEY

        public static bool Available
        {
            get { return ModulusHex != null && ModulusHex.Length >= 128; }
        }
    }

    /// <summary>哈希与发布签名校验。</summary>
    public static class MachineCrypto
    {
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            if ((hex.Length & 1) != 0) return new byte[0];
            byte[] outBytes = new byte[hex.Length / 2];
            for (int i = 0; i < outBytes.Length; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return new byte[0];
                outBytes[i] = (byte)((hi << 4) | lo);
            }
            return outBytes;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        public static string Sha256Hex(byte[] data)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(data);
                    var sb = new StringBuilder(h.Length * 2);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static bool IsSha256Hex(string s)
        {
            if (s == null || s.Length != 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// 校验发布签名。返回 true 表示"确实由持私钥的发布者签名且内容未被修改"。
        /// 语义上：无法验证 = 不通过（fail closed），绝不因为"运行环境不支持"就放行。
        /// </summary>
        public static bool VerifyRelease(byte[] data, byte[] signature, out string reason)
        {
            reason = "";
            if (!ReleaseKey.Available)
            {
                reason = "client has no embedded release public key";
                return false;
            }
            if (data == null || data.Length == 0) { reason = "empty payload"; return false; }
            if (signature == null || signature.Length == 0) { reason = "missing signature"; return false; }

            byte[] n = FromHex(ReleaseKey.ModulusHex);
            byte[] e = FromHex(ReleaseKey.ExponentHex);
            if (n.Length == 0 || e.Length == 0) { reason = "bad embedded key"; return false; }

            // 首选平台实现；Mono/IL2CPP 下若不可用，退化为纯托管 BigInteger 实现
            try
            {
                var rsa = new RSACryptoServiceProvider();
                try
                {
                    rsa.PersistKeyInCsp = false;
                }
                catch (Exception) { }
                var p = new RSAParameters();
                p.Modulus = n;
                p.Exponent = e;
                rsa.ImportParameters(p);
                bool ok = rsa.VerifyData(data, "SHA256", signature);
                if (!ok) reason = "signature does not match the embedded public key";
                return ok;
            }
            catch (Exception ex)
            {
                MachineLog.Warn("RSA (platform) unavailable, using managed fallback: " + ex.Message);
            }

            try
            {
                bool ok = VerifyReleaseManaged(n, e, data, signature);
                if (!ok) reason = "signature does not match the embedded public key";
                return ok;
            }
            catch (Exception ex2)
            {
                reason = "verification failed: " + ex2.Message;
                return false;
            }
        }

        // ---- 纯托管 RSA PKCS#1 v1.5 验签（不依赖平台的 RSA 实现）----

        private static byte[] BeToBigIntegerBytes(byte[] bigEndian)
        {
            // BigInteger(byte[]) 期望小端且把最高位当符号位 → 反转并补一个 0x00
            byte[] le = new byte[bigEndian.Length + 1];
            for (int i = 0; i < bigEndian.Length; i++) le[i] = bigEndian[bigEndian.Length - 1 - i];
            le[bigEndian.Length] = 0;
            return le;
        }

        private static byte[] BigIntegerToFixed(byte[] le, int len)
        {
            byte[] r = new byte[len];
            int n = Math.Min(len, le.Length);
            for (int i = 0; i < n; i++) r[len - 1 - i] = le[i];
            return r;
        }

        private static readonly byte[] Sha256DigestInfoPrefix = new byte[]
        {
            0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65,
            0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20
        };

        private static bool VerifyReleaseManaged(byte[] nBe, byte[] eBe, byte[] data, byte[] signature)
        {
            int k = nBe.Length;
            if (signature.Length != k) return false;

            var n = new System.Numerics.BigInteger(BeToBigIntegerBytes(nBe));
            var e = new System.Numerics.BigInteger(BeToBigIntegerBytes(eBe));
            var sig = new System.Numerics.BigInteger(BeToBigIntegerBytes(signature));
            if (n <= System.Numerics.BigInteger.Zero || e <= System.Numerics.BigInteger.One) return false;

            var m = System.Numerics.BigInteger.ModPow(sig, e, n);
            byte[] em = BigIntegerToFixed(m.ToByteArray(), k);

            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(data);

            int tLen = Sha256DigestInfoPrefix.Length + digest.Length;
            if (k < tLen + 11) return false;

            if (em[0] != 0x00 || em[1] != 0x01) return false;
            int ff = 0;
            int idx = 2;
            while (idx < k && em[idx] == 0xFF) { ff++; idx++; }
            if (ff < 8) return false;
            if (idx >= k || em[idx] != 0x00) return false;
            int tStart = idx + 1;
            if (k - tStart != tLen) return false;
            for (int i = 0; i < Sha256DigestInfoPrefix.Length; i++)
                if (em[tStart + i] != Sha256DigestInfoPrefix[i]) return false;
            for (int i = 0; i < digest.Length; i++)
                if (em[tStart + Sha256DigestInfoPrefix.Length + i] != digest[i]) return false;
            return true;
        }
    }

    /// <summary>update.json 的校验结果（所有字段都已清洗）。</summary>
    public class UpdateConfig
    {
        public string Repo = "";
        public string Branch = "main";
        public string Channel = "stable";
        public bool Enabled = true;
        public bool RequireSignature = true;
        /// <summary>清洗过程的告警，供日志输出。</summary>
        public string Warnings = "";
        public bool RepoUsable { get { return Repo.Length > 0 && Enabled; } }
    }

    /// <summary>net.json 的校验结果。</summary>
    public class NetConfig
    {
        public string Server = "";
        public int Port = 26460;
        public string PlayerName = "Pilot";
        /// <summary>
        /// 远程玩家的外观来源：
        ///   "clone"（默认）= 视觉克隆本机玩家飞机（真实机型，见 NetSync.BuildRealVisual）
        ///   "box"          = 旧的方块飞机占位模型
        /// 联机时若克隆出来的东西不对（比如房间里各人存档里的机型不同），
        /// 在 Machine\net.json 里改成 "box" 即可立刻回到占位模型，不需要重编译。
        /// </summary>
        public string RemoteModel = "clone";
        public string Warnings = "";
    }

    /// <summary>
    /// 配置文件校验：net.json / update.json / profile.json 都属于"外部输入"，
    /// 字段缺失、类型错误、越界、含非法字符一律在这里拦掉并回落到安全默认值。
    /// </summary>
    public static class ConfigGuard
    {
        public const int DefaultPort = 26460;

        public static UpdateConfig ReadUpdate(string machineDir)
        {
            var cfg = new UpdateConfig();
            string path = System.IO.Path.Combine(machineDir, "update.json");
            JsonValue jv = null;
            try
            {
                if (!System.IO.File.Exists(path)) return cfg;
                jv = JsonValue.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception e)
            {
                cfg.Warnings = "update.json unreadable: " + e.Message;
                return cfg;
            }
            if (jv == null || jv.Kind != "object")
            {
                cfg.Warnings = "update.json is not a JSON object; using defaults";
                return cfg;
            }

            var w = new StringBuilder();
            string repo = jv.GetString("repo", "");
            if (repo.Length > 0)
            {
                repo = repo.Trim().Trim('/');
                if (!IsSafeRepo(repo))
                {
                    w.Append("repo rejected (expect owner/repo); ");
                    repo = "";
                }
            }
            cfg.Repo = repo;

            string branch = jv.GetString("branch", "main");
            branch = branch == null ? "" : branch.Trim();
            if (branch.Length == 0 || !IsSafeBranch(branch))
            {
                if (jv.Get("branch") != null) w.Append("branch rejected; using main; ");
                branch = "main";
            }
            cfg.Branch = branch;

            string channel = jv.GetString("channel", "stable");
            if (channel != "stable" && channel != "beta")
            {
                if (jv.Get("channel") != null) w.Append("channel rejected; using stable; ");
                channel = "stable";
            }
            cfg.Channel = channel;

            var en = jv.Get("enabled");
            cfg.Enabled = en == null ? true : (en.Kind == "bool" ? en.B : (en.Kind == "string" ? en.Str != "false" : true));

            var rs = jv.Get("requireSignature");
            if (rs == null) cfg.RequireSignature = true;
            else if (rs.Kind == "bool") cfg.RequireSignature = rs.B;
            else if (rs.Kind == "string") cfg.RequireSignature = rs.Str != "false";
            else { w.Append("requireSignature invalid; using true; "); cfg.RequireSignature = true; }

            cfg.Warnings = w.ToString();
            return cfg;
        }

        public static NetConfig ReadNet(string machineDir)
        {
            var cfg = new NetConfig();
            string path = System.IO.Path.Combine(machineDir, "net.json");
            JsonValue jv = null;
            try
            {
                if (!System.IO.File.Exists(path)) return cfg;
                jv = JsonValue.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception e)
            {
                cfg.Warnings = "net.json unreadable: " + e.Message;
                return cfg;
            }
            if (jv == null || jv.Kind != "object")
            {
                cfg.Warnings = "net.json is not a JSON object; using defaults";
                return cfg;
            }

            var w = new StringBuilder();
            string server = jv.GetString("server", "");
            server = server == null ? "" : server.Trim();
            if (server.Length > 0 && !IsSafeHost(server))
            {
                w.Append("server rejected (not a valid IP/hostname); ");
                server = "";
            }
            cfg.Server = server;

            double port = jv.GetNumber("port", DefaultPort);
            int p = (int)Math.Round(port);
            if (p < 1 || p > 65535)
            {
                if (jv.Get("port") != null) w.Append("port out of range (1..65535); using 26460; ");
                p = DefaultPort;
            }
            cfg.Port = p;

            string name = jv.GetString("playerName", "Pilot");
            if (!IsSafeNick(name))
            {
                if (jv.Get("playerName") != null) w.Append("playerName rejected; using Pilot; ");
                name = "Pilot";
            }
            cfg.PlayerName = name;

            // 远程玩家外观来源：只接受 "clone"（默认，真实机型）/"box"（方块占位），
            // 其余值一律回落 clone 并记警告 —— 与其它字段同一套"外部输入必须校验"的规矩。
            string rm = jv.GetString("remoteModel", "clone");
            rm = rm == null ? "clone" : rm.Trim().ToLowerInvariant();
            if (rm != "clone" && rm != "box")
            {
                if (jv.Get("remoteModel") != null) w.Append("remoteModel must be \"clone\" or \"box\"; using clone; ");
                rm = "clone";
            }
            cfg.RemoteModel = rm;

            cfg.Warnings = w.ToString();
            return cfg;
        }

        // ---- 字段级校验 ----

        public static bool IsSafeRepo(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('/');
            if (parts.Length != 2) return false;
            if (parts[0].Length == 0 || parts[0].Length > 39) return false;
            if (parts[1].Length == 0 || parts[1].Length > 100) return false;
            return IsRepoPart(parts[0], true) && IsRepoPart(parts[1], false);
        }

        private static bool IsRepoPart(string s, bool first)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool alnum = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (alnum) continue;
                if (c == '.' || c == '_' || c == '-') continue;
                return false;
            }
            if (first)
            {
                char c0 = s[0];
                bool alnum = (c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z') || (c0 >= '0' && c0 <= '9');
                if (!alnum) return false;
            }
            return true;
        }

        public static bool IsSafeBranch(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 100) return false;
            if (s.Contains("..")) return false;
            if (s.StartsWith("/") || s.EndsWith("/") || s.Contains("//")) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '_' || c == '-' || c == '/';
                if (!ok) return false;
            }
            return true;
        }

        public static bool IsSafeHost(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 253) return false;
            // IPv4
            string[] parts = s.Split('.');
            bool allDigits = true;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 3) { allDigits = false; break; }
                for (int j = 0; j < parts[i].Length; j++)
                    if (parts[i][j] < '0' || parts[i][j] > '9') { allDigits = false; break; }
                if (!allDigits) break;
            }
            if (allDigits && parts.Length == 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    int v;
                    if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return false;
                    if (v < 0 || v > 255) return false;
                }
                return true;
            }
            // 主机名 / 域名
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '-';
                if (!ok) return false;
            }
            return s[0] != '.' && s[0] != '-' && s[s.Length - 1] != '.' && s[s.Length - 1] != '-';
        }

        public static bool IsSafeNick(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int len = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                len++;
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '_' || (c >= 0x4E00 && c <= 0x9FFF);
                if (!ok) return false;
            }
            return len >= 1 && len <= 16;
        }

        /// <summary>UID：11 位阿拉伯数字（首位非 0）。</summary>
        public static bool IsValidUid(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 11) return false;
            if (s[0] == '0') return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>RID：5 位阿拉伯数字（首位非 0）。空串表示不是管理员。</summary>
        public static bool IsValidRid(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length != 5) return false;
            if (s[0] == '0') return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>OID：1~16 位字母数字（空串表示非原始开发者）。</summary>
        public static bool IsValidOid(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length > 16) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>游玩时长上限（10 年，秒）。防止被改成负数或天文数字后溢出/异常。</summary>
        public const long MaxPlayTimeSeconds = 10L * 365L * 24L * 3600L;

        public static long ClampPlayTime(double v)
        {
            if (double.IsNaN(v) || v < 0) return 0;
            if (v > MaxPlayTimeSeconds) return MaxPlayTimeSeconds;
            return (long)v;
        }
    }
}
