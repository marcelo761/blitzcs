#!/usr/bin/env bash
# Runs a command (usually a Wine program that plays audio) against a PRIVATE audio server that has no access to hardware, so its
# output cannot reach the real speakers. Design, measurements and rules: docs/wine-audio-routing.md.
#
#   scripts/wine/audio-capture.sh [--out capture.wav] [--sink blitz_test] [--tail-ms 600] [--exe name.exe] -- command [args...]
#   scripts/wine/audio-capture.sh --dry-check [--exe name.exe] -- command [args...]   # private server and guard, no recording
#   scripts/wine/audio-capture.sh --legacy-guard ...   # UNSAFE on multi-application desktops: changes the real default sink (see the doc)
#
# Default mode:
#   * a private pipewire + pipewire-pulse pair runs in a new /tmp/blitz-audio.XXXXXX (mode 700) with NO session manager and an empty
#     environment: no ALSA device exists on it (checked before the run); the real default sink and other applications are never touched;
#   * the command gets PULSE_SERVER, PULSE_RUNTIME_PATH, PIPEWIRE_REMOTE and PIPEWIRE_RUNTIME_DIR pointing at the private server, and
#     PULSE_SINK=<null sink>; XDG_RUNTIME_DIR stays the user's (Wine and X need it);
#   * OBSERVE-ONLY cross-check against the real server, polled during the run and after the tail: a NEW sink input there that is named like
#     --exe, or owned by the command's process tree or by a process with this run's environment, makes the run stop with status 4
#     ('audio-capture: stream reached the real audio server').
# Exit status: the command's status; 4 when the real-server cross-check found a stream of the run (or could not be read); 3 when a
#   tool, the real server, the private server, or the recorder is missing or fails; 64 for a usage error.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../.." && pwd)
out="$root/artifacts/audio/capture.wav"; sink=""; tail_ms=600; dry=0; exe_arg=""; legacy=0
while [ $# -gt 0 ]; do case $1 in
  --out) out=$2; shift 2;;
  --sink) sink=$2; shift 2;;
  --tail-ms) tail_ms=$2; shift 2;;
  --dry-check) dry=1; shift;;
  --exe) exe_arg=$2; shift 2;;
  --legacy-guard) legacy=1; shift;;
  -h|--help) sed -n 2,22p "$0"; exit 0;;
  --) shift; break;;
  *) break;;
esac; done
[ $# -gt 0 ] || { echo "usage: $0 [--dry-check] [--out f.wav] [--sink name] [--tail-ms ms] [--exe name.exe] [--legacy-guard] -- command..." >&2; exit 64; }

if [ "$legacy" = 1 ]; then
  # ---- LEGACY guard (unsafe): the null sink becomes the real default sink for the run and other applications' streams are moved into it ----
  echo "audio-capture: --legacy-guard changes the default sink and moves streams of other applications: unsafe on a multi-application desktop" >&2
  [ -n "$sink" ] || sink="blitz_test_$$"
for tool in pactl ps setsid awk; do command -v "$tool" >/dev/null || { echo "audio-capture: $tool not found" >&2; exit 3; }; done
pactl info >/dev/null 2>&1 || { echo "audio-capture: no PulseAudio/PipeWire-pulse server reachable" >&2; exit 3; }
recorder=""
if [ "$dry" = 0 ]; then
  if command -v ffmpeg >/dev/null; then recorder=ffmpeg; elif command -v parecord >/dev/null; then recorder=parecord; elif command -v pw-record >/dev/null; then recorder=pw-record; else echo "audio-capture: none of ffmpeg, parecord, pw-record found" >&2; exit 3; fi
  mkdir -p "$(dirname "$out")"
fi

# basename of the --exe option or of the first *.exe argument, lower case: application.name must equal it (or it without .exe)
exe_full=""; if [ -n "$exe_arg" ]; then exe_full=$(basename "${exe_arg,,}"); else for a in "$@"; do case ${a,,} in *.exe) exe_full=$(basename "${a,,}"); break;; esac; done; fi
exe_bare=${exe_full%.exe}
name_is_exe() { local n=${1,,}; [ -n "$exe_full" ] && { [ "$n" = "$exe_full" ] || [ "$n" = "$exe_bare" ]; }; }

