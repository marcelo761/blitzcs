#!/usr/bin/env python3
"""Analyse a PCM WAV file (stdlib only): duration, RMS, peak, silence ratio and the dominant frequency.

    analyze_wav.py capture.wav [--expect-freq 440 --freq-tolerance 5 --min-rms-dbfs -30 --min-seconds 1.0 --json]

The dominant frequency is found on the loudest analysis window (up to 65536 frames of the channel mix) with a Hann-windowed radix-2 FFT plus
parabolic peak interpolation; a Goertzel scan around the peak and a zero-crossing estimate are reported as cross-checks. Exit status: 0 ok, 1 an
--expect-* check failed, 2 unreadable input. Supports 8/16/24/32-bit integer PCM.
"""
import argparse
import cmath
import json
import math
import struct
import sys
import wave


def read_samples(path):
    with wave.open(path, "rb") as w:
        ch, width, rate, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)
    count = len(raw) // width
    if width == 1:
        data = [(b - 128) / 128.0 for b in raw]
    elif width == 2:
        data = [v / 32768.0 for v in struct.unpack("<%dh" % count, raw[: count * 2])]
    elif width == 3:
        data = []
        for i in range(0, count * 3, 3):
            v = int.from_bytes(raw[i : i + 3], "little", signed=True)
            data.append(v / 8388608.0)
    elif width == 4:
        data = [v / 2147483648.0 for v in struct.unpack("<%di" % count, raw[: count * 4])]
    else:
        raise ValueError("unsupported sample width %d" % width)
    mono = [sum(data[i : i + ch]) / ch for i in range(0, len(data) - ch + 1, ch)] if ch > 1 else data
    return mono, ch, rate, width


def fft(x):
    n = len(x)
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j ^= bit
        if i < j:
            x[i], x[j] = x[j], x[i]
    length = 2
    while length <= n:
        step = cmath.exp(-2j * math.pi / length)
        for start in range(0, n, length):
            w = 1.0
            half = length // 2
            for k in range(half):
                a, b = x[start + k], x[start + k + half] * w
                x[start + k], x[start + k + half] = a + b, a - b
                w *= step
        length <<= 1


def goertzel(samples, rate, freq):
    k = 2 * math.cos(2 * math.pi * freq / rate)
    s1 = s2 = 0.0
    for v in samples:
        s1, s2 = v + k * s1 - s2, s1
    power = s1 * s1 + s2 * s2 - k * s1 * s2
    return math.sqrt(max(power, 0.0)) * 2 / len(samples)


def loudest_window(mono, size):
    if len(mono) <= size:
        return mono
    hop = size // 2
    best, best_e = 0, -1.0
    for start in range(0, len(mono) - size + 1, hop):
        e = sum(v * v for v in mono[start : start + size : 8])
        if e > best_e:
            best, best_e = start, e
    return mono[best : best + size]


