"""根据给定的 (start, end, label, dest_path) 列表，从源音频剪辑并淡入淡出。"""
import sys, wave, numpy as np
from pathlib import Path

src = sys.argv[1]
items = []
i = 2
while i < len(sys.argv):
    items.append((float(sys.argv[i]), float(sys.argv[i+1]), sys.argv[i+2], sys.argv[i+3]))
    i += 4

w = wave.open(src, 'rb')
sr = w.getframerate(); n = w.getnframes(); raw = w.readframes(n); w.close()
x = np.frombuffer(raw, dtype=np.int16)
pad_ms = 80  # 每段前后留 80ms 静音余量，淡入淡出各 20ms
pad = int(sr * pad_ms / 1000)
fade = int(sr * 0.02)

for s, e, label, dest in items:
    a = max(0, int(s*sr) - pad)
    b = min(len(x), int(e*sr) + pad)
    seg = x[a:b].astype(np.float32)
    # 归一化到 -3dBFS 峰值附近（避免过响/过弱）
    p = np.max(np.abs(seg))
    if p > 100:
        seg = seg * (28000 / p)
    seg = seg.astype(np.int16)
    # 淡入淡出
    if len(seg) > 2*fade:
        seg[:fade] = (seg[:fade].astype(np.float32) * np.linspace(0,1,fade)).astype(np.int16)
        seg[-fade:] = (seg[-fade:].astype(np.float32) * np.linspace(1,0,fade)).astype(np.int16)
    Path(dest).parent.mkdir(parents=True, exist_ok=True)
    o = wave.open(dest, 'wb')
    o.setnchannels(1); o.setsampwidth(2); o.setframerate(sr)
    o.writeframes(seg.tobytes())
    o.close()
    print("wrote", dest, "  dur=%.2fs" % (len(seg)/sr), "  label=", label)
