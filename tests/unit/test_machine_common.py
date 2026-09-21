# -*- coding: utf-8 -*-
"""
machine_common.py 单元测试

测试内容：
- sha256_file / sha256_bytes：哈希计算
- rsa_verify_sha256：RSA签名验证
- pubkey_fingerprint：公钥指纹
- validate_update_config：更新配置验证
- validate_net_config：网络配置验证
- parse_release_manifest：发布清单解析
- compare_versions：版本比较
- write_audit_log / read_audit_log：审计日志
- is_version_revoked：版本撤销
"""
import os
import sys
import json
import tempfile
import hashlib
import pytest

# 导入machine_common
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'dist_upload'))
import machine_common as mc


# ===========================================================================
# 哈希计算测试
# ===========================================================================

class TestSha256:
    """测试SHA-256哈希计算函数。"""

    def test_sha256_bytes_empty(self):
        """测试空字节的哈希。"""
        result = mc.sha256_bytes(b"")
        expected = hashlib.sha256(b"").hexdigest()
        assert result == expected
        assert len(result) == 64  # SHA-256是64位十六进制

    def test_sha256_bytes_hello(self):
        """测试"hello"的哈希。"""
        result = mc.sha256_bytes(b"hello")
        expected = hashlib.sha256(b"hello").hexdigest()
        assert result == expected

    def test_sha256_bytes_large(self):
        """测试大数据的哈希。"""
        data = b"x" * 1000000  # 1MB
        result = mc.sha256_bytes(data)
        expected = hashlib.sha256(data).hexdigest()
        assert result == expected

    def test_sha256_file(self, tmp_path):
        """测试文件哈希计算。"""
        test_file = tmp_path / "test.txt"
        test_file.write_bytes(b"test content")
        result = mc.sha256_file(str(test_file))
        expected = hashlib.sha256(b"test content").hexdigest()
        assert result == expected

    def test_sha256_file_large(self, tmp_path):
        """测试大文件哈希计算（超过chunk大小）。"""
        test_file = tmp_path / "large.txt"
        data = b"y" * 2000000  # 2MB
        test_file.write_bytes(data)
        result = mc.sha256_file(str(test_file))
        expected = hashlib.sha256(data).hexdigest()
        assert result == expected

    def test_sha256_file_not_found(self):
        """测试不存在的文件应抛出异常。"""
        with pytest.raises(FileNotFoundError):
            mc.sha256_file("/nonexistent/file.txt")


# ===========================================================================
# RSA签名验证测试
# ===========================================================================

class TestRsaVerify:
    """测试RSA签名验证函数。"""

    def test_rsa_verify_no_pubkey(self):
        """测试没有内置公钥时返回False。"""
        # 保存原始值
        original_modulus = mc.PUBKEY_MODULUS_HEX
        try:
            mc.PUBKEY_MODULUS_HEX = ""
            ok, reason = mc.rsa_verify_sha256(b"test", b"sig")
            assert ok is False
            assert "未内置公钥" in reason
        finally:
            mc.PUBKEY_MODULUS_HEX = original_modulus

    def test_rsa_verify_invalid_modulus(self):
        """测试非法公钥格式。"""
        ok, reason = mc.rsa_verify_sha256(b"test", b"sig",
                                            modulus_hex="not_hex",
                                            exponent_hex="10001")
        assert ok is False
        assert "公钥格式非法" in reason

    def test_rsa_verify_wrong_signature_length(self):
        """测试签名长度不符。"""
        # 使用一个小的测试密钥
        small_modulus = "FF"  # 很小的模数
        ok, reason = mc.rsa_verify_sha256(b"test", b"short_sig",
                                            modulus_hex=small_modulus,
                                            exponent_hex="10001")
        assert ok is False
        assert "签名长度不符" in reason

    def test_rsa_verify_invalid_key_values(self):
        """测试非法的密钥数值。"""
        ok, reason = mc.rsa_verify_sha256(b"test", b"sig",
                                            modulus_hex="00",
                                            exponent_hex="01")
        assert ok is False
        assert "公钥数值非法" in reason


# ===========================================================================
# 公钥指纹测试
# ===========================================================================

class TestPubkeyFingerprint:
    """测试公钥指纹函数。"""

    def test_pubkey_fingerprint_normal(self):
        """测试正常情况下公钥指纹返回64位十六进制。"""
        if mc.PUBKEY_MODULUS_HEX:
            result = mc.pubkey_fingerprint()
            assert len(result) == 64
            assert all(c in "0123456789abcdef" for c in result)
        else:
            pytest.skip("没有内置公钥")

    def test_pubkey_fingerprint_no_key(self):
        """测试没有公钥时返回空字符串。"""
        original = mc.PUBKEY_MODULUS_HEX
        try:
            mc.PUBKEY_MODULUS_HEX = ""
            result = mc.pubkey_fingerprint()
            assert result == ""
        finally:
            mc.PUBKEY_MODULUS_HEX = original


