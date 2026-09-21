import os, sys, json
os.environ['HF_HUB_OFFLINE']='1'
os.environ['HF_HUB_DISABLE_PROGRESS_BARS']='1'
from faster_whisper import WhisperModel

MODEL = r'C:/Users/16857/.cache/huggingface/hub/models--Systran--faster-whisper-base/snapshots/v0'
m = WhisperModel(MODEL, device='cpu', compute_type='int8')
for path in sys.argv[1:]:
    print("="*60)
    print("FILE:", os.path.basename(path))
    segs, info = m.transcribe(
        path, beam_size=5, vad_filter=False,
        word_timestamps=True, condition_on_previous_text=False,
        language=None, initial_prompt='飞机驾驶舱语音告警，机内语音提示。')
    print("lang=%s dur=%.2fs" % (info.language, info.duration))
    for i, s in enumerate(segs):
        print("-- SEG %02d  %.2f-%.2f  %.2fs --" % (i, s.start, s.end, s.end-s.start))
        print("   TEXT:", s.text.strip())
        if s.words:
            for w in s.words:
                print("   %6.2f-%6.2f  %s" % (w.start, w.end, w.word))
