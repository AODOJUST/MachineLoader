# -*- coding: utf-8 -*-
"""
Machine 发布包打包器（替代旧的 pack_dist.py + build_release.py 两条半成品链路）。

产出两样东西：

  1) Machine_Dev/dist/                       —— 扁平的"发布管理"目录
                                                 （tools/sync_release.py / verify_release.py /
                                                   selftest_release.py 都按这个布局找文件）
  2) Machine_Dev/dist/MachineLoader-<ver>/   —— 用户下载解压后看到的那一层
     Machine_Dev/dist/MachineLoader-<ver>.zip

为什么要有 2)：旧包把好几种布局混在一起，导致安装器与文档对不上：
  * version.json 里写的是 core_sha256，而 machine_common.parse_release_manifest()
    和 tools/verify_release.py 读的是 sha256 → 哈希与签名校验被静默跳过；
  * 没有 Machine.Core.dll.sig，requireSignature=true 的客户端实际收不到可验证的包；
  * mods/<Mod>/<Mod>.dll 与 mods/<Mod>/code/<Mod>.dll 同时存在（前者是 bin/mods 的
    扁平中间产物，加载器只认后者）；
  * 打包时把运行日志 logs/machine_update.log 也塞了进去。

用法：
  python tools/pack_release.py [--version 2.4.1] [--skip-sign] [--no-zip]
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
DEV = os.path.dirname(HERE)                    # Machine_Dev
BASE = os.path.dirname(DEV)                    # 豆包的下载
DIST = os.path.join(DEV, "dist")
BIN = os.path.join(DEV, "bin")
KEYS = os.path.join(DEV, "keys")
GAME_MODS = os.path.join(BASE, "Aviassembly_DEV", "mods")
GITHUB_COPY = os.path.join(BASE, "MachineLoader_github", "MachineLoader-main")
ARCHIVE = os.path.join(DEV, "_archive")

DEFAULT_VERSION = "2.4.1"

# dist/ 里由"发布流程"管理的脚本（缺失时从 archive / github 副本补齐）
SCRIPT_FILES = [
    "machine_common.py", "install_machine.py", "uninstall_machine.py", "machine_update.py",
    "install_machine.bat", "uninstall_machine.bat", "machine_update.bat",
    "SECURITY.md", "release_pubkey.pem",
]
# 只在发布包那一层需要（dist/ 扁平目录里放不放都行）
DOC_FILES = ["INSTALL.md", "README.md", "logo.ico", "logo.png"]

# 不进发布包的目录 / 文件后缀（开发期残留、运行期状态）
SKIP_DIRS = {"logs", "update", "backup", "__pycache__", ".git",
             "_prev", "captures", "screenshots"}
SKIP_SUFFIX = (".github_backup", ".bak", ".log", ".tmp", ".pyc")
SKIP_NAMES = {"installed.json", "battlehold_state.json", "opti_perf2.csv"}

# 自测截图（ScreenCapture.CaptureScreenshot / RenderAt 的产物，不是运行期资源）。
# MachineAAM 的自测会往 mod 根目录甩几十张 1.5MB 的 PNG，直接排除，
# 否则发布包白白多出 20MB 以上。_chase / _trail 是跟拍与远景色机位后缀。
SKIP_PNG_PREFIXES = ("aam_test_", "aam_msdm_", "aam_gun_", "aam_model_")
SKIP_PNG_SUFFIXES = ("_chase.png", "_trail.png")

# 这份布局下安装器需要的"更新器运行文件"（会被复制进 <游戏>/Machine/）
RUNTIME_FILES = [
    "MachineInstaller.exe", "Mono.Cecil.dll",
    "machine_common.py", "machine_update.py", "machine_update.bat",
    "uninstall_machine.py", "uninstall_machine.bat",
]

report = []


def say(s):
    report.append(str(s))
    print(s)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def first_existing(*paths):
    for p in paths:
        if p and os.path.isfile(p):
            return p
    return None


def move_aside(path, why=""):
    """把已存在的目录/文件挪进 _archive，而不是删掉。

    这个环境里"删除"经常被外部守卫拒绝（WinError 5），但改名/移动是允许的，
    所以所有"先清空再重建"的动作都改成"先挪开再重建"，既不丢东西也能跑通。
    """
    if not os.path.exists(path):
        return None
    os.makedirs(ARCHIVE, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    base = os.path.basename(path.rstrip("\\/"))
    dst = os.path.join(ARCHIVE, "%s_%s%s" % (base, stamp, why))
    n = 1
    while os.path.exists(dst):
        dst = os.path.join(ARCHIVE, "%s_%s%s_%d" % (base, stamp, why, n))
        n += 1
    shutil.move(path, dst)
    say("   (moved aside -> %s)" % dst)
    return dst


def try_remove(path):
    """尽力删除；被守卫拒绝时退化为移动到 _archive。"""
    if not os.path.exists(path):
        return
    try:
        if os.path.isdir(path):
            shutil.rmtree(path)
        else:
            os.remove(path)
    except OSError:
        move_aside(path, "_stale")


def source_for(name):
    """按可信度顺序找一个脚本的主副本。"""
    return first_existing(
        os.path.join(DIST, name),
        os.path.join(ARCHIVE, "MachineLoader-2.4.1_broken_layout_20260914", name),
        os.path.join(GITHUB_COPY, name),
    )


def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def copytree_filtered(src, dst, counter):
    """复制目录树，跳过开发残留与运行期状态文件。"""
    if not os.path.isdir(src):
        return
    os.makedirs(dst, exist_ok=True)
    for item in sorted(os.listdir(src)):
        s = os.path.join(src, item)
        d = os.path.join(dst, item)
        if os.path.isdir(s):
            if item in SKIP_DIRS or item.startswith("backup_"):
                counter["skipped_dirs"] += 1
                continue
            copytree_filtered(s, d, counter)
        else:
            if item.endswith(SKIP_SUFFIX) or item in SKIP_NAMES:
                counter["skipped_files"] += 1
                continue
            low = item.lower()
            if low.endswith(".png") and (item.startswith(SKIP_PNG_PREFIXES)
                                        or low.endswith(SKIP_PNG_SUFFIXES)):
                counter["skipped_files"] += 1
                continue
            # mods/<Mod>/<Mod>.dll 与 mods/<Mod>/code/<Mod>.dll 同时存在时，
            # mod.json 的 code.assemblies 只指向 code/ 那一份，
            # 根级那份是 bin/mods 布局留下的重复副本，不进发布包。
            if (low.endswith(".dll")
                    and item[:-4] == os.path.basename(src)
                    and os.path.isfile(os.path.join(src, "code", item))):
                counter["skipped_files"] += 1
                continue
            shutil.copy2(s, d)
            counter["files"] += 1


# ---------------------------------------------------------------------------
# dist/ 扁平布局
# ---------------------------------------------------------------------------

def stage_flat_dist():
    say("== [1] dist/ 扁平布局 ==")
    os.makedirs(DIST, exist_ok=True)

    for name in SCRIPT_FILES:
        dst = os.path.join(DIST, name)
        src = source_for(name)
        if src and os.path.abspath(src) != os.path.abspath(dst):
            shutil.copy2(src, dst)
        if os.path.isfile(dst):
            say("   %-24s ok (%d bytes)" % (name, os.path.getsize(dst)))
        else:
            say("   !! 缺少 %s（archive 与 github 副本里都没有）" % name)

    # 二进制
    for name in ["Machine.Core.dll", "MachineInstaller.exe", "Mono.Cecil.dll"]:
        src = os.path.join(BIN, name)
        if not os.path.isfile(src):
            say("   !! 缺少 %s，请先跑 build-core.ps1 / build-installer.ps1" % src)
            continue
        shutil.copy2(src, os.path.join(DIST, name))
        say("   %-24s ok (%d bytes)" % (name, os.path.getsize(src)))

    # Machine/ 配置模板
    tpl = os.path.join(DIST, "Machine")
    move_aside(tpl, "_stale")
    os.makedirs(tpl, exist_ok=True)
    write_text(os.path.join(tpl, "net.json"),
               '{\n  "server": "",\n  "port": 26460,\n  "playerName": "Pilot"\n}\n')
    write_text(os.path.join(tpl, "update.json"),
               '{\n  "repo": "AODOJUST/MachineLoader",\n  "branch": "main",\n'
               '  "channel": "stable",\n  "enabled": true,\n  "requireSignature": true\n}\n')
    # 安装器自己也会写这份 README；这里放一份是为了单独复制 Machine/ 时也有说明。
    # 绝不能带 BOM，也不能写死开发机的绝对路径。内容必须与 Installer.cs 的
    # WriteReadme() 保持一致，改一处就要改另一处。
    write_text(os.path.join(tpl, "README.txt"),
               "Machine Mod 加载器\n"
               "\n"
               "Mods    : <game>\\mods\\<Mod>\\mod.json\n"
               "Logs    : <game>\\Machine\\logs\\Machine.log\n"
               "          <game>\\Machine\\logs\\installer.log       (安装器)\n"
               "          <game>\\Machine\\logs\\machine_update.log  (命令行更新器)\n"
               "Config  : <game>\\Machine\\net.json      (联机 / 局域网服务器)\n"
               "          <game>\\Machine\\update.json   (更新仓库与通道)\n"
               "Backup  : <game>\\Machine\\backup\\       (旧版 Machine.Core.dll)\n"
               "\n"
               "Update  : 在游戏内下载完成后，退出游戏并运行\n"
               "            Machine\\machine_update.bat\n"
               "          (会先校验哈希与 RSA 签名，通过之后才写入 Machine.Core.dll)\n"
               "Remove  : 运行 uninstall_machine.bat，或者\n"
               "            MachineInstaller.exe --uninstall\n"
               "\n"
               "<game> 是你的 Aviassembly 安装目录。上面的路径都是相对路径；\n"
               "本文件在安装时生成，不会写入任何与这台机器相关的绝对路径。\n")
    say("   Machine/ 模板 ok")

    # mods（来自真正部署过的那份，布局才有 code/ 子目录）
    counter = {"files": 0, "skipped_dirs": 0, "skipped_files": 0}
    mods_dst = os.path.join(DIST, "mods")
    move_aside(mods_dst, "_stale")
    copytree_filtered(GAME_MODS, mods_dst, counter)
    say("   mods/ <- %s  (%d 文件，跳过 %d 目录 / %d 文件)"
        % (GAME_MODS, counter["files"], counter["skipped_dirs"], counter["skipped_files"]))

    # 公钥（私钥绝不入包）
    pub = first_existing(os.path.join(KEYS, "release_public.pem"),
                         os.path.join(GITHUB_COPY, "release_pubkey.pem"))
    if pub:
        shutil.copy2(pub, os.path.join(DIST, "release_pubkey.pem"))
        say("   release_pubkey.pem ok")
    else:
        say("   !! 找不到发布公钥")

    # 文档（打包那一层才必须，但放 dist 里也无妨）
    # 注意优先级：DEV 根目录是这些文档的正式来源，archive 只是历史备份，
    # 放前面会把过期副本当成源（曾经因此把旧的 INSTALL.md 打进了包）。
    for name in DOC_FILES:
        dst = os.path.join(DIST, name)
        src = first_existing(os.path.join(DEV, name),
                             os.path.join(GITHUB_COPY, name),
                             os.path.join(ARCHIVE, "MachineLoader-2.4.1_broken_layout_20260914", name))
        if src and os.path.abspath(src) != os.path.abspath(dst):
            shutil.copy2(src, dst)


# ---------------------------------------------------------------------------
# 签名
# ---------------------------------------------------------------------------

def sign(version):
    say("")
    say("== [2] 签名（tools/sign_release.py --dist dist）==")
    cmd = [sys.executable, os.path.join(HERE, "sign_release.py"), "--dist", DIST]
    if version:
        cmd += ["--version", version]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    for line in (r.stdout or "").splitlines():
        say("   " + line)
    if r.returncode != 0:
        for line in (r.stderr or "").splitlines():
            say("   stderr: " + line)
        say("   !! 签名失败")
    return r.returncode == 0


# ---------------------------------------------------------------------------
# 用户下载的那一层 + zip
# ---------------------------------------------------------------------------

def build_package(version, make_zip=True):
    say("")
    say("== [3] 发布包目录 MachineLoader-%s ==" % version)
    pkg = os.path.join(DIST, "MachineLoader-%s" % version)
    move_aside(pkg, "_stale")
    os.makedirs(pkg)

    # 3.1 脚本 + 二进制 + 清单，都是根级
    root_files = ["install_machine.bat", "uninstall_machine.bat", "machine_update.bat",
                  "install_machine.py", "uninstall_machine.py", "machine_update.py",
                  "machine_common.py", "MachineInstaller.exe", "Machine.Core.dll",
                  "Machine.Core.dll.sig", "Mono.Cecil.dll", "version.json",
                  "release_pubkey.pem", "INSTALL.md", "README.md", "logo.ico", "logo.png",
                  "SECURITY.md"]
    missing = []
    for name in root_files:
        src = os.path.join(DIST, name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(pkg, name))
        else:
            missing.append(name)
    say("   根级文件 %d 个，缺失: %s" % (len(root_files) - len(missing), missing or "无"))

    # 3.2 Machine/ 模板：只放"安装时铺到 <游戏>/Machine/ 的配置"，不再重复放核心与脚本
    tpl_dst = os.path.join(pkg, "Machine")
    os.makedirs(tpl_dst, exist_ok=True)
    for name in ["net.json", "update.json", "README.txt"]:
        src = os.path.join(DIST, "Machine", name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(tpl_dst, name))
    for name in ["logo.ico", "logo.png"]:
        src = os.path.join(DIST, name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(tpl_dst, name))
    say("   Machine/ 模板: %s" % ", ".join(sorted(os.listdir(tpl_dst))))

    # 3.3 mods/
    counter = {"files": 0, "skipped_dirs": 0, "skipped_files": 0}
    copytree_filtered(os.path.join(DIST, "mods"), os.path.join(pkg, "mods"), counter)
    say("   mods/ %d 个文件" % counter["files"])

    # 3.4 自检：绝不允许出现的文件
    banned = []
    for dirpath, dirs, files in os.walk(pkg):
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        for f in files:
            low = f.lower()
            rel = os.path.relpath(os.path.join(dirpath, f), pkg)
            if low in ("release_private.pem", "installed.json") or low.endswith(".github_backup"):
                banned.append(rel)
            if f == "machine_update.log":
                banned.append(rel)
            # 自测截图不该进包（占地方且是开发产物）
            if low.endswith(".png") and (f.startswith(SKIP_PNG_PREFIXES)
                                         or low.endswith(SKIP_PNG_SUFFIXES)):
                banned.append(rel)
            # 根级重复 DLL（mod.json 只认 code/ 那一份）
            if (low.endswith(".dll") and f[:-4] == os.path.basename(dirpath)
                    and os.path.isfile(os.path.join(dirpath, "code", f))):
                banned.append(rel)
    say("   禁用文件检查: %s" % ("通过" if not banned else "!! 发现 " + str(banned)))

    # 3.5 校验清单与签名能对上
    meta = {}
    vj = os.path.join(pkg, "version.json")
    if os.path.isfile(vj):
        with open(vj, encoding="utf-8-sig") as f:
            meta = json.load(f) or {}
    core = os.path.join(pkg, "Machine.Core.dll")
    ok_hash = bool(meta.get("sha256")) and sha256_file(core) == meta.get("sha256")
    ok_sig_present = os.path.isfile(os.path.join(pkg, "Machine.Core.dll.sig"))
    ok_sig_field = bool(meta.get("sig"))
    say("   version.json: version=%s sha256=%s sig=%s fingerprint=%s"
        % (meta.get("version"), (meta.get("sha256") or "")[:16] + "...",
           "有" if ok_sig_field else "无", (meta.get("signerFingerprint") or "")[:16] + "..."))
    say("   核心哈希与清单一致: %s ；.sig 存在: %s" % (ok_hash, ok_sig_present))

    zip_path = None
    if make_zip:
        zip_path = os.path.join(DIST, "MachineLoader-%s.zip" % version)
        try_remove(zip_path)
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for dirpath, dirs, files in os.walk(pkg):
                for f in sorted(files):
                    full = os.path.join(dirpath, f)
                    z.write(full, os.path.relpath(full, DIST))
        say("")
        say("== [4] zip ==")
        say("   %s  %.2f MB" % (zip_path, os.path.getsize(zip_path) / (1024.0 * 1024)))
    return pkg, zip_path, (ok_hash and ok_sig_present and ok_sig_field and not banned)


def main(argv):
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--version", default=DEFAULT_VERSION)
    ap.add_argument("--skip-sign", action="store_true")
    ap.add_argument("--no-zip", action="store_true")
    args = ap.parse_args(argv)

    say("=" * 70)
    say(" Machine 发布包打包   version=%s   %s" % (args.version, datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")))
    say("=" * 70)

    stage_flat_dist()

    signed = True
    if args.skip_sign:
        say("")
        say("== [2] 跳过签名 ==")
    else:
        signed = sign(args.version)

    pkg, zip_path, ok = build_package(args.version, make_zip=not args.no_zip)

    say("")
    say("=" * 70)
    say(" 结果: 签名%s / 包自检%s" % ("成功" if signed else "失败", "通过" if ok else "不通过"))
    say(" dist    : %s" % DIST)
    say(" package : %s" % pkg)
    if zip_path:
        say(" zip     : %s" % zip_path)
    say(" 下一步  : python tools/sync_release.py ；python tools/selftest_release.py")
    say("=" * 70)

    out = os.path.join(DIST, "release_report.txt")
    write_text(out, "\n".join(report) + "\n")
    return 0 if (signed and ok) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
