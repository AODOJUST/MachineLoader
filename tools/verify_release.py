# -*- coding: utf-8 -*-
"""只读校验同版本 AAM 发布副本；不代表所有 mod 的完整运行时兼容性测试。

核对 core 清单哈希、独立签名与内联签名、公钥指纹、指定 AAM 文件与开发部署
一致、测试开关关闭，并检查意外混入的发布私钥。任一检查失败退出 1。
用法：python tools/verify_release.py [--expected-version 2.4.1] [--self-test]
"""
import argparse
import contextlib
import io
import json
import os
import sys
import unittest
from unittest import mock

sys.dont_write_bytecode = True
BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(BASE)
sys.path.insert(0, os.path.join(BASE, "dist_upload"))
import machine_common as mc

TARGETS = [
    os.path.join(BASE, "dist"),
    os.path.join(BASE, "dist_upload"),
    os.path.join(REPO, "MachineLoader_github", "MachineLoader-main"),
]
SOURCE = os.path.join(REPO, "Aviassembly_DEV", "mods", "MachineAAM")
MOD_FILES = ("code/MachineAAM.dll", "aam_config.json", "R-37.planedesign", "AIM-424.planedesign",
             "decoy flare.planedesign")
# 同版本刷新时一并刷进发布件的其它 mod 文件（mod 的 DLL 不签名）。
# 每个 mod 的 code/<Mod>.dll 都必须与 Aviassembly_DEV 一致 —— 否则发布件里
# 装的是旧程序集，玩家拿到的行为与开发机不同（2026-09-15 发现全 mod 漂移，
# 旧版只查 MachineShop 一个，没拦住，故补全）。
EXTRA_MODS = {
    "BattleCore": ("code/BattleCore.dll",),
    "BattleHold": ("code/BattleHold.dll",),
    "DropTank": ("code/DropTank.dll",),
    "FactionSystem": ("code/FactionSystem.dll",),
    "FlightTrails": ("code/FlightTrails.dll",),
    "GMeter": ("code/GMeter.dll",),
    "GVision": ("code/GVision.dll",),
    "KillFeed": ("code/KillFeed.dll",),
    "MachineShop": ("code/MachineShop.dll",),
    "OptiMod": ("code/OptiMod.dll",),
    "Radar": ("code/Radar.dll",),
    "VoiceAlerts": ("code/VoiceAlerts.dll",),
    "ZoomMod": ("code/ZoomMod.dll",),
}
TEST_FLAGS = ("autoTest", "autoEnterFlyMode", "captureScreenshots", "testAmmo", "avoidTest", "evadeTest",
              "aiPersonaTest", "flareTest", "playerHitTest")


def read_json(path):
    with open(path, encoding="utf-8-sig") as stream:
        return json.load(stream)


def verify_target(root, expected_version):
    checks = []

    def check(label, ok):
        checks.append(bool(ok))
        print("  [%s] %s" % ("PASS" if ok else "FAIL", label))

    print("=== %s ===" % root)
    try:
        core = os.path.join(root, "Machine.Core.dll")
        meta = read_json(os.path.join(root, "version.json"))
        check("version = " + expected_version, meta.get("version") == expected_version)
        check("core SHA-256 matches manifest", mc.sha256_file(core) == meta.get("sha256"))
        # verify_signed_file 返回 dict；非空的 {ok: False} 也为真，必须检查 ok 字段。
        sidecar = mc.verify_signed_file(core, core + ".sig", label="Machine.Core.dll")
        check("sidecar RSA signature", sidecar.get("ok") is True)
        if sidecar.get("ok") is not True:
            print("    reason:", sidecar.get("reason", "unknown"))
        inline = mc.verify_signed_file(core, (None, meta.get("sig")), label="manifest core signature")
        check("manifest inline RSA signature", inline.get("ok") is True)
        check("signer fingerprint", mc.pubkey_fingerprint() == meta.get("signerFingerprint"))
        for name in MOD_FILES:
            path = os.path.join(root, "mods", "MachineAAM", name)
            check("AAM " + name + " SHA-256 matches deployed source",
                  mc.sha256_file(path) == mc.sha256_file(os.path.join(SOURCE, name)))
        for mod, names in EXTRA_MODS.items():
            for name in names:
                path = os.path.join(root, "mods", mod, name)
                check(mod + " " + name + " SHA-256 matches deployed source",
                      mc.sha256_file(path) == mc.sha256_file(
                          os.path.join(REPO, "Aviassembly_DEV", "mods", mod, name)))
        cfg = read_json(os.path.join(root, "mods", "MachineAAM", "aam_config.json"))
        for flag in TEST_FLAGS:
            check(flag + " disabled", cfg.get(flag) is False)
        check("evadeEnabled active", cfg.get("evadeEnabled") is True)
        check("normal observation setting", cfg.get("testObserveSeconds") == 150)
        leaks = []
        def scan_error(error):
            raise error
        for directory, _dirs, files in os.walk(root, onerror=scan_error):
            for name in files:
                if name.lower() == "release_private.pem":
                    leaks.append(os.path.join(directory, name))
        check("no release private key in package", not leaks)
        for path in leaks:
            print("    forbidden path:", path)
    except Exception as exc:
        check("read/check exception: " + type(exc).__name__ + ": " + str(exc), False)
    return bool(checks) and all(checks)


