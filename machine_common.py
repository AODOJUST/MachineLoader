# -*- coding: utf-8 -*-
"""
Machine 公共模块：日志 / 完整性校验 / 进程与权限探测 / 备份与原子替换 / 输入校验。

被 install_machine.py、uninstall_machine.py、machine_update.py 共用。
设计原则（对应代码评审意见）：
  * 失败必须失败：所有外部调用检查返回码，所有 IO 捕获异常。
  * 不信任任何输入文件：更新包必须通过 SHA-256 + RSA 签名校验才会被应用。
  * 可恢复：覆盖任何文件前先备份，写临时文件后原子替换，失败自动回滚。
  * 可排查：每个动作都写入 Machine/logs/machine_update.log。

依赖：仅 Python 3 标准库（RSA 验签为纯 Python 实现，不依赖 cryptography/openssl）。
"""
from __future__ import print_function

import base64
import binascii
import datetime
import hashlib
import hmac
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

# ---------------------------------------------------------------------------
# 版本 / 常量
# ---------------------------------------------------------------------------

LOADER_NAME = "Machine"
LOADER_VERSION = "2.4.1"

GAME_PROCESS = "Aviassembly.exe"
GAME_EXE = "Aviassembly.exe"
MANAGED_REL = os.path.join("Aviassembly_Data", "Managed")
CORE_DLL = "Machine.Core.dll"
ASM_DLL = "Assembly-CSharp.dll"
INSTALLER_EXE = "MachineInstaller.exe"
CECIL_DLL = "Mono.Cecil.dll"

LOG_DIRNAME = "logs"
LOG_FILENAME = "machine_update.log"
BACKUP_DIRNAME = "backup"
UPDATE_DIRNAME = "update"
RECORD_FILENAME = "installed.json"

# 退出码（供 .bat / 自动化判断，语义固定）
EXIT_OK = 0
EXIT_USAGE = 1          # 参数/环境/路径不合法
EXIT_HASH = 2           # 哈希校验失败
EXIT_SIGNATURE = 3      # 签名校验失败
EXIT_INSTALLER = 4      # 安装器返回非零
EXIT_ROLLBACK = 5       # 更新失败且已回滚
EXIT_CANCELLED = 6      # 用户取消
EXIT_GAME_RUNNING = 7   # 游戏正在运行
EXIT_PERMISSION = 8     # 权限不足

# ---------------------------------------------------------------------------
# 公钥（RSA-3072 / PKCS#1 v1.5 / SHA-256）
# BEGIN MACHINE_PUBKEY
PUBKEY_MODULUS_HEX = "91d8e329a3e546f01aacbb356e46dd7cd99e3aa8b90e91307d9da678485c91cdfe2f1989a529b3e328b43711eb32acdd32ec2180d04ae9b95e32476fff28ace3a53a1f0a1eb560f018e0bd68e16aa4b531e44abda36aa1851a7ac50f45d4fafb0cc7a1ef8b2b477421303af8cba1cd511661ced32ffe1a52c442d6897e718068a2672c9613b5a1d968bbf3052f3354f2df26187be701e68f0c1c69a376bd72c375a3e3306037eebe278a897afa221c8470a5aeb61c12f5f100553020422e6d18ac5e6592b804f4368fdc9055ab6843d8d1f659c9e11c08ccf50a7c3eff1b3132097d364c9e2bf7d21f5d7228cb040e615d71df8ccc25a9b4f26e5dfe00c363e49a8d30f2d530b70d21348af8e2c709af99d0f1b30cac5826dd53b4aece1436f979c52c45094d32bf44cab3e54041a87266dcc2a1b8f0e31bde7760ffffff2254bbd1dbda45fe6fe571748a8fbd1f815fe301277d428368aa341dbbd3c17c392d9095e8806d2297532322f20958d5fb020aa51f6f42f962d300ed348fefbfbe99"
PUBKEY_EXPONENT_HEX = "010001"
PUBKEY_SHA256 = "93c4ab8598865fc82e6b0e1bcdf9ad5cf11f767d45db1286c14eee9bad70065a"
# END MACHINE_PUBKEY
# ---------------------------------------------------------------------------


