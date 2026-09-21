import os
import sys
import tempfile
import shutil
import pytest

# 将dist_upload目录加入Python路径，确保能导入machine_common
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'dist_upload'))


@pytest.fixture
def temp_game_dir():
    """创建一个临时的假游戏目录，用于集成测试。"""
    tmpdir = tempfile.mkdtemp(prefix='machine_test_')
    # 创建游戏目录结构
    os.makedirs(os.path.join(tmpdir, 'Aviassembly_Data', 'Managed'), exist_ok=True)
    os.makedirs(os.path.join(tmpdir, 'Machine'), exist_ok=True)
    os.makedirs(os.path.join(tmpdir, 'Machine', 'update'), exist_ok=True)
    os.makedirs(os.path.join(tmpdir, 'Machine', 'logs'), exist_ok=True)
    os.makedirs(os.path.join(tmpdir, 'Machine', 'backups'), exist_ok=True)
    
    # 创建假的游戏exe和dll
    with open(os.path.join(tmpdir, 'Aviassembly.exe'), 'w') as f:
        f.write('fake game')
    with open(os.path.join(tmpdir, 'Aviassembly_Data', 'Managed', 'Assembly-CSharp.dll'), 'w') as f:
        f.write('fake assembly')
    
    yield tmpdir
    
    # 清理
    shutil.rmtree(tmpdir, ignore_errors=True)


@pytest.fixture
def sample_update_package(temp_game_dir):
    """创建一个示例更新包。"""
    update_dir = os.path.join(temp_game_dir, 'Machine', 'update')
    
    # 创建假的Machine.Core.dll
    core_path = os.path.join(update_dir, 'Machine.Core.dll')
    with open(core_path, 'w') as f:
        f.write('fake new core')
    
    # 创建apply.json
    import json
    manifest = {
        'version': '2.5.0',
        'sha256': '',
        'sig': '',
        'channel': 'stable',
        'url': 'https://example.com/update'
    }
    manifest_path = os.path.join(update_dir, 'apply.json')
    with open(manifest_path, 'w') as f:
        json.dump(manifest, f)
    
    return update_dir
