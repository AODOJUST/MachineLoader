import sys, subprocess
FFMPEG = r"C:/Users/16857/.workbuddy/binaries/python/envs/audio/Lib/site-packages/imageio_ffmpeg/binaries/ffmpeg-win-x86_64-v7.1.exe"
video, outdir = sys.argv[1], sys.argv[2]
times = [float(t) for t in sys.argv[3:]]
import os
os.makedirs(outdir, exist_ok=True)
for i, t in enumerate(times):
    out = os.path.join(outdir, "f%02d_%.2fs.jpg" % (i, t))
    cmd = [FFMPEG, "-y", "-hide_banner", "-loglevel", "error",
           "-ss", "%.2f" % t, "-i", video, "-frames:v", "1", "-q:v", "2", out]
    subprocess.run(cmd, check=True)
    print("written", out)
