# -*- coding: utf-8 -*-
import json

P = r"D:/豆包的下载/Aviassembly_DEV/mods/MachineAAM/aam_config.json"

ANCHOR = '  "evadeAgl": 300,'

BLOCK = '  "evadeAgl": 300,\n' \
        '\n' \
        '  "irEnabled": true,\n' \
        '  "irThrottleK": 1.2,\n' \
        '  "irSpeedK": 0.1,\n' \
        '  "irHeatK": 0.55,\n' \
        '  "irCoolRate": 26,\n' \
        '  "irCoolRef": 60,\n' \
        '  "irLog": false,\n' \
        '\n' \
        '  "seekerEnabled": true,\n' \
        '  "seekerFov": 30,\n' \
        '  "seekerRange": 9000,\n' \
        '  "ircmEnabled": true,\n' \
        '  "ircmReact": 0.35,\n' \
        '  "gateWide": 18,\n' \
        '  "gateNarrow": 2.5,\n' \
        '  "gateShrink": 1.5,\n' \
        '  "ircmDecoyPenalty": 0.22,\n' \
        '\n' \
        '  "flareEnabled": true,\n' \
        '  "flareKey": "X",\n' \
        '  "flareDesign": "decoy flare.planedesign",\n' \
        '  "flarePrice": 400,\n' \
        '  "flareWeight": 0.8,\n' \
        '  "flareSpace": 2,\n' \
        '  "flareGive": 16,\n' \
        '  "flareBurst": 2,\n' \
        '  "flareBurstGap": 0.12,\n' \
        '  "flareCooldown": 0.5,\n' \
        '  "flareDrag": 0.85,\n' \
        '  "flareEjectDown": 20,\n' \
        '  "flareEjectBack": 12,\n' \
        '  "flareEjectSide": 9,\n' \
        '  "flareTest": false,'

raw = open(P, encoding="utf-8-sig").read()
if '"irEnabled"' in raw:
    print("skip: already patched")
else:
    assert ANCHOR in raw, "anchor not found"
    raw = raw.replace(ANCHOR, BLOCK, 1)
    open(P, "w", encoding="utf-8-sig", newline="\n").write(raw)

cfg = json.loads(open(P, encoding="utf-8-sig").read())
for k in ("irEnabled", "irThrottleK", "irSpeedK", "irHeatK", "irCoolRate", "irCoolRef",
          "seekerEnabled", "seekerFov", "ircmEnabled", "ircmReact", "gateWide",
          "gateNarrow", "gateShrink", "ircmDecoyPenalty",
          "flareEnabled", "flareKey", "flareDesign", "flareGive", "flareBurst", "flareTest"):
    print("  %-20s = %s" % (k, cfg.get(k)))
print("keys total:", len(cfg))
print("bom kept:", open(P, "rb").read(3) == b"\xef\xbb\xbf")
