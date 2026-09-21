# -*- coding: utf-8 -*-
import subprocess, json, sys

audio_json = json.dumps({"bitrate_kbps": 192, "bitrate_mode": "cbr", "sample_rate": 44100, "channels": 2})
cmd = [
    "mediakit-cli", "audio", "transcode-audio",
    "--audio-url", u"D:\\edgeDownload\\舱门打开_-_音效素材_免费下载_-_爱给网\\空空导弹发射.mp3",
    "--container-format", "MP3",
    "--audio", audio_json,
]
r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
print(r.stdout)
print(r.stderr[-500:] if r.stderr else "")
