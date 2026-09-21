# -*- coding: utf-8 -*-
import subprocess

jobs = [
    (u"D:\\edgeDownload\\剩余油量.mp3", "bingo"),
    (u"D:\\edgeDownload\\迎角过大.mp3", "pitch"),
    (u"D:\\edgeDownload\\锁定.mp3", "lock"),
    (u"D:\\edgeDownload\\燃油检测.mp3", "fuel45"),
    (u"D:\\edgeDownload\\姿态仪失效.mp3", "roll"),
]
for src, tag in jobs:
    cmd = ["mediakit-cli", "editing", "extract-audio", "--video-url", src, "--format", "wav"]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(tag, "->", (r.stdout or r.stderr).strip().replace("\n", " ")[:220])
