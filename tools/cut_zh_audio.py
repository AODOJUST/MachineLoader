# -*- coding: utf-8 -*-
"""剪辑中文警报音频片段 -> tools/zh/*.wav"""
import os, struct
import miniaudio

src = u"D:\\edgeDownload\\audio_[vocals].mp3"
outdir = u"D:\\豆包的下载\\Machine_Dev\\tools\\zh"
os.makedirs(outdir, exist_ok=True)

dec = miniaudio.decode_file(src, output_format=miniaudio.SampleFormat.SIGNED16, nchannels=1)
pcm = bytes(dec.samples)
sr = dec.sample_rate or 44100

def cut(name, start, end, pad=0.05):
    i0 = max(0, int((start - pad) * sr) * 2)
    i1 = min(len(pcm), int((end + pad) * sr) * 2)
    data = pcm[i0:i1]
    path = os.path.join(outdir, name)
    import wave
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr)
        w.writeframes(data)
    print(name, "%.2f-%.2f -> %d bytes" % (start, end, len(data)))

# 核心 5 词（对应 VoiceAlerts 警报）
cut("stall.wav",      30.84, 32.98)   # 最小速度（失速）
cut("angle.wav",      2.64,  5.98)    # 最大迎角（AOA）
cut("overspeed.wav",  6.60,  9.74)    # 最大过载
cut("pullup.wav",     10.60, 12.62)   # 拉起
cut("missile.wav",    17.64, 20.94)   # 敌导弹

# 备用片段（暂存，不接入播放逻辑）
cut("warning.wav",    21.64, 23.46)   # 警告
cut("fire_l.wav",     24.08, 26.12)   # 左发起火
cut("fire_r.wav",     27.80, 30.66)   # 右发起火
cut("hydraulic.wav",  33.12, 36.42)   # 液压失效
cut("missile_dir.wav",36.92, 46.98)   # 导弹方位告警系列
cut("eject.wav",      47.36, 48.78)   # 弹射
cut("ecm.wav",        49.20, 50.62)   # 电子对抗失效
print("done")
