# -*- coding: utf-8 -*-
import urllib.request, miniaudio, wave, os

url = u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/b3d80f8fc71c4011af8b818b43146f96?preview=1&auth_key=1789211466-r0-u0-39aa527c83ed5160f1f35dc187f8725d"
tmp = u"D:\\豆包的下载\\Machine_Dev\\tools\\launch_reenc.mp3"
req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
with urllib.request.urlopen(req, timeout=120) as r:
    data = r.read()
with open(tmp, "wb") as f:
    f.write(data)
print("downloaded", len(data))

dec = miniaudio.decode_file(tmp, output_format=miniaudio.SampleFormat.SIGNED16, nchannels=2)
pcm = bytes(dec.samples); sr = dec.sample_rate
out = u"D:\\豆包的下载\\Machine_Dev\\tools\\launch_sfx.wav"
with wave.open(out, "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(sr)
    w.writeframes(pcm)
print("wav", out, os.path.getsize(out), "sr", sr, "len", len(pcm)/(2*2*sr))
