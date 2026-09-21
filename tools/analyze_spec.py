# -*- coding: utf-8 -*-
import wave, struct, subprocess, os
import numpy as np

w = wave.open(r"D:\豆包的下载\Machine_Dev\tools\enemy_missile.wav", "rb")
n = w.getnframes(); sr = w.getframerate()
samples = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32)
dur = n / sr

# 每 0.5s 窗口 FFT，算频谱重心 + 主导频率
win = int(sr * 0.5)
freqs = np.fft.rfftfreq(win, 1.0/sr)
print("time   centroid(Hz)  dom(Hz)  rms%%")
prev = None
for i in range(0, n - win, win//2):   # 0.25s 步进
    chunk = samples[i:i+win]
    t0 = i / sr
    rms = float(np.sqrt(np.mean(chunk**2)))
    if rms < 50:
        print("%6.2f  (silence) rms=%.0f" % (t0, rms)); continue
    spec = np.abs(np.fft.rfft(chunk * np.hanning(win)))
    # 频谱重心（排除 DC）
    cent = float(np.sum(freqs[1:] * spec[1:]) / max(np.sum(spec[1:]), 1))
    dom = float(freqs[1:][np.argmax(spec[1:])])
    print("%6.2f  %8.0f  %7.0f  %4.0f" % (t0, cent, dom, rms/30))
