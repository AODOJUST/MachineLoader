# -*- coding: utf-8 -*-
import urllib.request, miniaudio, wave, os

url = u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/40eb6c9f9dcd49aca57f008b50e22c5a?preview=1&auth_key=1789211500-r0-u0-5adaad78be9a908d04c7f09714db8ddb"
tmp = u"D:\\豆包的下载\\Machine_Dev\\tools\\launch_reenc.ogg"
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
print("wav", out, os.path.getsize(out), "sr", sr, "len %.2f" % (len(pcm) / (2 * 2 * sr)))
