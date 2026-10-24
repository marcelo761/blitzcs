#!/usr/bin/env python3
"""Writes assets/tone.wav: a 440 Hz sine, 16-bit mono, 44.1 kHz, 1.0 s (the file main.bb loads)."""
import math, os, struct, wave

os.makedirs("assets", exist_ok=True)
with wave.open("assets/tone.wav", "wb") as wav:
    wav.setnchannels(1)
    wav.setsampwidth(2)
    wav.setframerate(44100)
    wav.writeframes(b"".join(struct.pack("<h", round(0.5 * 32767 * math.sin(2 * math.pi * 440 * i / 44100))) for i in range(44100)))
