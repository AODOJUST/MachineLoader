# -*- coding: utf-8 -*-
"""
集成测试：模拟假游戏目录，测试更新流程的各个环节

测试内容：
- 游戏目录验证
- 备份和回滚
- 记录读写
- 审计日志在真实目录结构中的行为
- 更新配置文件的读写
"""
import os
import sys
import json
import shutil
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'dist_upload'))
import machine_common as mc


# ===========================================================================
# 游戏目录验证测试
# ===========================================================================

class TestGameDirValidation:
    """测试游戏目录验证函数。"""

    def test_valid_game_dir(self, temp_game_dir):
        """测试有效的游戏目录。"""
        assert mc.game_dir_is_valid(temp_game_dir) is True

    def test_invalid_game_dir_no_exe(self, tmp_path):
        """测试缺少游戏exe的目录。"""
        game_dir = str(tmp_path)
        os.makedirs(os.path.join(game_dir, 'Aviassembly_Data', 'Managed'), exist_ok=True)
        # 只创建dll，不创建exe
        with open(os.path.join(game_dir, 'Aviassembly_Data', 'Managed', 'Assembly-CSharp.dll'), 'w') as f:
            f.write('fake')
        assert mc.game_dir_is_valid(game_dir) is False

    def test_invalid_game_dir_no_dll(self, tmp_path):
        """测试缺少Assembly-CSharp.dll的目录。"""
        game_dir = str(tmp_path)
        with open(os.path.join(game_dir, 'Aviassembly.exe'), 'w') as f:
            f.write('fake')
        assert mc.game_dir_is_valid(game_dir) is False

    def test_nonexistent_dir(self):
        """测试不存在的目录。"""
        assert mc.game_dir_is_valid("/nonexistent/path") is False

    def test_managed_dir(self, temp_game_dir):
        """测试Managed目录路径。"""
        managed = mc.managed_dir(temp_game_dir)
        assert managed.endswith(os.path.join('Aviassembly_Data', 'Managed'))
        assert os.path.isdir(managed)

    def test_core_path(self, temp_game_dir):
        """测试Machine.Core.dll路径。"""
        core = mc.core_path(temp_game_dir)
        assert core.endswith('Machine.Core.dll')

    def test_update_dir(self, temp_game_dir):
        """测试更新目录路径。"""
        update = mc.update_dir(temp_game_dir)
        assert update.endswith(os.path.join('Machine', 'update'))
        assert os.path.isdir(update)

    def test_log_dir(self, temp_game_dir):
        """测试日志目录路径。"""
        log = mc.log_dir(temp_game_dir)
        assert 'logs' in log

    def test_backup_dir(self, temp_game_dir):
        """测试备份目录路径。"""
        backup = mc.backup_dir(temp_game_dir)
        assert 'backup' in backup

    def test_record_path(self, temp_game_dir):
        """测试记录文件路径。"""
        record = mc.record_path(temp_game_dir)
        assert record.endswith('installed.json')


# ===========================================================================
# 备份和回滚测试
# ===========================================================================

