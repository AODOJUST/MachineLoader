# -*- coding: utf-8 -*-
"""
Machine 发布签名工具（仅发布者在本机运行，不随发布包分发）。

做什么：
  1. 用 keys/release_private.pem 对 dist/Machine.Core.dll 做 RSA-SHA256 签名
     -> dist/Machine.Core.dll.sig（base64 文本）
  2. 生成/更新 dist/version.json（含 version / channel / minVersion / core url / sha256 / sig / notes）
  3. 把公钥（modulus / exponent / 指纹）回写进带标记的源码：
       dist/machine_common.py     （Python 更新器与服务端）
       src/Machine.Core/Config.cs （游戏内下载器）
     这样公钥不会到处手抄漂移。
  4. 自校验：用回写后的公钥抓 machine_common 的验签函数验证刚生成的签名。
  5. 版本号三处对齐检查/同步（machine_common.py / UI.cs / version.json）。

私钥只在本机 keys/ 下，永远不要放进发布包。万一私钥泄露，必须重新生成密钥对
（客户端需发一次新版本才能换成新公钥）。

用法：
  python tools/sign_release.py                          # 用现有版本号签名
  python tools/sign_release.py --set-version 2.4.0      # 先统一版本号再签名
  python tools/sign_release.py --check                  # 只做一致性检查，不签名
  python tools/sign_release.py --notes "..." --channel stable
"""
import argparse
import base64
import binascii
import datetime
import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEV = os.path.dirname(HERE)                       # Machine_Dev
DIST = os.path.join(DEV, "dist")
KEYS = os.path.join(DEV, "keys")
PRIVATE_PEM = os.path.join(KEYS, "release_private.pem")
PUBLIC_PEM = os.path.join(KEYS, "release_public.pem")

COMMON_PY = os.path.join(DIST, "machine_common.py")
CONFIG_CS = os.path.join(DEV, "src", "Machine.Core", "Config.cs")
UI_CS = os.path.join(DEV, "src", "Machine.Core", "UI.cs")
CORE_DLL = os.path.join(DIST, "Machine.Core.dll")
CORE_SIG = os.path.join(DIST, "Machine.Core.dll.sig")
VERSION_JSON = os.path.join(DIST, "version.json")

SHA256_DIGESTINFO_PREFIX = binascii.unhexlify("3031300d060960864801650304020105000420")


# ---------------------------------------------------------------------------
# 极简 DER 解析（只够读 RSA 私钥）
# ---------------------------------------------------------------------------

class Der(object):
    def __init__(self, data):
        self.d = data
        self.p = 0

    def _len(self):
        b = self.d[self.p]
        self.p += 1
        if b < 0x80:
            return b
        n = b & 0x7F
        v = 0
        for _ in range(n):
            v = (v << 8) | self.d[self.p]
            self.p += 1
        return v

    def tlv(self):
        tag = self.d[self.p]
        self.p += 1
        ln = self._len()
        val = self.d[self.p:self.p + ln]
        self.p += ln
        return tag, val

    def integer(self):
        tag, val = self.tlv()
        if tag != 0x02:
            raise ValueError("expected INTEGER, got tag 0x%02x" % tag)
        return int.from_bytes(val, "big")


def parse_rsa_private_key(pem_text):
    """
    从 PEM 私钥里取出 (n, e, d)。支持 PKCS#8 (PrivateKeyInfo) 与 PKCS#1 (RSAPrivateKey)。
    openssl genpkey 产出的是 PKCS#8；openssl rsa -traditional 产出 PKCS#1。
    """
    body = "".join(l.strip() for l in pem_text.splitlines() if "-----" not in l)
    der = base64.b64decode(body)
    tag, seq = Der(der).tlv()
    if tag != 0x30:
        raise ValueError("PEM 不是有效的 RSA 私钥（顶层不是 SEQUENCE）")

    # 先按 PKCS#8 探测：INTEGER version, SEQUENCE algid, OCTET STRING key
    probe = Der(seq)
    probe.integer()
    t2, _ = probe.tlv()
    t3, val3 = probe.tlv()
    if t2 == 0x30 and t3 == 0x04:
        tag2, seq2 = Der(val3).tlv()
        if tag2 != 0x30:
            raise ValueError("PKCS#8 内部私钥不是 SEQUENCE")
        inner = Der(seq2)
    else:
        inner = Der(seq)   # PKCS#1：seq 内容本身就是 RSAPrivateKey 的字段

    inner.integer()        # version
    n = inner.integer()
    e = inner.integer()
    d = inner.integer()
    if n <= 0 or e <= 1 or d <= 0:
        raise ValueError("私钥字段非法")
    return n, e, d


