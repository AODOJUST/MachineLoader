using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Machine.Core
{
    /// <summary>
    /// Machine 联网更新检查（加载器内置）。
    ///
    /// 安全模型（相对旧版的关键变化）：
    ///   * 信任根 = 编译进 ReleaseKey 的发布公钥，而不是 update.json / version.json。
    ///     所以即使这两个 JSON 被替换，也无法让客户端接受一个非官方签名的 DLL。
    ///   * 下载完成后先比对 SHA-256，再用内置公钥验 RSA 签名；任一不过就删除文件并报错，
    ///     Machine/update/ 里绝不会留下一个"来路不明但看起来能装"的 DLL。
    ///   * 下载地址只允许 https + 白名单主机（GitHub 相关域名），避免 version.json
    ///     把客户端指向任意服务器。
    ///   * update.json / version.json 的每个字段都过 schema 校验（ConfigGuard）。
    ///
    /// 配置：Machine/update.json
    ///   { "repo":"owner/repo", "branch":"main", "channel":"stable",
    ///     "enabled":true, "requireSignature":true }
    /// version.json 格式（放在仓库根）：
    ///   { "version":"2.4.0", "channel":"stable", "minVersion":"2.0.0",
    ///     "core":"<https 直链>", "sha256":"<64位十六进制>", "sig":"<base64 RSA签名>",
    ///     "notes":"..." }
    /// </summary>
    public class MachineUpdate
    {
        private MachineRuntime _rt;
        private bool _started;
        private volatile bool _busy;
        private volatile bool _downloaded;
        private string _latestVersion = "";
        private string _coreUrl = "";
        private string _sha256 = "";
        private string _sig = "";
        private string _channel = "";
        private string _minVersion = "";
        private string _notes = "";
        private string _error = "";
        private bool _hasUpdate;
        private float _popupDelay = 2.5f;
        private UpdateUI _ui;

        public bool HasUpdate { get { return _hasUpdate; } }
        public string LatestVersion { get { return _latestVersion; } }
        public string Notes { get { return _notes; } }
        public bool Downloaded { get { return _downloaded; } }
        public string Error { get { return _error; } }
        public bool Critical { get { return _minVersion.Length > 0; } }

        public MachineUpdate(MachineRuntime rt) { _rt = rt; }

        public void StartCheck()
        {
            if (_started) return;
            _started = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate (object o) { Check(); });
            }
            catch (Exception e) { MachineLog.Warn("update check start failed " + e.Message); }
        }

        // ------------------------------------------------------------------
        // 网络
        // ------------------------------------------------------------------

        private const int HttpTimeoutMs = 20000;
        private const int HttpRetries = 2;

        /// <summary>把 https 统一走 TLS1.2；老 Mono 上可能不支持该枚举值，失败不影响主流程。</summary>
        private static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch (Exception) { }
        }

        /// <summary>白名单校验下载地址：必须 https，且主机必须是 GitHub 相关域名。</summary>
        public static bool IsAllowedCoreUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string host = u.Host.ToLowerInvariant();
            string[] allowed = new string[]
            {
                "raw.githubusercontent.com", "github.com", "objects.githubusercontent.com",
                "codeload.github.com", "githubusercontent.com"
            };
            for (int i = 0; i < allowed.Length; i++)
            {
                if (host == allowed[i] || host.EndsWith("." + allowed[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static byte[] HttpGetBytes(string url)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= HttpRetries; attempt++)
            {
                try
                {
                    EnsureTls();
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = HttpTimeoutMs;
                    req.ReadWriteTimeout = HttpTimeoutMs;
                    req.UserAgent = "MachineLoader/" + MachineLoader.Version;
                    req.AllowAutoRedirect = true;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        return ms.ToArray();
                    }
                }
                catch (Exception e)
                {
                    last = e;
                    if (attempt < HttpRetries) Thread.Sleep(400 * (attempt + 1));
                }
            }
            throw new Exception(last != null ? last.Message : "request failed");
        }

        private static string HttpGetString(string url)
        {
            return Encoding.UTF8.GetString(HttpGetBytes(url));
        }

        // ------------------------------------------------------------------
        // 检查更新
        // ------------------------------------------------------------------

        private void Check()
        {
            try
            {
                var cfg = ConfigGuard.ReadUpdate(_rt.MachineDir);
                if (cfg.Warnings.Length > 0) MachineLog.Warn("update.json: " + cfg.Warnings);
                if (!cfg.RepoUsable)
                {
                    MachineLog.Info("update check skipped (no usable repo in Machine/update.json)");
                    return;
                }
                if (!ReleaseKey.Available && cfg.RequireSignature)
                {
                    MachineLog.Warn("update check skipped: no embedded release public key "
                                    + "but requireSignature=true (cannot trust any download)");
                    return;
                }

                string url = "https://raw.githubusercontent.com/" + cfg.Repo + "/"
                             + cfg.Branch + "/version.json";
                string body = HttpGetString(url);
                var jv = JsonValue.Parse(body);
                if (jv == null || jv.Kind != "object")
                {
                    // 附带一小段响应预览，便于区分「网络错误页 / 404 HTML / 空响应 / 格式不对」
                    MachineLog.Warn("update check: version.json is not a JSON object"
                                    + " (bytes=" + (body == null ? 0 : body.Length)
                                    + ", head='" + SafeStr(body, 60) + "')");
                    return;
                }

                string ver = SafeStr(jv.GetString("version", ""), 32);
                string core = jv.GetString("core", "");
                string notes = SafeStr(jv.GetString("notes", ""), 400);
                string sha = jv.GetString("sha256", "").ToLowerInvariant();
                string sig = jv.GetString("sig", "");
                string channel = jv.GetString("channel", "stable");
                string minVer = SafeStr(jv.GetString("minVersion", ""), 32);

                if (!IsVersionString(ver))
                {
                    MachineLog.Warn("update check: version.json has invalid 'version'; ignored");
                    return;
                }
                if (channel != "stable" && channel != "beta") channel = "stable";
                if (minVer.Length > 0 && !IsVersionString(minVer)) minVer = "";
                if (sha.Length > 0 && !MachineCrypto.IsSha256Hex(sha))
                {
                    MachineLog.Warn("update check: version.json 'sha256' malformed; ignored");
                    sha = "";
                }
                if (core.Length > 0 && !IsAllowedCoreUrl(core))
                {
                    MachineLog.Warn("update check: 'core' url rejected (must be https on a GitHub host)");
                    core = "";
                }

                // 通道过滤：稳定通道不接受 beta 发布
                if (cfg.Channel == "stable" && channel == "beta")
                {
                    MachineLog.Info("update check: v" + ver + " is a beta release; channel=stable -> skipped");
                    return;
                }

                if (cfg.RequireSignature && (sha.Length == 0 || sig.Length == 0))
                {
                    MachineLog.Warn("update check: release is unsigned but requireSignature=true; ignored");
                    return;
                }

                _latestVersion = ver;
                _coreUrl = core;
                _notes = notes;
                _sha256 = sha;
                _sig = sig;
                _channel = channel;
                _minVersion = minVer;

                if (CompareVersions(ver, MachineLoader.Version) > 0)
                {
                    _hasUpdate = true;
                    MachineLog.Info("update available: v" + ver + " (current v" + MachineLoader.Version
                                    + ", channel " + channel + (minVer.Length > 0 ? ", min " + minVer : "") + ")");
                }
                else
                {
                    MachineLog.Info("update check: up to date (v" + ver + ")");
                }
            }
            catch (Exception e)
            {
                MachineLog.Warn("update check failed (offline?) " + e.Message);
            }
        }

        private static string SafeStr(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length && sb.Length < max; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\t' || c >= ' ') sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsVersionString(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 32) return false;
            int dots = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.') { dots++; if (dots > 3) return false; continue; }
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static int CompareVersions(string a, string b)
        {
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(pa[i], out x);
                if (i < pb.Length) int.TryParse(pb[i], out y);
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // 下载 + 校验
        // ------------------------------------------------------------------

        /// <summary>
        /// 下载新版 Machine.Core.dll 到 Machine/update/。
        /// 校验顺序：sha256 -> RSA 签名 -> 落盘（先 .tmp，再原子改名）。
        /// 任一环节失败都会删掉临时文件并给出可读原因，绝不会留下未验证的 DLL。
        /// </summary>
        public void Download()
        {
            if (_downloaded) return;
            if (_coreUrl.Length == 0)
            {
                _error = "NO DOWNLOAD URL IN version.json";
                MachineLog.Warn("update: no core url in version.json");
                return;
            }
            if (_busy) return;
            _busy = true;
            _error = "";
            try
            {
                var cfg = ConfigGuard.ReadUpdate(_rt.MachineDir);
                MachineLog.Info("update: downloading " + _coreUrl);
                byte[] data = HttpGetBytes(_coreUrl);

                // 1) 基本形态检查：DLL 头 "MZ"
                if (data.Length < 1024 || data[0] != 0x4D || data[1] != 0x5A)
                {
                    _error = "DOWNLOADED FILE IS NOT A DLL";
                    MachineLog.Warn("update: downloaded payload has no MZ header; rejected");
                    return;
                }

                // 2) SHA-256
                string actual = MachineCrypto.Sha256Hex(data);
                if (_sha256.Length > 0 && !string.Equals(actual, _sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _error = "HASH MISMATCH - FILE MAY BE TAMPERED";
                    MachineLog.Warn("update: sha256 mismatch (expect " + _sha256 + ", got " + actual + "); rejected");
                    return;
                }
                if (_sha256.Length == 0)
                {
                    if (cfg.RequireSignature)
                    {
                        _error = "RELEASE HAS NO HASH - REJECTED";
                        MachineLog.Warn("update: version.json has no sha256 while requireSignature=true; rejected");
                        return;
                    }
                    MachineLog.Warn("update: version.json has no sha256; relying on signature only");
                }

                // 3) RSA 签名
                bool verified = false;
                if (_sig.Length > 0)
                {
                    byte[] sigBytes = null;
                    try { sigBytes = Convert.FromBase64String(_sig.Trim()); }
                    catch (FormatException) { sigBytes = null; }
                    if (sigBytes == null || sigBytes.Length == 0)
                    {
                        _error = "SIGNATURE FIELD MALFORMED - REJECTED";
                        MachineLog.Warn("update: 'sig' is not valid base64; rejected");
                        return;
                    }
                    string reason;
                    verified = MachineCrypto.VerifyRelease(data, sigBytes, out reason);
                    if (!verified)
                    {
                        _error = "SIGNATURE INVALID - REJECTED";
                        MachineLog.Warn("update: release signature rejected: " + reason);
                        return;
                    }
                    MachineLog.Info("update: release signature verified (key "
                                    + ReleaseKey.FingerprintSha256.Substring(0,
                                        Math.Min(16, ReleaseKey.FingerprintSha256.Length)) + "...)");
                }

                if (!verified)
                {
                    if (cfg.RequireSignature)
                    {
                        _error = "RELEASE IS UNSIGNED - REJECTED";
                        MachineLog.Warn("update: release is not signed while requireSignature=true; rejected");
                        return;
                    }
                    MachineLog.Warn("update: applying an UNSIGNED release (requireSignature=false)");
                }

                // 4) 落盘：先 .tmp 再改名，避免半截文件被更新器看到
                string upDir = Path.Combine(_rt.MachineDir, "update");
                if (!Directory.Exists(upDir)) Directory.CreateDirectory(upDir);
                string dst = Path.Combine(upDir, "Machine.Core.dll");
                string tmp = dst + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);

                if (_sig.Length > 0)
                    File.WriteAllText(Path.Combine(upDir, "Machine.Core.dll.sig"), _sig.Trim());

                var mf = new StringBuilder();
                mf.Append("{\"version\":\"").Append(_latestVersion).Append("\"");
                mf.Append(",\"channel\":\"").Append(_channel).Append("\"");
                mf.Append(",\"sha256\":\"").Append(_sha256).Append("\"");
                mf.Append(",\"sig\":\"").Append(_sig).Append("\"");
                mf.Append(",\"url\":\"").Append(_coreUrl.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                mf.Append(",\"minVersion\":\"").Append(_minVersion).Append("\"");
                mf.Append(",\"verified\":").Append(verified ? "true" : "false");
                mf.Append(",\"time\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\"}");
                File.WriteAllText(Path.Combine(upDir, "apply.json"), mf.ToString());

                _downloaded = true;
                MachineLog.Info("update downloaded & verified to Machine/update (v" + _latestVersion + ")");
            }
            catch (Exception e)
            {
                _downloaded = false;
                _error = "DOWNLOAD FAILED";
                MachineLog.Warn("update download failed " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        public void Tick()
        {
            if (!_hasUpdate || _ui != null) return;
            _popupDelay -= Time.unscaledDeltaTime;
            if (_popupDelay > 0f) return;
            _popupDelay = 9999f;
            try
            {
                // 只在主菜单显示
                bool menu = false;
                try { menu = (MainMenu)UnityEngine.Object.FindFirstObjectByType(typeof(MainMenu)) != null; } catch { }
                if (!menu) { _popupDelay = 0.5f; return; }
                _ui = new UpdateUI(this, _rt);
                _ui.Show();
            }
            catch (Exception e) { MachineLog.Error("update ui failed " + e.Message); }
        }
    }

    /// <summary>更新提示弹窗（游戏风格白底圆角）。</summary>
    public class UpdateUI
    {
        private MachineUpdate _upd;
        private MachineRuntime _rt;
        private GameObject _root;
        private TMP_FontAsset _font;
        private TextMeshProUGUI _status;

        public UpdateUI(MachineUpdate upd, MachineRuntime rt) { _upd = upd; _rt = rt; }

        public void Show()
        {
            Build();
            if (_root == null) return;
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            if (_status != null)
                _status.text = UiFactory.Safe("VERSION " + _upd.LatestVersion
                                              + "  (CURRENT " + MachineLoader.Version + ")");
        }

        public void Hide()
        {
            try { if (_root != null) _root.SetActive(false); }
            catch (Exception e) { MachineLog.Error("UpdateUI hide failed " + e); }
        }

        private Transform FindCanvas()
        {
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                Canvas best = null;
                int bestOrder = int.MinValue;
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas cv = canvases[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy) continue;
                    if (cv.renderMode == RenderMode.ScreenSpaceOverlay && cv.sortingOrder > bestOrder)
                    {
                        bestOrder = cv.sortingOrder;
                        best = cv;
                    }
                }
                if (best == null) best = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                return best != null ? best.transform : null;
            }
            catch { return null; }
        }

        private TextMeshProUGUI Lbl(Transform parent, string text, int size, Color color)
        {
            var t = UiFactory.NewText(parent, text, size, color, _font);
            try { t.fontStyle = FontStyles.Bold; } catch { }
            return t;
        }

        private Button Btn(Transform parent, string label, Action onClick, bool primary = false)
        {
            Button b = UiFactory.NewButton(parent, label, onClick, _font, primary);
            try
            {
                var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null) { t.fontSize = 28; t.fontStyle = FontStyles.Bold; }
            }
            catch { }
            return b;
        }

        private void Build()
        {
            if (_root != null) return;
            try
            {
                Transform canvas = FindCanvas();
                if (canvas == null) { MachineLog.Warn("UpdateUI: no canvas found"); return; }
                _font = UiFactory.HarvestFont();

                _root = UiFactory.NewRect("MachineUpdate", canvas).gameObject;
                UiFactory.Stretch(_root.GetComponent<RectTransform>());
                _root.transform.SetAsLastSibling();

                var mask = UiFactory.NewRect("Mask", _root.transform);
                UiFactory.Stretch(mask);
                var maskImg = mask.gameObject.AddComponent<Image>();
                maskImg.color = new Color(0f, 0f, 0f, 0.55f);

                var panel = UiFactory.NewRect("Panel", _root.transform);
                var prt = (RectTransform)panel.transform;
                prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(900f, 520f);
                var pimg = panel.gameObject.AddComponent<Image>();
                pimg.sprite = UiFactory.RoundedSprite();
                pimg.type = Image.Type.Sliced;
                pimg.color = new Color(0.96f, 0.96f, 0.97f, 0.97f);

                var title = Lbl(panel.transform, "MACHINE UPDATE AVAILABLE", 34, new Color(0.13f, 0.14f, 0.17f, 1f));
                var trt = (RectTransform)title.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.anchoredPosition = new Vector2(0f, -26f);
                trt.sizeDelta = new Vector2(700f, 48f);
                title.alignment = TextAlignmentOptions.Center;

                _status = Lbl(panel.transform, "", 28, new Color(0.2f, 0.22f, 0.26f, 1f));
                var srt = (RectTransform)_status.transform;
                srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
                srt.anchoredPosition = new Vector2(0f, 96f);
                srt.sizeDelta = new Vector2(820f, 40f);
                _status.alignment = TextAlignmentOptions.Center;

                var notes = Lbl(panel.transform, UiFactory.Safe(_upd.Notes), 22, new Color(0.35f, 0.38f, 0.43f, 1f));
                var nrt = (RectTransform)notes.transform;
                nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
                nrt.anchoredPosition = new Vector2(0f, 26f);
                nrt.sizeDelta = new Vector2(820f, 80f);
                notes.alignment = TextAlignmentOptions.Center;
                notes.enableWordWrapping = true;

                Button dl = Btn(panel.transform, "Download & Install", delegate () { OnDownload(); }, true);
                var drt = (RectTransform)dl.transform;
                drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
                drt.anchoredPosition = new Vector2(-140f, -70f);
                drt.sizeDelta = new Vector2(280f, 64f);

                Button later = Btn(panel.transform, "Later", delegate () { Hide(); });
                var lrt = (RectTransform)later.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.anchoredPosition = new Vector2(140f, -70f);
                lrt.sizeDelta = new Vector2(280f, 64f);
            }
            catch (Exception e) { MachineLog.Error("UpdateUI build failed " + e); }
        }

        private void OnDownload()
        {
            _upd.Download();
            if (_upd.Downloaded)
            {
                if (_status != null)
                    _status.text = UiFactory.Safe("DOWNLOADED & VERIFIED v" + _upd.LatestVersion
                        + "!\nEXIT GAME, THEN RUN  Machine/machine_update.bat");
                MachineLog.Info("update ready - run machine_update.bat after exit");
            }
            else
            {
                string msg = _upd.Error.Length > 0 ? _upd.Error : "DOWNLOAD FAILED";
                if (_status != null)
                    _status.text = UiFactory.Safe(msg + "\nCHECK CONNECTION, TRY LATER");
            }
        }
    }
}
