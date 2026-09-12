# -*- coding: utf-8 -*-
"""
Machine 加载器 傻瓜式安装程序
用法：双击 install_machine.bat（或 python install_machine.py）
流程：扫盘定位 Aviassembly → 用户确认/修改路径 → 校验 → 注入+复制 → 完成提示
安全须知：游戏无法启动或出问题时，用游戏目录中的 uninstall_machine.bat 移除 Machine，
恢复纯净版后再重新安装。
"""
import os
import sys
import subprocess
import re

DIST = os.path.dirname(os.path.abspath(__file__))
INSTALLER = os.path.join(DIST, "MachineInstaller.exe")
CORE = os.path.join(DIST, "Machine.Core.dll")


def say(s):
    print(s)


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
            txt = open(vdf, encoding="utf-8", errors="ignore").read()
            for m in re.finditer(r'"path"\s+"([^"]+)"', txt):
                p = m.group(1).replace("\\\\", "\\").replace("/", "\\")
                cands.append(os.path.join(p, "steamapps", "common", "Aviassembly"))
    except Exception:
        pass
    return [c for c in cands if os.path.isdir(c)]


def scan_drives():
    """全盘搜索：找含 Aviassembly_Data\\Managed\\Assembly-CSharp.dll 的目录（限深度）。"""
    found = []
    letters = [chr(x) + ":\\" for x in range(ord("C"), ord("Z") + 1)]
    for drive in letters:
        if not os.path.exists(drive):
            continue
        try:
            for root, dirs, files in os.walk(drive):
                # 限制深度：进入过深会非常慢
                depth = root[len(drive):].count(os.sep)
                if depth > 5:
                    dirs[:] = []
                    continue
                name = os.path.basename(root)
                if name.lower().startswith("aviassembly"):
                    if os.path.exists(os.path.join(root, "Aviassembly_Data", "Managed", "Assembly-CSharp.dll")):
                        found.append(root)
                        dirs[:] = []
                        continue
                if depth >= 4:
                    dirs[:] = []
        except Exception:
            continue
    return found


def is_valid_game(dirpath):
    return (os.path.exists(os.path.join(dirpath, "Aviassembly_Data", "Managed", "Assembly-CSharp.dll"))
            and os.path.exists(os.path.join(dirpath, "Aviassembly.exe")))


def pick_target():
    cands = [c for c in find_steam_candidates() if is_valid_game(c)]
    # 去重（大小写不敏感）
    seen = set()
    uniq = []
    for c in cands:
        k = c.casefold()
        if k in seen:
            continue
        seen.add(k)
        uniq.append(c)
    cands = uniq
    if not cands:
        say("未从 Steam 注册表找到，开始全盘扫描（可能需要几十秒）...")
        cands = [c for c in scan_drives() if is_valid_game(c)]
    if not cands:
        say("未找到 Aviassembly 游戏目录。")
        return None
    say("找到候选游戏目录:")
    for i, c in enumerate(cands):
        say("  [%d] %s" % (i + 1, c))
    sel = input("直接回车使用第一个，或输入编号 / 手动路径: ").strip()
    if sel == "":
        return cands[0]
    if sel.isdigit() and 1 <= int(sel) <= len(cands):
        return cands[int(sel) - 1]
    if os.path.isdir(sel):
        return sel
    say("无法识别的输入，使用第一个候选。")
    return cands[0]


def copy_tree(src, dst, overwrite=False, skip_dirs=None):
    """复制目录树；skip_dirs 里的子目录名跳过（保护玩家数据）。"""
    skip_dirs = skip_dirs or []
    if not os.path.exists(src):
        return
    os.makedirs(dst, exist_ok=True)
    for item in os.listdir(src):
        s = os.path.join(src, item)
        d = os.path.join(dst, item)
        if os.path.isdir(s):
            if item in skip_dirs:
                continue
            copy_tree(s, d, overwrite, skip_dirs)
        else:
            if os.path.exists(d) and not overwrite:
                continue
            try:
                os.makedirs(os.path.dirname(d), exist_ok=True)
                if os.path.exists(d):
                    os.remove(d)
                import shutil
                shutil.copy2(s, d)
            except Exception as e:
                say("  跳过 %s (%s)" % (item, e))


def run_installer(game_dir, args=None):
    cmd = [INSTALLER, game_dir] + (args or [])
    say("运行: %s" % " ".join(cmd))
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="ignore")
    if r.stdout:
        say(r.stdout.strip())
    if r.stderr:
        say(r.stderr.strip())
    return r.returncode == 0


def main():
    say("=" * 52)
    say("   Machine 加载器 - 安装程序")
    say("=" * 52)
    game_dir = pick_target()
    if not game_dir:
        say("安装中止。")
        input("按回车退出...")
        return 1
    say("目标游戏目录: %s" % game_dir)
    if not is_valid_game(game_dir):
        say("错误: 该目录不是有效的 Aviassembly 游戏目录（缺少 Aviassembly.exe 或 Managed\\Assembly-CSharp.dll）")
        input("按回车退出...")
        return 1

    # 检测是否已安装
    installed = os.path.exists(os.path.join(game_dir, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    if installed:
        ans = input("检测到 Machine 已安装。覆盖安装（保留存档与配置）? (y/N): ").strip().lower()
        if ans not in ("y", "yes"):
            say("已取消。")
            input("按回车退出...")
            return 0

    # 1) 注入启动钩子 + 复制 Machine.Core.dll（MachineInstaller.exe）
    if not os.path.exists(INSTALLER):
        say("错误: 找不到 MachineInstaller.exe（应与本程序同目录）")
        input("按回车退出...")
        return 1
    if not run_installer(game_dir):
        say("错误: 安装器执行失败（游戏可能正在运行，请先关闭游戏再安装）")
        input("按回车退出...")
        return 1

    # 2) 复制 Machine/ 模板（不覆盖 profile.json / logs / update）
    machine_tpl = os.path.join(DIST, "Machine")
    if os.path.exists(machine_tpl):
        copy_tree(machine_tpl, os.path.join(game_dir, "Machine"), overwrite=False,
                  skip_dirs=["logs", "update"])

    # 3) 复制 mods/（全新 mod；同名 mod 保留玩家版本）
    mods_tpl = os.path.join(DIST, "mods")
    if os.path.exists(mods_tpl):
        copy_tree(mods_tpl, os.path.join(game_dir, "mods"), overwrite=False)

    # 4) 把更新器三件套复制到 Machine/（自包含，玩家无需额外文件）
    for fname in ["machine_update.bat", "machine_update.py", "MachineInstaller.exe", "Mono.Cecil.dll"]:
        src = os.path.join(DIST, fname)
        if os.path.exists(src):
            try:
                import shutil
                shutil.copy2(src, os.path.join(game_dir, "Machine", fname))
            except Exception as e:
                say("更新器 %s 复制失败: %s" % (fname, e))

    say("=" * 52)
    say("安装完成！现在可以启动游戏。")
    say("提示:")
    say("  1. 如果游戏无法启动或出现异常，请到游戏目录运行:")
    say("        Machine\\machine_update.bat   (应用更新)")
    say("        或 一键卸载程序 uninstall_machine.bat   (恢复纯净版)")
    say("  2. 确认纯净版游戏正常后，再重新安装 Machine。")
    say("=" * 52)
    input("按回车退出...")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n已取消。")
    except EOFError:
        pass
