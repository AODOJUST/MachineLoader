#!/usr/bin/env python3
# Mimic AamWavUtil.Decode's parsing to confirm our flare.wav will load in-game.
import struct

def check(path):
    with open(path, 'rb') as f:
        b = f.read()
    if len(b) < 44:
        return "TOO_SHORT"
    if b[0:4] != b'RIFF' or b[8:12] != b'WAVE':
        return "NOT_WAVE"
    pos = 12
    channels = 2; sampleRate = 44100; bits = 16; dataOff = -1; dataLen = 0
    while pos + 8 <= len(b):
        cid = b[pos:pos+4]
        size = struct.unpack('<i', b[pos+4:pos+8])[0]
        if cid == b'fmt ':
            fmt = struct.unpack('<h', b[pos+8:pos+10])[0]
            if fmt != 1:
                return "FMT_NOT_PCM(%d)" % fmt
            channels = struct.unpack('<h', b[pos+10:pos+12])[0]
            sampleRate = struct.unpack('<i', b[pos+12:pos+16])[0]
            bits = struct.unpack('<h', b[pos+22:pos+24])[0]
        elif cid == b'data':
            dataOff = pos + 8
            dataLen = size
            break
        pos += 8 + size + (size % 2)
        if size <= 0:
            break
    if dataOff < 0:
        return "NO_DATA"
    bps = bits // 8
    samples = dataLen // bps // channels
    if samples <= 0 or channels <= 0 or channels > 8:
        return "BAD_SAMPLES"
    return "OK channels=%d sampleRate=%d bits=%d samples=%d dur=%.3fs" % (
        channels, sampleRate, bits, samples, samples / sampleRate)

print(check(r"D:\豆包的下载\Aviassembly_DEV\mods\MachineAAM\audio\flare.wav"))