# ---------------------------------------------------------------------------
# 日志
# ---------------------------------------------------------------------------

class _Logger(object):
    """同时写控制台与 Machine/logs/machine_update.log 的极简日志器。"""

    def __init__(self):
        self.path = None
        self._fh = None
        self.verbose = True

    def open(self, machine_dir):
        try:
            log_dir = os.path.join(machine_dir, LOG_DIRNAME)
            if not os.path.isdir(log_dir):
                os.makedirs(log_dir)
            self.path = os.path.join(log_dir, LOG_FILENAME)
            self._fh = open(self.path, "a", encoding="utf-8", errors="replace")
            self.info("=" * 60)
            self.info("%s tooling v%s (python %s)" % (LOADER_NAME, LOADER_VERSION, sys.version.split()[0]))
            self.info("cwd=%s" % os.getcwd())
        except Exception as e:  # 日志不可用绝不能拦住主流程
            self._fh = None
            print("[warn] cannot open log file: %s" % e)

    def close(self):
        try:
            if self._fh:
                self._fh.close()
        except Exception:
            pass
        self._fh = None

    def reopen(self, machine_dir, note=""):
        """把日志从"发布包目录"切到"游戏目录/Machine/logs"（安装器一开始还不知道游戏在哪）。"""
        prev = self.path
        self.close()
        self.open(machine_dir)
        if note:
            self.info(note)
        if prev and prev != self.path:
            self.info("日志已切换到 %s（之前写在 %s）" % (self.path, prev))

    def _write(self, level, msg):
        line = "%s [%s] %s" % (datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), level, msg)
        try:
            if self._fh:
                self._fh.write(line + "\n")
                self._fh.flush()
        except Exception:
            pass
        if self.verbose:
            try:
                print(msg)
            except Exception:
                try:
                    print(msg.encode("ascii", "replace").decode("ascii"))
                except Exception:
                    pass

    def info(self, msg):
        self._write("INFO", msg)

    def warn(self, msg):
        self._write("WARN", msg)

    def error(self, msg):
        self._write("ERROR", msg)

    def step(self, msg):
        self._write("STEP", msg)


LOG = _Logger()


def setup_console():
    """让中文在 chcp 65001 的控制台下尽量正常输出；失败也不影响运行。"""
    for stream in ("stdout", "stderr"):
        s = getattr(sys, stream, None)
        if s is None:
            continue
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def is_interactive():
    try:
        return bool(sys.stdin) and sys.stdin.isatty()
    except Exception:
        return False


def pause(msg="按回车退出..."):
    """仅交互式终端下等待按键；非交互（自动化/管道）直接返回，避免 EOFError。"""
    if not is_interactive():
        return
    try:
        input(msg)
    except (EOFError, KeyboardInterrupt):
        pass


def fail(msg, code=EXIT_USAGE, hint=None):
    LOG.error(msg)
    if hint:
        LOG.error(hint)
    LOG.close()
    pause()
    sys.exit(code)


# ---------------------------------------------------------------------------
# 哈希 / 签名
# ---------------------------------------------------------------------------

def sha256_file(path, chunk=1024 * 1024):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(chunk)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


# PKCS#1 v1.5 中 SHA-256 的 DigestInfo 前缀（RFC 8017 / EMSA-PKCS1-v1_5）
_SHA256_DIGESTINFO_PREFIX = binascii.unhexlify("3031300d060960864801650304020105000420")