class TestBackupAndRollback:
    """测试备份和回滚功能。"""

    def test_backup_file(self, temp_game_dir):
        """测试文件备份。"""
        # 创建一个假的Machine.Core.dll
        core_path = mc.core_path(temp_game_dir)
        with open(core_path, 'w') as f:
            f.write('original content')

        # 备份
        backup_dir = mc.backup_dir(temp_game_dir)
        backup = mc.backup_file(core_path, backup_dir, tag='2.4.0')

        assert backup is not None
        assert os.path.isfile(backup)
        assert '2.4.0' in os.path.basename(backup)

        # 验证备份内容
        with open(backup, 'r') as f:
            assert f.read() == 'original content'

    def test_prune_backups(self, temp_game_dir):
        """测试备份清理（保留最近5个）。"""
        backup_dir = mc.backup_dir(temp_game_dir)
        os.makedirs(backup_dir, exist_ok=True)

        # 创建10个假备份
        for i in range(10):
            backup_file = os.path.join(backup_dir, 'Machine.Core_%d.dll.bak' % i)
            with open(backup_file, 'w') as f:
                f.write('backup %d' % i)

        # 清理，保留5个
        mc.prune_backups(backup_dir, keep=5)

        remaining = os.listdir(backup_dir)
        assert len(remaining) == 5

    def test_atomic_replace(self, temp_game_dir, tmp_path):
        """测试原子替换。"""
        # 创建源文件和目标文件
        src = str(tmp_path / 'src.dll')
        dst = mc.core_path(temp_game_dir)

        with open(src, 'w') as f:
            f.write('new content')
        with open(dst, 'w') as f:
            f.write('old content')

        # 原子替换
        mc.atomic_replace(src, dst)

        # 验证目标文件内容已更新
        with open(dst, 'r') as f:
            assert f.read() == 'new content'

    def test_move_into_place(self, temp_game_dir, tmp_path):
        """测试移动到位置。"""
        src = str(tmp_path / 'src.dll')
        dst = mc.core_path(temp_game_dir)

        with open(src, 'w') as f:
            f.write('new content')

        mc.move_into_place(src, dst)

        with open(dst, 'r') as f:
            assert f.read() == 'new content'


# ===========================================================================
# 记录读写测试
# ===========================================================================

class TestRecordReadWrite:
    """测试安装记录的读写。"""

    def test_write_and_read_record(self, temp_game_dir):
        """测试写入和读取安装记录。"""
        record = {
            "loaderVersion": "2.4.0",
            "coreVersion": "2.4.0",
            "coreSha256": "a" * 64,
            "installerSha256": "b" * 64,
            "installedAt": "2026-01-01 00:00:00",
            "installerName": "MachineInstaller.exe",
            "installChannel": "stable"
        }

        # 写入
        ok = mc.write_record(temp_game_dir, record)
        assert ok is True

        # 读取
        read_back = mc.read_record(temp_game_dir)
        assert read_back["loaderVersion"] == "2.4.0"
        assert read_back["coreVersion"] == "2.4.0"
        assert read_back["installerName"] == "MachineInstaller.exe"
        assert read_back["installChannel"] == "stable"
        # 确保没有绝对路径
        assert "installerPath" not in read_back

    def test_read_nonexistent_record(self, tmp_path):
        """测试读取不存在的记录。"""
        result = mc.read_record(str(tmp_path))
        assert result == {}

    def test_record_no_absolute_path(self, temp_game_dir):
        """测试记录中不包含绝对路径。"""
        record = {
            "loaderVersion": "2.4.0",
            "coreVersion": "2.4.0",
            "installerName": "MachineInstaller.exe"
        }
        mc.write_record(temp_game_dir, record)
        read_back = mc.read_record(temp_game_dir)

        # 检查所有值都不是绝对路径
        for key, value in read_back.items():
            if isinstance(value, str):
                assert not value.startswith('C:\\'), "记录中包含绝对路径: %s=%s" % (key, value)
                assert not value.startswith('D:\\'), "记录中包含绝对路径: %s=%s" % (key, value)


# ===========================================================================
# 审计日志集成测试
# ===========================================================================

