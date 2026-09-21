"""用 faster-whisper 转写一段音频，输出每段开始/结束/文本。"""
import sys, os
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
from faster_whisper import WhisperModel

path = sys.argv[1]
model_size = sys.argv[2] if len(sys.argv) > 2 else "base"
# 中文 + 英文双语；不强制单语（混合视频里既有中文也有英文单词）
model = WhisperModel(model_size, device="cpu", compute_type="int8")
segments, info = model.transcribe(
    path,
    beam_size=5,
    vad_filter=True,
    vad_parameters={"min_silence_duration_ms": 250, "speech_pad_ms": 200},
    word_timestamps=True,
    condition_on_previous_text=False,
    language=None,        # 自动检测
    initial_prompt="飞机驾驶舱语音告警，机内语音。"  # 提示词帮助模型
)
print("detected_lang=%s duration=%.2fs" % (info.language, info.duration))
for i, s in enumerate(segments):
    print("%02d  %6.2fs-%6.2fs  dur=%5.2fs  [%.2f]  %s" % (
        i, s.start, s.end, s.end-s.start,
        getattr(s, 'avg_logprob', 0), s.text.strip()
    ))
