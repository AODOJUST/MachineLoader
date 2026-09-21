import sys
from pydub import AudioSegment
from pydub.silence import split_on_silence

path = sys.argv[1]
audio = AudioSegment.from_wav(path)
print("duration_ms=", len(audio), "  = %.2fs" % (len(audio)/1000.0))
print("channels=", audio.channels, "rate=", audio.frame_rate, "db=", audio.dBFS)

# 静音检测分段：低于 -40dBFS 且持续 >= 350ms 视为段间静音
segs = split_on_silence(audio, min_silence_len=350, silence_thresh=-40, keep_silence=250)
print("segments=", len(segs))
total = 0
for i, s in enumerate(segs):
    start = total/1000.0
    dur = len(s)/1000.0
    print("%02d  start=%7.2fs  dur=%6.2fs  peak=%.1fdB" % (i, start, dur, s.max_dBFS))
    total += len(s)