# ===========================================================================
# 更新配置验证测试
# ===========================================================================

class TestValidateUpdateConfig:
    """测试更新配置验证函数。"""

    def test_valid_config(self):
        """测试合法的配置。"""
        config = {
            "repo": "owner/repo",
            "branch": "main",
            "channel": "stable",
            "enabled": True,
            "requireSignature": True
        }
        out, warns = mc.validate_update_config(config)
        assert out["repo"] == "owner/repo"
        assert out["branch"] == "main"
        assert out["channel"] == "stable"
        assert out["enabled"] is True
        assert out["requireSignature"] is True
        assert len(warns) == 0

    def test_empty_config(self):
        """测试空配置返回默认值。"""
        out, warns = mc.validate_update_config({})
        assert out["repo"] == ""
        assert out["branch"] == "main"
        assert out["channel"] == "stable"
        assert out["enabled"] is True
        assert out["requireSignature"] is True

    def test_none_config(self):
        """测试None配置返回默认值和警告。"""
        out, warns = mc.validate_update_config(None)
        assert out["branch"] == "main"
        assert len(warns) == 1
        assert "不是 JSON 对象" in warns[0]

    def test_invalid_repo(self):
        """测试非法的repo格式。"""
        config = {"repo": "not-a-valid-repo!!!"}
        out, warns = mc.validate_update_config(config)
        assert out["repo"] == ""
        assert any("repo 格式非法" in w for w in warns)

    def test_invalid_branch(self):
        """测试非法的branch。"""
        config = {"branch": "invalid..branch"}
        out, warns = mc.validate_update_config(config)
        assert out["branch"] == "main"
        assert any("branch 非法" in w for w in warns)

    def test_invalid_channel(self):
        """测试非法的channel。"""
        config = {"channel": "nightly"}
        out, warns = mc.validate_update_config(config)
        assert out["channel"] == "stable"
        assert any("channel 只支持" in w for w in warns)

    def test_beta_channel(self):
        """测试beta通道。"""
        config = {"channel": "beta"}
        out, warns = mc.validate_update_config(config)
        assert out["channel"] == "beta"

    def test_invalid_require_signature(self):
        """测试非法的requireSignature。"""
        config = {"requireSignature": "yes"}
        out, warns = mc.validate_update_config(config)
        assert out["requireSignature"] is True
        assert any("requireSignature 非布尔值" in w for w in warns)

    def test_disabled_update(self):
        """测试禁用更新。"""
        config = {"enabled": False}
        out, warns = mc.validate_update_config(config)
        assert out["enabled"] is False


# ===========================================================================
# 网络配置验证测试
# ===========================================================================

class TestValidateNetConfig:
    """测试网络配置验证函数。"""

    def test_valid_config(self):
        """测试合法的配置。"""
        config = {
            "server": "192.168.1.1",
            "port": 8080,
            "playerName": "TestPlayer"
        }
        out, warns = mc.validate_net_config(config)
        assert out["server"] == "192.168.1.1"
        assert out["port"] == 8080
        assert out["playerName"] == "TestPlayer"
        assert len(warns) == 0

    def test_empty_config(self):
        """测试空配置返回默认值。"""
        out, warns = mc.validate_net_config({})
        assert out["server"] == ""
        assert out["port"] == 26460
        assert out["playerName"] == "Pilot"

    def test_none_config(self):
        """测试None配置返回默认值和警告。"""
        out, warns = mc.validate_net_config(None)
        assert out["port"] == 26460
        assert len(warns) == 1

    def test_valid_ipv4(self):
        """测试合法的IPv4地址。"""
        config = {"server": "10.0.0.255"}
        out, warns = mc.validate_net_config(config)
        assert out["server"] == "10.0.0.255"

    def test_invalid_ipv4_oob(self):
        """测试越界的IPv4地址。"""
        config = {"server": "256.1.1.1"}
        out, warns = mc.validate_net_config(config)
        assert out["server"] == ""
        assert any("IPv4 段越界" in w for w in warns)

    def test_valid_hostname(self):
        """测试合法的主机名。"""
        config = {"server": "example.com"}
        out, warns = mc.validate_net_config(config)
        assert out["server"] == "example.com"

    def test_invalid_server(self):
        """测试非法的服务器地址。"""
        config = {"server": "not valid!!!"}
        out, warns = mc.validate_net_config(config)
        assert out["server"] == ""
        assert any("不是合法 IP/主机名" in w for w in warns)

    def test_valid_port(self):
        """测试合法的端口。"""
        config = {"port": 12345}
        out, warns = mc.validate_net_config(config)
        assert out["port"] == 12345

    def test_port_too_high(self):
        """测试端口过高。"""
        config = {"port": 70000}
        out, warns = mc.validate_net_config(config)
        assert out["port"] == 26460
        assert any("port 越界" in w for w in warns)

    def test_port_too_low(self):
        """测试端口过低。"""
        config = {"port": 0}
        out, warns = mc.validate_net_config(config)
        assert out["port"] == 26460

    def test_invalid_port_type(self):
        """测试非法的端口类型。"""
        config = {"port": "not_a_number"}
        out, warns = mc.validate_net_config(config)
        assert out["port"] == 26460
        assert any("port 非数字" in w for w in warns)

    def test_valid_player_name(self):
        """测试合法的玩家名。"""
        config = {"playerName": "AODo_001"}
        out, warns = mc.validate_net_config(config)
        assert out["playerName"] == "AODo_001"

    def test_invalid_player_name(self):
        """测试非法的玩家名（包含特殊字符）。"""
        config = {"playerName": "name with spaces!!!"}
        out, warns = mc.validate_net_config(config)
        assert out["playerName"] == "Pilot"
        assert any("playerName 非法" in w for w in warns)


