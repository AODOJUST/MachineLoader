# -*- coding: utf-8 -*-
"""按 ASR 时间戳从音轨剪辑单词 -> 语言集 wav（wave 模块，支持立体声/单声道）。"""
import wave, struct, os, sys

def load(path):
    with wave.open(path, "rb") as w:
        ch = w.getnchannels(); sw = w.getsampwidth(); sr = w.getframerate()
        n = w.getnframes()
        data = w.readframes(n)
    print(path.split("\\")[-1], "ch", ch, "sw", sw, "sr", sr, "frames", n)
    return data, ch, sw, sr

def cut(data, ch, sw, sr, start, end, out):
    i0 = int(start * sr) * ch
    i1 = int(end * sr) * ch
    seg = data[i0 * sw:i1 * sw]
    with wave.open(out, "wb") as w:
        w.setnchannels(ch); w.setsampwidth(sw); w.setframerate(sr)
        w.writeframes(seg)
    print("  ->", os.path.basename(out), "%.2f-%.2f" % (start, end))

base = u"D:\\豆包的下载\\Machine_Dev\\tools"
zh = os.path.join(base, "zh")
en = os.path.join(base, "en")

# ---- 英文（F/A-18C）：替换 bingo / fuellow ----
d, ch, sw, sr = load(os.path.join(en, "fa18_track.wav"))
cut(d, ch, sw, sr, 0.40, 2.40, os.path.join(en, "bingo.wav"))
cut(d, ch, sw, sr, 14.70, 17.60, os.path.join(en, "fuellow.wav"))

# ---- 中文（J-11）：overspeed / bingo / fuellow / fox1 ----
d2, ch2, sw2, sr2 = load(os.path.join(zh, "j11_track.wav"))
cut(d2, ch2, sw2, sr2, 5.75, 8.05, os.path.join(zh, "overspeed.wav"))
cut(d2, ch2, sw2, sr2, 16.30, 17.68, os.path.join(zh, "bingo.wav"))
cut(d2, ch2, sw2, sr2, 29.35, 30.35, os.path.join(zh, "fuellow.wav"))
cut(d2, ch2, sw2, sr2, 44.05, 45.50, os.path.join(zh, "missile_j11.wav"))
cut(d2, ch2, sw2, sr2, 48.35, 49.60, os.path.join(zh, "fox1.wav"))
# audio_[vocals] 的"最大过载"存为 overg 备用
print("done")
