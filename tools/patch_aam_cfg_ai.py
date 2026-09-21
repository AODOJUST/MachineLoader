import io, os, re

FILES = [
    r'D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\aam_config.json',
    r'D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\Machine_Dev\dist\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\Machine_Dev\dist_upload\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\MachineLoader_work\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\MachineLoader_github\MachineLoader-main\mods\MachineAAM\aam_config.json',
]

def setkv(text, key, val):
    """已存在就改值，返回 (text, found)"""
    pat = re.compile(r'("' + re.escape(key) + r'"\s*:\s*)([^,\n}]+)')
    if pat.search(text):
        return pat.sub(lambda m: m.group(1) + val, text, count=1), True
    return text, False

rules = [
    ('evadeHold', '1.6', None),
    ('avoidTerminalRange', '1600', 'avoidProfileMargin'),
    ('avoidTerminalMin', '0.30', 'avoidProfileMargin'),
    ('aiMissileName', '"R-37"', 'aiSpeedScale'),
    ('aiLockMemory', '6', 'aiSpeedScale'),
    ('aiKinematicGate', 'true', 'aiSpeedScale'),
    ('aiAutoPersona', 'true', 'aiSpeedScale'),
]

for p in FILES:
    if not os.path.exists(p):
        print("SKIP missing " + p); continue
    raw = io.open(p, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    t = raw.decode('utf-8-sig')
    added, changed = [], []
    for key, val, anchor in rules:
        t2, found = setkv(t, key, val)
        if found:
            if t2 != t: changed.append(key)
            t = t2
            continue
        # 不存在 -> 插在 anchor 那行之后
        lines = t.split('\n')
        out, done = [], False
        for ln in lines:
            out.append(ln)
            if not done and anchor and ('"' + anchor + '"') in ln:
                indent = ln[:len(ln) - len(ln.lstrip())]
                out.append(indent + '"' + key + '": ' + val + ',')
                done = True
        if not done:
            print("  !! anchor not found for " + key + " in " + p); continue
        t = '\n'.join(out)
        added.append(key)
    io.open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + t.encode('utf-8'))
    print("OK  " + p + "  added=" + (",".join(added) if added else "-")
          + "  changed=" + (",".join(changed) if changed else "-"))
