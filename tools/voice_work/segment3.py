"""更稳健的静音分段：固定阈值 -45dB（兜底），并支持外部传阈值。"""
import sys, wave, numpy as np

path, abs_thr = sys.argv[1], float(sys.argv[2] if len(sys.argv) > 2 else -45)
w = wave.open(path, 'rb')
sr = w.getframerate()
n = w.getnframes()
data = w.readframes(n)
w.close()
x = np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0
if x.size == 0:
    print("empty"); sys.exit(0)
print("sr=%d total=%.2fs peak=%.1fdB" % (sr, n/sr, 20*np.log10(np.max(np.abs(x))+1e-10)))

frame = int(sr*0.02)
hop = int(sr*0.01)
nf = max(1, (len(x)-frame)//hop + 1)
rms = np.zeros(nf)
for i in range(nf):
    seg = x[i*hop:i*hop+frame]
    rms[i] = np.sqrt(np.mean(seg*seg)+1e-10)
db = 20*np.log10(rms+1e-10)
print("abs_thr=%.1fdB" % abs_thr)
active = db > abs_thr

events = []
i = 0
while i < nf:
    if not active[i]:
        i += 1; continue
    start = i
    while i < nf and active[i]:
        i += 1
    # 短暂停顿（<300ms）继续合并
    while i < nf:
        j = i
        while j < nf and not active[j]:
            j += 1
        if (j - i) * hop / sr < 0.30 and j < nf:
            i = j
            while i < nf and active[i]:
                i += 1
        else:
            break
    end = i - 1
    s = start*hop/sr
    e = end*hop/sr
    events.append((s, e))

for k,(s,e) in enumerate(events):
    print("%02d  start=%7.2fs  end=%7.2fs  dur=%6.2fs" % (k, s, e, e-s))
