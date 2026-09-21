# -*- coding: utf-8 -*-
import wave, struct, subprocess, sys, os

ff = r"C:\Users\16857\AppData\Local\Doubao\User Data\sandbox_runtime\bases\c98c5042338ed152c6f10ecd8591889f\python\Lib\site-packages\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe"
src = r"D:\edgeDownload\敌导弹.mp3"
out = r"D:\豆包的下载\Machine_Dev\tools\enemy_missile.wav"

subprocess.run([ff, "-y", "-i", src, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", out], check=True)
print("wav ok:", os.path.getsize(out))

w = wave.open(out, "rb")
n = w.getnframes(); sr = w.getframerate()
frames = w.readframes(n)
samples = struct.unpack("<%dh" % n, frames)
dur = n / sr
print("dur=%.2f sr=%d frames=%d" % (dur, sr, n))

# RMS per 0.1s window
win = sr // 10
rms = []
for i in range(0, len(samples) - win, win):
    chunk = samples[i:i+win]
    s = sum(x*x for x in chunk) / len(chunk)
    rms.append(s**0.5)
mx = max(rms) or 1.0
print("max_rms=%.1f" % mx)

# 活动图：每行 60 个字符，'#' = 活跃（>12% max），'.' = 安静
# 每格 0.1s
N = len(rms)
per_line = 60
print("activity map (0.1s per char, 60 chars per line):")
line = []
for i in range(N):
    v = rms[i] / mx
    line.append("#" if v > 0.12 else ("+" if v > 0.04 else "."))
    if len(line) == per_line:
        t0 = (i - per_line + 1) * 0.1
        t1 = i * 0.1
        print("%6.1f-%6.1f  %s" % (t0, t1, "".join(line)))
        line = []
if line:
    t0 = (N - len(line)) * 0.1
    print("%6.1f-%6.1f  %s" % (t0, N*0.1, "".join(line)))

# 找出所有静音段（连续 <4% 超过 1.0s）
sil_start = None
sils = []
for i in range(N):
    if rms[i] / mx < 0.04:
        if sil_start is None: sil_start = i
    else:
        if sil_start is not None and (i - sil_start) >= 10:
            sils.append((sil_start*0.1, i*0.1))
        sil_start = None
if sil_start is not None and (N - sil_start) >= 10:
    sils.append((sil_start*0.1, N*0.1))
print("silences >=1s:", [(round(a,1), round(b,1)) for a,b in sils])

# 输出每 5s 平均 RMS，帮助粗分
print("per-5s avg rms (%% of max):")
for seg in range(0, int(dur)//5 + 1):
    i0 = seg*50; i1 = min(i0+50, N)
    if i0 >= N: break
    sub = rms[i0:i1]
    avg = (sum(sub)/len(sub))/mx*100
    bar = "#" * int(avg/4)
    print("  %4.1f-%4.1f s  %5.1f%%  %s" % (seg*5, min((seg+1)*5, dur), avg, bar))
