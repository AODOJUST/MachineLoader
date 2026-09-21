# -*- coding: utf-8 -*-
"""
打包 Machine 发布目录（dist）：

  dist/
    MachineInstaller.exe          # 注入/卸载/更新执行器（含备份+原子替换+回滚）
    Machine.Core.dll              # 加载器核心
    Machine.Core.dll.sig          # 核心的 RSA 签名（base64）
    version.json                  # 发布清单：version/channel/sha256/sig/notes
    machine_common.py             # 公共模块：日志/哈希/验签/备份/配置校验
    install_machine.py/.bat       # 傻瓜式安装
    uninstall_machine.py/.bat     # 一键卸载（保留玩家数据与 mods）
    machine_update.py/.bat        # 更新应用（哈希+签名校验后才会覆盖 DLL）
    release_pubkey.pem            # 发布公钥（供用户核对）
    SECURITY.md                   # 威胁模型与发布流程
    Machine/                      # 配置模板（net.json / update.json / README.txt）
    mods/                         # 全部 Machine Mod（含默认配置）

顺序（严格按此执行）：
  build-core.ps1  ->  bin/Machine.Core.dll
  build-installer.ps1 -> bin/MachineInstaller.exe
  pack_dist.py    ->  把 bin/ 的二进制 + 配置模板 + mods 复制进 dist/
  tools/sign_release.py -> 对 dist/Machine.Core.dll 签名并写 dist/version.json

（签名必须最后做：pack_dist 会覆盖 Machine.Core.dll，旧签名随即失效。
  顺序颠倒会导致发布包里是未签名的裸 DLL，客户端 requireSignature=true 直接拒收。）

⚠ 2026-09-15 加固：
  1) mods 不再用 copytree 整体镜像，改为**白名单复制**（复用 tools/sync_mods_from_dev.wanted），
     避免调试截图 / _prev / .github_backup / 根级重复 DLL 被带进发布件。
  2) dist/version.json 已存在时，**默认拒绝覆盖 Machine.Core.dll**（那会让已签名发布件失效）。
     确实要重建签名链时显式加 --force。
"""
import argparse
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(ROOT, "dist")
DEV = os.path.join(os.path.dirname(ROOT), "Aviassembly_DEV")
BIN = os.path.join(ROOT, "bin")

sys.path.insert(0, os.path.join(ROOT, "tools"))
from sync_mods_from_dev import wanted  # noqa: E402  同一套发布白名单

DEFAULT_REPO = "AODOJUST/MachineLoader"
DEFAULT_BRANCH = "main"
DEFAULT_PORT = 26460

# 这些文件由脚本自身维护，清理 dist 时必须保留
KEEP = {
    "install_machine.py", "uninstall_machine.py", "machine_update.py", "machine_common.py",
    "install_machine.bat", "uninstall_machine.bat", "machine_update.bat",
    # Machine.Core.dll 必须保留：dist/version.json 已存在时（已签名发布件）
    # 下面会【跳过】从 bin/ 覆盖它，若这里不保留就会被 ensure_clean_dist 删掉，
    # 结果是发布包里没有 core —— 2026-09-15 加守卫时踩到。
    "Machine.Core.dll",
    "Machine.Core.dll.sig", "version.json", "release_pubkey.pem", "SECURITY.md",
}

README_TXT = (
    "Machine Mod Loader\n"
    "\n"
    "Mods    : <game>\\mods\\<Mod>\\mod.json\n"
    "Logs    : <game>\\Machine\\logs\\Machine.log\n"
    "          <game>\\Machine\\logs\\machine_update.log   (installer / updater)\n"
    "Config  : <game>\\Machine\\net.json      (online / LAN server)\n"
    "          <game>\\Machine\\update.json   (update repo + channel)\n"
    "Backup  : <game>\\Machine\\backup\\       (previous Machine.Core.dll)\n"
    "\n"
    "Update  : after downloading in game, exit the game and run\n"
    "            Machine\\machine_update.bat\n"
    "          (applies a hash + RSA-signature verified Machine.Core.dll)\n"
    "Remove  : run MachineInstaller.exe \"<game>\" --uninstall\n"
    "          or use uninstall_machine.bat from the release package\n"
    "\n"
    "<game> is your Aviassembly install directory. Paths above are relative;\n"
    "this file is generated at install time and never contains machine-specific\n"
    "absolute paths.\n"
)


def ensure_clean_dist():
    keep = KEEP
    for item in os.listdir(DIST):
        if item in keep:
            continue
        p = os.path.join(DIST, item)
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
        else:
            try:
                os.remove(p)
            except OSError:
                pass