# ---------------------------------------------------------------------------
# PKCS#1 v1.5 SHA-256 签名
# ---------------------------------------------------------------------------

def rsa_sign_sha256(message, n, e, d):
    k = (n.bit_length() + 7) // 8
    digest = hashlib.sha256(message).digest()
    t = SHA256_DIGESTINFO_PREFIX + digest
    if k < len(t) + 11:
        raise ValueError("密钥太短，无法承载 SHA-256 签名")
    em = b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t
    m = int.from_bytes(em, "big")
    s = pow(m, d, n)
    return s.to_bytes(k, "big")


# ---------------------------------------------------------------------------
# 公钥常量回写
# ---------------------------------------------------------------------------

def patch_block(path, begin, end, new_lines, label):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    if begin not in text or end not in text:
        raise SystemExit("!! %s 中找不到标记 %s / %s，无法回写公钥" % (path, begin, end))
    i = text.index(begin)
    j = text.index(end) + len(end)
    patched = text[:i] + begin + "\n" + "\n".join(new_lines) + "\n" + end + text[j:]
    if patched == text:
        print("   %s 公钥已是最新（%s）" % (label, path))
        return False
    with open(path, "w", encoding="utf-8") as f:
        f.write(patched)
    print("   已更新 %s 中的公钥（%s）" % (label, path))
    return True


def b64_to_hex(b):
    return binascii.hexlify(b).decode("ascii")


def read_pubkey_from_pem(pem_text):
    """只用于和私钥交叉核对（解析 SPKI 里的 RSAPublicKey）。"""
    body = "".join(l.strip() for l in pem_text.splitlines() if "-----" not in l)
    der = base64.b64decode(body)
    top = Der(der)
    tag, seq = top.tlv()
    inner = Der(seq)
    inner.tlv()          # AlgorithmIdentifier
    t, bits = inner.tlv()  # BIT STRING
    if t != 0x03:
        raise ValueError("expected BIT STRING")
    bits = bits[1:]      # 去掉 unused-bits 字节
    rk = Der(bits)
    t2, seq2 = rk.tlv()
    if t2 != 0x30:
        raise ValueError("expected RSAPublicKey SEQUENCE")
    rk = Der(seq2)
    n = rk.integer()
    e = rk.integer()
    return n, e


# ---------------------------------------------------------------------------
# 版本号对齐
# ---------------------------------------------------------------------------

def read_version_common():
    with open(COMMON_PY, "r", encoding="utf-8") as f:
        m = re.search(r'^LOADER_VERSION\s*=\s*"([^"]+)"', f.read(), re.M)
    return m.group(1) if m else ""


def read_version_ui():
    with open(UI_CS, "r", encoding="utf-8") as f:
        m = re.search(r'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"', f.read())
    return m.group(1) if m else ""


def read_version_json():
    if not os.path.isfile(VERSION_JSON):
        return ""
    with open(VERSION_JSON, "r", encoding="utf-8-sig") as f:
        return (json.load(f) or {}).get("version", "")


def write_version_common(v):
    with open(COMMON_PY, "r", encoding="utf-8") as f:
        text = f.read()
    text = re.sub(r'^LOADER_VERSION\s*=\s*"[^"]+"', 'LOADER_VERSION = "%s"' % v, text, count=1, flags=re.M)
    with open(COMMON_PY, "w", encoding="utf-8") as f:
        f.write(text)


def write_version_ui(v):
    with open(UI_CS, "r", encoding="utf-8") as f:
        text = f.read()
    text = re.sub(r'(public\s+const\s+string\s+Version\s*=\s*)"[^"]+"', r'\1"%s"' % v, text, count=1)
    with open(UI_CS, "w", encoding="utf-8") as f:
        f.write(text)


