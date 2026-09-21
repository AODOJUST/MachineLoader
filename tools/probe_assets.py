# -*- coding: utf-8 -*-
# Diagnose: list object type counts per asset file + sample MonoBehaviour typetree keys.
import sys, os, json, collections

def main():
    dataDir = sys.argv[1]
    outFile = sys.argv[2]
    import UnityPy
    lines = []
    for n in sorted(os.listdir(dataDir)):
        p = os.path.join(dataDir, n)
        if not os.path.isfile(p):
            continue
        if not ('.assets' in n or n.startswith('level')):
            continue
        try:
            env = UnityPy.load(p)
        except Exception as e:
            lines.append('%s LOAD FAIL %s' % (n, e))
            continue
        cnt = collections.Counter()
        keysets = collections.Counter()
        mono_samples = []
        for obj in env.objects:
            t = obj.type.name
            cnt[t] += 1
            if t == 'MonoBehaviour' and len(mono_samples) < 400:
                try:
                    tt = obj.read_typetree()
                    ks = tuple(sorted(tt.keys()))
                    keysets[ks] += 1
                    if any('irport' in k.lower() for k in tt.keys()):
                        mono_samples.append((obj.path_id, tt))
                except Exception as e:
                    keysets[('ERR:' + type(e).__name__,)] += 1
        lines.append('===== %s =====' % n)
        lines.append('  types: ' + json.dumps(dict(cnt), ensure_ascii=False))
        lines.append('  total in file: %d' % sum(cnt.values()))
        lines.append('  distinct mono keysets: %d' % len(keysets))
        top = keysets.most_common(6)
        for ks, c in top:
            lines.append('    x%d  %s' % (c, ','.join(ks[:12])))
        for pid, tt in mono_samples[:40]:
            lines.append('  AIRPORT-LIKE path=%d %s' % (pid, json.dumps(tt, ensure_ascii=False, default=str)[:600]))
    with open(outFile, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('done')

main()
