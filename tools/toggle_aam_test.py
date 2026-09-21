# -*- coding: utf-8 -*-
"""为 aiCombatTest 实机自测临时打开开关（行级改动，保持格式）。

用法:
  python tools/toggle_aam_test.py on     # 备份原文件 + 打开测试开关
  python tools/toggle_aam_test.py off    # 从备份原样还原（保证零残留）
"""
import io, os, shutil, sys

P = r"D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\aam_config.json"
BAK = P + ".pretest_bak"


def load():
    return io.open(P, "r", encoding="utf-8-sig").read()


def save(txt):
    io.open(P, "w", encoding="utf-8-sig", newline="").write(txt)


def main():
    mode = (sys.argv[1] if len(sys.argv) > 1 else "").lower()

    if mode == "on":
        if not os.path.exists(BAK):
            shutil.copy2(P, BAK)
            print("backup ->", BAK)
        else:
            print("backup already exists, reusing")

        txt = load()
        lines = txt.split("\n")
        out = []
        seen = {"aiCombatTest": False, "aiDamagePlayer": False, "autoEnterFlyMode": False}
        for ln in lines:
            s = ln.strip()
            if s.startswith('"aiCombatTest"'):
                ln = '  "aiCombatTest": true,'; seen["aiCombatTest"] = True
            elif s.startswith('"aiDamagePlayer"'):
                ln = ln.replace(": true", ": false"); seen["aiDamagePlayer"] = True
            elif s.startswith('"autoEnterFlyMode"'):
                ln = ln.replace(": false", ": true"); seen["autoEnterFlyMode"] = True
            out.append(ln)
            if s.startswith('"aiAutoPersona"') and not seen["aiCombatTest"]:
                out.append('  "aiCombatTest": true,'); seen["aiCombatTest"] = True
        txt = "\n".join(out)
        save(txt)
        print("applied:", seen)
        print("autoTestTestSave:", '"testAutoLoadSave": true' in txt)
        return 0

    if mode == "off":
        if not os.path.exists(BAK):
            print("!! no backup, nothing to restore"); return 1
        shutil.copy2(BAK, P)
        os.remove(BAK)
        print("restored from backup; backup removed")
        txt = load()
        bad = [k for k in ("aiCombatTest", '"aiDamagePlayer": false', '"autoEnterFlyMode": true')
               if k in txt]
        print("residual test flags:", bad if bad else "none")
        return 0

    print("usage: toggle_aam_test.py on|off")
    return 1


if __name__ == "__main__":
    sys.exit(main())