def rsa_verify_sha256(message, signature, modulus_hex=None, exponent_hex=None):
    """
    纯 Python 的 RSA PKCS#1 v1.5 + SHA-256 验签。

    返回 (ok, reason)。任何结构异常都返回 False 而不是抛异常 —— 校验失败必须
    表现为"拒绝"，而不是让上层脚本崩掉。
    """
    n_hex = modulus_hex if modulus_hex else PUBKEY_MODULUS_HEX
    e_hex = exponent_hex if exponent_hex else PUBKEY_EXPONENT_HEX
    if not n_hex:
        return False, "未内置公钥（发布方尚未运行 tools/sign_release.py）"
    try:
        n = int(n_hex, 16)
        e = int(e_hex, 16)
    except ValueError:
        return False, "公钥格式非法"
    if n <= 0 or e <= 1:
        return False, "公钥数值非法"

    k = (n.bit_length() + 7) // 8
    if len(signature) != k:
        return False, "签名长度不符（期望 %d 字节，实际 %d 字节）" % (k, len(signature))

    m = pow(int.from_bytes(signature, "big"), e, n)
    em = m.to_bytes(k, "big")

    digest = hashlib.sha256(message).digest()
    t = _SHA256_DIGESTINFO_PREFIX + digest
    if k < len(t) + 11:
        return False, "密钥长度不足以承载 SHA-256 签名"
    expected = b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t
    if not hmac.compare_digest(em, expected):
        return False, "签名与公钥不匹配（文件可能被替换）"
    return True, "ok"


def pubkey_fingerprint():
    """公钥指纹：SHA-256(modulus||exponent)，用于人工核对。"""
    if not PUBKEY_MODULUS_HEX:
        return ""
    raw = binascii.unhexlify(PUBKEY_MODULUS_HEX) + binascii.unhexlify(PUBKEY_EXPONENT_HEX)
    return hashlib.sha256(raw).hexdigest()


def load_signature_bytes(*candidates):
    """
    依次尝试若干签名来源，返回 (bytes, 描述) 或 (None, 失败原因)。
    支持二进制 .sig 与 base64 文本（含 PEM 风格的换行）。
    """
    for c in candidates:
        if not c:
            continue
        path, inline = (c, None) if isinstance(c, str) else c
        if inline:
            raw = _decode_b64(inline)
            if raw:
                return raw, "内联签名"
            continue
        if path and os.path.isfile(path):
            try:
                with open(path, "rb") as f:
                    data = f.read()
            except OSError as e:
                return None, "读取签名文件失败: %s" % e
            if not data:
                return None, "签名文件为空: %s" % path
            # base64 文本签名（可读、便于随包分发）
            if data[:1] not in (b"\x00", b"\x01") or b"\n" in data[:64]:
                decoded = _decode_b64(data.decode("ascii", "ignore"))
                if decoded:
                    return decoded, path
            return data, path
    return None, "未找到签名（.sig / apply.json sig 字段均缺失）"


def _decode_b64(text):
    try:
        cleaned = "".join(text.split())
        return base64.b64decode(cleaned, validate=False) or None
    except (binascii.Error, ValueError, AttributeError):
        return None


def verify_signed_file(path, *sig_candidates, label=None):
    """
    校验一个文件的完整性：先算 SHA-256（返回给调用方做比对），再验签。

    返回 dict: { ok, sha256, signer, reason }
    """
    label = label or os.path.basename(path)
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        return {"ok": False, "sha256": "", "signer": "", "reason": "读取失败: %s" % e}

    digest = sha256_bytes(data)
    sig, signer = load_signature_bytes(*sig_candidates)
    if sig is None:
        return {"ok": False, "sha256": digest, "signer": "", "reason": signer}
    ok, reason = rsa_verify_sha256(data, sig)
    if not ok:
        reason = "%s: %s" % (label, reason)
    return {"ok": ok, "sha256": digest, "signer": signer, "reason": reason}


# ---------------------------------------------------------------------------
# 环境探测
# ---------------------------------------------------------------------------

def is_admin():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        # 非 Windows 或调用失败：退化为"能否写系统目录"判断，交给 can_write() 兜底
        return False


def can_write(path):
    """真实可写性探测（os.access 在 Windows 上不可靠）。"""
    try:
        if os.path.isdir(path):
            probe = os.path.join(path, ".machine_write_probe.tmp")
            with open(probe, "wb") as f:
                f.write(b"x")
            os.remove(probe)
            return True
        if os.path.exists(path):
            with open(path, "r+b"):
                pass
            return True
    except Exception:
        return False
    return False


