import sys, wave
import numpy as np

path = sys.argv[1]
w = wave.open(path, 'rb')
sr = w.getframerate()
nch = w.getnchannels()
sw = w.getsampwidth()
n = w.getnframes()
data = w.readframes(n)
w.close()
x = np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0
if nch > 1:
    x = x.reshape(-1, nch).mean(axis=1)
total = n / sr
print("sr=%d total=%.2fs" % (sr, total))

# RMS 包络，帧长 20ms，步进 10ms
frame = int(sr*0.02)
hop = int(sr*0.01)
nf = (len(x)-frame)//hop + 1
rms = np.zeros(nf)
for i in range(nf):
    seg = x[i*hop:i*hop+frame]
    rms[i] = np.sqrt(np.mean(seg*seg))
db = 20*np.log10(rms+1e-10)
# 背景噪声底：取 5% 分位 + 6dB 作为阈值
noise = np.percentile(db, 5)
thr = noise + 8
print("noise_floor=%.1fdB thr=%.1fdB" % (noise, thr))
active = db > thr

# 合并相邻活动帧（间隔 < 250ms 视为同一段）
events = []
in_ev = False
for i in range(nf):
    if active[i] and not in_ev:
        start = i; in_ev = True
    elif not active[i] and in_ev:
        # 看是否只是短暂停顿（<250ms）
        gap_end = i
        j = i
        while j < nf and not active[j]:
            j += 1
        gap_frames = j - i
        gap_s = gap_frames * hop / sr
        if gap_s < 0.25 and j < nf:
            continue  # 短暂停顿，继续
        events.append((start*hop/sr, (gap_end-1)*hop/sr))
        in_ev = False
        i = j-1
if in_ev:
    events.append((start*hop/sr, (nf-1)*hop/sr))

for k,(s,e) in enumerate(events):
    print("%02d  start=%7.2fs  end=%7.2fs  dur=%6.2fs" % (k, s, e, e-s))
