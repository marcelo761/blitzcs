#!/usr/bin/env bash
# Runs a command with its PulseAudio output routed into a private NULL SINK, records the sink's monitor to a WAV file and tears the sink down again.
# Nothing is played on real speakers: PULSE_SINK=<null sink> is set for the command only.
#
#   scripts/wine/audio-capture.sh [--out capture.wav] [--sink blitz_test] [--tail-ms 600] -- command [args...]
#
# Example (the opt-in HostWinmm tone test under Wine, then analyse what left the process):
#   scripts/wine/audio-capture.sh --out artifacts/audio/tone.wav -- env BLITZCS_AUDIO_TONE_TEST=1 \
#       scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Win32.Tests --filter WaveToneTests
#   python3 tools/audio/analyze_wav.py artifacts/audio/tone.wav --expect-freq 440
#
# The sink module is unloaded and the recorder stopped by an EXIT/INT/TERM trap, so nothing leaks when the command fails or is interrupted.
# Needs pactl and one of ffmpeg / parecord / pw-record (PipeWire's pulse server is fine). ffmpeg is preferred: it finalises the WAV on SIGINT, whereas parecord drops its last
# 64 KiB block. Exit status: the command's, or 3 when no sound server/recorder is available.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../.." && pwd)
out="$root/artifacts/audio/capture.wav"; sink="blitz_test_$$"; tail_ms=600
while [ $# -gt 0 ]; do case $1 in
  --out) out=$2; shift 2;;
  --sink) sink=$2; shift 2;;
  --tail-ms) tail_ms=$2; shift 2;;
  -h|--help) sed -n 2,12p "$0"; exit 0;;
  --) shift; break;;
  *) break;;
esac; done
[ $# -gt 0 ] || { echo "usage: $0 [--out f.wav] [--sink name] -- command..." >&2; exit 64; }
command -v pactl >/dev/null || { echo "audio-capture: pactl not found" >&2; exit 3; }
pactl info >/dev/null 2>&1 || { echo "audio-capture: no PulseAudio/PipeWire-pulse server reachable" >&2; exit 3; }
if command -v ffmpeg >/dev/null; then recorder=ffmpeg; elif command -v parecord >/dev/null; then recorder=parecord; elif command -v pw-record >/dev/null; then recorder=pw-record; else echo "audio-capture: none of ffmpeg, parecord, pw-record found" >&2; exit 3; fi
mkdir -p "$(dirname "$out")"

module=""; rec_pid=""
cleanup() {
  trap - EXIT INT TERM
  if [ -n "$rec_pid" ] && kill -0 "$rec_pid" 2>/dev/null; then kill -INT "$rec_pid" 2>/dev/null; wait "$rec_pid" 2>/dev/null; fi
  if [ -n "$module" ]; then pactl unload-module "$module" >/dev/null 2>&1 && echo "audio-capture: unloaded null sink $sink (module $module)" >&2; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

module=$(pactl load-module module-null-sink "sink_name=$sink" "sink_properties=device.description=BlitzCS-capture") || { echo "audio-capture: cannot create the null sink" >&2; exit 3; }
echo "audio-capture: null sink '$sink' = module $module; recording '$sink.monitor' to $out with $recorder" >&2

rm -f "$out"
if [ "$recorder" = ffmpeg ]; then ffmpeg -nostdin -loglevel error -y -f pulse -i "$sink.monitor" -ar 44100 -ac 2 -acodec pcm_s16le "$out" & rec_pid=$!
elif [ "$recorder" = parecord ]; then parecord -d "$sink.monitor" --file-format=wav --rate=44100 --channels=2 "$out" & rec_pid=$!
else pw-record --target "$sink" -P stream.capture.sink=true --rate 44100 --channels 2 "$out" & rec_pid=$!; fi
sleep 0.5
if ! kill -0 "$rec_pid" 2>/dev/null; then echo "audio-capture: the recorder did not start" >&2; exit 3; fi

PULSE_SINK="$sink" "$@"
status=$?
sleep "$(awk "BEGIN{print $tail_ms/1000}")"   # let the last buffers drain out of the sink
cleanup
if [ -s "$out" ]; then echo "audio-capture: wrote $out ($(stat -c %s "$out") bytes)" >&2; else echo "audio-capture: no audio was recorded" >&2; fi
exit $status
