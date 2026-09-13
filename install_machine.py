# -*- coding: utf-8 -*-
"""
Machine 加载器 傻瓜式安装程序

用法：双击 install_machine.bat（或 python install_machine.py [游戏目录] [选项]）
流程：定位 Aviassembly → 校验发布包哈希/签名 → 检查环境 → 注入+复制 → 校验结果 → 写安装记录

安全须知：游戏无法启动或出问题时，用 uninstall_machine.bat 移除 Machine，
恢复纯净版后再重新安装。

选项：
  游戏目录                直接指定游戏目录，跳过自动扫描
  -y, --yes               全部使用默认答案（自动化）
  --allow-unsigned        允许安装未签名的 Machine.Core.dll（仅开发/自测）
  --quiet                 只写日志
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import machine_common as mc

DIST = os.path.dirname(os.path.abspath(__file__))
INSTALLER = os.path.join(DIST, mc.INSTALLER_EXE)
CORE = os.path.join(DIST, mc.CORE_DLL)
CORE_SIG = os.path.join(DIST, mc.CORE_DLL + ".sig")
VERSION_JSON = os.path.join(DIST, "version.json")

# 安装完成后必须存在于 <游戏目录>/Machine/ 的文件（更新器依赖它们）
RUNTIME_FILES = [
    mc.INSTALLER_EXE, mc.CECIL_DLL,
    "machine_update.py", "machine_update.bat", "machine_common.py",
]


def parse_args(argv):
    p = argparse.ArgumentParser(add_help=True)
    p.add_argument("game_dir", nargs="?", default=None)
    p.add_argument("-y", "--yes", action="store_true")
    p.add_argument("--allow-unsigned", action="store_true")
    p.add_argument("--quiet", action="store_true")
    return p.parse_args(argv)


def say(s):
    mc.LOG.info(s)


def ask(args, question, default=""):
    if args.yes or not mc.is_interactive():
        return default
    try:
        return input(question).strip()
    except (EOFError, KeyboardInterrupt):
        return default


def confirm(args, question, default=False):
    """--yes 表示"全部同意"，因此直接返回 True（不是返回 default）。"""
    if args.yes:
        return True
    if not mc.is_interactive():
        return default
    try:
        ans = input(question).strip().lower()
    except (EOFError, KeyboardInterrupt):
        return False
    return ans in ("y", "yes")


# ---------------------------------------------------------------------------
# 游戏目录定位
# ---------------------------------------------------------------------------

def find_steam_candidates():
    """从 Steam 注册表 + libraryfolders.vdf 找 Aviassembly。"""
    cands = []
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
        steam_path, _ = winreg.QueryValueEx(key, "SteamPath")
        steam_path = steam_path.replace("/", "\\")
        cands.append(os.path.join(steam_path, "steamapps", "common", "Aviassembly"))
        vdf = os.path.join(steam_path, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            with open(vdf, encoding="utf-8", errors="ignore") as f:
                txt = f.read()
            for m in re.finditer(r'"path"\s+"([^"]+)"', txt):
                p = m.group(1).replace("\\\\", "\\").replace("/", "\\")
                cands.append(os.path.join(p, "steamapps", "common", "Aviassembly"))
    except Exception as e:
        mc.LOG.warn("读取 Steam 安装信息失败（不影响继续）: %s" % e)
    return [c for c in cands if os.path.isdir(c) and mc.game_dir_is_valid(c)]


def scan_drives():
    """全盘搜索：找含 Aviassembly_Data\\Managed\\Assembly-CSharp.dll 的目录（限深度）。"""
    found = []
    letters = [chr(x) + ":\\" for x in range(ord("C"), ord("Z") + 1)]
    for drive in letters:
        if not os.path.exists(drive):
            continue
        try:
            for root, dirs, files in os.walk(drive):
                depth = root[len(drive):].count(os.sep)
                if depth > 5:
                    dirs[:] = []
                    continue
                name = os.path.basename(root)
                if name.lower().startswith("aviassembly"):
                    if mc.game_dir_is_valid(root):
                        found.append(root)
                        dirs[:] = []
                        continue
                if depth >= 4:
                    dirs[:] = []
        except Exception:
            continue
    return found


def pick_target(args):
    if args.game_dir:
        return os.path.abspath(args.game_dir.strip('"').strip())
    cands = find_steam_candidates()
    seen, uniq = set(), []
    for c in cands:
        k = c.casefold()
        if k in seen:
            continue
        seen.add(k)
        uniq.append(c)
    cands = uniq
    if not cands:
        say("未从 Steam 注册表找到，开始全盘扫描（可能需要几十秒）...")
        cands = scan_drives()
    if not cands:
        say("未找到 Aviassembly 游戏目录。")
        return None
    say("找到候选游戏目录:")
    for i, c in enumerate(cands):
        say("  [%d] %s" % (i + 1, c))
    sel = ask(args, "直接回车使用第一个，或输入编号 / 手动路径: ", "")
    if sel == "":
        return cands[0]
    if sel.isdigit() and 1 <= int(sel) <= len(cands):
        return cands[int(sel) - 1]
    if os.path.isdir(sel):
        return os.path.abspath(sel)
    say("无法识别的输入，使用第一个候选。")
    return cands[0]


# ---------------------------------------------------------------------------
# 发布包自检
# ---------------------------------------------------------------------------

def verify_dist(args):
    """
    校验本次要安装的 Machine.Core.dll：
      * 与 version.json 里的 sha256 是否一致
      * 是否能被内置公钥验签
    返回 (sha256, version)。未签名时按 --allow-unsigned / 交互确认决定。
    """
    if not os.path.isfile(CORE):
        mc.fail("错误: 找不到 %s（应与本程序同目录）" % mc.CORE_DLL, mc.EXIT_USAGE)

    manifest, warns = mc.parse_release_manifest(VERSION_JSON)
    for w in warns:
        mc.LOG.warn("version.json: %s" % w)
    if manifest is None:
        mc.LOG.warn("未找到 %s，无法核对发布包版本与哈希" % VERSION_JSON)
        manifest = {}

    res = mc.verify_signed_file(CORE, CORE_SIG, (None, manifest.get("sig", "")))
    sha = res["sha256"]
    version = manifest.get("version", "")
    say("发布包版本: %s" % (version or "?"))
    say("Machine.Core.dll SHA-256: %s" % sha)

    expected = manifest.get("sha256", "")
    if expected and expected != sha:
        mc.fail("发布包自检失败：%s 与 version.json 记录的哈希不一致（包可能损坏或被篡改）"
                % mc.CORE_DLL, mc.EXIT_HASH,
                "  期望: %s\n  实际: %s\n请重新下载发布包。" % (expected, sha))

    if res["ok"]:
        say("签名校验通过（%s）" % res["signer"])
        say("发布公钥指纹: %s" % mc.pubkey_fingerprint())
        return sha, version

    if res["signer"]:
        # 有签名文件但验签失败 → 一定有问题，直接拒绝
        mc.fail("签名校验失败，拒绝安装：%s" % res["reason"], mc.EXIT_SIGNATURE,
                "请从官方发布页重新下载安装包。")
    if not args.allow_unsigned:
        mc.LOG.warn("未找到签名文件 %s（本次发布包未签名）" % os.path.basename(CORE_SIG))
        if not confirm(args, "安装未签名的加载器有被篡改的风险。仍要继续? (y/N): ", False):
            mc.fail("已取消安装（未签名）", mc.EXIT_CANCELLED)
    else:
        mc.LOG.warn("!! --allow-unsigned：跳过未签名告警（仅限开发自测）")
    return sha, version


# ---------------------------------------------------------------------------
# 文件复制
# ---------------------------------------------------------------------------

def copy_tree(src, dst, overwrite=False, skip_dirs=None):
    """复制目录树；skip_dirs 里的子目录名跳过（保护玩家数据）。返回失败列表。"""
    skip_dirs = skip_dirs or []
    failed = []
    if not os.path.exists(src):
        return failed
    try:
        os.makedirs(dst, exist_ok=True)
    except OSError as e:
        mc.LOG.error("无法创建目录 %s: %s" % (dst, e))
        return [dst]
    for item in os.listdir(src):
        s = os.path.join(src, item)
        d = os.path.join(dst, item)
        if os.path.isdir(s):
            if item in skip_dirs:
                mc.LOG.info("  跳过（保护玩家数据）: %s" % item)
                continue
            failed += copy_tree(s, d, overwrite, skip_dirs)
        else:
            if os.path.exists(d) and not overwrite:
                continue
            try:
                os.makedirs(os.path.dirname(d), exist_ok=True)
                if os.path.exists(d):
                    os.remove(d)
                shutil.copy2(s, d)
            except OSError as e:
                mc.LOG.warn("  跳过 %s (%s)" % (item, e))
                failed.append(s)
    return failed


def run_installer(game_dir, extra=None):
    """调用安装器并检查返回码。返回 (ok, code)。"""
    cmd = [INSTALLER, game_dir] + (extra or [])
    say("运行: %s" % " ".join('"%s"' % c if " " in c else c for c in cmd))
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=300, creationflags=flags)
    except FileNotFoundError:
        mc.LOG.error("找不到安装器: %s" % INSTALLER)
        return False, -1
    except PermissionError:
        mc.LOG.error("没有权限执行安装器（请以管理员身份运行）: %s" % INSTALLER)
        return False, -1
    except subprocess.TimeoutExpired:
        mc.LOG.error("安装器超过 300 秒未返回，已中止")
        return False, -1
    except OSError as e:
        mc.LOG.error("启动安装器失败: %s" % e)
        return False, -1

    for line in (r.stdout or "").strip().splitlines():
        say("  installer> %s" % line)
    for line in (r.stderr or "").strip().splitlines():
        mc.LOG.warn("  installer(err)> %s" % line)
    return r.returncode == 0, r.returncode


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def main(argv):
    args = parse_args(argv)
    mc.setup_console()
    if args.quiet:
        mc.LOG.verbose = False
    mc.LOG.open(DIST)

    say("=" * 52)
    say("   Machine 加载器 - 安装程序  v%s" % mc.LOADER_VERSION)
    say("=" * 52)

    # 1) 环境自检
    if sys.version_info < (3, 6):
        mc.fail("需要 Python 3.6 或更高版本（当前 %s）" % sys.version.split()[0], mc.EXIT_USAGE,
                "请安装 Python 3（安装时勾选 Add Python to PATH）。")

    if not os.path.isfile(INSTALLER):
        mc.fail("错误: 找不到 %s（应与本程序同目录）" % mc.INSTALLER_EXE, mc.EXIT_USAGE)
    if not os.path.isfile(os.path.join(DIST, mc.CECIL_DLL)):
        mc.fail("错误: 找不到 %s（安装器运行所需，应与本程序同目录）" % mc.CECIL_DLL,
                mc.EXIT_USAGE)

    # 2) 发布包完整性/签名
    core_sha, core_ver = verify_dist(args)

    # 3) 定位游戏
    game_dir = pick_target(args)
    if not game_dir:
        mc.fail("安装中止：未找到游戏目录。", mc.EXIT_USAGE)
    say("目标游戏目录: %s" % game_dir)
    mc.LOG.info("目标游戏目录: %s" % game_dir)
    mc.LOG.reopen(os.path.join(game_dir, mc.LOADER_NAME), "安装日志（游戏目录）")
    if not mc.game_dir_is_valid(game_dir):
        mc.fail("错误: 该目录不是有效的 Aviassembly 游戏目录"
                "（缺少 %s 或 %s）" % (mc.GAME_EXE, os.path.join(mc.MANAGED_REL, mc.ASM_DLL)),
                mc.EXIT_USAGE)

    # 4) 环境检查：游戏未运行 / 文件未被占用 / 可写
    pids = mc.running_processes()
    if pids:
        mc.fail("检测到游戏正在运行 (PID: %s)，请先完全退出游戏再安装。" % ", ".join(pids),
                mc.EXIT_GAME_RUNNING)
    asm_path = os.path.join(mc.managed_dir(game_dir), mc.ASM_DLL)
    if mc.is_file_locked(asm_path):
        mc.fail("目标程序集被占用: %s" % asm_path, mc.EXIT_GAME_RUNNING,
                "请确认游戏与杀毒软件未占用该文件。")
    if not mc.can_write(mc.managed_dir(game_dir)):
        mc.LOG.error("没有写权限: %s" % mc.managed_dir(game_dir))
        if not mc.is_admin():
            mc.LOG.error("提示：请右键 install_machine.bat -> 以管理员身份运行。")
        mc.fail("权限不足，安装中止。", mc.EXIT_PERMISSION)

    # 5) 已安装？确认覆盖
    core_installed = os.path.join(mc.managed_dir(game_dir), mc.CORE_DLL)
    if os.path.isfile(core_installed):
        old = ""
        try:
            old = mc.sha256_file(core_installed)
        except OSError:
            pass
        say("检测到 Machine 已安装（当前核心 %s）" % (old[:16] or "?"))
        if old == core_sha:
            say("与本次要安装的版本完全相同。")
        if not confirm(args, "覆盖安装（保留存档与配置）? (y/N): "):
            say("已取消。")
            mc.LOG.close()
            mc.pause()
            return mc.EXIT_CANCELLED

    # 6) 注入启动钩子 + 复制 Machine.Core.dll
    ok, code = run_installer(game_dir)
    if not ok:
        mc.fail("错误: 安装器执行失败（exit=%s）" % code, mc.EXIT_INSTALLER,
                "游戏可能仍在运行，或杀毒软件拦截了对 %s 的修改。"
                "可先运行 uninstall_machine.bat 恢复纯净版后重试。" % mc.ASM_DLL)

    # 7) 校验安装结果，不一致就整体回滚，不留半成品
    try:
        got = mc.sha256_file(core_installed)
    except OSError as e:
        mc.fail("无法读取安装后的 %s: %s" % (mc.CORE_DLL, e), mc.EXIT_HASH)
    if got != core_sha:
        mc.LOG.error("安装后的 %s 哈希与发布包不一致（期望 %s，实际 %s）" % (mc.CORE_DLL, core_sha, got))
        mc.LOG.error("将回滚 Assembly-CSharp.dll 并移除已写入的核心。")
        # 卸载会删除 Machine/（连带日志），而本进程的日志句柄正开在 Machine/logs/ 下，
        # Windows 不允许删除被打开的文件 —— 先把日志另存一份留证，再关句柄回滚。
        try:
            if mc.LOG.path and os.path.isfile(mc.LOG.path):
                stamp = time.strftime("%Y%m%d_%H%M%S")
                dst_log = os.path.join(game_dir, "Machine_install_failed_%s.log" % stamp)
                shutil.copy2(mc.LOG.path, dst_log)
                print("详细日志已另存为: %s" % dst_log)
        except OSError as e:
            mc.LOG.warn("日志另存失败: %s" % e)
        mc.LOG.close()
        run_installer(game_dir, ["--uninstall"])
        mc.fail("安装结果校验失败，已回滚。", mc.EXIT_ROLLBACK)
    say("安装核心校验通过: %s" % got)

    # 8) 复制 Machine/ 模板（不覆盖 profile.json / logs / update / backup / lang）
    machine_dir = os.path.join(game_dir, mc.LOADER_NAME)
    machine_tpl = os.path.join(DIST, mc.LOADER_NAME)
    if os.path.exists(machine_tpl):
        copy_tree(machine_tpl, machine_dir, overwrite=False,
                  skip_dirs=["logs", mc.UPDATE_DIRNAME, mc.BACKUP_DIRNAME, "lang"])

    # 9) 部署更新器运行所需的全部文件到 Machine/
    #    原实现只复制了 machine_update.bat，而它调用的 machine_update.py 与
    #    MachineInstaller.exe/Mono.Cecil.dll 都没被复制，导致"退出游戏后运行更新器"
    #    这条路径实际不可用。这里一并部署。
    for name in RUNTIME_FILES:
        src = os.path.join(DIST, name)
        if not os.path.isfile(src):
            mc.LOG.warn("更新器文件缺失，跳过: %s" % name)
            continue
        dst = os.path.join(machine_dir, name)
        try:
            shutil.copy2(src, dst)
            mc.LOG.info("已部署更新器文件: %s" % name)
        except OSError as e:
            mc.LOG.warn("复制 %s 失败: %s" % (name, e))

    # 10) mods/ 模板（不覆盖玩家已有 mod）
    mods_tpl = os.path.join(DIST, "mods")
    if os.path.exists(mods_tpl):
        copy_tree(mods_tpl, os.path.join(game_dir, "mods"), overwrite=False)

    # 11) 写安装记录（更新器据此判断"安装器是否被换过"）
    try:
        asm_sha = mc.sha256_file(asm_path) if os.path.isfile(asm_path) else ""
    except OSError:
        asm_sha = ""
    mc.write_record(game_dir, {
        "loaderVersion": mc.LOADER_VERSION,
        "coreVersion": core_ver,
        "coreSha256": core_sha,
        "installerSha256": mc.sha256_file(INSTALLER),
        "assemblySha256AfterInject": asm_sha,
        "installedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "installerPath": INSTALLER,
    })

    # 12) 提前确认更新器真的可用，而不是等用户退出游戏才发现少文件
    if os.path.isfile(os.path.join(machine_dir, "machine_update.py")):
        say("更新器已就绪: Machine\\machine_update.bat")
    else:
        mc.LOG.warn("更新器 machine_update.py 未部署成功，游戏内将无法一键应用更新。")

    say("=" * 52)
    say("安装完成！现在可以启动游戏。")
    say("提示:")
    say("  1. 游戏内检查到新版本 -> 点击下载 -> 退出游戏后运行:")
    say("       Machine\\machine_update.bat")
    say("  2. 若游戏无法启动或异常，运行 uninstall_machine.bat 一键恢复纯净版，")
    say("     确认纯净版正常后再重新安装。")
    say("  3. 更新日志: Machine\\logs\\machine_update.log")
    say("=" * 52)
    mc.LOG.close()
    mc.pause()
    return mc.EXIT_OK


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        print("\n已取消。")
        sys.exit(mc.EXIT_CANCELLED)
    except SystemExit:
        raise
    except Exception as e:
        import traceback
        mc.LOG.error("未预期的错误: %s" % e)
        mc.LOG.error(traceback.format_exc())
        mc.LOG.close()
        print("发生未预期的错误，详情见日志文件。")
        print("错误摘要: %s" % e)
        mc.pause()
        sys.exit(9)
