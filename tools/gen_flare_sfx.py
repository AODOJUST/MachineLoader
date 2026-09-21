#!/usr/bin/env python3
# Generate a short flare/chaff ejection "pop" SFX as 16-bit PCM mono WAV.
# Pure stdlib (wave/struct/math/random), no numpy needed.
import math, random, struct, wave, os

SR = 44100
DUR = 0.26
N = int(SR * DUR)

random.seed(20260915)

def env(t, tau):
    return math.exp(-t / tau)

samples = [0.0] * N
for i in range(N):
    t = i / SR
    s = 0.0
    # sharp click transient (cartridge snap): very short broadband
    if t < 0.06:
        s += (random.random() * 2 - 1) * env(t, 0.005) * 0.95
    # pop body: descending sine 520 -> 110 Hz over 0.09s
    if t < 0.10:
        f = 520.0 + (110.0 - 520.0) * (t / 0.10)
        s += math.sin(2 * math.pi * f * t) * env(t, 0.045) * 0.55
    # low thump for weight
    s += math.sin(2 * math.pi * 80.0 * t) * env(t, 0.075) * 0.40
    # hiss tail of the cartridge
    if t < 0.18:
        s += (random.random() * 2 - 1) * env(t, 0.10) * 0.22
    samples[i] = s

peak = max(1e-6, max(abs(x) for x in samples))
scale = 0.95 / peak
pcm = bytearray()
for x in samples:
    v = int(max(-1.0, min(1.0, x * scale)) * 32767)
    pcm += struct.pack('<h', v)

out = r"D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\audio\flare.wav"
os.makedirs(os.path.dirname(out), exist_ok=True)
with wave.open(out, 'wb') as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(bytes(pcm))

with open(r"C:\Users\16857\flare_report.txt", "w", encoding="ascii") as f:
    f.write("WROTE %s samples=%d dur=%.3fs bytes=%d\n" % (out, N, DUR, len(pcm)))
print("done")