def running_processes(image_name=GAME_PROCESS):
    """返回匹配 image_name 的 PID 列表；tasklist 不可用时返回 []。"""
    try:
        flags = 0
        if os.name == "nt":
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + image_name, "/NH"],
                           capture_output=True, text=True, errors="replace", timeout=15,
                           creationflags=flags)
        if r.returncode != 0:
            return []
        pids = []
        for line in (r.stdout or "").splitlines():
            parts = [p.strip() for p in line.split()]
            if len(parts) >= 2 and parts[0].lower() == image_name.lower():
                pids.append(parts[1])
        return pids
    except Exception:
        return []


def is_file_locked(path):
    """独占打开测试：被游戏占用时返回 True。"""
    if not os.path.exists(path):
        return False
    try:
        fd = os.open(path, os.O_RDWR | getattr(os, "O_BINARY", 0))
    except PermissionError:
        return True
    except OSError:
        return True
    else:
        os.close(fd)
        return False


def game_dir_is_valid(game_dir):
    return (os.path.isfile(os.path.join(game_dir, GAME_EXE))
            and os.path.isfile(os.path.join(game_dir, MANAGED_REL, ASM_DLL)))


def managed_dir(game_dir):
    return os.path.join(game_dir, MANAGED_REL)


def core_path(game_dir):
    return os.path.join(managed_dir(game_dir), CORE_DLL)


def update_dir(game_dir):
    return os.path.join(game_dir, LOADER_NAME, UPDATE_DIRNAME)


def log_dir(game_dir):
    return os.path.join(game_dir, LOADER_NAME, LOG_DIRNAME)


def backup_dir(game_dir):
    return os.path.join(game_dir, LOADER_NAME, BACKUP_DIRNAME)


def record_path(game_dir):
    return os.path.join(game_dir, LOADER_NAME, RECORD_FILENAME)


