# -*- coding: utf-8 -*-
"""分析音频：解码 mp3，音量包络找语音片段（静音切分）。"""
import io, sys, struct
import miniaudio

path = u"D:\\edgeDownload\\audio_[vocals].mp3"

# 解码为单声道 16bit PCM（一次性解码）
dec = miniaudio.decode_file(path, output_format=miniaudio.SampleFormat.SIGNED16, nchannels=1)
pcm = bytes(dec.samples)
sr = dec.sample_rate or 44100
print("sample_rate", sr, "pcm_bytes", len(pcm))

n = len(pcm) // 2
samples = struct.unpack("<%dh" % n, pcm[: n * 2])

# 50ms 窗口 RMS
win = int(sr * 0.05)
rms = []
i = 0
while i + win <= len(samples):
    s = samples[i:i + win]
    r = (sum(x * x for x in s) / win) ** 0.5
    rms.append(r)
    i += win

# 找片段：RMS 超过阈值（相对最大值的比例 + 绝对下限）
peak = max(rms) if rms else 0
thr = max(peak * 0.06, 120.0)
print("peak_rms", round(peak, 1), "thr", round(thr, 1))

active = [r > thr for r in rms]
# 合并片段：静音 gap 必须 >= 0.35s 才切分
gap_win = int(0.35 / 0.05)
segs = []
in_seg = False
start = 0
last_active = -1
for k, a in enumerate(active):
    if a and not in_seg:
        in_seg = True
        start = k
        last_active = k
    elif a:
        last_active = k
    elif in_seg:
        if k - last_active >= gap_win:
            in_seg = False
            segs.append((start, last_active))
if in_seg:
    segs.append((start, last_active))

print("segments:", len(segs))
for s, e in segs:
    print("  [%.2f - %.2f] dur=%.2fs" % (s * 0.05, (e + 1) * 0.05, (e - s + 1) * 0.05))