class TestAuditLogIntegration:
    """测试审计日志在真实目录结构中的行为。"""

    def test_audit_log_in_update_dir(self, temp_game_dir):
        """测试审计日志存储在update目录中。"""
        mc.write_audit_log(temp_game_dir, "install", "success", version="2.4.0")

        audit_path = os.path.join(mc.update_dir(temp_game_dir), "audit_log.json")
        assert os.path.isfile(audit_path)

    def test_audit_log_multiple_actions(self, temp_game_dir):
        """测试多种操作类型的审计日志。"""
        actions = [
            ("install", "success", "2.4.0"),
            ("update", "success", "2.5.0"),
            ("update", "failure", "2.6.0"),
            ("rollback", "success", "2.5.0"),
            ("check", "success", ""),
        ]

        for action, result, version in actions:
            mc.write_audit_log(temp_game_dir, action, result, version=version)

        logs = mc.read_audit_log(temp_game_dir, limit=0)
        assert len(logs) == 5

        # 验证操作类型
        action_types = set(log["action"] for log in logs)
        assert "install" in action_types
        assert "update" in action_types
        assert "rollback" in action_types
        assert "check" in action_types

    def test_audit_log_failure_has_error(self, temp_game_dir):
        """测试失败日志包含错误信息。"""
        mc.write_audit_log(temp_game_dir, "update", "failure",
                           version="2.5.0", error="signature verification failed")

        logs = mc.read_audit_log(temp_game_dir)
        assert logs[0]["result"] == "failure"
        assert logs[0]["error"] == "signature verification failed"

    def test_audit_log_timestamp_format(self, temp_game_dir):
        """测试审计日志时间戳格式。"""
        mc.write_audit_log(temp_game_dir, "check", "success")

        logs = mc.read_audit_log(temp_game_dir)
        timestamp = logs[0]["timestamp"]
        # 时间戳应该是 YYYY-MM-DD HH:MM:SS 格式
        assert len(timestamp) == 19
        assert timestamp[4] == '-'
        assert timestamp[7] == '-'
        assert timestamp[10] == ' '
        assert timestamp[13] == ':'
        assert timestamp[16] == ':'


# ===========================================================================
# 更新配置集成测试
# ===========================================================================

class TestUpdateConfigIntegration:
    """测试更新配置文件的读写集成。"""

    def test_write_and_validate_update_config(self, temp_game_dir):
        """测试写入和验证更新配置。"""
        config = {
            "repo": "AODOJUST/MachineLoader",
            "branch": "main",
            "channel": "stable",
            "enabled": True,
            "requireSignature": True
        }

        # 写入配置文件
        config_path = os.path.join(temp_game_dir, 'Machine', 'update.json')
        with open(config_path, 'w') as f:
            json.dump(config, f)

        # 读取并验证
        with open(config_path, 'r') as f:
            raw = json.load(f)
        out, warns = mc.validate_update_config(raw)

        assert out["repo"] == "AODOJUST/MachineLoader"
        assert out["branch"] == "main"
        assert out["channel"] == "stable"
        assert out["enabled"] is True
        assert out["requireSignature"] is True
        assert len(warns) == 0

    def test_update_config_with_invalid_channel(self, temp_game_dir):
        """测试包含非法channel的配置。"""
        config = {
            "repo": "owner/repo",
            "channel": "nightly",  # 非法
            "enabled": True
        }

        out, warns = mc.validate_update_config(config)
        assert out["channel"] == "stable"  # 回落到默认值
        assert any("channel 只支持" in w for w in warns)

    def test_net_config_integration(self, temp_game_dir):
        """测试网络配置文件的读写集成。"""
        config = {
            "server": "192.168.1.100",
            "port": 8080,
            "playerName": "TestPlayer"
        }

        # 写入配置文件
        config_path = os.path.join(temp_game_dir, 'Machine', 'net.json')
        with open(config_path, 'w') as f:
            json.dump(config, f)

        # 读取并验证
        with open(config_path, 'r') as f:
            raw = json.load(f)
        out, warns = mc.validate_net_config(raw)

        assert out["server"] == "192.168.1.100"
        assert out["port"] == 8080
        assert out["playerName"] == "TestPlayer"


# ===========================================================================
# 发布清单集成测试
# ===========================================================================