def write_text(path, text):
    """UTF-8 无 BOM；行尾统一 \n，避免依赖平台的解析器把 BOM 当内容。"""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def copy_mods_whitelisted(src_mods, dst_mods):
    """白名单式复制 mods（与 tools/sync_mods_from_dev.wanted 同一套规则）。

    不用 copytree 整体镜像：源目录里可能残留调试截图、_prev/、.github_backup、
    根级重复 DLL，整体镜像会把它们重新打进发布件。
    """
    copied = skipped = 0
    skipped_bytes = 0
    for dirpath, _dirs, names in os.walk(src_mods):
        for n in names:
            full = os.path.join(dirpath, n)
            rel = os.path.relpath(full, src_mods).replace("\\", "/")
            if not wanted(rel):
                skipped += 1
                try:
                    skipped_bytes += os.path.getsize(full)
                except OSError:
                    pass
                continue
            dst = os.path.join(dst_mods, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(full, dst)
            copied += 1
    return copied, skipped, skipped_bytes


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--force", action="store_true",
                    help="允许覆盖 dist/Machine.Core.dll（会让已签名发布件失效，需重签）")
    args = ap.parse_args(argv)

    os.makedirs(DIST, exist_ok=True)
    version_json = os.path.join(DIST, "version.json")
    signed_release = os.path.isfile(version_json)
    if signed_release and not args.force:
        print("!! dist/version.json 已存在 —— 这是一份已签名发布件。")
        print("   本次将【跳过】Machine.Core.dll 覆盖，以免签名失效。")
        print("   要重建签名链，请加 --force，并记得随后跑 tools/sign_release.py。")
    ensure_clean_dist()

    # 1) 二进制
    for f in ["MachineInstaller.exe", "Machine.Core.dll", "Mono.Cecil.dll"]:
        src = os.path.join(BIN, f)
        if not os.path.exists(src):
            print("WARN: missing", src)
            continue
        if f == "Machine.Core.dll" and signed_release and not args.force:
            print("skip  Machine.Core.dll（保护已签名发布件；dist 内保留原文件）")
            continue
        shutil.copy2(src, os.path.join(DIST, f))
        print("copied", f)

    # 2) Machine 配置模板（严格 schema，配合机器码校验的 ConfigGuard）
    tpl = os.path.join(DIST, "Machine")
    os.makedirs(tpl, exist_ok=True)

    write_text(os.path.join(tpl, "net.json"),
               '{\n'
               '  "server": "",\n'
               '  "port": %d,\n'
               '  "playerName": "Pilot"\n'
               '}\n' % DEFAULT_PORT)

    write_text(os.path.join(tpl, "update.json"),
               '{\n'
               '  "repo": "%s",\n'
               '  "branch": "%s",\n'
               '  "channel": "stable",\n'
               '  "enabled": true,\n'
               '  "requireSignature": true\n'
               '}\n' % (DEFAULT_REPO, DEFAULT_BRANCH))

    write_text(os.path.join(tpl, "README.txt"), README_TXT)
    print("Machine/ template ok (update.json repo=%s, requireSignature=true)" % DEFAULT_REPO)

    # 3) mods（从 dev 游戏复制 mod 集 —— 白名单式，不带调试产物）
    src_mods = os.path.join(DEV, "mods")
    dst_mods = os.path.join(DIST, "mods")
    if os.path.isdir(src_mods):
        copied, skipped, skipped_bytes = copy_mods_whitelisted(src_mods, dst_mods)
        print("mods copied: %d 个文件，%d 个 mod" % (copied, len(os.listdir(dst_mods))))
        print("mods skipped: %d 个非发布文件（%.1f MB）—— 调试截图 / 备份 / 根级重复 DLL"
              % (skipped, skipped_bytes / 1048576.0))
    else:
        print("WARN: dev mods not found at", src_mods)

    # 4) 附带公钥，方便用户核对签名来源（私钥绝不入包）
    pub = os.path.join(ROOT, "keys", "release_public.pem")
    if os.path.isfile(pub):
        shutil.copy2(pub, os.path.join(DIST, "release_pubkey.pem"))
        print("copied release_pubkey.pem")
    else:
        print("WARN: keys/release_public.pem missing (run the openssl keygen step)")

    print("=== dist pack done ===")
    for item in sorted(os.listdir(DIST)):
        p = os.path.join(DIST, item)
        print("  %s%s" % (item, "/" if os.path.isdir(p) else ""))
    print()
    print("NEXT: run build-core.ps1 + build-installer.ps1, then tools/sign_release.py")


if __name__ == "__main__":
    main()