def find_installer(game_dir):
    """
    按可信度顺序查找 MachineInstaller.exe，返回 (path, tried_list)。

    这里修掉了原实现的兜底错误：原代码用 os.path.dirname(HERE)（即游戏根目录）
    当 dist 目录用，因此"兜底"永远失效。现在枚举所有真实可能出现的位置。
    """
    here = os.path.join(game_dir, LOADER_NAME)
    candidates = [
        os.path.join(here, INSTALLER_EXE),                      # 安装时随更新器一起复制过来（首选）
        os.path.join(here, "installer", INSTALLER_EXE),
        os.path.join(game_dir, INSTALLER_EXE),                  # 游戏根目录
        os.path.join(here, "update", INSTALLER_EXE),
        os.path.join(here, "dist", INSTALLER_EXE),
        os.path.join(os.path.dirname(game_dir), "dist", INSTALLER_EXE),  # 开发机上的发布目录
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c, candidates
    return None, candidates


# ---------------------------------------------------------------------------
# 配置校验（net.json / update.json / profile.json）
# ---------------------------------------------------------------------------

_REPO_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")
_BRANCH_RE = re.compile(r"^[A-Za-z0-9._/-]{1,100}$")
_SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")
_NICK_RE = re.compile(r"^[A-Za-z0-9_\u4e00-\u9fff]{1,16}$")
_IPV4_RE = re.compile(r"^(\d{1,3})(\.\d{1,3}){3}$")
_HOST_RE = re.compile(r"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")


def load_json_file(path):
    """读 JSON；失败返回 (None, 原因)。"""
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError as e:
        return None, "读取失败: %s" % e
    try:
        return json.loads(text), None
    except ValueError as e:
        return None, "JSON 解析失败: %s" % e


def validate_update_config(jv):
    """
    校验 Machine/update.json。返回 (cleaned_dict, [warnings])。
    不合法的字段会被丢弃并回落到安全默认值，而不是让上游拿着脏数据去联网。
    """
    warns = []
    out = {"repo": "", "branch": "main", "channel": "stable", "enabled": True,
           "requireSignature": True}
    if not isinstance(jv, dict):
        return out, ["update.json 不是 JSON 对象，已全部回落默认值"]

    repo = jv.get("repo", "")
    if repo:
        if not isinstance(repo, str) or not _REPO_RE.match(repo.strip().strip("/")):
            warns.append("repo 格式非法（应为 owner/repo），已忽略")
            repo = ""
        else:
            repo = repo.strip().strip("/")
    out["repo"] = repo

    branch = jv.get("branch", "main")
    if not isinstance(branch, str) or not _BRANCH_RE.match(branch or "") or ".." in (branch or ""):
        warns.append("branch 非法，回落 main")
        branch = "main"
    out["branch"] = branch

    channel = jv.get("channel", "stable")
    if channel not in ("stable", "beta"):
        warns.append("channel 只支持 stable/beta，回落 stable")
        channel = "stable"
    out["channel"] = channel

    en = jv.get("enabled", True)
    out["enabled"] = bool(en) if isinstance(en, bool) else True

    req = jv.get("requireSignature", True)
    if not isinstance(req, bool):
        warns.append("requireSignature 非布尔值，回落 true")
        req = True
    out["requireSignature"] = req
    return out, warns


def validate_net_config(jv):
    """校验 Machine/net.json。返回 (cleaned_dict, [warnings])。"""
    warns = []
    out = {"server": "", "port": 26460, "playerName": "Pilot"}
    if not isinstance(jv, dict):
        return out, ["net.json 不是 JSON 对象，已全部回落默认值"]

    server = jv.get("server", "")
    if server:
        if not isinstance(server, str):
            warns.append("server 非字符串，已忽略")
            server = ""
        else:
            server = server.strip()
            if server and not (_IPV4_RE.match(server) or _HOST_RE.match(server)):
                warns.append("server 不是合法 IP/主机名，已忽略")
                server = ""
            elif _IPV4_RE.match(server):
                for seg in server.split("."):
                    if not (0 <= int(seg) <= 255):
                        warns.append("server IPv4 段越界，已忽略")
                        server = ""
                        break
    out["server"] = server

    port = jv.get("port", 26460)
    if isinstance(port, bool) or not isinstance(port, (int, float)):
        warns.append("port 非数字，回落 26460")
        port = 26460
    port = int(port)
    if not (1 <= port <= 65535):
        warns.append("port 越界（应在 1..65535），回落 26460")
        port = 26460
    out["port"] = port

    name = jv.get("playerName", "Pilot")
    if not isinstance(name, str) or not _NICK_RE.match(name or ""):
        warns.append("playerName 非法（1-16 位中英文/数字/下划线），回落 Pilot")
        name = "Pilot"
    out["playerName"] = name
    return out, warns


# ---------------------------------------------------------------------------
# 发布清单（apply.json / version.json）
# ---------------------------------------------------------------------------

def parse_release_manifest(path):
    """
    解析更新清单。返回 (manifest_dict, [warnings])；缺字段不算致命，
    由调用方按 requireSignature 策略决定是否拒绝。
    """
    warns = []
    out = {"version": "", "sha256": "", "sig": "", "channel": "", "url": "",
           "notes": "", "minVersion": "", "time": "", "core": ""}
    jv, err = load_json_file(path)
    if err:
        return None, ["清单不可用: %s" % err]
    if not isinstance(jv, dict):
        return None, ["清单不是 JSON 对象"]

    for k in ("version", "sha256", "sig", "channel", "url", "notes", "minVersion",
              "time", "core"):
        v = jv.get(k, "")
        out[k] = v if isinstance(v, str) else ""

    if out["sha256"] and not _SHA256_RE.match(out["sha256"]):
        warns.append("sha256 字段格式非法（应为 64 位十六进制），已丢弃")
        out["sha256"] = ""
    if out["version"] and not re.match(r"^[0-9]+(\.[0-9]+){0,3}$", out["version"]):
        warns.append("version 字段格式非法，已丢弃")
        out["version"] = ""
    out["sha256"] = out["sha256"].lower()
    return out, warns


def compare_versions(a, b):
    """语义化比较：a>b 返回 1，a<b 返回 -1，相同 0。"""
    def norm(v):
        parts = []
        for p in str(v or "").split("."):
            try:
                parts.append(int(p))
            except ValueError:
                parts.append(0)
        return parts
    pa, pb = norm(a), norm(b)
    n = max(len(pa), len(pb))
    pa += [0] * (n - len(pa))
    pb += [0] * (n - len(pb))
    for x, y in zip(pa, pb):
        if x != y:
            return 1 if x > y else -1
    return 0


def assembly_version(path):
    """不加载程序集、只读元数据地取 AssemblyVersion（失败返回 ""）。"""
    try:
        import struct
        with open(path, "rb") as f:
            data = f.read(4096)
        # 直接找 #Blob 里的版本号不现实；这里退化为不做解析，交由调用方用哈希比对。
        return ""
    except Exception:
        return ""


# ---------------------------------------------------------------------------
# 备份 / 原子替换 / 回滚
# ---------------------------------------------------------------------------

def backup_file(path, dest_dir, tag=""):
    """把一个文件备份到 dest_dir，返回备份路径（失败抛 OSError）。"""
    if not os.path.isfile(path):
        return None
    if not os.path.isdir(dest_dir):
        os.makedirs(dest_dir)
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    base = os.path.basename(path)
    name = "%s%s.%s.bak" % (base, ("." + tag) if tag else "", stamp)
    dst = os.path.join(dest_dir, name)
    shutil.copy2(path, dst)
    return dst


def prune_backups(dest_dir, keep=5):
    """只保留最近 keep 个备份，避免无限增长。"""
    try:
        if not os.path.isdir(dest_dir):
            return
        files = [os.path.join(dest_dir, f) for f in os.listdir(dest_dir)]
        files = [f for f in files if os.path.isfile(f) and f.endswith(".bak")]
        files.sort(key=lambda p: os.path.getmtime(p), reverse=True)
        for old in files[keep:]:
            try:
                os.remove(old)
                LOG.info("清理旧备份: %s" % old)
            except OSError:
                pass
    except Exception as e:
        LOG.warn("清理旧备份失败: %s" % e)


def atomic_replace(src, dst):
    """
    同目录内写临时文件后 os.replace（NTFS 上是原子的），避免"覆盖到一半断电"。
    失败抛 OSError，调用方负责回滚。
    """
    dst_dir = os.path.dirname(os.path.abspath(dst))
    if not os.path.isdir(dst_dir):
        os.makedirs(dst_dir)
    fd, tmp = tempfile.mkstemp(prefix=".machine_", suffix=".tmp", dir=dst_dir)
    os.close(fd)
    try:
        shutil.copy2(src, tmp)
        # 简单完整性自检：字节数一致
        if os.path.getsize(tmp) != os.path.getsize(src):
            raise OSError("临时文件大小与源不一致")
        os.replace(tmp, dst)
    except Exception:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise


def move_into_place(src, dst):
    """更新包文件挪到目标位置（同分区用 replace，跨分区回退为 copy+remove）。"""
    try:
        os.replace(src, dst)
        return
    except OSError:
        pass
    shutil.copy2(src, dst)
    os.remove(src)


# ---------------------------------------------------------------------------
# 安装记录（记录安装来源，供更新时做"安装器是否被换过"的告警）
# ---------------------------------------------------------------------------

def write_record(game_dir, data):
    try:
        path = record_path(game_dir)
        d = os.path.dirname(path)
        if not os.path.isdir(d):
            os.makedirs(d)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
        return True
    except Exception as e:
        LOG.warn("写入 %s 失败: %s" % (RECORD_FILENAME, e))
        return False


def read_record(game_dir):
    jv, err = load_json_file(record_path(game_dir))
    if err or not isinstance(jv, dict):
        return {}
    return jv


def detect_origin(installer_path):
    """粗略判断安装器是不是从官方发布包来的（同目录是否有 Machine.Core.dll + version.json）。"""
    d = os.path.dirname(os.path.abspath(installer_path))
    has_core = os.path.isfile(os.path.join(d, CORE_DLL))
    has_ver = os.path.isfile(os.path.join(d, "version.json"))
    return has_core and has_ver