# ===========================================================================
# 发布清单解析测试
# ===========================================================================

class TestParseReleaseManifest:
    """测试发布清单解析函数。"""

    def test_valid_manifest(self, tmp_path):
        """测试合法的清单。"""
        manifest = {
            "version": "2.5.0",
            "sha256": "a" * 64,
            "sig": "base64signature",
            "channel": "stable",
            "url": "https://example.com/update",
            "notes": "Test release"
        }
        manifest_file = tmp_path / "apply.json"
        manifest_file.write_text(json.dumps(manifest))

        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out["version"] == "2.5.0"
        assert out["sha256"] == "a" * 64
        assert out["channel"] == "stable"
        assert out["url"] == "https://example.com/update"
        assert len(warns) == 0

    def test_missing_file(self, tmp_path):
        """测试不存在的清单文件。"""
        out, warns = mc.parse_release_manifest(str(tmp_path / "nonexistent.json"))
        assert out is None
        assert len(warns) == 1
        assert "清单不可用" in warns[0]

    def test_invalid_json(self, tmp_path):
        """测试非法的JSON。"""
        manifest_file = tmp_path / "bad.json"
        manifest_file.write_text("not valid json {{{")
        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out is None

    def test_not_dict(self, tmp_path):
        """测试清单不是JSON对象。"""
        manifest_file = tmp_path / "array.json"
        manifest_file.write_text("[1, 2, 3]")
        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out is None
        assert any("不是 JSON 对象" in w for w in warns)

    def test_invalid_sha256(self, tmp_path):
        """测试非法的sha256格式。"""
        manifest = {"version": "1.0", "sha256": "not_hex"}
        manifest_file = tmp_path / "apply.json"
        manifest_file.write_text(json.dumps(manifest))
        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out["sha256"] == ""
        assert any("sha256 字段格式非法" in w for w in warns)

    def test_invalid_version(self, tmp_path):
        """测试非法的version格式。"""
        manifest = {"version": "not-a-version"}
        manifest_file = tmp_path / "apply.json"
        manifest_file.write_text(json.dumps(manifest))
        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out["version"] == ""
        assert any("version 字段格式非法" in w for w in warns)

    def test_empty_manifest(self, tmp_path):
        """测试空清单。"""
        manifest_file = tmp_path / "empty.json"
        manifest_file.write_text("{}")
        out, warns = mc.parse_release_manifest(str(manifest_file))
        assert out["version"] == ""
        assert out["sha256"] == ""


# ===========================================================================
# 版本比较测试
# ===========================================================================

class TestCompareVersions:
    """测试版本比较函数。"""

    def test_equal_versions(self):
        """测试相同版本。"""
        assert mc.compare_versions("1.0.0", "1.0.0") == 0

    def test_greater_major(self):
        """测试主版本号更大。"""
        assert mc.compare_versions("2.0.0", "1.0.0") == 1

    def test_less_major(self):
        """测试主版本号更小。"""
        assert mc.compare_versions("1.0.0", "2.0.0") == -1

    def test_greater_minor(self):
        """测试次版本号更大。"""
        assert mc.compare_versions("1.2.0", "1.1.0") == 1

    def test_greater_patch(self):
        """测试补丁版本号更大。"""
        assert mc.compare_versions("1.0.2", "1.0.1") == 1

    def test_different_length(self):
        """测试不同长度的版本号。"""
        assert mc.compare_versions("1.0", "1.0.0") == 0
        assert mc.compare_versions("1.0.1", "1.0") == 1

    def test_none_version(self):
        """测试None版本号。"""
        assert mc.compare_versions(None, "1.0.0") == -1
        assert mc.compare_versions("1.0.0", None) == 1

    def test_empty_version(self):
        """测试空版本号。"""
        assert mc.compare_versions("", "1.0.0") == -1

    def test_non_numeric_parts(self):
        """测试非数字部分（应视为0）。"""
        assert mc.compare_versions("1.x.0", "1.0.0") == 0

    def test_four_part_version(self):
        """测试四部分版本号。"""
        assert mc.compare_versions("1.0.0.1", "1.0.0.0") == 1
        assert mc.compare_versions("1.0.0.0", "1.0.0.1") == -1


