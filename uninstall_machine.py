# -*- coding: utf-8 -*-
"""
Machine 加载器 一键卸载程序
用法：双击 uninstall_machine.bat（或 python uninstall_machine.py [游戏目录]）
移除：Assembly-CSharp.dll 注入还原 / Machine.Core.dll / Machine/ 目录 / mods/ 目录
之后请启动游戏确认纯净版正常运行；确认无误后再重新安装 Machine。
"""
import os
import sys
import subprocess
import shutil

DIST = os.path.dirname(os.path.abspath(__file__))
INSTALLER = os.path.join(DIST, "MachineInstaller.exe")


def find_candidates():
    cands = []
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
        steam_path, _ = winreg.QueryValueEx(key, "SteamPath")
        cands.append(os.path.join(steam_path.replace("/", "\\"), "steamapps", "common", "Aviassembly"))
    except Exception:
        pass
    return [c for c in cands if os.path.isdir(c)]


def is_valid_game(p):
    return os.path.exists(os.path.join(p, "Aviassembly_Data", "Managed", "Assembly-CSharp.dll"))


def main():
    print("=" * 52)
    print("   Machine 加载器 - 一键卸载")
    print("=" * 52)
    game_dir = None
    if len(sys.argv) > 1 and os.path.isdir(sys.argv[1]):
        game_dir = sys.argv[1]
    else:
        cands = [c for c in find_candidates() if is_valid_game(c)]
        if cands:
            game_dir = cands[0]
            print("检测到游戏目录: %s" % game_dir)
        else:
            game_dir = input("未自动找到游戏目录，请输入 Aviassembly 完整路径: ").strip().strip('"')
    if not game_dir or not is_valid_game(game_dir):
        print("错误: 无效的游戏目录。")
        input("按回车退出...")
        return 1

    print("将移除以下内容:")
    print("  - Machine.Core.dll（Managed 内）")
    print("  - Machine/ 目录（日志、配置、更新缓存）")
    print("  - mods/ 目录（所有 Machine Mod）")
    print("  - Assembly-CSharp.dll 的注入钩子（还原为原版）")
    ans = input("确认卸载? (y/N): ").strip().lower()
    if ans not in ("y", "yes"):
        print("已取消。")
        input("按回车退出...")
        return 0

    # 1) 还原 Assembly-CSharp.dll + 删 Machine.Core.dll + Machine/ + RuntimeInit 登记
    if os.path.exists(INSTALLER):
        print("运行安装器卸载模式...")
        r = subprocess.run([INSTALLER, game_dir, "--uninstall"], capture_output=True,
                           text=True, encoding="utf-8", errors="ignore")
        if r.stdout:
            print(r.stdout.strip())
        if r.stderr:
            print(r.stderr.strip())
        if r.returncode != 0:
            print("警告: 还原过程中有错误（文件可能被占用），请关闭游戏后重试。")
            input("按回车退出...")
            return 1
    else:
        # 兜底：手动清理
        print("警告: 找不到 MachineInstaller.exe，尝试手动清理...")
        managed = os.path.join(game_dir, "Aviassembly_Data", "Managed")
        asm = os.path.join(managed, "Assembly-CSharp.dll")
        bak = asm + ".machinebak"
        if os.path.exists(bak):
            try:
                shutil.copy2(bak, asm)
                os.remove(bak)
                print("已从备份还原 Assembly-CSharp.dll")
            except Exception as e:
                print("还原失败: %s" % e)
        core = os.path.join(managed, "Machine.Core.dll")
        if os.path.exists(core):
            try:
                os.remove(core)
                print("已删除 Machine.Core.dll")
            except Exception as e:
                print("删除失败: %s" % e)

    # 2) 删除 Machine/ 与 mods/
    for name in ["Machine", "mods"]:
        p = os.path.join(game_dir, name)
        if os.path.exists(p):
            try:
                shutil.rmtree(p)
                print("已删除 %s/" % name)
            except Exception as e:
                print("删除 %s/ 失败: %s（请手动删除）" % (name, e))

    print("=" * 52)
    print("卸载完成。")
    print("下一步：启动游戏，确认纯净版可正常运行；")
    print("确认无误后再重新安装 Machine。")
    print("=" * 52)
    input("按回车退出...")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n已取消。")
    except EOFError:
        pass