# one line per sink input: id|sink index|application.process.id|application.name
parse_sinkinputs='
function val(   v) { v = $0; sub(/^[^=]*=[ \t]*/, "", v); gsub(/"/, "", v); gsub(/\|/, "/", v); return v }
function emit() { if (id != "") print id "|" sk "|" pid "|" name }
/^Sink Input #/ { emit(); id = substr($3, 2); sk = ""; pid = ""; name = "" }
/^[ \t]*Sink: / { sk = $2 }
/application\.process\.id = / { pid = val() }
/application\.name = / { name = val() }
END { emit() }'
list_streams() { LC_ALL=C pactl list sink-inputs 2>/dev/null | awk "$parse_sinkinputs"; }

declare -A base=() tree=() seen=() kind=() first_sink=() first_pid=() first_name=() last_sink=() last_name=() moved=() attempts=()
module=""; rec_pid=""; cmd_pid=""; prev_default=""; default_changed=0; nullidx=""; ps_fail=0
abort_sink="?"; abort_detail=""

sink_label() {  # sink index -> name ('none' for an unlinked stream)
  case $1 in
    none) echo none;;
    "$nullidx") echo "$sink(null)";;
    *) LC_ALL=C pactl list short sinks 2>/dev/null | awk -v i="$1" '$1 == i { print $2; found = 1 } END { if (!found) print "sink#" i }';;
  esac
}
alive() { local st; st=$(cat "/proc/$1/stat" 2>/dev/null) || return 1; st=${st##*) }; [ "${st%% *}" != Z ]; }
snapshot_tree() {  # the command and its descendants in the current process table, plus those accumulated before
  local known="${!tree[*]}"
  ps -e -o pid= -o ppid= | awk -v root="$cmd_pid" -v known="$known" '
    BEGIN { n = split(known, k, " "); for (i = 1; i <= n; i++) in_set[k[i]] = 1; in_set[root] = 1 }
    { parent[$1] = $2 }
    END { changed = 1; while (changed) { changed = 0; for (p in parent) if (!(p in in_set) && (parent[p] in in_set)) { in_set[p] = 1; changed = 1 } }
          for (p in in_set) print p }'
}
# Wine re-parents its client processes (to systemd --user), so the process tree alone misses them. They do inherit the command's environment, and PULSE_SINK
# holds this run's unique sink name: a process whose environment has that exact PULSE_SINK entry was started by this run.
owned_by_run() {  # pid: in the command's tree, or started with this run's PULSE_SINK
  [ -n "${tree[$1]+x}" ] && return 0
  grep -qzxF -- "PULSE_SINK=$sink" "/proc/$1/environ" 2>/dev/null
}
run_pids() { local f p; for f in /proc/[0-9]*/environ; do grep -qzxF -- "PULSE_SINK=$sink" "$f" 2>/dev/null && { p=${f#/proc/}; echo "${p%/environ}"; }; done; }
kill_command() {  # TERM, then KILL, the command's process group, its known descendants and every process started by this run
  local p
  if [ -n "$cmd_pid" ]; then kill -TERM -- "-$cmd_pid" 2>/dev/null; fi
  for p in $(snapshot_tree) $(run_pids); do kill -TERM "$p" 2>/dev/null; done
  sleep 0.5
  if [ -n "$cmd_pid" ]; then kill -KILL -- "-$cmd_pid" 2>/dev/null; wait "$cmd_pid" 2>/dev/null; cmd_pid=""; fi
  for p in $(snapshot_tree) $(run_pids); do kill -KILL "$p" 2>/dev/null; done
}
abort_for() {  # id name pid sink reason
  abort_sink=$4; abort_detail="stream $1 application='$2' pid=${3:-none} on sink $4: $5"
}

poll() {  # one guard pass; returns 1 when the run must be aborted
  local raw snap id sk pid name p
  for p in $(snapshot_tree); do tree[$p]=1; done      # refresh before listing: a listed stream's owner is then already known
  raw=$(LC_ALL=C pactl list sink-inputs 2>/dev/null); if [ $? -ne 0 ]; then
    ps_fail=$((ps_fail + 1))
    if [ "$ps_fail" -ge 3 ]; then abort_for "-" "-" "" "?" "pactl list sink-inputs failed 3 times: streams can no longer be verified"; return 1; fi
    return 0
  fi
  ps_fail=0
  snap=$(printf '%s\n' "$raw" | awk "$parse_sinkinputs")
  while IFS='|' read -r id sk pid name; do
    [ -n "$id" ] || continue
    [ -n "${base[$id]+x}" ] && continue                # existed before the command started
    if [ -z "${seen[$id]+x}" ]; then seen[$id]=1; first_sink[$id]=${sk:-none}; first_pid[$id]=$pid; first_name[$id]=$name; fi
    last_sink[$id]=${sk:-none}; last_name[$id]=$name
    [ -n "$sk" ] && [ "$sk" != 4294967295 ] || continue   # 4294967295 = not linked to a sink yet
    if [ -z "${kind[$id]+x}" ]; then
      if [ -n "$pid" ] && owned_by_run "$pid"; then kind[$id]=ours
      elif [ -n "$pid" ]; then if name_is_exe "$name"; then kind[$id]=ambiguous; else kind[$id]=foreign; fi
      elif name_is_exe "$name"; then kind[$id]=ours
      else kind[$id]=unattributed; fi
    fi
    [ "$sk" = "$nullidx" ] && continue
    case ${kind[$id]} in
      ours) ;;
      foreign) continue;;
      *) abort_for "$id" "$name" "$pid" "$sk" "${kind[$id]} stream on a real device"; return 1;;
    esac
    if [ "${attempts[$id]:-0}" -ge 3 ]; then abort_for "$id" "$name" "$pid" "$sk" "still on a real device after 3 moves"; return 1; fi
    attempts[$id]=$(( ${attempts[$id]:-0} + 1 ))
    if LC_ALL=C pactl move-sink-input "$id" "$sink" >/dev/null 2>&1; then
      moved[$id]=1; echo "audio-capture: moved stream $id ('$name', pid ${pid:-?}) from sink $sk to $sink" >&2
    else
      abort_for "$id" "$name" "$pid" "$sk" "move-sink-input to $sink failed"; return 1
    fi
  done <<< "$snap"
  return 0
}

ours_total=0; ours_on_real=0
report() {
  echo "audio-capture: stream report (null sink '$sink' = index $nullidx; default sink before the run: ${prev_default:-unknown})" >&2
  if [ "${#seen[@]}" -eq 0 ]; then echo "audio-capture:   no new stream was seen" >&2; return; fi
  local id k mv fs ls
  printf 'audio-capture:   %-7s %-22s %-8s %-9s %-26s %-6s %s\n' id application pid owner "sink at first sight" moved "final sink" >&2
  for id in $(printf '%s\n' "${!seen[@]}" | sort -n); do
    k=${kind[$id]:-unlinked}; ls=${last_sink[$id]}
    if [ "$k" = ours ]; then ours_total=$((ours_total + 1)); [ "$ls" != "$nullidx" ] && [ "$ls" != none ] && ours_on_real=$((ours_on_real + 1)); fi
    mv=${moved[$id]:+yes}; mv=${mv:-no}; [ "$k" = foreign ] && mv=n/a
    printf 'audio-capture:   %-7s %-22s %-8s %-9s %-26s %-6s %s\n' "$id" "${last_name[$id]:-${first_name[$id]}}" "${first_pid[$id]:-none}" "$k" "$(sink_label "${first_sink[$id]}")" "$mv" "$(sink_label "$ls")" >&2
  done
}

cleanup() {
  trap - EXIT INT TERM
  kill_command
  if [ -n "$rec_pid" ] && kill -0 "$rec_pid" 2>/dev/null; then kill -INT "$rec_pid" 2>/dev/null; wait "$rec_pid" 2>/dev/null; fi
  rec_pid=""
  if [ "$default_changed" = 1 ]; then
    if LC_ALL=C pactl set-default-sink "$prev_default" >/dev/null 2>&1; then echo "audio-capture: default sink restored to $prev_default" >&2
    else echo "audio-capture: WARNING: could not restore the default sink to '$prev_default'" >&2; fi
    default_changed=0
  fi
  if [ -n "$module" ]; then pactl unload-module "$module" >/dev/null 2>&1 && echo "audio-capture: unloaded null sink $sink (module $module)" >&2; module=""; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

prev_default=$(LC_ALL=C pactl get-default-sink 2>/dev/null) || prev_default=""
module=$(pactl load-module module-null-sink "sink_name=$sink" "sink_properties=device.description=BlitzCS-capture") || { echo "audio-capture: cannot create the null sink" >&2; module=""; exit 3; }
nullidx=$(LC_ALL=C pactl list short sinks | awk -v s="$sink" '$2 == s { print $1; exit }')
[ -n "$nullidx" ] || { echo "audio-capture: null sink '$sink' has no index" >&2; exit 3; }
if [ -n "$prev_default" ]; then
  LC_ALL=C pactl set-default-sink "$sink" >/dev/null 2>&1 || { echo "audio-capture: cannot make '$sink' the default sink" >&2; exit 3; }
  default_changed=1
fi
echo "audio-capture: null sink '$sink' = module $module (index $nullidx); default sink $prev_default -> $sink for the run" >&2

if [ "$dry" = 0 ]; then
  rm -f "$out"
  if [ "$recorder" = ffmpeg ]; then ffmpeg -nostdin -loglevel error -y -f pulse -i "$sink.monitor" -ar 44100 -ac 2 -acodec pcm_s16le "$out" & rec_pid=$!
  elif [ "$recorder" = parecord ]; then parecord -d "$sink.monitor" --file-format=wav --rate=44100 --channels=2 "$out" & rec_pid=$!
  else pw-record --target "$sink" -P stream.capture.sink=true --rate 44100 --channels 2 "$out" & rec_pid=$!; fi
  sleep 0.5
  if ! kill -0 "$rec_pid" 2>/dev/null; then echo "audio-capture: the recorder did not start" >&2; rec_pid=""; exit 3; fi
fi

while IFS='|' read -r id _ _ _; do [ -n "$id" ] && base[$id]=1; done < <(list_streams)

# the command gets its own session (pgid = pid), so an abort can kill all of it; PULSE_SINK names the null sink for it only
PULSE_SINK="$sink" setsid "$@" &
cmd_pid=$!
status=0; aborted=0
while alive "$cmd_pid"; do
  if ! poll; then aborted=1; break; fi
  sleep 0.05
done
if [ "$aborted" = 0 ]; then
  wait "$cmd_pid"; status=$?; cmd_pid=""
  for ((i = 0; i * 50 < tail_ms; i++)); do   # the last buffers drain out of the sink; still guarded
    if ! poll; then aborted=1; break; fi
    sleep 0.05
  done
fi
if [ "$aborted" = 1 ]; then
  kill_command
  status=4
  echo "audio-capture: a stream reached a real device (sink $abort_sink); aborted" >&2
  echo "audio-capture:   $abort_detail" >&2
fi
cleanup
report
if [ "$dry" = 1 ]; then
  if [ "$aborted" = 0 ] && [ "$ours_total" -gt 0 ] && [ "$ours_on_real" = 0 ]; then echo "audio-capture: dry-check: routing verified ($ours_total stream(s) on the null sink)" >&2
  elif [ "$aborted" = 0 ] && [ "$ours_total" = 0 ]; then echo "audio-capture: dry-check: no stream of the command was seen; routing NOT verified" >&2
  else echo "audio-capture: dry-check: routing NOT verified" >&2; fi
elif [ -s "$out" ]; then echo "audio-capture: wrote $out ($(stat -c %s "$out") bytes)" >&2; else echo "audio-capture: no audio was recorded" >&2; fi
exit $status
fi

# ===== DEFAULT MODE: private audio server (no hardware, no session manager) =====
sink=${sink:-blitz_test}
for tool in pactl pipewire pipewire-pulse ps awk setsid env mktemp; do command -v "$tool" >/dev/null || { echo "audio-capture: $tool not found" >&2; exit 3; }; done
recorder=""
if [ "$dry" = 0 ]; then
  command -v ffmpeg >/dev/null || { echo "audio-capture: ffmpeg not found" >&2; exit 3; }
  recorder=ffmpeg
  mkdir -p "$(dirname "$out")"
fi
# the real server is only observed: it must be readable for the cross-check
env -u PULSE_SERVER -u PULSE_RUNTIME_PATH LC_ALL=C pactl info >/dev/null 2>&1 || { echo "audio-capture: no reachable real audio server (the cross-check needs it)" >&2; exit 3; }

# basename of the --exe option or of the first *.exe argument, lower case: application.name must equal it (or it without .exe)
exe_full=""; if [ -n "$exe_arg" ]; then exe_full=$(basename "${exe_arg,,}"); else for a in "$@"; do case ${a,,} in *.exe) exe_full=$(basename "${a,,}"); break;; esac; done; fi
exe_bare=${exe_full%.exe}
name_is_exe() { local n=${1,,}; [ -n "$exe_full" ] && { [ "$n" = "$exe_full" ] || [ "$n" = "$exe_bare" ]; }; }

# one line per sink input: id|sink index|application.process.id|application.name
sinkinput_awk='
function val(   v) { v = $0; sub(/^[^=]*=[ \t]*/, "", v); gsub(/"/, "", v); gsub(/\|/, "/", v); return v }
function emit() { if (id != "") print id "|" sk "|" pid "|" name }
/^Sink Input #/ { emit(); id = substr($3, 2); sk = ""; pid = ""; name = "" }
/^[ \t]*Sink: / { sk = $2 }
/application\.process\.id = / { pid = val() }
/application\.name = / { name = val() }
END { emit() }'

real_pa() { env -u PULSE_SERVER -u PULSE_RUNTIME_PATH LC_ALL=C pactl "$@"; }   # the user's real server (observed only)
priv=""; srv_pw=""; srv_pwp=""; rec_pid=""; cmd_pid=""; nullidx=""; status=0; aborted=0; abort_msg=""; real_fail=0
declare -A real_base=() real_seen=() priv_first=() priv_pid=() priv_name=() priv_last=()

priv_pa() { env -u PULSE_RUNTIME_PATH PULSE_SERVER="unix:$priv/pulse/native" LC_ALL=C pactl "$@"; }
priv_env() { env -i PATH="$PATH" HOME="$priv/home" LC_ALL=C XDG_RUNTIME_DIR="$priv" PIPEWIRE_RUNTIME_DIR="$priv" PIPEWIRE_REMOTE="$priv/pipewire-0" \
  PULSE_RUNTIME_PATH="$priv/pulse" PULSE_SERVER="unix:$priv/pulse/native" "$@"; }
alive() { local st; st=$(cat "/proc/$1/stat" 2>/dev/null) || return 1; st=${st##*) }; [ "${st%% *}" != Z ]; }

start_private() {  # private runtime dir, pipewire, pipewire-pulse: no session manager, empty environment, no D-Bus session
  priv=$(mktemp -d /tmp/blitz-audio.XXXXXX) || return 1
  chmod 700 "$priv" && mkdir -m 700 "$priv/home" "$priv/pulse" "$priv/config" "$priv/state" "$priv/cache" "$priv/data" || return 1
  local -a e=(env -i "PATH=$PATH" "HOME=$priv/home" "LC_ALL=C" "XDG_RUNTIME_DIR=$priv" "PIPEWIRE_RUNTIME_DIR=$priv" "PULSE_RUNTIME_PATH=$priv/pulse"
              "XDG_CONFIG_HOME=$priv/config" "XDG_STATE_HOME=$priv/state" "XDG_CACHE_HOME=$priv/cache" "XDG_DATA_HOME=$priv/data"
              "DBUS_SESSION_BUS_ADDRESS=unix:path=$priv/no-bus")
  "${e[@]}" pipewire >"$priv/pipewire.log" 2>&1 & srv_pw=$!
  local t=0
  while [ ! -S "$priv/pipewire-0" ]; do t=$((t + 1)); [ "$t" -ge 200 ] && return 1; kill -0 "$srv_pw" 2>/dev/null || return 1; sleep 0.05; done
  "${e[@]}" pipewire-pulse >"$priv/pipewire-pulse.log" 2>&1 & srv_pwp=$!
  t=0
  until priv_pa info >/dev/null 2>&1; do t=$((t + 1)); [ "$t" -ge 200 ] && return 1; kill -0 "$srv_pwp" 2>/dev/null || return 1; sleep 0.05; done
  return 0
}
verify_no_hardware() {  # the private server must list no card, and no sink or source except its own null sink and the built-in null sink
  [ "$(priv_pa list short cards 2>/dev/null | wc -l)" = 0 ] || return 1
  [ "$(priv_pa list short sinks 2>/dev/null | awk '$2 !~ /^(blitz_test|auto_null)$/' | wc -l)" = 0 ] || return 1
  [ "$(priv_pa list short sources 2>/dev/null | awk '$2 !~ /^(blitz_test\.monitor|auto_null\.monitor)$/' | wc -l)" = 0 ] || return 1
  if command -v pw-cli >/dev/null; then   # no ALSA device object in the PipeWire graph either
    [ "$(priv_env pw-cli ls Device 2>/dev/null | grep -c -i alsa)" = 0 ] || return 1
  fi
  return 0
}
sink_label() { LC_ALL=C pactl list short sinks 2>/dev/null | awk -v i="$1" '$1 == i { print $2; found = 1 } END { if (!found) print "unlinked" }'; }
snapshot_tree() {  # the command and its descendants (accumulated)
  local known="${!tree[*]}"
  ps -e -o pid= -o ppid= | awk -v root="$cmd_pid" -v known="$known" '
    BEGIN { n = split(known, k, " "); for (i = 1; i <= n; i++) in_set[k[i]] = 1; in_set[root] = 1 }
    { parent[$1] = $2 }
    END { changed = 1; while (changed) { changed = 0; for (p in parent) if (!(p in in_set) && (parent[p] in in_set)) { in_set[p] = 1; changed = 1 } }
          for (p in in_set) print p }'
}
declare -A tree=()
# a process started by this run: it carries this run's private PULSE_RUNTIME_PATH (Wine re-parents its clients, so the tree is not enough)
run_marked() { grep -qzxF -- "PULSE_RUNTIME_PATH=$priv/pulse" "/proc/$1/environ" 2>/dev/null; }
run_pids() { local f p; for f in /proc/[0-9]*/environ; do grep -qzxF -- "PULSE_RUNTIME_PATH=$priv/pulse" "$f" 2>/dev/null && { p=${f#/proc/}; echo "${p%/environ}"; }; done; }
run_owned() { [ -n "${tree[$1]+x}" ] && return 0; run_marked "$1"; }
kill_run() {  # TERM, then KILL: the command's group, its descendants and every process started with this run's environment
  local p
  [ -n "$cmd_pid" ] && kill -TERM -- "-$cmd_pid" 2>/dev/null
  for p in $(snapshot_tree) $(run_pids); do kill -TERM "$p" 2>/dev/null; done
  sleep 0.5
  if [ -n "$cmd_pid" ]; then kill -KILL -- "-$cmd_pid" 2>/dev/null; wait "$cmd_pid" 2>/dev/null; cmd_pid=""; fi
  for p in $(snapshot_tree) $(run_pids); do kill -KILL "$p" 2>/dev/null; done
}

# one pass: record the private server's streams (all of them belong to this run) and check the real server for new streams of this run
poll_once() {
  local raw id sk pid name p
  for p in $(snapshot_tree); do tree[$p]=1; done
  raw=$(priv_pa list sink-inputs 2>/dev/null) || raw=""
  while IFS='|' read -r id sk pid name; do
    [ -n "$id" ] || continue
    if [ -z "${priv_first[$id]+x}" ]; then priv_first[$id]=${sk:-none}; priv_pid[$id]=$pid; priv_name[$id]=$name; fi
    priv_last[$id]=${sk:-none}
  done < <(printf '%s\n' "$raw" | awk "$sinkinput_awk")
  if ! raw=$(real_pa list sink-inputs 2>/dev/null); then
    real_fail=$((real_fail + 1)); if [ "$real_fail" -ge 3 ]; then aborted=1; abort_msg="the real server could not be listed 3 times: the run cannot be verified"; return 1; fi
    return 0
  fi
  real_fail=0
  while IFS='|' read -r id sk pid name; do
    [ -n "$id" ] || continue
    [ -n "${real_base[$id]+x}" ] && continue          # existed before the run
    [ -n "${real_seen[$id]+x}" ] && continue
    real_seen[$id]=1
    if name_is_exe "$name" || { [ -n "$pid" ] && run_owned "$pid"; }; then
      aborted=1; abort_msg="stream $id application='$name' pid=${pid:-none} appeared on the real server (sink ${sk:-?})"; return 1
    fi
  done < <(printf '%s\n' "$raw" | awk "$sinkinput_awk")
  return 0
}

# one report of the private server's streams (all of them belong to this run); printed once, also when the run is interrupted
reported=0
report_streams() {
  [ "$reported" = 1 ] && return; reported=1
  echo "audio-capture: private server streams of this run (null sink '$sink' = index ${nullidx:-?})" >&2
  if [ "${#priv_first[@]}" -eq 0 ]; then echo "audio-capture:   none was seen" >&2; return; fi
  printf 'audio-capture:   %-7s %-22s %-8s %-26s %s\n' id application pid "sink at first sight" "final sink" >&2
  local id
  for id in $(printf '%s\n' "${!priv_first[@]}" | sort -n); do
    printf 'audio-capture:   %-7s %-22s %-8s %-26s %s\n' "$id" "${priv_name[$id]}" "${priv_pid[$id]:-none}" \
      "$( [ "${priv_first[$id]}" = none ] && echo unlinked || sink_label "${priv_first[$id]}")" \
      "$( [ "${priv_last[$id]}" = none ] && echo unlinked || sink_label "${priv_last[$id]}")" >&2
  done
}
cleanup() {
  trap - EXIT INT TERM
  kill_run
  report_streams
  if [ -n "$rec_pid" ] && kill -0 "$rec_pid" 2>/dev/null; then kill -INT "$rec_pid" 2>/dev/null; wait "$rec_pid" 2>/dev/null; fi
  rec_pid=""
  for p in "$srv_pwp" "$srv_pw"; do [ -n "$p" ] && kill -TERM "$p" 2>/dev/null; done
  for p in "$srv_pwp" "$srv_pw"; do [ -n "$p" ] && { wait "$p" 2>/dev/null; kill -KILL "$p" 2>/dev/null; }; done
  if [ -n "$priv" ]; then case $priv in /tmp/blitz-audio.*) rm -rf -- "$priv" ;; esac; echo "audio-capture: private audio server stopped, $priv removed" >&2; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Wine: with the default driver list, a server that cannot be reached makes Wine fall back to ALSA, which is the real hardware. Only Audio=pulse
# (winepulse, which uses PULSE_SERVER alone) removes that path, so a Wine command needs it in its prefix.
case $1 in wine|wine64|*/wine|*/wine64)
  reg="${WINEPREFIX:-$HOME/.wine}/user.reg"
  if ! awk 'index($0, "[Software\\\\Wine\\\\Drivers]") == 1 { f = 1; next } /^\[/ { f = 0 } f && /^"Audio"="pulse"/ { ok = 1 } END { exit !ok }' "$reg" 2>/dev/null; then
    echo "audio-capture: the Wine prefix '$reg' lacks Audio=pulse (HKCU\\Software\\Wine\\Drivers); set it with: BLITZCS_WINE_AUDIO_DRIVER=pulse . scripts/wine/env.sh" >&2
    exit 3
  fi;;
esac

if ! start_private; then
  echo "audio-capture: the private audio server did not start within 10 s" >&2
  for l in "$priv"/*.log; do [ -f "$l" ] && tail -n 3 "$l" >&2; done
  exit 3
fi
verify_no_hardware || { echo "audio-capture: the private server exposes a card, sink or source other than its own null sink; refusing to run" >&2; exit 3; }
mod=$(priv_pa load-module module-null-sink "sink_name=$sink" "sink_properties=device.description=BlitzCS-capture") || { echo "audio-capture: cannot load the null sink on the private server" >&2; exit 3; }
nullidx=$(priv_pa list short sinks | awk -v s="$sink" '$2 == s { print $1; exit }')
[ -n "$nullidx" ] || { echo "audio-capture: null sink '$sink' has no index on the private server" >&2; exit 3; }
# without a session manager the private server has no 'default' metadata, so no default sink can be set there: the command gets PULSE_SINK
echo "audio-capture: private server: null sink '$sink' (index $nullidx, module $mod); the command uses PULSE_SINK=$sink" >&2

if [ "$dry" = 0 ]; then
  rm -f "$out"
  priv_env ffmpeg -nostdin -loglevel error -y -f pulse -i "$sink.monitor" -ar 44100 -ac 2 -acodec pcm_s16le "$out" & rec_pid=$!
  sleep 0.5
  if ! kill -0 "$rec_pid" 2>/dev/null; then echo "audio-capture: the recorder did not start (no '$sink.monitor' on the private server)" >&2; rec_pid=""; exit 3; fi
fi

while IFS='|' read -r id _ _ _; do [ -n "$id" ] && real_base[$id]=1; done < <(real_pa list sink-inputs 2>/dev/null | awk "$sinkinput_awk")
# the command gets its own session (pgid = pid) so an abort can kill all of it; the audio variables name the private server for it only
setsid env "PULSE_SERVER=unix:$priv/pulse/native" "PULSE_RUNTIME_PATH=$priv/pulse" "PULSE_SINK=$sink" \
  "PIPEWIRE_REMOTE=$priv/pipewire-0" "PIPEWIRE_RUNTIME_DIR=$priv" "$@" &
cmd_pid=$!
while alive "$cmd_pid"; do
  if ! poll_once; then break; fi
  sleep 0.05
done
if [ "$aborted" = 0 ]; then
  wait "$cmd_pid"; status=$?; cmd_pid=""
  for ((i = 0; i * 50 < tail_ms; i++)); do   # the last buffers drain out of the server; still checked
    if ! poll_once; then break; fi
    sleep 0.05
  done
fi
if [ "$aborted" = 1 ]; then
  kill_run
  status=4
  echo "audio-capture: stream reached the real audio server; aborted" >&2
  echo "audio-capture:   $abort_msg" >&2
fi

# ---- report: every stream of the run was on the private server ----
report_streams
if [ "$dry" = 1 ]; then
  if [ "$aborted" = 0 ] && [ "${#priv_first[@]}" -gt 0 ]; then echo "audio-capture: dry-check: routing verified (${#priv_first[@]} stream(s) on the private server, none on the real server)" >&2
  elif [ "$aborted" = 0 ]; then echo "audio-capture: dry-check: no stream of the command was seen on the private server; routing NOT verified" >&2
  else echo "audio-capture: dry-check: routing NOT verified" >&2; fi
elif [ -s "$out" ]; then echo "audio-capture: wrote $out ($(stat -c %s "$out") bytes)" >&2
else echo "audio-capture: no audio was recorded ('$sink.monitor' delivered nothing)" >&2; [ "$status" = 0 ] && status=3; fi
exit $status