# ===========================================================================
# 审计日志测试
# ===========================================================================

class TestAuditLog:
    """测试审计日志函数。"""

    def test_write_and_read(self, tmp_path):
        """测试写入和读取审计日志。"""
        # 创建必要的目录结构
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        # 写入日志
        ok = mc.write_audit_log(str(tmp_path), "update", "success",
                                 version="2.5.0", source="https://example.com")
        assert ok is True

        # 读取日志
        logs = mc.read_audit_log(str(tmp_path))
        assert len(logs) == 1
        assert logs[0]["action"] == "update"
        assert logs[0]["result"] == "success"
        assert logs[0]["version"] == "2.5.0"
        assert "timestamp" in logs[0]

    def test_multiple_logs(self, tmp_path):
        """测试多条日志。"""
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        for i in range(5):
            mc.write_audit_log(str(tmp_path), "update", "success", version=f"2.{i}.0")

        logs = mc.read_audit_log(str(tmp_path))
        assert len(logs) == 5
        assert logs[-1]["version"] == "2.4.0"  # 最后一条

    def test_log_limit(self, tmp_path):
        """测试日志数量限制（100条）。"""
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        for i in range(150):
            mc.write_audit_log(str(tmp_path), "update", "success", version=f"2.{i}.0")

        logs = mc.read_audit_log(str(tmp_path), limit=0)
        assert len(logs) == 100  # 只保留最近100条

    def test_read_limit(self, tmp_path):
        """测试读取限制。"""
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        for i in range(10):
            mc.write_audit_log(str(tmp_path), "update", "success", version=f"2.{i}.0")

        logs = mc.read_audit_log(str(tmp_path), limit=3)
        assert len(logs) == 3

    def test_failure_log(self, tmp_path):
        """测试失败日志。"""
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        mc.write_audit_log(str(tmp_path), "update", "failure",
                           version="2.5.0", error="hash mismatch")

        logs = mc.read_audit_log(str(tmp_path))
        assert logs[0]["result"] == "failure"
        assert logs[0]["error"] == "hash mismatch"

    def test_extra_fields(self, tmp_path):
        """测试额外字段。"""
        update_dir = os.path.join(str(tmp_path), "Machine", "update")
        os.makedirs(update_dir, exist_ok=True)

        mc.write_audit_log(str(tmp_path), "rollback", "success",
                           extra={"backup": "backup_001.dll", "channel": "stable"})

        logs = mc.read_audit_log(str(tmp_path))
        assert logs[0]["backup"] == "backup_001.dll"
        assert logs[0]["channel"] == "stable"

    def test_read_empty(self, tmp_path):
        """测试读取空日志。"""
        logs = mc.read_audit_log(str(tmp_path))
        assert logs == []


# ===========================================================================
# 版本撤销测试
# ===========================================================================

class TestVersionRevoked:
    """测试版本撤销函数。"""

    def test_not_revoked(self):
        """测试未被撤销的版本。"""
        manifest = {"revoked": ["2.4.0"]}
        revoked, reason = mc.is_version_revoked(manifest, "2.5.0")
        assert revoked is False
        assert reason == ""

    def test_revoked(self):
        """测试被撤销的版本。"""
        manifest = {
            "revoked": ["2.4.0", "2.4.1-beta"],
            "revocationReasons": {
                "2.4.0": "Critical bug causing game crash"
            }
        }
        revoked, reason = mc.is_version_revoked(manifest, "2.4.0")
        assert revoked is True
        assert "Critical bug" in reason

    def test_revoked_no_reason(self):
        """测试被撤销但没有原因的版本。"""
        manifest = {"revoked": ["2.4.0"]}
        revoked, reason = mc.is_version_revoked(manifest, "2.4.0")
        assert revoked is True
        assert "revoked by the publisher" in reason

    def test_no_revoked_field(self):
        """测试没有revoked字段的清单。"""
        manifest = {"version": "2.5.0"}
        revoked, reason = mc.is_version_revoked(manifest, "2.5.0")
        assert revoked is False

    def test_none_manifest(self):
        """测试None清单。"""
        revoked, reason = mc.is_version_revoked(None, "2.5.0")
        assert revoked is False

    def test_invalid_revoked_type(self):
        """测试非法的revoked类型。"""
        manifest = {"revoked": "not_a_list"}
        revoked, reason = mc.is_version_revoked(manifest, "2.5.0")
        assert revoked is False


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
