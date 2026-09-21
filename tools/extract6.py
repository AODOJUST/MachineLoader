# -*- coding: utf-8 -*-
import subprocess

jobs = [
    (u"D:\\edgeDownload\\高度.mp3", "sinkrate"),
    (u"D:\\edgeDownload\\注意油量.mp3", "fuel35"),
    (u"D:\\edgeDownload\\注意过载.mp3", "overg"),
    (u"D:\\edgeDownload\\敌机.mp3", "bandit"),
    (u"D:\\edgeDownload\\最大过载三次.mp3", "overg14"),
]
for src, tag in jobs:
    cmd = ["mediakit-cli", "editing", "extract-audio", "--video-url", src, "--format", "wav"]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(tag, "->", (r.stdout or r.stderr).strip().replace("\n", " ")[:220])
