#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Machine Loader - 一键发布包构建脚本

功能：
1. 运行所有测试
2. 验证代码规范
3. 编译C#核心和Mod
4. 打包发布文件
5. 生成发布清单（apply.json）
6. 签名发布包（如果有私钥）

用法：
    python build_release.py [--version X.Y.Z] [--skip-tests] [--skip-sign]
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

# 项目根目录
PROJECT_ROOT = os.path.dirname(os.path.abspath(__file__))
DIST_DIR = os.path.join(PROJECT_ROOT, "dist")
DIST_UPLOAD_DIR = os.path.join(PROJECT_ROOT, "dist_upload")
SRC_DIR = os.path.join(PROJECT_ROOT, "src")
BIN_DIR = os.path.join(PROJECT_ROOT, "bin")
KEYS_DIR = os.path.join(PROJECT_ROOT, "keys")

# 版本号
DEFAULT_VERSION = "2.5.0"


def run_tests():
    """运行所有测试。"""
    print("\n" + "=" * 60)
    print("运行测试...")
    print("=" * 60)

    result = subprocess.run(
        [sys.executable, "-m", "pytest", "tests/", "-v", "--tb=short"],
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True
    )

    print(result.stdout)
    if result.stderr:
        print(result.stderr)

    if result.returncode != 0:
        print("\n✗ 测试失败！")
        return False

    print("\n✓ 所有测试通过")
    return True


def check_code_quality():
    """检查代码规范。"""
    print("\n" + "=" * 60)
    print("代码规范检查...")
    print("=" * 60)

    # 尝试运行black检查（如果已安装）
    try:
        result = subprocess.run(
            [sys.executable, "-m", "black", "--check", "--diff", "dist_upload/", "tests/"],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True
        )
        if result.returncode == 0:
            print("✓ black 格式检查通过")
        else:
            print("⚠ black 发现格式问题（非致命）")
            print(result.stdout[:500])
    except FileNotFoundError:
        print("⚠ black 未安装，跳过格式检查")

    # 尝试运行ruff检查
    try:
        result = subprocess.run(
            [sys.executable, "-m", "ruff", "check", "dist_upload/", "tests/"],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True
        )
        if result.returncode == 0:
            print("✓ ruff  lint检查通过")
        else:
            print("⚠ ruff 发现lint问题（非致命）")
            print(result.stdout[:500])
    except FileNotFoundError:
        print("⚠ ruff 未安装，跳过lint检查")

    return True


def build_csharp():
    """编译C#核心和Mod。"""
    print("\n" + "=" * 60)
    print("编译C#核心和Mod...")
    print("=" * 60)

    # 编译核心
    build_core = os.path.join(PROJECT_ROOT, "build-core.ps1")
    if os.path.isfile(build_core):
        print("编译 Machine.Core.dll...")
        result = subprocess.run(
            ["powershell", "-ExecutionPolicy", "Bypass", "-File", build_core],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True
        )
        if result.returncode != 0:
            print("✗ Machine.Core.dll 编译失败")
            print(result.stderr)
            return False
        print("✓ Machine.Core.dll 编译成功")
    else:
        print("⚠ build-core.ps1 未找到，跳过C#编译")

    # 编译Mod
    build_mods = os.path.join(PROJECT_ROOT, "build-mods.ps1")
    if os.path.isfile(build_mods):
        print("编译 Mod DLL...")
        result = subprocess.run(
            ["powershell", "-ExecutionPolicy", "Bypass", "-File", build_mods],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True
        )
        if result.returncode != 0:
            print("✗ Mod DLL 编译失败")
            print(result.stderr)
            return False
        print("✓ Mod DLL 编译成功")

    return True


