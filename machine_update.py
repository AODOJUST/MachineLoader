# -*- coding: utf-8 -*-
"""
Machine 更新应用器（游戏内下载完成后，退出游戏运行本程序）

位置：<游戏目录>/Machine/machine_update.py
（安装时与 machine_common.py、MachineInstaller.exe、Mono.Cecil.dll 一起复制到 Machine/）

流程：
  1. 定位游戏目录与更新器（多路径探测；找不到就明确报错，不再"失败当成功"）
  2. 读取 Machine/update/apply.json 清单
  3. SHA-256 比对下载文件
  4. RSA 签名验证（内置公钥，私钥只在发布方手里）——不通过直接拒绝，绝不覆盖 DLL
  5. 检查游戏是否仍在运行 / 目标目录是否可写（必要时提示管理员）
  6. 备份当前 Machine.Core.dll
  7. 调用 MachineInstaller.exe --update（该程序内部再做临时文件 + 原子替换 + 回滚）
  8. 复核结果哈希；不符则自动回滚
  9. 全程写日志到 Machine/logs/machine_update.log

用法：
  python machine_update.py [游戏目录] [选项]
选项：
  -y, --yes              不询问，直接应用（自动化用）
  --allow-unsigned       允许无签名的更新包（危险，仅本地自测）
  --force                跳过版本比较（版本不新也应用）
  --wait N               检测到游戏在运行则最多等待 N 秒
  --kill                 检测到游戏在运行则强制结束它（会丢未存档进度）
  --keep-update          应用成功后保留 Machine/update/ 内容（便于排查）
  --quiet                只写日志文件，不打印到控制台
"""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import machine_common as mc


def parse_args(argv):
    p = argparse.ArgumentParser(add_help=True)
    p.add_argument("game_dir", nargs="?", default=None)
    p.add_argument("-y", "--yes", action="store_true")
    p.add_argument("--allow-unsigned", action="store_true")
    p.add_argument("--force", action="store_true")
    p.add_argument("--wait", type=int, default=0)
    p.add_argument("--kill", action="store_true")
    p.add_argument("--keep-update", action="store_true")
    p.add_argument("--quiet", action="store_true")
    return p.parse_args(argv)


def resolve_game_dir(explicit):
    if explicit:
        return os.path.abspath(explicit.strip('"').strip())
    # 本脚本位于 <游戏目录>/Machine/ 下
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def confirm(args, question):
    if args.yes:
        return True
    if not mc.is_interactive():
        mc.LOG.warn("非交互终端且未指定 -y，按取消处理")
        return False
    try:
        return input(question).strip().lower() in ("y", "yes")
    except (EOFError, KeyboardInterrupt):
        return False


def ensure_game_closed(args, game_dir):
    """游戏在运行会让 DLL 覆盖失败（或让游戏崩溃），必须先确认它已退出。"""
    pids = mc.running_processes()
    locked = mc.is_file_locked(mc.core_path(game_dir))
    if not pids and not locked:
        return True

    if pids:
        mc.LOG.warn("检测到游戏正在运行 (PID: %s)" % ", ".join(pids))
    if locked:
        mc.LOG.warn("目标文件被占用: %s" % mc.core_path(game_dir))

    if args.kill and pids:
        if not confirm(args, "将强制结束游戏进程，未保存的进度会丢失。继续? (y/N): "):
            return False
        for pid in pids:
            try:
                subprocess.run(["taskkill", "/PID", pid, "/T", "/F"],
                               capture_output=True, text=True, timeout=20)
                mc.LOG.info("已结束进程 PID %s" % pid)
            except Exception as e:
                mc.LOG.error("结束进程 %s 失败: %s" % (pid, e))
        time.sleep(2.0)
        return not mc.is_file_locked(mc.core_path(game_dir))

    if args.wait > 0:
        deadline = time.time() + args.wait
        mc.LOG.info("等待游戏退出（最多 %d 秒）..." % args.wait)
        while time.time() < deadline:
            time.sleep(2.0)
            if not mc.running_processes() and not mc.is_file_locked(mc.core_path(game_dir)):
                mc.LOG.info("游戏已退出")
                return True
        mc.LOG.error("等待超时，游戏仍在运行")
        return False

    if args.yes or not mc.is_interactive():
        mc.LOG.error("游戏仍在运行，无法应用更新。请先完全退出游戏"
                     "（或使用 --kill 自动结束 / --wait N 等待）。")
        return False

    mc.LOG.info("请关闭游戏后按回车重试；输入 q 放弃。")
    try:
        return input("> ").strip().lower() not in ("q", "quit")
    except (EOFError, KeyboardInterrupt):
        return False