def check_versions(strict=True):
    v_common = read_version_common()
    v_ui = read_version_ui()
    v_json = read_version_json()
    print("版本号一致性检查:")
    print("   machine_common.py LOADER_VERSION = %s" % (v_common or "<缺失>"))
    print("   Config/UI.cs MachineLoader.Version = %s" % (v_ui or "<缺失>"))
    print("   dist/version.json version          = %s" % (v_json or "<无>"))
    problems = []
    if not v_common:
        problems.append("machine_common.py 缺少 LOADER_VERSION")
    if not v_ui:
        problems.append("UI.cs 缺少 MachineLoader.Version")
    if v_common and v_ui and v_common != v_ui:
        problems.append("machine_common.py(%s) 与 UI.cs(%s) 不一致" % (v_common, v_ui))
    if v_json and v_common and v_json != v_common:
        problems.append("version.json(%s) 与源码(%s) 不一致（发布前必须一致）" % (v_json, v_common))
    for p in problems:
        print("   !! %s" % p)
    if problems and strict:
        return None
    return v_common or v_ui


def main(argv):
    global DIST, CORE_DLL, CORE_SIG, VERSION_JSON
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--key", default=PRIVATE_PEM)
    ap.add_argument("--dist", default=None)
    ap.add_argument("--version", default="")
    ap.add_argument("--set-version", default="")
    ap.add_argument("--notes", default="")
    ap.add_argument("--channel", default="stable", choices=["stable", "beta"])
    ap.add_argument("--min-version", default="")
    ap.add_argument("--repo", default="AODOJUST/MachineLoader")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--core-url", default="")
    args = ap.parse_args(argv)

    if args.dist:
        DIST = os.path.abspath(args.dist)
        CORE_DLL = os.path.join(DIST, "Machine.Core.dll")
        CORE_SIG = os.path.join(DIST, "Machine.Core.dll.sig")
        VERSION_JSON = os.path.join(DIST, "version.json")

    print("=" * 60)
    print(" Machine 发布签名工具")
    print("=" * 60)

    if args.set_version:
        print("[1] 同步版本号 -> %s" % args.set_version)
        write_version_common(args.set_version)
        write_version_ui(args.set_version)
    ver = check_versions(strict=not args.check)
    if args.check:
        return 0 if ver else 1
    if not ver:
        print("中止：请先用 --set-version 统一版本号。")
        return 1
    if args.version:
        ver = args.version

    if not os.path.isfile(args.key):
        print("!! 找不到私钥: %s" % args.key)
        print("   生成密钥对：")
        print('     openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out keys/release_private.pem')
        print('     openssl rsa -in keys/release_private.pem -pubout -out keys/release_public.pem')
        return 1

    # --- 读私钥 ---
    print("[2] 读取私钥 %s" % args.key)
    with open(args.key, "r", encoding="utf-8") as f:
        priv_pem = f.read()
    try:
        n, e, d = parse_rsa_private_key(priv_pem)
    except Exception as ex:
        print("!! 解析私钥失败: %s" % ex)
        return 1
    bits = n.bit_length()
    print("    RSA-%d" % bits)
    if bits < 2048:
        print("!! 警告: 密钥短于 2048 位，安全性不足")
    if e != 65537:
        print("!! 警告: 公钥指数不是 65537 (e=%d)" % e)

    # --- 与公钥文件交叉核对 ---
    if os.path.isfile(PUBLIC_PEM):
        try:
            with open(PUBLIC_PEM, "r", encoding="utf-8") as f:
                pub_pem = f.read()
            pn, pe = read_pubkey_from_pem(pub_pem)
            if pn != n or pe != e:
                print("!! 私钥与 %s 不匹配（公钥文件过期？）" % PUBLIC_PEM)
                return 1
            print("    与 %s 一致" % os.path.basename(PUBLIC_PEM))
        except Exception as ex:
            print("!! 公钥文件解析失败: %s" % ex)

    mod_hex = "%x" % n
    if len(mod_hex) % 2:
        mod_hex = "0" + mod_hex
    exp_hex = "%x" % e
    if len(exp_hex) % 2:
        exp_hex = "0" + exp_hex
    fp = hashlib.sha256(binascii.unhexlify(mod_hex) + binascii.unhexlify(exp_hex)).hexdigest()
    print("    公钥指纹 SHA-256: %s" % fp)

    # --- 签名 DLL ---
    if not os.path.isfile(CORE_DLL):
        print("!! 找不到 %s，请先运行 pack_dist.py" % CORE_DLL)
        return 1
    with open(CORE_DLL, "rb") as f:
        data = f.read()
    sha = hashlib.sha256(data).hexdigest()
    print("[3] 签名 %s (%d 字节)" % (os.path.basename(CORE_DLL), len(data)))
    print("    SHA-256: %s" % sha)
    sig = rsa_sign_sha256(data, n, e, d)
    sig_b64 = base64.b64encode(sig).decode("ascii")
    with open(CORE_SIG, "w", encoding="ascii", newline="\n") as f:
        for i in range(0, len(sig_b64), 64):
            f.write(sig_b64[i:i + 64] + "\n")
    print("    已写 %s (%d 字节签名)" % (os.path.basename(CORE_SIG), len(sig)))

    # --- 回写公钥 ---
    print("[4] 回写公钥常量")
    py_lines = [
        'PUBKEY_MODULUS_HEX = "%s"' % mod_hex,
        'PUBKEY_EXPONENT_HEX = "%s"' % exp_hex,
        'PUBKEY_SHA256 = "%s"' % fp,
    ]
    cs_lines = [
        'public const string ModulusHex = "%s";' % mod_hex,
        'public const string ExponentHex = "%s";' % exp_hex,
        'public const string FingerprintSha256 = "%s";' % fp,
    ]
    patch_block(COMMON_PY, "# BEGIN MACHINE_PUBKEY", "# END MACHINE_PUBKEY", py_lines, "Python")
    if os.path.isfile(CONFIG_CS):
        patch_block(CONFIG_CS, "// BEGIN MACHINE_PUBKEY", "// END MACHINE_PUBKEY", cs_lines, "C#")

    # --- 自校验：用 machine_common 的验签函数验证刚生成的签名 ---
    print("[5] 自校验（用回写后的公钥验签）")
    sys.path.insert(0, DIST)
    try:
        import importlib
        import machine_common as mc
        importlib.reload(mc)
        ok, reason = mc.rsa_verify_sha256(data, sig)
        if not ok:
            print("!! 自校验失败: %s" % reason)
            return 1
        if mc.sha256_file(CORE_DLL) != sha:
            print("!! 自校验失败: 文件哈希与签名时不一致")
            return 1
        if mc.pubkey_fingerprint() != fp:
            print("!! 自校验失败: 回写后的指纹与私钥不匹配")
            return 1
        print("    通过：签名有效，公钥指纹 %s" % fp[:16])
    except Exception as ex:
        print("!! 自校验异常: %s" % ex)
        return 1

    # --- 写 version.json ---
    core_url = args.core_url or ("https://raw.githubusercontent.com/%s/%s/Machine.Core.dll"
                                 % (args.repo, args.branch))
    prior = {}
    if os.path.isfile(VERSION_JSON):
        try:
            with open(VERSION_JSON, "r", encoding="utf-8-sig") as f:
                prior = json.load(f) or {}
        except Exception:
            prior = {}
    notes = args.notes or prior.get("notes", "")
    min_ver = args.min_version or prior.get("minVersion", "")
    doc = {
        "version": ver,
        "channel": args.channel,
        "minVersion": min_ver,
        "core": core_url,
        "sha256": sha,
        "sig": "".join(sig_b64[i:i + 64] for i in range(0, len(sig_b64), 64)),
        "notes": notes,
        "signedAt": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "signerFingerprint": fp,
    }
    with open(VERSION_JSON, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("[6] 已写 %s (v%s, channel=%s)" % (os.path.basename(VERSION_JSON), ver, args.channel))

    print("=" * 60)
    print(" 完成。发布前请确认:")
    print("   * keys/release_private.pem 没有进入发布包")
    print("   * 把 %s 与 %s 一起上传到仓库（签名内嵌在 JSON 里，.sig 供离线校验）"
          % (os.path.basename(CORE_DLL), os.path.basename(VERSION_JSON)))
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