def create_release_package(version):
    """创建发布包。"""
    print("\n" + "=" * 60)
    print("创建发布包 v%s..." % version)
    print("=" * 60)

    # 清理并创建发布目录
    release_dir = os.path.join(DIST_DIR, "MachineLoader-%s" % version)
    if os.path.isdir(release_dir):
        shutil.rmtree(release_dir)
    os.makedirs(release_dir)

    # 复制文件
    files_to_copy = [
        # Python脚本
        ("dist_upload/machine_common.py", "Machine/machine_common.py"),
        ("dist_upload/machine_update.py", "Machine/machine_update.py"),
        ("dist_upload/machine_update.bat", "Machine/machine_update.bat"),
        ("dist_upload/install_machine.py", "install_machine.py"),
        ("dist_upload/install_machine.bat", "install_machine.bat"),
        ("dist_upload/uninstall_machine.py", "uninstall_machine.py"),
        ("dist_upload/uninstall_machine.bat", "uninstall_machine.bat"),
        # 公钥
        ("dist_upload/release_pubkey.pem", "Machine/release_pubkey.pem"),
        # 安装器
        ("bin/MachineInstaller.exe", "MachineInstaller.exe"),
        # C#核心
        ("bin/Machine.Core.dll", "Machine/Machine.Core.dll"),
        # Mono.Cecil
        ("bin/Mono.Cecil.dll", "Machine/Mono.Cecil.dll"),
        # 文档
        ("INSTALL.md", "INSTALL.md"),
        ("README.md", "README.md"),
    ]

    for src, dst in files_to_copy:
        src_path = os.path.join(PROJECT_ROOT, src)
        dst_path = os.path.join(release_dir, dst)
        if os.path.isfile(src_path):
            os.makedirs(os.path.dirname(dst_path), exist_ok=True)
            shutil.copy2(src_path, dst_path)
            print("  ✓ %s" % dst)
        else:
            print("  ⚠ 未找到: %s" % src)

    # 复制Mod DLL
    mods_bin_dir = os.path.join(BIN_DIR, "mods")
    if os.path.isdir(mods_bin_dir):
        mods_dst_dir = os.path.join(release_dir, "mods")
        os.makedirs(mods_dst_dir, exist_ok=True)
        for mod_file in os.listdir(mods_bin_dir):
            if mod_file.endswith(".dll"):
                shutil.copy2(
                    os.path.join(mods_bin_dir, mod_file),
                    os.path.join(mods_dst_dir, mod_file)
                )
                print("  ✓ mods/%s" % mod_file)

    # 创建默认配置
    default_config = {
        "repo": "AODOJUST/MachineLoader",
        "branch": "main",
        "channel": "stable",
        "enabled": True,
        "requireSignature": True
    }
    with open(os.path.join(release_dir, "Machine", "update.json"), "w") as f:
        json.dump(default_config, f, indent=2)
    print("  ✓ Machine/update.json")

    # 创建mods目录占位
    os.makedirs(os.path.join(release_dir, "mods"), exist_ok=True)
    with open(os.path.join(release_dir, "mods", "README.txt"), "w") as f:
        f.write("将Mod文件夹放入此目录即可加载Mod。\n")
    print("  ✓ mods/README.txt")

    # 计算文件哈希
    print("\n计算文件哈希...")
    file_hashes = {}
    for root, dirs, files in os.walk(release_dir):
        for filename in files:
            filepath = os.path.join(root, filename)
            rel_path = os.path.relpath(filepath, release_dir)
            with open(filepath, "rb") as f:
                file_hash = hashlib.sha256(f.read()).hexdigest()
            file_hashes[rel_path] = file_hash

    # 生成发布清单
    core_dll_path = os.path.join(release_dir, "Machine", "Machine.Core.dll")
    core_hash = file_hashes.get("Machine/Machine.Core.dll", "")

    manifest = {
        "version": version,
        "sha256": core_hash,
        "sig": "",
        "channel": "stable",
        "url": "https://github.com/AODOJUST/MachineLoader/releases/download/v%s/MachineLoader-%s.zip" % (version, version),
        "notes": "Machine Loader v%s release" % version,
        "minVersion": "2.0.0",
        "time": time.strftime("%Y-%m-%d %H:%M:%S"),
        "files": file_hashes
    }

    manifest_path = os.path.join(release_dir, "Machine", "apply.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    print("  ✓ Machine/apply.json")

    # 创建ZIP包
    zip_path = os.path.join(DIST_DIR, "MachineLoader-%s.zip" % version)
    if os.path.isfile(zip_path):
        os.remove(zip_path)

    print("\n创建ZIP包...")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zipf:
        for root, dirs, files in os.walk(release_dir):
            for filename in files:
                filepath = os.path.join(root, filename)
                arcname = os.path.relpath(filepath, DIST_DIR)
                zipf.write(filepath, arcname)

    zip_size = os.path.getsize(zip_path) / (1024 * 1024)
    print("✓ 发布包已创建: %s (%.2f MB)" % (zip_path, zip_size))

    return zip_path, manifest


def sign_release(manifest, zip_path, version):
    """签名发布包（如果有私钥）。"""
    print("\n" + "=" * 60)
    print("签名发布包...")
    print("=" * 60)

    private_key = os.path.join(KEYS_DIR, "release_private.pem")
    if not os.path.isfile(private_key):
        print("⚠ 未找到私钥，跳过签名")
        print("  私钥路径: %s" % private_key)
        return manifest

    # 使用sign_release.py签名
    sign_script = os.path.join(PROJECT_ROOT, "tools", "sign_release.py")
    if os.path.isfile(sign_script):
        result = subprocess.run(
            [sys.executable, sign_script, zip_path, "--version", version],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True
        )
        if result.returncode == 0:
            print("✓ 发布包已签名")
            print(result.stdout)
        else:
            print("⚠ 签名失败")
            print(result.stderr)
    else:
        print("⚠ sign_release.py 未找到，跳过签名")

    return manifest


def main():
    parser = argparse.ArgumentParser(description="Machine Loader 发布包构建脚本")
    parser.add_argument("--version", default=DEFAULT_VERSION, help="版本号 (默认: %s)" % DEFAULT_VERSION)
    parser.add_argument("--skip-tests", action="store_true", help="跳过测试")
    parser.add_argument("--skip-lint", action="store_true", help="跳过代码规范检查")
    parser.add_argument("--skip-build", action="store_true", help="跳过C#编译")
    parser.add_argument("--skip-sign", action="store_true", help="跳过签名")
    args = parser.parse_args()

    print("=" * 60)
    print("Machine Loader 发布包构建")
    print("版本: %s" % args.version)
    print("=" * 60)

    # 1. 运行测试
    if not args.skip_tests:
        if not run_tests():
            print("\n✗ 构建中止：测试失败")
            sys.exit(1)
    else:
        print("\n⚠ 跳过测试")

    # 2. 代码规范检查
    if not args.skip_lint:
        check_code_quality()
    else:
        print("\n⚠ 跳过代码规范检查")

    # 3. 编译C#
    if not args.skip_build:
        if not build_csharp():
            print("\n✗ 构建中止：C#编译失败")
            sys.exit(1)
    else:
        print("\n⚠ 跳过C#编译")

    # 4. 创建发布包
    zip_path, manifest = create_release_package(args.version)

    # 5. 签名
    if not args.skip_sign:
        manifest = sign_release(manifest, zip_path, args.version)

    # 完成
    print("\n" + "=" * 60)
    print("✓ 构建完成！")
    print("=" * 60)
    print("发布包: %s" % zip_path)
    print("版本: %s" % args.version)
    print("\n下一步:")
    print("  1. 测试发布包")
    print("  2. 更新 CHANGELOG.md")
    print("  3. 提交代码并打 tag")
    print("  4. 上传到 GitHub Releases")


if __name__ == "__main__":
    main()
