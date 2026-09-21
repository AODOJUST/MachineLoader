# -*- coding: utf-8 -*-
"""按 key=value 修改 aam_config.json（保留 BOM、保持键序、写完回读校验）。

值按 JSON 语法解析：true / false / 45 / 0.5 / "X" / "abc"（不带引号的数字与布尔自动识别）。
用法: python tools/set_aam_config.py autoTest=true testObserveSeconds=45 flareKey=X
"""
import json
import os
import sys

P = r"D:/豆包的下载/Aviassembly_DEV/mods/MachineAAM/aam_config.json"


def parse_value(raw):
    low = raw.strip().lower()
    if low == "true":
        return True
    if low == "false":
        return False
    if raw.strip().startswith('"'):
        return json.loads(raw)
    try:
        if "." in raw or "e" in low:
            return float(raw)
        return int(raw)
    except ValueError:
        return raw


def main():
    pairs = sys.argv[1:]
    if not pairs:
        print("usage: set_aam_config.py key=value [key=value ...]")
        return 1

    raw = open(P, encoding="utf-8-sig").read()
    cfg = json.loads(raw)
    applied = []
    for pair in pairs:
        if "=" not in pair:
            print("bad arg:", pair)
            return 1
        key, val = pair.split("=", 1)
        key = key.strip()
        new = parse_value(val)
        if key not in cfg:
            print("WARN: new key (not in file yet):", key)
        old = cfg.get(key)
        cfg[key] = new
        applied.append("%s: %s -> %s" % (key, old, new))

    text = json.dumps(cfg, indent=2, ensure_ascii=False)
    open(P, "w", encoding="utf-8-sig", newline="\n").write(text + "\n")

    back = json.loads(open(P, encoding="utf-8-sig").read())
    for pair in pairs:
        key = pair.split("=", 1)[0].strip()
        if key not in back:
            print("FAIL: key lost", key)
            return 1
    for line in applied:
        print("  " + line)
    print("ok: %d keys, bom=%s" % (len(back), open(P, "rb").read(3) == b"\xef\xbb\xbf"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
