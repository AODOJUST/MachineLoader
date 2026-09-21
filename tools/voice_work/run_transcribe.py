import os, sys
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
from faster_whisper import WhisperModel

model = WhisperModel("base", device="cpu", compute_type="int8")
for path in sys.argv[1:]:
    print("="*60)
    print("FILE:", os.path.basename(path))
    segments, info = model.transcribe(
        path, beam_size=5, vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 200, "speech_pad_ms": 150},
        word_timestamps=True, condition_on_previous_text=False, language=None,
        initial_prompt="飞机驾驶舱语音告警，机内语音提示。"
    )
    print("detected_lang=%s duration=%.2fs" % (info.language, info.duration))
    for i, s in enumerate(segments):
        print("%02d  %6.2f-%6.2f  dur=%5.2f  %s" % (i, s.start, s.end, s.end-s.start, s.text.strip()))
