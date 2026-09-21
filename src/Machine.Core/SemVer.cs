using System;
using System.Text.RegularExpressions;

namespace Machine.Core
{
    /// <summary>
    /// 语义化版本（Semantic Versioning）工具类。
    /// 格式：MAJOR.MINOR.PATCH（如 2.4.0）
    /// 支持版本范围解析：>=2.4.0、>2.3.0、<=3.0.0、<3.0.0、=2.4.0
    /// 支持多条件组合：">=2.3.0 <3.0.0"（空格分隔，AND关系）
    /// </summary>
    public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        public int Major;
        public int Minor;
        public int Patch;
        public string PreRelease = "";  // 如 -beta、-rc.1
        public string Build = "";        // 如 +build.123

        /// <summary>解析版本字符串。失败返回 null。</summary>
        public static SemVer Parse(string version)
        {
            if (string.IsNullOrEmpty(version)) return null;
            version = version.Trim();
            // 去掉前导v（如 v2.4.0）
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(1);

            // 正则：MAJOR.MINOR.PATCH[-prerelease][+build]
            var m = Regex.Match(version,
                @"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$");
            if (!m.Success)
            {
                // 尝试简化格式：MAJOR.MINOR 或 MAJOR
                var m2 = Regex.Match(version, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?$");
                if (!m2.Success) return null;
                var sv = new SemVer();
                sv.Major = int.Parse(m2.Groups[1].Value);
                sv.Minor = m2.Groups[2].Success ? int.Parse(m2.Groups[2].Value) : 0;
                sv.Patch = m2.Groups[3].Success ? int.Parse(m2.Groups[3].Value) : 0;
                return sv;
            }

            var result = new SemVer();
            result.Major = int.Parse(m.Groups[1].Value);
            result.Minor = int.Parse(m.Groups[2].Value);
            result.Patch = int.Parse(m.Groups[3].Value);
            result.PreRelease = m.Groups[4].Value;
            result.Build = m.Groups[5].Value;
            return result;
        }

        /// <summary>尝试解析版本字符串。</summary>
        public static bool TryParse(string version, out SemVer result)
        {
            result = Parse(version);
            return result != null;
        }

        /// <summary>比较两个版本。返回负数表示this < other，0表示相等，正数表示this > other。</summary>
        public int CompareTo(SemVer other)
        {
            if (other == null) return 1;
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
            // 预发布版本：有预发布的 < 无预发布的（如 1.0.0-alpha < 1.0.0）
            bool thisHasPre = !string.IsNullOrEmpty(PreRelease);
            bool otherHasPre = !string.IsNullOrEmpty(other.PreRelease);
            if (thisHasPre && !otherHasPre) return -1;
            if (!thisHasPre && otherHasPre) return 1;
            if (thisHasPre && otherHasPre)
                return string.CompareOrdinal(PreRelease, other.PreRelease);
            return 0;
        }

        public bool Equals(SemVer other)
        {
            if (other == null) return false;
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch
                && PreRelease == other.PreRelease;
        }

        public override bool Equals(object obj) { return Equals(obj as SemVer); }
        public override int GetHashCode() { return Major * 1000000 + Minor * 1000 + Patch; }

        public override string ToString()
        {
            string s = Major + "." + Minor + "." + Patch;
            if (!string.IsNullOrEmpty(PreRelease)) s += "-" + PreRelease;
            if (!string.IsNullOrEmpty(Build)) s += "+" + Build;
            return s;
        }

        // 运算符重载
        public static bool operator <(SemVer a, SemVer b) { return a != null && a.CompareTo(b) < 0; }
        public static bool operator >(SemVer a, SemVer b) { return a != null && a.CompareTo(b) > 0; }
        public static bool operator <=(SemVer a, SemVer b) { return a != null && a.CompareTo(b) <= 0; }
        public static bool operator >=(SemVer a, SemVer b) { return a != null && a.CompareTo(b) >= 0; }
        public static bool operator ==(SemVer a, SemVer b)
        {
            if (ReferenceEquals(a, b)) return true;
            if ((object)a == null || (object)b == null) return false;
            return a.Equals(b);
        }
        public static bool operator !=(SemVer a, SemVer b) { return !(a == b); }
    }

    /// <summary>
    /// 版本范围解析器。
    /// 支持：>=2.4.0、>2.3.0、<=3.0.0、<3.0.0、=2.4.0、2.4.0（默认=）
    /// 多条件组合：">=2.3.0 <3.0.0"（空格分隔，AND关系）
    /// </summary>
    public static class VersionRange
    {
        /// <summary>检查版本是否满足范围要求。</summary>
        public static bool Satisfies(string version, string range)
        {
            if (string.IsNullOrEmpty(range)) return true; // 无范围限制
            SemVer ver = SemVer.Parse(version);
            if (ver == null) return false;
            return Satisfies(ver, range);
        }

        /// <summary>检查版本是否满足范围要求。</summary>
        public static bool Satisfies(SemVer version, string range)
        {
            if (version == null) return false;
            if (string.IsNullOrEmpty(range)) return true;

            // 按空格分割多个条件（AND关系）
            string[] conditions = range.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string cond in conditions)
            {
                if (!CheckSingleCondition(version, cond)) return false;
            }
            return true;
        }

        private static bool CheckSingleCondition(SemVer version, string condition)
        {
            condition = condition.Trim();
            if (string.IsNullOrEmpty(condition)) return true;

            // 解析操作符
            string op = "=";
            string verStr = condition;

            if (condition.StartsWith(">=")) { op = ">="; verStr = condition.Substring(2); }
            else if (condition.StartsWith("<=")) { op = "<="; verStr = condition.Substring(2); }
            else if (condition.StartsWith(">")) { op = ">"; verStr = condition.Substring(1); }
            else if (condition.StartsWith("<")) { op = "<"; verStr = condition.Substring(1); }
            else if (condition.StartsWith("=")) { op = "="; verStr = condition.Substring(1); }
            else if (condition.StartsWith("!=")) { op = "!="; verStr = condition.Substring(2); }

            SemVer target = SemVer.Parse(verStr);
            if (target == null) return true; // 无法解析的条件视为通过

            switch (op)
            {
                case ">=": return version >= target;
                case "<=": return version <= target;
                case ">": return version > target;
                case "<": return version < target;
                case "=": return version == target;
                case "!=": return version != target;
                default: return version == target;
            }
        }

        /// <summary>解析依赖字符串，返回 (modId, versionRange)。</summary>
        /// 格式："id" 或 "id>=1.0.0" 或 "id>=1.0.0 <2.0.0"
        public static bool ParseDependency(string dep, out string modId, out string versionRange)
        {
            modId = "";
            versionRange = "";
            if (string.IsNullOrEmpty(dep)) return false;
            dep = dep.Trim();

            // 找到第一个操作符的位置
            int opPos = -1;
            string[] ops = { ">=", "<=", "!=", ">", "<", "=" };
            foreach (string op in ops)
            {
                int pos = dep.IndexOf(op, StringComparison.Ordinal);
                if (pos > 0 && (opPos < 0 || pos < opPos)) opPos = pos;
            }

            if (opPos > 0)
            {
                modId = dep.Substring(0, opPos).Trim();
                versionRange = dep.Substring(opPos).Trim();
            }
            else
            {
                modId = dep;
                versionRange = "";
            }
            return !string.IsNullOrEmpty(modId);
        }
    }
}
