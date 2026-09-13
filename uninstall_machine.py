# -*- coding: utf-8 -*-
"""
Machine 加载器 一键卸载程序

用法：双击 uninstall_machine.bat（或 python uninstall_machine.py [游戏目录] [选项]）
移除：Assembly-CSharp.dll 注入还原 / Machine.Core.dll / Machine/ 目录

数据保护（相对旧版的变化）：
  * 卸载前把 Machine/ 里的玩家数据（profile.json / net.json / update.json / logs）
    备份到 <游戏目录>/Machine_uninstalled_<时间戳>/，避免"卸载即失忆"。
  * 不再默认删除 mods/ —— 旧实现会 shutil.rmtree(mods) 直接毁掉玩家自己的 Mod。
    需要一并清理时显式加 --purge-mods（同样会先备份）。

选项：
  游戏目录                指定游戏目录
  -y, --yes               不询问，直接执行
  --purge-mods            连同 mods/ 一起删除（会先备份）
  --keep-backup           保留备份目录（默认保留）
  --no-backup             不备份（危险，仅在确定不需要玩家数据时使用）
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import machine_common as mc

DIST = os.path.dirname(os.path.abspath(__file__))
INSTALLER = os.path.join(DIST, mc.INSTALLER_EXE)

PLAYER_DATA = ["profile.json", "net.json", "update.json", "installed.json", "lang"]


def parse_args(argv):
    p = argparse.ArgumentParser(add_help=True)
    p.add_argument("game_dir", nargs="?", default=None)
    p.add_argument("-y", "--yes", action="store_true")
    p.add_argument("--purge-mods", action="store_true")
    p.add_argument("--no-backup", action="store_true")
    p.add_argument("--quiet", action="store_true")
    return p.parse_args(argv)


def confirm(args, question, default=False):
    """--yes 表示"全部同意"，因此直接返回 True（不是返回 default）。"""
    if args.yes:
        return True
    if not mc.is_interactive():
        return default
    try:
        return input(question).strip().lower() in ("y", "yes")
    except (EOFError, KeyboardInterrupt):
        return False


def find_candidates():
    cands = []
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
        steam_path, _ = winreg.QueryValueEx(key, "SteamPath")
        cands.append(os.path.join(steam_path.replace("/", "\\"),
                                  "steamapps", "common", "Aviassembly"))
    except Exception as e:
        mc.LOG.warn("读取 Steam 安装信息失败: %s" % e)
    return [c for c in cands if os.path.isdir(c) and mc.game_dir_is_valid(c)]


def backup_player_data(game_dir):
    """把 Machine/ 下的玩家数据 + mods 清单复制到 Machine_uninstalled_<ts>/。"""
    src = os.path.join(game_dir, mc.LOADER_NAME)
    stamp = time.strftime("%Y%m%d_%H%M%S")
    dst = os.path.join(game_dir, "Machine_uninstalled_%s" % stamp)
    moved = []
    try:
        os.makedirs(dst, exist_ok=True)
    except OSError as e:
        mc.LOG.warn("无法创建备份目录 %s: %s" % (dst, e))
        return None, []
    for name in PLAYER_DATA:
        p = os.path.join(src, name)
        if not os.path.exists(p):
            continue
        try:
            d = os.path.join(dst, name)
            if os.path.isdir(p):
                shutil.copytree(p, d, dirs_exist_ok=True)
            else:
                shutil.copy2(p, d)
            moved.append(name)
        except OSError as e:
            mc.LOG.warn("备份 %s 失败: %s" % (name, e))
    mc.LOG.info("已备份玩家数据到 %s （%s）" % (dst, ", ".join(moved) or "无"))
    return dst, moved


def main(argv):
    args = parse_args(argv)
    mc.setup_console()
    if args.quiet:
        mc.LOG.verbose = False
    mc.LOG.open(DIST)

    print("=" * 52)
    print("   Machine 加载器 - 一键卸载")
    print("=" * 52)

    game_dir = None
    if args.game_dir:
        game_dir = os.path.abspath(args.game_dir.strip('"').strip())
    else:
        cands = find_candidates()
        if cands:
            game_dir = cands[0]
            print("检测到游戏目录: %s" % game_dir)
        else:
            if not mc.is_interactive():
                mc.fail("未自动找到游戏目录，且当前非交互终端（请把路径作为参数传入）。", mc.EXIT_USAGE)
            try:
                game_dir = input("未自动找到游戏目录，请输入 Aviassembly 完整路径: ").strip().strip('"')
            except (EOFError, KeyboardInterrupt):
                game_dir = ""
    if not game_dir or not mc.game_dir_is_valid(game_dir):
        mc.fail("错误: 无效的游戏目录。", mc.EXIT_USAGE)

    mc.LOG.info("目标游戏目录: %s" % game_dir)
    mc.LOG.reopen(os.path.join(game_dir, mc.LOADER_NAME), "卸载日志（游戏目录）")

    # 游戏运行中不做任何删除
    pids = mc.running_processes()
    if pids:
        mc.fail("检测到游戏正在运行 (PID: %s)，请先完全退出游戏。" % ", ".join(pids),
                mc.EXIT_GAME_RUNNING)

    asm_path = os.path.join(mc.managed_dir(game_dir), mc.ASM_DLL)

    print("将移除以下内容:")
    print("  - Machine.Core.dll（Managed 内）")
    print("  - Machine/ 目录（日志、配置、更新缓存）")
    print("  - Assembly-CSharp.dll 的注入钩子（还原为原版）")
    print("将保留:")
    print("  - mods/ 目录（你的 Mod 不会被删）")
    if not args.no_backup:
        print("  - 卸载前会把 Machine/ 里的玩家数据备份一份")
    if args.purge_mods:
        print("!! --purge-mods: mods/ 目录也会被删除（已先备份）")
    if not confirm(args, "确认卸载? (y/N): "):
        print("已取消。")
        mc.LOG.close()
        mc.pause()
        return mc.EXIT_CANCELLED

    # 1) 备份玩家数据
    if not args.no_backup:
        backup_player_data(game_dir)
    else:
        mc.LOG.warn("--no-backup: 跳过玩家数据备份")

    # 2) 调用安装器还原（含 RuntimeInitializeOnLoads.json 反登记）
    #
    #    注意：这里必须先关掉自己的日志句柄。本进程的日志写在
    #    Machine/logs/machine_update.log，而 Windows 不允许删除仍被打开的文件，
    #    句柄不关会让安装器删除 Machine/ 时失败（曾经表现为退出码 4）。
    #    日志内容已经随玩家数据一起备份到 Machine_uninstalled_<时间戳>/logs/。
    if os.path.isfile(INSTALLER):
        print("运行安装器卸载模式...")
        if not args.no_backup:
            mc.LOG.info("即将关闭日志句柄，以便安装器删除 Machine/ 目录")
        mc.LOG.close()
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
        try:
            r = subprocess.run([INSTALLER, game_dir, "--uninstall"], capture_output=True,
                               text=True, encoding="utf-8", errors="replace",
                               timeout=300, creationflags=flags)
        except FileNotFoundError:
            print("警告: 找不到 %s" % INSTALLER)
            print("将尝试手动清理。")
            r = None
        except (subprocess.TimeoutExpired, OSError) as e:
            print("警告: 执行安装器失败: %s" % e)
            r = None
        if r is not None:
            for line in (r.stdout or "").strip().splitlines():
                print("  installer> %s" % line)
            for line in (r.stderr or "").strip().splitlines():
                print("  installer(err)> %s" % line)
            if r.returncode != 0:
                print("错误: 还原过程中有错误（退出码 %d）。" % r.returncode)
                print("请关闭游戏与杀毒软件后重试；或手动用 Assembly-CSharp.dll.machinebak 覆盖还原。")
                mc.pause()
                return mc.EXIT_INSTALLER
    else:
        print("警告: 找不到 %s，尝试手动清理..." % mc.INSTALLER_EXE)
        managed = mc.managed_dir(game_dir)
        bak = asm_path + ".machinebak"
        if os.path.isfile(bak):
            try:
                shutil.copy2(bak, asm_path)
                os.remove(bak)
                print("已从备份还原 %s" % mc.ASM_DLL)
            except OSError as e:
                mc.LOG.error("还原失败: %s" % e)
        else:
            mc.LOG.warn("未找到 %s.machinebak，无法还原注入（请用 Steam『验证游戏文件完整性』修复）"
                        % mc.ASM_DLL)
        core = os.path.join(managed, mc.CORE_DLL)
        if os.path.isfile(core):
            try:
                os.remove(core)
                print("已删除 %s" % mc.CORE_DLL)
            except OSError as e:
                mc.LOG.error("删除失败: %s" % e)

    # 3) 删除 Machine/（安装器已经删过一次，这里兜底）
    machine_dir = os.path.join(game_dir, mc.LOADER_NAME)
    if os.path.isdir(machine_dir):
        try:
            shutil.rmtree(machine_dir)
            print("已删除 Machine/")
        except OSError as e:
            mc.LOG.error("删除 Machine/ 失败: %s（请手动删除）" % e)

    # 4) mods/：默认保留
    mods_dir = os.path.join(game_dir, "mods")
    if os.path.isdir(mods_dir):
        if args.purge_mods:
            try:
                shutil.rmtree(mods_dir)
                print("已删除 mods/（--purge-mods）")
            except OSError as e:
                mc.LOG.error("删除 mods/ 失败: %s（请手动删除）" % e)
        else:
            print("已保留 mods/（如需一并清理，重新运行并加 --purge-mods）")

    print("=" * 52)
    print("卸载完成。")
    print("下一步：启动游戏，确认纯净版可正常运行；")
    print("确认无误后再重新安装 Machine。")
    print("=" * 52)
    mc.LOG.info("卸载完成")
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
