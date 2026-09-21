# -*- coding: utf-8 -*-
# Offline scan of Aviassembly asset files for AirportData ScriptableObjects.
# Usage: python dump_airports.py <gameDataDir> <outFile>
import sys, os, json, traceback

def main():
    dataDir = sys.argv[1]
    outFile = sys.argv[2]
    import UnityPy
    lines = []
    files = []
    for n in sorted(os.listdir(dataDir)):
        p = os.path.join(dataDir, n)
        if os.path.isfile(p) and ('.assets' in n or n.startswith('level')):
            files.append(p)
    lines.append('scan dir: %s' % dataDir)
    lines.append('files: %d' % len(files))
    total = 0
    for p in files:
        try:
            env = UnityPy.load(p)
        except Exception as e:
            lines.append('  LOAD FAIL %s : %s' % (os.path.basename(p), e))
            continue
        hits = []
        n_mono = 0
        for obj in env.objects:
            try:
                tn = obj.type.name
            except Exception:
                continue
            if tn != 'MonoBehaviour':
                continue
            n_mono += 1
            try:
                d = obj.read()
            except Exception:
                continue
            nm = getattr(d, 'airportName', None)
            if nm is None:
                try:
                    tt = obj.read_typetree()
                    nm = tt.get('airportName')
                    if nm is not None:
                        hits.append(tt)
                        continue
                except Exception:
                    pass
                continue
            rec = {
                'name': nm,
                'baseAirport': bool(getattr(d, 'baseAirport', False)),
                'initialSpawn': bool(getattr(d, 'initialSpawnAirport', False)),
                'offshore': bool(getattr(d, 'offshoreAirport', False)),
                'refuel': bool(getattr(d, 'refuelAvailable', False)),
                'rot': getattr(d, 'rotation', None),
                'landingMessage': getattr(d, 'landingMessage', None),
            }
            pf = getattr(d, 'airportPrefab', None)
            rec['prefab'] = getattr(pf, 'm_Name', None) if pf is not None else None
            hits.append(rec)
        if hits:
            lines.append('### %s  (mono=%d)' % (os.path.basename(p), n_mono))
            for h in hits:
                lines.append('  ' + json.dumps(h, ensure_ascii=False, default=str))
        total += len(hits)
    lines.append('TOTAL AirportData = %d' % total)
    with open(outFile, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('done', total)

if __name__ == '__main__':
    try:
        main()
    except Exception:
        with open(sys.argv[2], 'w', encoding='utf-8') as f:
            f.write(traceback.format_exc())
        raise