def verify_update_package(game_dir, updir, require_signature):
    """
    校验更新包：签名 + 哈希。
    返回 (ok, info dict, reason, exit_code)。签名不通过绝不返回 ok=True。
    """
    core = os.path.join(updir, mc.CORE_DLL)
    if not os.path.isfile(core):
        return False, {}, "未找到待应用的更新 (Machine/update/%s)" % mc.CORE_DLL, mc.EXIT_USAGE

    manifest_path = os.path.join(updir, "apply.json")
    manifest, warns = mc.parse_release_manifest(manifest_path)
    for w in warns:
        mc.LOG.warn("清单提示: %s" % w)
    if manifest is None:
        mc.LOG.warn("未找到或无法解析 %s（更新包缺少来源信息）" % manifest_path)
        manifest = {"version": "", "sha256": "", "sig": "", "channel": "",
                    "url": "", "notes": ""}

    sig_file = os.path.join(updir, mc.CORE_DLL + ".sig")
    result = mc.verify_signed_file(core, sig_file, (None, manifest.get("sig", "")))
    info = {"core": core, "manifest": manifest, "sha256": result["sha256"],
            "signed": result["ok"]}

    if not result["ok"]:
        if result["signer"]:
            # 签名文件存在但验签失败 → 一定是被替换过，无条件拒绝
            return False, info, "签名无效，已拒绝应用更新：%s" % result["reason"], mc.EXIT_SIGNATURE
        if require_signature:
            return (False, info,
                    "更新包缺少有效签名，已拒绝应用更新：%s" % result["reason"],
                    mc.EXIT_SIGNATURE)
        mc.LOG.warn("!! 更新包未签名（--allow-unsigned 显式指定）。仅限本地自测。")
    else:
        mc.LOG.info("签名校验通过（%s）" % result["signer"])
        mc.LOG.info("公钥指纹: %s" % mc.pubkey_fingerprint())

    expected = manifest.get("sha256", "")
    if expected:
        if expected != result["sha256"]:
            return (False, info,
                    "SHA-256 不匹配，更新包已损坏或被替换\n  期望: %s\n  实际: %s"
                    % (expected, result["sha256"]),
                    mc.EXIT_HASH)
        mc.LOG.info("SHA-256 校验通过: %s" % result["sha256"])
    elif require_signature:
        return False, info, "更新清单缺少 sha256 字段，且要求强制签名，已拒绝", mc.EXIT_HASH
    else:
        mc.LOG.warn("更新清单缺少 sha256 字段，仅依赖签名校验")

    return True, info, "", mc.EXIT_OK


def run_installer(game_dir, installer, core, expected_sha):
    """调用安装器应用更新并检查返回码。返回 (ok, code)。"""
    cmd = [installer, game_dir, "--update", "--source", core, "--expect-sha256", expected_sha]
    mc.LOG.step("执行: %s" % " ".join('"%s"' % c if " " in c else c for c in cmd))
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=300, creationflags=flags)
    except FileNotFoundError:
        mc.LOG.error("找不到安装器: %s" % installer)
        return False, -1
    except PermissionError:
        mc.LOG.error("没有权限执行安装器（尝试以管理员身份运行）: %s" % installer)
        return False, -1
    except subprocess.TimeoutExpired:
        mc.LOG.error("安装器超过 300 秒未返回，已中止（可能被安全软件拦截）")
        return False, -1
    except OSError as e:
        mc.LOG.error("启动安装器失败: %s" % e)
        return False, -1

    for line in (r.stdout or "").strip().splitlines():
        mc.LOG.info("  installer> %s" % line)
    for line in (r.stderr or "").strip().splitlines():
        mc.LOG.warn("  installer(err)> %s" % line)
    return r.returncode == 0, r.returncode


def rollback(game_dir, backup):
    """从备份恢复 Managed/Machine.Core.dll。"""
    if not backup or not os.path.isfile(backup):
        mc.LOG.error("没有可用备份，无法自动回滚。请重新运行 install_machine.bat 覆盖安装。")
        return False
    try:
        mc.atomic_replace(backup, mc.core_path(game_dir))
        mc.LOG.warn("已回滚到更新前的 Machine.Core.dll（%s）" % os.path.basename(backup))
        return True
    except OSError as e:
        mc.LOG.error("回滚失败: %s。备份仍在: %s" % (e, backup))
        return False


