# -*- coding: utf-8 -*-
import subprocess, json, sys

jobs = [
    (u"D:\\edgeDownload\\拉起.mp3", "pullup"),
    (u"D:\\edgeDownload\\发射.mp3", "fox1"),
    (u"D:\\edgeDownload\\燃油告警.mp3", "fuellow"),
    (u"D:\\edgeDownload\\敌导弹 (2).mp3", "missile"),
]
for src, tag in jobs:
    cmd = ["mediakit-cli", "editing", "extract-audio",
           "--video-url", src, "--format", "wav"]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(tag, "->", r.stdout.strip().replace("\n", " ") if r.stdout else r.stderr[:200])
