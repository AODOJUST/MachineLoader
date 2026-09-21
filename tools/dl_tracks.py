# -*- coding: utf-8 -*-
import urllib.request, os

urls = [
    (u"D:\\豆包的下载\\Machine_Dev\\tools\\en\\fa18_track.wav",
     u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/66c0f1cf-ce8d-4204-91f1-08656cf2546c.wav?preview=1&auth_key=1789205232-r0-u0-a22e775fec6e6c6dccd3b547b5baf7f4"),
    (u"D:\\豆包的下载\\Machine_Dev\\tools\\zh\\j11_track.wav",
     u"https://2130825428-amk-2130605177-default-534849.vod.cn-north-1.volcvideo.com/81755195-2238-4432-b27d-e5b8754854c1.wav?preview=1&auth_key=1789205241-r0-u0-fb6f58e01ac09936c6fa0949505dbcb0"),
]
for path, url in urls:
    d = os.path.dirname(path)
    if not os.path.isdir(d):
        os.makedirs(d)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        data = r.read()
    with open(path, "wb") as f:
        f.write(data)
    print(path, len(data), "bytes")
