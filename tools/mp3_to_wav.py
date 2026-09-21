#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""MP3 -> PCM WAV converter for Machine mod voice packs.

Why this exists
---------------
The VoiceAlerts mod decodes audio itself (VoiceAlerts.WavUtil.Decode). That decoder
handles PCM WAV natively, but its MP3 branch drives UnityWebRequest with a blocking
Thread.Sleep spin, which can never complete on Unity's main thread -> every MP3 clip
fails to load and the alert silently falls back to the English pack.
The shipped English and Chinese voice packs are therefore PCM WAV; any new pack
(e.g. Russian) must be converted before shipping.

The repo's Mp3ToWav.exe relies on the Windows MCI "mpegvideo" device, which is not
available in this headless environment (it prints "converted" but writes no file).
This script uses the pure-Python `miniaudio` decoder instead (no ffmpeg needed).

ASCII-staging caveat
--------------------
miniaudio's decode_file() fails on paths containing non-ASCII characters, while
get_file_info() on the same path succeeds -- an easy trap that looks like "the MP3 is
corrupt". The workaround (verified) is to stage the input through an ASCII temp dir.

Usage
-----
    python tools/mp3_to_wav.py <dir-or-mp3> [...]
    python tools/mp3_to_wav.py mods/VoiceAlerts/audio/ru

For every input .mp3 a sibling .wav is written (same basename). Existing .wav files
are skipped unless --force is given. Originals are never deleted.

Requires: pip install miniaudio   (isolated venv recommended)
"""
import argparse
import os
import shutil
import sys
import tempfile
import wave

try:
    import miniaudio
except ImportError:
    sys.exit("miniaudio not installed. Run: pip install miniaudio")

TEMP_ROOT = os.path.join(tempfile.gettempdir(), "machine_mp3_to_wav")


def _decode_via_ascii_staging(mp3_path, out_wav_path):
    """Decode mp3 -> wav using an ASCII temp dir (non-ASCII paths break miniaudio)."""
    os.makedirs(TEMP_ROOT, exist_ok=True)
    stage_in = os.path.join(TEMP_ROOT, "in.mp3")
    stage_out = os.path.join(TEMP_ROOT, "out.wav")
    shutil.copyfile(mp3_path, stage_in)
    try:
        decoded = miniaudio.decode_file(
            stage_in, output_format=miniaudio.SampleFormat.SIGNED16
        )
    finally:
        if os.path.exists(stage_in):
            os.remove(stage_in)

    frames = decoded.samples
    if len(frames) == 0:
        raise ValueError("decode produced 0 samples")
    if max(abs(s) for s in frames) == 0:
        raise ValueError("decoded audio is pure silence")

    with wave.open(stage_out, "wb") as w:
        w.setnchannels(decoded.nchannels)
        w.setsampwidth(decoded.sample_width)
        w.setframerate(decoded.sample_rate)
        w.writeframes(frames.tobytes())
    shutil.move(stage_out, out_wav_path)
    return decoded


def convert(mp3_path, wav_path, force=False):
    """Decode one MP3 to 16-bit PCM WAV. Returns (ok, message)."""
    if os.path.exists(wav_path) and not force:
        return False, "skip (wav exists, use --force)"
    try:
        decoded = _decode_via_ascii_staging(mp3_path, wav_path)
    except Exception as exc:  # noqa: BLE001 - surface any decoder failure verbatim
        return False, "decode failed: %s" % exc

    # read the written file back so the reported duration is what the game will see
    with wave.open(wav_path, "rb") as w:
        frames_n = w.getnframes()
        rate = w.getframerate()
        ch = w.getnchannels()
        width = w.getsampwidth()
    dur = frames_n / float(rate)
    return True, "ok ch=%d rate=%d bits=%d dur=%.2fs bytes=%d" % (
        ch,
        rate,
        width * 8,
        dur,
        os.path.getsize(wav_path),
    )


def main():
    ap = argparse.ArgumentParser(description="Convert MP3 voice clips to PCM WAV")
    ap.add_argument("paths", nargs="+", help="directories or .mp3 files")
    ap.add_argument("--force", action="store_true", help="overwrite existing .wav")
    args = ap.parse_args()

    targets = []
    for p in args.paths:
        if os.path.isdir(p):
            targets += [
                os.path.join(p, f)
                for f in sorted(os.listdir(p))
                if f.lower().endswith(".mp3")
            ]
        else:
            targets.append(p)
    if not targets:
        sys.exit("no .mp3 inputs found")

    failures = 0
    for src in targets:
        dst = os.path.splitext(src)[0] + ".wav"
        ok, msg = convert(src, dst, args.force)
        print("%-24s -> %-24s %s" % (os.path.basename(src), os.path.basename(dst), msg))
        if not ok and not msg.startswith("skip"):
            failures += 1
    print("done: %d input(s), %d failure(s)" % (len(targets), failures))
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