def main(argv):
    args = parse_args(argv)
    game_dir = resolve_game_dir(args.game_dir)
    mc.setup_console()
    if args.quiet:
        mc.LOG.verbose = False

    # 日志目录：优先游戏目录，其次脚本目录
    log_base = os.path.join(game_dir, mc.LOADER_NAME)
    if not os.path.isdir(log_base):
        log_base = os.path.dirname(os.path.abspath(__file__))
    mc.LOG.open(log_base)
    mc.LOG.info("目标游戏目录: %s" % game_dir)

    if not mc.game_dir_is_valid(game_dir):
        mc.fail("不是有效的 Aviassembly 游戏目录（缺少 %s 或 %s）"
                % (mc.GAME_EXE, os.path.join(mc.MANAGED_REL, mc.ASM_DLL)),
                mc.EXIT_USAGE,
                "如果游戏装在别处，把路径作为第一个参数传入："
                "python machine_update.py \"D:\\你的路径\\Aviassembly\"")

    # --- 更新配置（决定是否强制签名；信任根永远是脚本内置公钥）---
    raw_cfg, err = mc.load_json_file(os.path.join(log_base, "update.json"))
    if err:
        mc.LOG.warn("update.json: %s" % err)
    cfg, warns = mc.validate_update_config(raw_cfg)
    for w in warns:
        mc.LOG.warn("update.json: %s" % w)
    require_signature = cfg["requireSignature"]
    if args.allow_unsigned:
        mc.LOG.warn("!! --allow-unsigned：本次允许未签名更新包（仅限本地自测）")
        require_signature = False
    mc.LOG.info("channel=%s branch=%s requireSignature=%s"
                % (cfg["channel"], cfg["branch"], require_signature))
    if not mc.pubkey_fingerprint():
        mc.LOG.warn("脚本未内置发布公钥，签名校验不可用（发布方需先运行 tools/sign_release.py）")

    updir = mc.update_dir(game_dir)
    ok, info, reason, code = verify_update_package(game_dir, updir, require_signature)
    if not ok:
        # 签名/哈希失败属于安全事件：显式拒绝并留下日志，游戏目录不做任何修改
        if code == mc.EXIT_USAGE:
            mc.fail(reason, code,
                    "本程序只负责应用『已下载的更新』。请先在游戏内点击下载更新，"
                    "退出游戏后再运行本程序。")
        mc.fail(reason, code,
                "更新包未被信任，游戏目录未被修改。"
                "请删除 Machine/update/ 后重新在游戏内下载。")

    manifest = info["manifest"]
    expected_sha = info["sha256"]
    core = info["core"]
    new_ver = manifest.get("version", "")

    # --- 版本比较 ---
    cur_ver = mc.read_record(game_dir).get("coreVersion", "")
    mc.LOG.info("更新包版本=%s 当前记录版本=%s" % (new_ver or "?", cur_ver or "?"))
    if new_ver and cur_ver and mc.compare_versions(new_ver, cur_ver) <= 0 and not args.force:
        mc.LOG.warn("更新包版本不高于当前版本（%s <= %s）" % (new_ver, cur_ver))
        if args.yes:
            mc.fail("已跳过：版本不新（如确实要重装同一版本，加 --force）", mc.EXIT_USAGE)
        if not confirm(args, "仍要应用吗? (y/N): "):
            mc.LOG.info("已取消")
            mc.LOG.close()
            return mc.EXIT_CANCELLED

    # --- 安装器 ---
    installer, tried = mc.find_installer(game_dir)
    if not installer:
        mc.fail("找不到 %s（更新器）" % mc.INSTALLER_EXE, mc.EXIT_USAGE,
                "已尝试以下路径:\n  " + "\n  ".join(tried) +
                "\n请从发布包中把 %s 与 %s 一起复制到 %s\\"
                % (mc.INSTALLER_EXE, mc.CECIL_DLL, mc.LOADER_NAME))
    mc.LOG.info("使用安装器: %s" % installer)

    # 安装器是否被换过：比对安装时记录的哈希
    rec = mc.read_record(game_dir)
    rec_inst = rec.get("installerSha256", "")
    if rec_inst:
        try:
            now_inst = mc.sha256_file(installer)
        except OSError as e:
            mc.fail("无法读取安装器: %s" % e, mc.EXIT_USAGE)
        if now_inst != rec_inst:
            mc.LOG.warn("安装器与安装时记录不一致！")
            mc.LOG.warn("  安装时: %s" % rec_inst)
            mc.LOG.warn("  现在  : %s" % now_inst)
            if not confirm(args, "安装器已被替换。继续将以它的权限执行它。继续? (y/N): "):
                mc.LOG.info("已取消")
                mc.LOG.close()
                return mc.EXIT_CANCELLED

    # --- 前置检查 ---
    if not ensure_game_closed(args, game_dir):
        mc.fail("游戏未退出，未应用更新", mc.EXIT_GAME_RUNNING)

    managed = mc.managed_dir(game_dir)
    if not mc.can_write(managed):
        mc.LOG.error("没有写权限: %s" % managed)
        if not mc.is_admin():
            mc.LOG.error("请右键以【管理员身份运行】，或把游戏装在用户可写目录。")
        mc.fail("权限不足，未应用更新", mc.EXIT_PERMISSION)

    # --- 备份 ---
    cur_core = mc.core_path(game_dir)
    bdir = mc.backup_dir(game_dir)
    backup = None
    if os.path.isfile(cur_core):
        try:
            backup = mc.backup_file(cur_core, bdir, tag=cur_ver or "unknown")
            mc.LOG.info("已备份当前核心 -> %s" % backup)
            mc.prune_backups(bdir, keep=5)
        except OSError as e:
            mc.fail("备份当前 %s 失败: %s（为避免无法恢复，已中止）" % (mc.CORE_DLL, e),
                    mc.EXIT_USAGE)
    else:
        mc.LOG.warn("未发现已安装的 %s，按首次安装处理（无备份可回滚）" % mc.CORE_DLL)

    # --- 应用 ---
    mc.LOG.step("正在应用 Machine 更新 ...")
    ok, code = run_installer(game_dir, installer, core, expected_sha)
    if not ok:
        mc.LOG.error("安装器执行失败（exit=%s）" % code)
        if code == 3:
            mc.LOG.error("安装器报告哈希校验失败——文件在调用前被替换，已中止。")
        rollback(game_dir, backup)
        mc.LOG.close()
        mc.pause()
        return mc.EXIT_ROLLBACK if backup else mc.EXIT_INSTALLER

    # --- 复核 ---
    mc.LOG.step("复核安装结果 ...")
    try:
        final_sha = mc.sha256_file(cur_core)
    except OSError as e:
        final_sha = ""
        mc.LOG.error("无法读取安装后的 %s: %s" % (mc.CORE_DLL, e))
    if final_sha != expected_sha:
        mc.LOG.error("安装结果哈希与更新包不一致（期望 %s，实际 %s）"
                     % (expected_sha, final_sha or "读取失败"))
        rollback(game_dir, backup)
        mc.LOG.close()
        mc.pause()
        return mc.EXIT_ROLLBACK
    mc.LOG.info("安装结果校验通过: %s" % final_sha)

    # --- 记录 + 清理 ---
    mc.write_record(game_dir, {
        "coreVersion": new_ver or cur_ver,
        "coreSha256": final_sha,
        "installerSha256": mc.sha256_file(installer),
        "channel": manifest.get("channel", ""),
        "source": manifest.get("url", ""),
        "appliedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
    })

    if not args.keep_update:
        for name in (mc.CORE_DLL, mc.CORE_DLL + ".sig", "apply.json"):
            p = os.path.join(updir, name)
            try:
                if os.path.isfile(p):
                    os.remove(p)
            except OSError as e:
                mc.LOG.warn("清理 %s 失败: %s" % (p, e))
        mc.LOG.info("已清理 Machine/update/ 中的更新包")
    else:
        mc.LOG.info("已保留 Machine/update/ 内容 (--keep-update)")

    mc.LOG.step("更新完成：Machine.Core.dll v%s 已生效。现在可以重新启动游戏。" % (new_ver or "?"))
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
    except Exception as e:  # 任何未预期异常也不许把裸栈甩给用户
        import traceback
        mc.LOG.error("未预期的错误: %s" % e)
        mc.LOG.error(traceback.format_exc())
        mc.LOG.close()
        print("发生未预期的错误，详情见 Machine/logs/machine_update.log")
        print("错误摘要: %s" % e)
        mc.pause()
        sys.exit(9)
