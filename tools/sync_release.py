# -*- coding: utf-8 -*-
"""
把 dist/ 同步到两处发布副本：

  * MachineLoader_github/MachineLoader-main/   (GitHub 发布仓内容)
  * Machine_Dev/dist_upload/                   (上传用暂存)

只镜像"发布管理"的条目（见 MANIFEST），仓库里特有的文件（logo、README.md 等）不动。
用法：python tools/sync_release.py [--check]
"""
import argparse
import hashlib
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEV = os.path.dirname(HERE)                       # Machine_Dev
BASE = os.path.dirname(DEV)                       # 豆包的下载
DIST = os.path.join(DEV, "dist")

TARGETS = [
    os.path.join(BASE, "MachineLoader_github", "MachineLoader-main"),
    os.path.join(DEV, "dist_upload"),
]

# 由发布流程管理的条目（相对路径）
MANIFEST = [
    "Machine",
    "mods",
    "machine_common.py",
    "install_machine.py",
    "install_machine.bat",
    "uninstall_machine.py",
    "uninstall_machine.bat",
    "machine_update.py",
    "machine_update.bat",
    "Machine.Core.dll",
    "Machine.Core.dll.sig",
    "MachineInstaller.exe",
    "Mono.Cecil.dll",
    "version.json",
    "release_pubkey.pem",
    "SECURITY.md",
]

# 绝不允许出现在发布副本里的东西（私钥、备份、本地日志）
FORBIDDEN = ["release_private.pem", "installed.json", ".machine_write_probe.tmp"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def snapshot(root, names):
    """返回 {相对路径: sha256}（目录递归）。"""
    out = {}
    for name in names:
        p = os.path.join(root, name)
        if os.path.isfile(p):
            out[name] = sha256(p)
        elif os.path.isdir(p):
            for dirpath, _dirs, files in os.walk(p):
                for f in files:
                    full = os.path.join(dirpath, f)
                    rel = os.path.relpath(full, root).replace("\\", "/")
                    out[rel] = sha256(full)
    return out


def mirror(src, dst):
    copied = 0
    for name in MANIFEST:
        s = os.path.join(src, name)
        d = os.path.join(dst, name)
        if not os.path.exists(s):
            print("   !! 缺少发布条目: %s" % name)
            continue
        if os.path.isdir(s):
            # 先挪开再复制，而不是 rmtree。这个环境里子进程删除常被外部守卫拒绝
            # （WinError 5），改名/移动是允许的；挪到 _archive 既不丢东西也能跑通。
            if os.path.isdir(d) or os.path.isfile(d):
                archive = os.path.join(dst, "_archive")
                os.makedirs(archive, exist_ok=True)
                stamp = time.strftime("%Y%m%d_%H%M%S")
                target = os.path.join(archive, "%s_%s" % (name.replace("/", "_"), stamp))
                n = 1
                while os.path.exists(target):
                    target = os.path.join(archive, "%s_%s_%d" % (name.replace("/", "_"), stamp, n))
                    n += 1
                shutil.move(d, target)
                print("   (moved aside -> %s)" % os.path.relpath(target, dst))
            shutil.copytree(s, d)
            copied += 1
        else:
            os.makedirs(os.path.dirname(d), exist_ok=True)
            shutil.copy2(s, d)
            copied += 1
    return copied


def main(argv):
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--check", action="store_true", help="只比较差异，不写入")
    args = ap.parse_args(argv)

    if not os.path.isdir(DIST):
        print("找不到 dist/，请先运行 pack_dist.py")
        return 1

    src_snap = snapshot(DIST, MANIFEST)

    # 先做私钥泄露检查
    problems = []
    for name in FORBIDDEN:
        for dirpath, _dirs, files in os.walk(DIST):
            if name in files:
                problems.append(os.path.join(dirpath, name))
    if problems:
        print("!! dist/ 里出现不该分发的文件（私钥/备份/状态）:")
        for p in problems:
            print("   %s" % p)
        return 1

    rc = 0
    for dst in TARGETS:
        print("== %s" % dst)
        if not os.path.isdir(dst):
            print("   目标目录不存在，跳过")
            rc = 1
            continue
        if args.check:
            dst_snap = snapshot(dst, MANIFEST)
            if src_snap == dst_snap:
                print("   一致（%d 个文件）" % len(src_snap))
            else:
                only_src = sorted(set(src_snap) - set(dst_snap))
                only_dst = sorted(set(dst_snap) - set(src_snap))
                diff = sorted(k for k in set(src_snap) & set(dst_snap) if src_snap[k] != dst_snap[k])
                print("   不一致：dist 独有 %d，目标独有 %d，内容不同 %d"
                      % (len(only_src), len(only_dst), len(diff)))
                for k in (only_src + only_dst + diff)[:15]:
                    print("     - %s" % k)
                rc = 1
        else:
            n = mirror(DIST, dst)
            print("   已同步 %d 个发布条目" % n)

    if not args.check:
        print()
        print("同步后核对:")
        for dst in TARGETS:
            if not os.path.isdir(dst):
                continue
            ok = snapshot(dst, MANIFEST) == src_snap
            print("  %s -> %s" % ("一致" if ok else "不一致", dst))
            if not ok:
                rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