def dominant_frequency(mono, rate):
    size = 1
    while size * 2 <= min(len(mono), 65536):
        size *= 2
    if size < 64:
        return None
    win = loudest_window(mono, size)
    mean = sum(win) / len(win)
    buf = [complex((v - mean) * (0.5 - 0.5 * math.cos(2 * math.pi * i / (size - 1))), 0) for i, v in enumerate(win)]
    fft(buf)
    mags = [abs(buf[i]) for i in range(size // 2)]
    peak = max(range(1, len(mags) - 1), key=lambda i: mags[i])
    a, b, c = (math.log(mags[peak + d] + 1e-30) for d in (-1, 0, 1))
    denom = a - 2 * b + c
    shift = 0.5 * (a - c) / denom if denom else 0.0
    return (peak + shift) * rate / size, win


def zero_crossing_frequency(mono, rate):
    if len(mono) < 2:
        return None
    crossings = [i for i in range(1, len(mono)) if (mono[i - 1] < 0) != (mono[i] < 0)]
    if len(crossings) < 3:
        return None
    return (len(crossings) - 1) / 2.0 * rate / (crossings[-1] - crossings[0])


def analyse(path):
    mono, ch, rate, width = read_samples(path)
    n = len(mono)
    result = {"file": path, "channels": ch, "sample_rate": rate, "bits": width * 8, "frames": n, "duration_s": n / rate if rate else 0.0}
    if n == 0:
        result.update(rms_dbfs=None, peak=0.0, peak_dbfs=None, silence_ratio=1.0, dominant_hz=None)
        return result
    rms = math.sqrt(sum(v * v for v in mono) / n)
    peak = max(abs(v) for v in mono)
    block = max(1, rate // 100)  # 10 ms blocks; silent = block peak below -50 dBFS
    blocks = [mono[i : i + block] for i in range(0, n, block)]
    silent = sum(1 for b in blocks if max(abs(v) for v in b) < 10 ** (-50 / 20))
    result.update(rms_dbfs=20 * math.log10(rms) if rms > 0 else None, peak=peak, peak_dbfs=20 * math.log10(peak) if peak > 0 else None, silence_ratio=silent / len(blocks))
    found = dominant_frequency(mono, rate)
    if found:
        freq, win = found
        result["dominant_hz"] = freq
        result["goertzel_at_dominant"] = goertzel(win, rate, freq)
        result["goertzel_amplitude_ratio_vs_1.5x"] = goertzel(win, rate, freq) / max(goertzel(win, rate, freq * 1.5), 1e-12)
        result["zero_crossing_hz"] = zero_crossing_frequency(win, rate)
    else:
        result["dominant_hz"] = None
    return result


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("wav")
    p.add_argument("--expect-freq", type=float)
    p.add_argument("--freq-tolerance", type=float, default=5.0, help="Hz (default 5)")
    p.add_argument("--min-rms-dbfs", type=float, help="fail when quieter than this")
    p.add_argument("--min-seconds", type=float, help="fail when shorter than this (including silence)")
    p.add_argument("--json", action="store_true")
    a = p.parse_args(argv)
    try:
        r = analyse(a.wav)
    except (OSError, wave.Error, ValueError, EOFError) as e:
        print("analyze_wav: cannot read %s: %s" % (a.wav, e), file=sys.stderr)
        return 2
    failures = []
    if a.expect_freq is not None and (r["dominant_hz"] is None or abs(r["dominant_hz"] - a.expect_freq) > a.freq_tolerance):
        failures.append("dominant frequency %s Hz, expected %.1f +/- %.1f" % (r["dominant_hz"], a.expect_freq, a.freq_tolerance))
    if a.min_rms_dbfs is not None and (r["rms_dbfs"] is None or r["rms_dbfs"] < a.min_rms_dbfs):
        failures.append("RMS %s dBFS is below %.1f" % (r["rms_dbfs"], a.min_rms_dbfs))
    if a.min_seconds is not None and r["duration_s"] < a.min_seconds:
        failures.append("duration %.3f s is below %.3f" % (r["duration_s"], a.min_seconds))
    r["failures"] = failures
    if a.json:
        print(json.dumps(r, indent=2))
    else:
        f = lambda v, fmt: "n/a" if v is None else fmt % v
        print("file        %s" % r["file"])
        print("format      %d ch, %d Hz, %d-bit, %d frames" % (r["channels"], r["sample_rate"], r["bits"], r["frames"]))
        print("duration    %.3f s" % r["duration_s"])
        print("rms         %s dBFS" % f(r["rms_dbfs"], "%.2f"))
        print("peak        %.4f (%s dBFS)" % (r["peak"], f(r["peak_dbfs"], "%.2f")))
        print("silence     %.1f %% of 10 ms blocks below -50 dBFS" % (100 * r["silence_ratio"]))
        print("dominant    %s Hz (zero-crossing %s Hz)" % (f(r["dominant_hz"], "%.2f"), f(r.get("zero_crossing_hz"), "%.2f")))
        for m in failures:
            print("FAIL: " + m)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