def verify_all(targets, expected_version):
    results = [verify_target(root, expected_version) for root in targets]
    ok = bool(results) and all(results)
    print("RESULT:", "ALL OK" if ok else "FAILED")
    return 0 if ok else 1


class VerifierTests(unittest.TestCase):
    """内存注入失败结果；不修改真实发布件、不创建伪造签名文件。"""
    def run_case(self, signature=None, inline=None, version="2.4.1", bad_hash=False,
                 bad_config=False, missing=False, private_key=False, bad_fingerprint=False):
        meta = {"version": version, "sha256": "core", "sig": "encoded",
                "signerFingerprint": "fingerprint"}
        cfg = dict((name, False) for name in TEST_FLAGS)
        cfg.update(evadeEnabled=True, testObserveSeconds=150)
        if bad_config:
            cfg["evadeTest"] = True
        good = {"ok": True, "reason": "ok"}
        walk = [("fixture", [], ["release_private.pem"])] if private_key else []
        def digest(path):
            if missing:
                raise FileNotFoundError("fixture missing file")
            if path.endswith("Machine.Core.dll"):
                return "core"
            if bad_hash and path.startswith("fixture") and path.endswith("MachineAAM.dll"):
                return "modified"
            return "mod"
        with mock.patch(__name__ + ".read_json", side_effect=[meta, cfg]), \
             mock.patch.object(mc, "sha256_file", side_effect=digest), \
             mock.patch.object(mc, "verify_signed_file", side_effect=[signature if signature is not None else good, inline if inline is not None else good]), \
             mock.patch.object(mc, "pubkey_fingerprint", return_value="bad" if bad_fingerprint else "fingerprint"), \
             mock.patch.object(os, "walk", return_value=walk), \
             contextlib.redirect_stdout(io.StringIO()):
            return verify_all(["fixture"], "2.4.1")

    def test_valid_package_exits_zero(self):
        self.assertEqual(self.run_case(), 0)

    def test_nonempty_failed_signature_exits_nonzero(self):
        self.assertEqual(self.run_case(signature={"ok": False, "reason": "bad RSA"}), 1)

    def test_inline_signature_failure(self):
        self.assertEqual(self.run_case(inline={"ok": False}), 1)

    def test_missing_signature_ok_field(self):
        self.assertEqual(self.run_case(signature={"reason": "no signature"}), 1)

    def test_wrong_version(self):
        self.assertEqual(self.run_case(version="2.5.0"), 1)

    def test_modified_mod(self):
        self.assertEqual(self.run_case(bad_hash=True), 1)

    def test_test_mode_left_on(self):
        self.assertEqual(self.run_case(bad_config=True), 1)

    def test_missing_file(self):
        self.assertEqual(self.run_case(missing=True), 1)

    def test_private_key_leak(self):
        self.assertEqual(self.run_case(private_key=True), 1)

    def test_wrong_fingerprint(self):
        self.assertEqual(self.run_case(bad_fingerprint=True), 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-version", default="2.4.1")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(VerifierTests)
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        return 0 if result.wasSuccessful() else 1
    return verify_all(TARGETS, args.expected_version)


if __name__ == "__main__":
    sys.exit(main())
