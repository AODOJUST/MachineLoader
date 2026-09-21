import os, sys, glob
os.environ['HF_HUB_OFFLINE']='1'
os.environ['HF_HUB_DISABLE_PROGRESS_BARS']='1'
from faster_whisper import WhisperModel
MODEL = r'C:/Users/16857/.cache/huggingface/hub/models--Systran--faster-whisper-base/snapshots/v0'
m = WhisperModel(MODEL, device='cpu', compute_type='int8')
for path in sorted(glob.glob(sys.argv[1])):
    name = os.path.basename(path)
    segs, info = m.transcribe(path, beam_size=5, vad_filter=False,
        condition_on_previous_text=False, language=None)
    txt = " / ".join(s.text.strip() for s in segs)
    print("%-14s [%s] %s" % (name, info.language, txt))
