import os, sys
os.environ['HF_HUB_OFFLINE']='1'
os.environ['HF_HUB_DISABLE_PROGRESS_BARS']='1'
from faster_whisper import WhisperModel
MODEL = r'C:/Users/16857/.cache/huggingface/hub/models--Systran--faster-whisper-base/snapshots/v0'
m = WhisperModel(MODEL, device='cpu', compute_type='int8')
path = sys.argv[1]
segs, info = m.transcribe(path, beam_size=5, vad_filter=True,
    vad_parameters={"threshold": 0.4, "min_speech_duration_ms": 250, "min_silence_duration_ms": 200, "speech_pad_ms": 150},
    word_timestamps=True, condition_on_previous_text=False, language=None,
    initial_prompt="aircraft cockpit voice warning, altitude, sink rate, pull up, bingo fuel, engine fire, flight controls")
print("lang=%s dur=%.2fs" % (info.language, info.duration))
for i, s in enumerate(segs):
    print("%02d  %6.2f-%6.2f  %.2fs  %s" % (i, s.start, s.end, s.end-s.start, s.text.strip()))