class TestReleaseManifestIntegration:
    """测试发布清单在真实目录结构中的行为。"""

    def test_parse_manifest_in_update_dir(self, temp_game_dir):
        """测试解析update目录中的发布清单。"""
        manifest = {
            "version": "2.5.0",
            "sha256": "a" * 64,
            "sig": "base64signature",
            "channel": "stable",
            "url": "https://example.com/update",
            "notes": "Test release notes"
        }

        manifest_path = os.path.join(mc.update_dir(temp_game_dir), "apply.json")
        with open(manifest_path, 'w') as f:
            json.dump(manifest, f)

        out, warns = mc.parse_release_manifest(manifest_path)
        assert out["version"] == "2.5.0"
        assert out["sha256"] == "a" * 64
        assert out["channel"] == "stable"

    def test_revoked_version_in_manifest(self, temp_game_dir):
        """测试包含撤销版本的发布清单。"""
        manifest = {
            "version": "2.5.0",
            "revoked": ["2.4.0", "2.4.1"],
            "revocationReasons": {
                "2.4.0": "Critical security vulnerability"
            }
        }

        # 测试未撤销的版本
        revoked, reason = mc.is_version_revoked(manifest, "2.5.0")
        assert revoked is False

        # 测试被撤销的版本
        revoked, reason = mc.is_version_revoked(manifest, "2.4.0")
        assert revoked is True
        assert "Critical security vulnerability" in reason

        # 测试被撤销但没有原因的版本
        revoked, reason = mc.is_version_revoked(manifest, "2.4.1")
        assert revoked is True
        assert "revoked by the publisher" in reason


# ===========================================================================
# 完整更新流程模拟测试
# ===========================================================================

class TestFullUpdateFlow:
    """模拟完整的更新流程。"""

    def test_simulate_update_success(self, temp_game_dir):
        """模拟成功的更新流程。"""
        # 1. 初始状态：安装2.4.0
        initial_record = {
            "loaderVersion": "2.4.0",
            "coreVersion": "2.4.0",
            "coreSha256": "old" * 16,
            "installerName": "MachineInstaller.exe",
            "installChannel": "stable"
        }
        mc.write_record(temp_game_dir, initial_record)

        # 创建当前的Machine.Core.dll
        core_path = mc.core_path(temp_game_dir)
        with open(core_path, 'w') as f:
            f.write('old core content')

        # 2. 备份当前版本
        backup_dir = mc.backup_dir(temp_game_dir)
        backup = mc.backup_file(core_path, backup_dir, tag='2.4.0')
        assert backup is not None

        # 3. 记录更新开始
        mc.write_audit_log(temp_game_dir, "update", "success",
                           version="2.5.0", source="https://example.com")

        # 4. 模拟安装新版本
        with open(core_path, 'w') as f:
            f.write('new core content')

        # 5. 更新记录
        new_record = mc.read_record(temp_game_dir)
        new_record["coreVersion"] = "2.5.0"
        new_record["coreSha256"] = "new" * 16
        mc.write_record(temp_game_dir, new_record)

        # 6. 验证最终状态
        final_record = mc.read_record(temp_game_dir)
        assert final_record["coreVersion"] == "2.5.0"

        with open(core_path, 'r') as f:
            assert f.read() == 'new core content'

        # 审计日志应该有一条更新记录
        logs = mc.read_audit_log(temp_game_dir)
        assert logs[-1]["action"] == "update"
        assert logs[-1]["result"] == "success"

    def test_simulate_update_failure_and_rollback(self, temp_game_dir):
        """模拟更新失败并回滚的流程。"""
        # 1. 初始状态
        core_path = mc.core_path(temp_game_dir)
        with open(core_path, 'w') as f:
            f.write('original content')

        # 2. 备份
        backup_dir = mc.backup_dir(temp_game_dir)
        backup = mc.backup_file(core_path, backup_dir, tag='2.4.0')

        # 3. 模拟安装失败（写入错误内容）
        with open(core_path, 'w') as f:
            f.write('corrupted content')

        # 4. 记录失败
        mc.write_audit_log(temp_game_dir, "update", "failure",
                           version="2.5.0", error="hash mismatch after install")

        # 5. 回滚
        mc.atomic_replace(backup, core_path)

        # 6. 验证回滚成功
        with open(core_path, 'r') as f:
            assert f.read() == 'original content'

        # 7. 记录回滚
        mc.write_audit_log(temp_game_dir, "rollback", "success",
                           version="2.4.0", extra={"backup": os.path.basename(backup)})

        # 验证审计日志
        logs = mc.read_audit_log(temp_game_dir)
        assert logs[-1]["action"] == "rollback"
        assert logs[-1]["result"] == "success"


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
