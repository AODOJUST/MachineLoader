# -*- coding: utf-8 -*-
"""Bump the flare eject/drag values in every aam_config.json copy."""
import io, os, glob, re

ROOTS = [
    r'D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\aam_config.json',
    r'D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\Machine_Dev\dist\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\Machine_Dev\dist_upload\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\Machine_Dev\bin\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\MachineLoader_work\mods\MachineAAM\aam_config.json',
    r'D:\豆包的下载\MachineLoader_github\MachineLoader-main\mods\MachineAAM\aam_config.json',
    r'D:\edgeDownload\MachineLoader-2.4.1 (1)\MachineLoader-2.4.1\mods\MachineAAM\aam_config.json',
]

NEW = {
    'flareDrag': '0.50',
    'flareEjectDown': '24',
    'flareEjectBack': '26',
    'flareEjectSide': '15',
}

out = []
for p in ROOTS:
    if not os.path.isfile(p):
        out.append('skip (missing) %s' % p)
        continue
    raw = io.open(p, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    txt = raw.decode('utf-8-sig')
    changed = []
    for k, v in NEW.items():
        pat = re.compile(r'("%s"\s*:\s*)([-0-9.eE+]+)' % re.escape(k))
        m = pat.search(txt)
        if not m:
            out.append('  !! key %s not found in %s' % (k, p))
            continue
        if m.group(2) != v:
            txt = pat.sub(lambda mm: mm.group(1) + v, txt, count=1)
            changed.append('%s %s->%s' % (k, m.group(2), v))
    data = txt.encode('utf-8')
    if bom:
        data = b'\xef\xbb\xbf' + data
    io.open(p, 'wb').write(data)
    out.append('%s  [%s]' % (p, ', '.join(changed) if changed else 'already ok'))

io.open(r'D:\豆包的下载\Machine_Dev\_cfg_report.txt', 'w', encoding='utf-8').write('\n'.join(out))
print('\n'.join(out))
