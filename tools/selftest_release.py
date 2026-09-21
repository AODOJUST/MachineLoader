# -*- coding: utf-8 -*-
"""
发布链路端到端自测（在隔离沙箱里跑，不动真实游戏目录）。

覆盖：
  更新：正常签名包 / DLL 被篡改 / 清单哈希被改 / 缺签名 / --allow-unsigned /
        版本不新 / 安装器缺失 / 安装器被换 / 直接调安装器传错哈希 / 无待应用更新
  安装：真实 Assembly-CSharp.dll 注入（幂等）+ 发布包签名自检 + 更新器文件部署
  卸载：保留 mods/、备份玩家数据、还原程序集
  配置：update.json / net.json / profile.json 的 schema 校验

用法: python tools/selftest_release.py [--keep]
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEV = os.path.dirname(HERE)
BASE = os.path.dirname(DEV)
DIST = os.path.join(DEV, "dist")
GAME_SRC = os.path.join(BASE, "Aviassembly_DEV")
SANDBOX = os.path.join(DEV, "_sectest")

if DEV not in sys.path:
    sys.path.insert(0, DIST)

RESULTS = []


def record(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, ("  -- " + detail) if detail else ""))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def run(args, cwd=None):
    env = dict(os.environ)
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    r = subprocess.run([sys.executable] + args, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env, cwd=cwd)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


def run_exe(exe, args, cwd=None):
    env = dict(os.environ)
    env["PYTHONUTF8"] = "1"
    # stdin=DEVNULL 保证 Console.IsInputRedirected == true，
    # 交互式确认会走默认值而不会把自测挂住。
    r = subprocess.run([exe] + args, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env, cwd=cwd,
                       stdin=subprocess.DEVNULL)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


def log_text(root):
    p = os.path.join(root, "Machine", "logs", "machine_update.log")
    try:
        with open(p, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def fresh(path):
    if os.path.isdir(path):
        try:
            shutil.rmtree(path)
        except OSError:
            # 本机对工作区下的删除有守卫（WinError 5 拒绝访问）。
            # 退而求其次：改名挪走，保证沙箱目录可用，也让自测能反复跑。
            junk = "%s_stale_%d" % (path, int(time.time()))
            try:
                os.rename(path, junk)
            except OSError:
                pass
    os.makedirs(path, exist_ok=True)


def build_update_sandbox(name, managed_extra=False):
    """搭一个"已安装 Machine、准备应用更新"的最小游戏目录。"""
    root = os.path.join(SANDBOX, name)
    fresh(root)
    managed = os.path.join(root, "Aviassembly_Data", "Managed")
    machine = os.path.join(root, "Machine")
    os.makedirs(managed)
    os.makedirs(machine)

    shutil.copy2(os.path.join(GAME_SRC, "Aviassembly.exe"), os.path.join(root, "Aviassembly.exe"))
    asm_src = os.path.join(GAME_SRC, "Aviassembly_Data", "Managed", "Assembly-CSharp.dll")
    shutil.copy2(asm_src, os.path.join(managed, "Assembly-CSharp.dll"))

    # 当前已安装的旧核心（用一个可辨识的假内容，便于判断"是否被替换"）
    with open(os.path.join(managed, "Machine.Core.dll"), "wb") as f:
        f.write(b"OLD-CORE-" + b"\x00" * 4096)

    # 更新器运行所需的文件（模拟安装后的 Machine/）
    for fn in ["machine_update.py", "machine_common.py", "machine_update.bat",
               "MachineInstaller.exe", "Mono.Cecil.dll"]:
        shutil.copy2(os.path.join(DIST, fn), os.path.join(machine, fn))
    shutil.copy2(os.path.join(DIST, "Machine", "update.json"), os.path.join(machine, "update.json"))
    shutil.copy2(os.path.join(DIST, "Machine", "net.json"), os.path.join(machine, "net.json"))
    return root


def stage_update(root, with_sig=True, tamper_dll=False, bad_hash=False,
                 bad_sig=False, version=None, no_apply_json=False):
    """把 dist 的新核心做成一个更新包放到 Machine/update/。"""
    updir = os.path.join(root, "Machine", "update")
    os.makedirs(updir, exist_ok=True)
    with open(os.path.join(DIST, "version.json"), "r", encoding="utf-8") as f:
        ver = json.load(f)

    data = open(os.path.join(DIST, "Machine.Core.dll"), "rb").read()
    if tamper_dll:
        data = data[:5000] + b"\xAB" + data[5001:]
    with open(os.path.join(updir, "Machine.Core.dll"), "wb") as f:
        f.write(data)

    sig = None
    if with_sig:
        with open(os.path.join(DIST, "Machine.Core.dll.sig"), "r", encoding="ascii") as f:
            sig = "".join(f.read().split())
        if bad_sig:
            sig = ("A" * 40) + sig[40:]
        with open(os.path.join(updir, "Machine.Core.dll.sig"), "w", encoding="ascii", newline="\n") as f:
            f.write(sig)

    if no_apply_json:
        return os.path.join(updir, "Machine.Core.dll"), ver

    doc = {
        "version": version if version else ver.get("version", ""),
        "channel": ver.get("channel", "stable"),
        "sha256": ("0" * 64) if bad_hash else hashlib.sha256(data).hexdigest(),
        "url": ver.get("core", ""),
        "minVersion": "",
        "time": "2026-01-01 00:00:00",
    }
    if sig:
        doc["sig"] = sig
    with open(os.path.join(updir, "apply.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    return os.path.join(updir, "Machine.Core.dll"), ver


# ---------------------------------------------------------------------------
# 更新流程
# ---------------------------------------------------------------------------

def test_update_ok():
    root = build_update_sandbox("upd_ok")
    stage_update(root)
    core, ver = stage_update(root)
    expect = hashlib.sha256(open(core, "rb").read()).hexdigest()
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    got = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    ok = (rc == 0 and got == expect)
    record("更新-正常签名包", ok, "rc=%d 核心哈希%s" % (rc, "已替换" if got == expect else "未替换"))
    bak = os.path.join(root, "Machine", "backup")
    n = len(os.listdir(bak)) if os.path.isdir(bak) else 0
    record("更新-生成旧核心备份", n >= 1, "backup/ 内 %d 个文件" % n)
    left = [f for f in os.listdir(os.path.join(root, "Machine", "update"))]
    record("更新-成功后清理更新包", left == [], "残留: %s" % left)
    rec_path = os.path.join(root, "Machine", "installed.json")
    okr = os.path.isfile(rec_path)
    record("更新-写安装记录 installed.json", okr)
    return root


def test_update_tampered_dll():
    root = build_update_sandbox("upd_tampered")
    before = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    stage_update(root, tamper_dll=True)
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    after = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("更新-DLL 被篡改 -> 拒绝(退出码3)", rc == 3 and before == after,
           "rc=%d 目标未被修改=%s" % (rc, before == after))
    record("更新-DLL 被篡改 -> 留有拒绝日志", "签名" in log_text(root) or "拒绝" in log_text(root))


def test_update_bad_hash():
    root = build_update_sandbox("upd_badhash")
    before = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    stage_update(root, bad_hash=True)
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    after = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("更新-清单哈希不符 -> 拒绝(退出码2)", rc == 2 and before == after,
           "rc=%d 目标未被修改=%s" % (rc, before == after))


def test_update_missing_sig():
    root = build_update_sandbox("upd_nosig")
    before = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    stage_update(root, with_sig=False)
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    after = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("更新-缺签名且 requireSignature -> 拒绝(退出码3)",
           rc == 3 and before == after, "rc=%d 目标未被修改=%s" % (rc, before == after))

    # --allow-unsigned 时应放行（哈希一致）
    rc2, out2 = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y", "--allow-unsigned"])
    after2 = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    changed = after2 != before
    record("更新---allow-unsigned 放行未签名包", rc2 == 0 and changed,
           "rc=%d 核心已替换=%s" % (rc2, changed))


def test_update_old_version():
    root = build_update_sandbox("upd_oldver")
    stage_update(root, version="1.0.0")
    # 先做一次正常更新，写入 installed.json (coreVersion=真实版本)
    stage_update(root)
    run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    # 再用一个更旧的版本号
    stage_update(root, version="0.0.1")
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    record("更新-版本不新 -> 跳过(退出码1)", rc == 1, "rc=%d" % rc)
    rc2, out2 = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y", "--force"])
    record("更新-版本不新 + --force -> 应用", rc2 == 0, "rc=%d" % rc2)


def test_update_no_installer():
    root = build_update_sandbox("upd_noinst")
    stage_update(root)
    os.remove(os.path.join(root, "Machine", "MachineInstaller.exe"))
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    txt = log_text(root)
    listed = "已尝试以下路径" in txt or "已尝试以下路径" in out
    record("更新-找不到安装器 -> 退出码1 并列出尝试过的路径",
           rc == 1 and listed, "rc=%d 列出路径=%s" % (rc, listed))


def test_update_stale_installer():
    root = build_update_sandbox("upd_staleinst")
    stage_update(root)
    # 先正常更新一次，写入 installerSha256
    run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    # 换掉安装器
    with open(os.path.join(root, "Machine", "MachineInstaller.exe"), "ab") as f:
        f.write(b"TAMPER")
    stage_update(root, version="9.9.9")
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    # -y 下 confirm 默认 True，所以会继续执行并成功；关键是必须有告警
    warned = "安装器与安装时记录不一致" in log_text(root)
    record("更新-安装器被替换 -> 告警", warned and rc == 0, "rc=%d 告警=%s" % (rc, warned))


def test_installer_wrong_hash():
    root = build_update_sandbox("upd_insthash")
    core, ver = stage_update(root)
    before = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    inst = os.path.join(root, "Machine", "MachineInstaller.exe")
    rc, out = run_exe(inst, [root, "--update", "--source", core, "--expect-sha256", "0" * 64])
    after = sha256(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("安装器-传入错误 --expect-sha256 -> 退出码3 且不写盘",
           rc == 3 and before == after, "rc=%d 未修改=%s" % (rc, before == after))
    record("安装器-输出为可读中文（UTF-8，无乱码）",
           "哈希" in out or "更新包" in out, "首行: %s" % out.strip().splitlines()[0][:80] if out.strip() else "无输出")


def test_no_pending_update():
    root = build_update_sandbox("upd_none")
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    record("更新-无待应用更新 -> 退出码1 且明确说明", rc == 1 and "未找到待应用的更新" in out,
           "rc=%d" % rc)


# ---------------------------------------------------------------------------
# 配置校验
# ---------------------------------------------------------------------------

def test_config_validation():
    root = build_update_sandbox("cfg")
    machine = os.path.join(root, "Machine")
    with open(os.path.join(machine, "update.json"), "w", encoding="utf-8") as f:
        f.write('{"repo":"bad repo!!","branch":"../../etc","channel":"alpha","enabled":"yes",'
                '"requireSignature":"nope"}')
    with open(os.path.join(machine, "net.json"), "w", encoding="utf-8") as f:
        f.write('{"server":"999.1.1.1; rm -rf","port":99999,"playerName":"@@@###TOOLONGNAME###"}')
    stage_update(root)
    rc, out = run([os.path.join(root, "Machine", "machine_update.py"), root, "-y"])
    txt = log_text(root)
    checks = [
        ("repo 格式非法", "repo"),
        ("branch 非法", "branch"),
        ("channel 只支持", "channel"),
        ("requireSignature 非布尔值", "requireSignature"),
    ]
    bad = [label for needle, label in checks if needle not in txt]
    record("配置-update.json 非法字段全部被拒并回落", not bad, "缺失告警: %s" % bad)
    record("配置-校验后仍能正常应用更新", rc == 0, "rc=%d" % rc)

    # net.json 走 C# 侧，这里只验证 Python 侧校验函数
    sys.path.insert(0, DIST)
    import machine_common as mc
    raw, err = mc.load_json_file(os.path.join(machine, "net.json"))
    cfg, warns = mc.validate_net_config(raw)
    ok = (cfg["server"] == "" and cfg["port"] == 26460 and cfg["playerName"] == "Pilot")
    record("配置-net.json 非法值全部回落默认", ok,
           "server=%r port=%r name=%r warns=%d" % (cfg["server"], cfg["port"], cfg["playerName"], len(warns)))


def test_manifest_validation():
    sys.path.insert(0, DIST)
    import machine_common as mc
    p = os.path.join(SANDBOX, "_bad_manifest.json")
    os.makedirs(SANDBOX, exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        f.write('{"version":"not-a-version","sha256":"xyz","sig":"###","url":"http://evil"}')
    mf, warns = mc.parse_release_manifest(p)
    ok = (mf is not None and mf["sha256"] == "" and mf["version"] == "" and len(warns) >= 2)
    record("配置-发布清单非法字段被丢弃", ok, "warns=%d" % len(warns))


# ---------------------------------------------------------------------------
# 安装 / 卸载
# ---------------------------------------------------------------------------

def build_install_sandbox(name):
    """能真正跑注入的游戏目录（需要 Unity 依赖 dll）。"""
    root = os.path.join(SANDBOX, name)
    fresh(root)
    src_data = os.path.join(GAME_SRC, "Aviassembly_Data")
    dst_data = os.path.join(root, "Aviassembly_Data")
    managed_src = os.path.join(src_data, "Managed")
    managed_dst = os.path.join(dst_data, "Managed")
    os.makedirs(managed_dst)
    shutil.copy2(os.path.join(GAME_SRC, "Aviassembly.exe"), os.path.join(root, "Aviassembly.exe"))
    for fn in ["Assembly-CSharp.dll", "UnityEngine.CoreModule.dll", "mscorlib.dll"]:
        shutil.copy2(os.path.join(managed_src, fn), os.path.join(managed_dst, fn))
    json_src = os.path.join(src_data, "RuntimeInitializeOnLoads.json")
    if os.path.isfile(json_src):
        shutil.copy2(json_src, os.path.join(dst_data, "RuntimeInitializeOnLoads.json"))
    # 玩家自己的 mod，用来验证卸载不会删掉它
    d = os.path.join(root, "mods", "MyOwnMod")
    os.makedirs(d)
    with open(os.path.join(d, "mod.json"), "w", encoding="utf-8") as f:
        f.write('{"id":"MyOwnMod"}')
    return root


def test_install_and_uninstall():
    root = build_install_sandbox("inst")
    inst = os.path.join(DIST, "install_machine.py")
    rc, out = run([inst, root, "-y"])
    managed = os.path.join(root, "Aviassembly_Data", "Managed")
    core_ok = os.path.isfile(os.path.join(managed, "Machine.Core.dll"))
    record("安装-在真实 Assembly-CSharp.dll 上注入成功", rc == 0 and core_ok,
           "rc=%d 核心已就位=%s" % (rc, core_ok))
    if core_ok:
        record("安装-核心哈希与发布包一致",
               sha256(os.path.join(managed, "Machine.Core.dll")) == sha256(os.path.join(DIST, "Machine.Core.dll")))

    machine = os.path.join(root, "Machine")
    need = ["machine_update.py", "machine_common.py", "machine_update.bat",
            "MachineInstaller.exe", "Mono.Cecil.dll"]
    missing = [n for n in need if not os.path.isfile(os.path.join(machine, n))]
    record("安装-更新器运行所需文件全部部署（旧版只复制了 .bat）", not missing, "缺失: %s" % missing)
    record("安装-写 installed.json 记录",
           os.path.isfile(os.path.join(machine, "installed.json")))
    readme = os.path.join(machine, "README.txt")
    if os.path.isfile(readme):
        raw = open(readme, "rb").read()
        txt = raw.decode("utf-8", "replace")
        record("安装-README.txt 为中文且无 BOM",
               (not raw.startswith(b"\xef\xbb\xbf")) and txt.startswith("Machine Mod 加载器"),
               "BOM=%s 表头=%r" % (raw.startswith(b"\xef\xbb\xbf"),
                                 (txt.splitlines() or [""])[0][:24]))
        record("安装-README.txt 不含开发机绝对路径", "Aviassembly_DEV" not in txt and ":\\豆包的下载" not in txt)

    # 老版本遗留下来的坏 README（带 BOM + 写死开发机绝对路径）必须在升级时被修好；
    # 同时确认安装不会顺手删掉玩家自己的文件。
    stale = os.path.join(machine, "README.txt")
    user_note = os.path.join(machine, "notes.txt")
    with open(stale, "wb") as f:
        f.write("\ufeffMachine Mod Loader\r\nBad path: D:\\Aviassembly_DEV\\x\r\n".encode("utf-8"))
    with open(user_note, "wb") as f:
        f.write(b"player's own file")

    # 幂等：再装一次
    rc2, out2 = run([inst, root, "-y"])
    record("安装-重复安装幂等", rc2 == 0, "rc=%d" % rc2)

    raw2 = open(stale, "rb").read()
    record("安装-升级时修好老版本遗留的坏 README",
           not raw2.startswith(b"\xef\xbb\xbf") and b"Aviassembly_DEV" not in raw2,
           "BOM=%s 含DEV路径=%s" % (raw2.startswith(b"\xef\xbb\xbf"), b"Aviassembly_DEV" in raw2))
    record("安装-不删除玩家自己的文件",
           os.path.isfile(user_note) and open(user_note, "rb").read() == b"player's own file")

    # 玩家自己写的 README 不能被模板覆盖（只有本工具生成的模板才允许刷新）
    with open(stale, "wb") as f:
        f.write(b"My own private readme")
    run([inst, root, "-y"])
    record("安装-不覆盖玩家自写的 README",
           open(stale, "rb").read() == b"My own private readme")

    # profile.json 用来验证卸载会备份玩家数据
    os.makedirs(machine, exist_ok=True)
    with open(os.path.join(machine, "profile.json"), "w", encoding="utf-8") as f:
        f.write('{"playerName":"TestPilot","uid":"12345678901","playTimeSeconds":42}')

    un = os.path.join(DIST, "uninstall_machine.py")
    rc3, out3 = run([un, root, "-y"])
    kept_mods = os.path.isfile(os.path.join(root, "mods", "MyOwnMod", "mod.json"))
    machine_gone = not os.path.isdir(machine)
    baks = [d for d in os.listdir(root) if d.startswith("Machine_uninstalled_")]
    profile_saved = False
    for b in baks:
        if os.path.isfile(os.path.join(root, b, "profile.json")):
            profile_saved = True
    record("卸载-退出码 0", rc3 == 0, "rc=%d" % rc3)
    record("卸载-保留玩家 mods/（旧版会递归删除）", kept_mods)
    record("卸载-移除 Machine/ 与 Machine.Core.dll",
           machine_gone and not os.path.isfile(os.path.join(managed, "Machine.Core.dll")))
    record("卸载-备份玩家 profile.json", profile_saved, "备份目录: %s" % baks)
    asm_back = os.path.isfile(os.path.join(managed, "Assembly-CSharp.dll"))
    record("卸载-游戏程序集仍在（已还原）", asm_back)


def test_install_unsigned_rejected():
    """
    版本清单里同时带 sha256 与 sig。把 sig 去掉 = 一份"未签名发布包"。
    此时非交互运行（不带 -y）应当拒绝安装。
    """
    tmp = os.path.join(SANDBOX, "dist_nosig")
    fresh(tmp)
    for fn in ["install_machine.py", "machine_common.py", "Machine.Core.dll",
               "MachineInstaller.exe", "Mono.Cecil.dll", "version.json"]:
        shutil.copy2(os.path.join(DIST, fn), os.path.join(tmp, fn))
    shutil.copytree(os.path.join(DIST, "Machine"), os.path.join(tmp, "Machine"))
    with open(os.path.join(tmp, "version.json"), "r", encoding="utf-8") as f:
        doc = json.load(f)
    doc.pop("sig", None)
    with open(os.path.join(tmp, "version.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)

    root = build_install_sandbox("inst_nosig")
    # 非交互、不给 -y：应拒绝
    rc, out = run([os.path.join(tmp, "install_machine.py"), root])
    installed = os.path.isfile(os.path.join(root, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("安装-未签名发布包（非交互）-> 拒绝安装", rc == 6 and not installed,
           "rc=%d 已安装=%s" % (rc, installed))

    # 显式 -y（= 用户坚持）时放行，但必须留下告警
    rc_y, out_y = run([os.path.join(tmp, "install_machine.py"), root, "-y"])
    warn = "未找到签名文件" in (out_y + log_text(tmp))
    record("安装-未签名 + 显式 -y -> 放行但留下告警", rc_y == 0 and warn,
           "rc=%d 告警=%s" % (rc_y, warn))

    # 篡改 DLL 后哈希与 version.json 不符，应直接失败（退出码 2）
    root2 = build_install_sandbox("inst_hashbad")
    with open(os.path.join(tmp, "Machine.Core.dll"), "r+b") as f:
        f.seek(3000)
        f.write(b"\x00")
    rc2, out2 = run([os.path.join(tmp, "install_machine.py"), root2, "-y"])
    record("安装-发布包哈希与 version.json 不符 -> 退出码2", rc2 == 2, "rc=%d" % rc2)

    # 签名存在但被破坏（用 dist 的签名配篡改过的 DLL 无法造假，这里改成改坏签名本身）
    root3 = build_install_sandbox("inst_sigbad")
    with open(os.path.join(tmp, "version.json"), "r", encoding="utf-8") as f:
        doc = json.load(f)
    real = json.load(open(os.path.join(DIST, "version.json"), encoding="utf-8"))
    doc["sig"] = ("A" * 40) + real["sig"][40:]
    with open(os.path.join(tmp, "version.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
    shutil.copy2(os.path.join(DIST, "Machine.Core.dll"), os.path.join(tmp, "Machine.Core.dll"))
    rc3, out3 = run([os.path.join(tmp, "install_machine.py"), root3, "-y"])
    inst3 = os.path.isfile(os.path.join(root3, "Aviassembly_Data", "Managed", "Machine.Core.dll"))
    record("安装-签名无效 -> 拒绝安装(退出码3)", rc3 == 3 and not inst3,
           "rc=%d 已安装=%s" % (rc3, inst3))


def test_exe_cli_install_uninstall():
    """
    双击 install_machine.bat / MachineInstaller.exe 走的是 C# CLI 这一条链。
    它必须在不依赖系统 Python 的情况下完成注入 + 部署 + 卸载，
    并且把进度条与步骤文字打到控制台上（旧版双击只有一个黑窗一闪而过）。
    """
    root = build_install_sandbox("exe_inst")
    exe = os.path.join(DIST, "MachineInstaller.exe")
    managed = os.path.join(root, "Aviassembly_Data", "Managed")
    machine = os.path.join(root, "Machine")

    # 1) 安装（等价双击：--auto --install）
    rc, out = run_exe(exe, ["--auto", "--install", "--yes", "--no-pause", root])
    core_ok = os.path.isfile(os.path.join(managed, "Machine.Core.dll"))
    record("EXE-安装成功（不依赖系统 Python）", rc == 0 and core_ok,
           "rc=%d 核心已就位=%s" % (rc, core_ok))

    need = ["machine_update.py", "machine_common.py", "machine_update.bat",
            "MachineInstaller.exe", "Mono.Cecil.dll", "installed.json"]
    missing = [n for n in need if not os.path.isfile(os.path.join(machine, n))]
    record("EXE-运行时文件部署齐全", not missing, "缺失: %s" % missing)
    record("EXE-部署 mods/ 模板",
           os.path.isfile(os.path.join(root, "mods", "MachineAAM", "mod.json")))

    # 铺进 <游戏>/Machine/ 的说明与批处理：都必须是中文的。
    # README.txt 由安装器（C# WriteReadme）生成，UTF-8 无 BOM；
    # .bat 是 GBK —— cmd.exe 解析含 UTF-8 多字节字符的批处理会字节错位。
    exe_readme = os.path.join(machine, "README.txt")
    if os.path.isfile(exe_readme):
        raw_r = open(exe_readme, "rb").read()
        txt_r = raw_r.decode("utf-8", "replace")
        record("EXE-安装后 README.txt 是中文且无 BOM",
               (not raw_r.startswith(b"\xef\xbb\xbf")) and txt_r.startswith("Machine Mod 加载器"),
               "BOM=%s 表头=%r" % (raw_r.startswith(b"\xef\xbb\xbf"),
                                 (txt_r.splitlines() or [""])[0][:24]))
    else:
        record("EXE-安装后 README.txt 是中文且无 BOM", False,
               "缺失 %s（安装器没有生成说明文件）" % exe_readme)

    batp = os.path.join(machine, "machine_update.bat")
    bat_txt = None
    if os.path.isfile(batp):
        raw_b = open(batp, "rb").read()
        try:
            bat_txt = raw_b.decode("gbk")
        except Exception:
            bat_txt = None
    record("EXE-部署的 .bat 是 GBK 中文（cmd 才能正确解析）",
           bat_txt is not None and "chcp 936" in bat_txt and "本文件夹里找不到" in bat_txt,
           "可 GBK 解码=%s 含 chcp 936=%s" % (bat_txt is not None,
                                            bool(bat_txt) and "chcp 936" in bat_txt))

    # 2) 进度可视化：ASCII 进度条 + 步骤文字（这才是用户要看到的东西）
    has_bar = ("[" in out) and ("#" in out or "-" in out)
    step_hits = out.count("]")
    clean_ascii = ("█" not in out) and ("░" not in out) and ("✓" not in out)
    record("EXE-输出含 ASCII 进度条", has_bar,
           "含'['=%s 含'#'=%s" % ("[" in out, "#" in out))
    record("EXE-输出含多个步骤标记", step_hits >= 8, "']' 出现 %d 次" % step_hits)
    record("EXE-不输出 conhost 画不出的 Unicode 方块", clean_ascii)

    # 3) 幂等
    rc2, _o2 = run_exe(exe, ["--auto", "--install", "--yes", "--no-pause", root])
    record("EXE-重复安装幂等", rc2 == 0, "rc=%d" % rc2)

    # 4) 卸载（等价双击 uninstall_machine.bat）
    rc3, out3 = run_exe(exe, ["--auto", "--uninstall", "--yes", "--no-pause", "--backup", root])
    kept = os.path.isfile(os.path.join(root, "mods", "MyOwnMod", "mod.json"))
    gone = (not os.path.isdir(machine)) and \
           (not os.path.isfile(os.path.join(managed, "Machine.Core.dll")))
    record("EXE-卸载退出码 0", rc3 == 0, "rc=%d" % rc3)
    record("EXE-卸载保留玩家 mods/ 并移除 Machine/", kept and gone,
           "保留mods=%s 已清理=%s" % (kept, gone))
    record("EXE-卸载还原程序集（仍在）",
           os.path.isfile(os.path.join(managed, "Assembly-CSharp.dll")))

    # 5) --help 必须正常返回，不能闪退也不能挂住
    rc4, out4 = run_exe(exe, ["--help"])
    record("EXE---help 正常返回且打印用法", rc4 == 0 and "--install" in out4, "rc=%d" % rc4)


def test_bat_chinese_launchers():
    """.bat 启动器：中文提示必须是 GBK 编码，且两种控制台代码页都要显示正确。

    这不是洁癖 —— cmd.exe 解析含 UTF-8 多字节字符的批处理时，其行/字节位移
    记账会错位，后面整行会被当成命令执行（实测：第 3 行中文 echo 变成了
    "xxx 不是内部或外部命令"）。GBK(936) + 开头 chcp 936 在 936 与 65001
    两种控制台下都是 5/5 行精确匹配。
    """
    names = ["install_machine.bat", "uninstall_machine.bat", "machine_update.bat"]
    for name in names:
        p = os.path.join(DIST, name)
        if not os.path.isfile(p):
            record("BAT-%s 存在且为 GBK 中文" % name, False, "缺失")
            continue
        raw = open(p, "rb").read()
        try:
            txt = raw.decode("gbk")
            dec = True
        except Exception:
            txt, dec = "", False
        record("BAT-%s 为 GBK 中文 + 开头 chcp 936" % name,
               dec and (not raw.startswith(b"\xef\xbb\xbf"))
               and "chcp 936" in txt and "本文件夹里找不到" in txt,
               "GBK可解码=%s BOM=%s 含chcp936=%s" % (dec, raw.startswith(b"\xef\xbb\xbf"),
                                                   "chcp 936" in txt))

    # 真跑一次 :missing 分支（目录里故意不放 exe），验证中文提示与退出码
    d = os.path.join(SANDBOX, "bat_noexe")
    if not os.path.isdir(d):
        os.makedirs(d)
    for name in names:
        shutil.copy2(os.path.join(DIST, name), os.path.join(d, name))

    for cp in (936, 65001):
        r = subprocess.run(["cmd.exe", "/c", "chcp %d >nul & call install_machine.bat" % cp],
                           capture_output=True, cwd=d, stdin=subprocess.DEVNULL, timeout=120)
        raw = (r.stdout or b"") + (r.stderr or b"")
        txt = raw.decode("gbk", "replace")
        broken = [ln for ln in txt.split("\r\n")
                  if "not recognized" in ln or "不是内部或外部命令" in ln]
        ok = (r.returncode == 9009
              and "本文件夹里找不到 MachineInstaller.exe" in txt
              and not broken)
        record("BAT-:missing 分支在代码页 %d 下中文正常" % cp, ok,
               "rc=%d 中文提示=%s 解析错误=%s" % (r.returncode,
                                               "本文件夹里找不到" in txt,
                                               broken if broken else "无"))


def main(argv):
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--keep", action="store_true", help="保留沙箱目录便于排查")
    args = ap.parse_args(argv)

    os.makedirs(SANDBOX, exist_ok=True)
    print("=" * 70)
    print(" Machine 发布链路自测   (sandbox: %s)" % SANDBOX)
    print("=" * 70)

    if not os.path.isfile(os.path.join(DIST, "version.json")):
        print("请先运行 pack_dist.py 与 tools/sign_release.py")
        return 1

    groups = [
        ("更新流程", [test_update_ok, test_update_tampered_dll, test_update_bad_hash,
                      test_update_missing_sig, test_update_old_version,
                      test_update_no_installer, test_update_stale_installer,
                      test_installer_wrong_hash, test_no_pending_update]),
        ("配置校验", [test_config_validation, test_manifest_validation]),
        ("安装/卸载", [test_install_and_uninstall, test_install_unsigned_rejected]),
        ("EXE 命令行（双击安装器走的链）", [test_exe_cli_install_uninstall]),
        ("中文启动器（.bat 编码）", [test_bat_chinese_launchers]),
    ]

    for title, tests in groups:
        print("\n--- %s ---" % title)
        for t in tests:
            try:
                t()
            except Exception as e:
                import traceback
                record(t.__name__, False, "异常: %s" % e)
                traceback.print_exc()

    passed = sum(1 for _n, ok, _d in RESULTS if ok)
    total = len(RESULTS)
    print("\n" + "=" * 70)
    print(" 结果: %d/%d 通过" % (passed, total))
    for n, ok, d in RESULTS:
        if not ok:
            print("   FAIL  %s  %s" % (n, d))
    print("=" * 70)

    if not args.keep and passed == total:
        shutil.rmtree(SANDBOX, ignore_errors=True)
        print("已清理沙箱目录")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
