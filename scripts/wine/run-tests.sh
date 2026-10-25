#!/usr/bin/env bash
# Runs xunit v2 test projects as win-x64 under Wine.
#   scripts/wine/run-tests.sh [options] [project ...]
#   project: a directory/name below tests/ (default: BinaryCompiler.Tests, Win32.Tests, Unpack.Tests and Linking.E2E.Tests), e.g. BlitzCS.BinaryCompiler.Tests
# options:  --filter <text>   only tests whose display name contains <text> (repeatable)
#           --threads <n>     parallel test collections (default 1)
#           --config <cfg>    Release (default) or Debug
#           --oracle-dir <d>  directory with prebuilt <name>.oracle.exe files (default: artifacts/x86_oracle; built here on Linux when missing)
#           --no-build        reuse the previously published output
#           --keep            keep the short-path copy under $BLITZCS_WINE_WORK
# Output (published apps, oracle EXEs) goes to artifacts/ (git-ignored). Exit code: 0 all passed, non-zero otherwise.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../.." && pwd)
config=Release; threads=1; build=1; filters=(); projects=(); oracle_dir="$root/artifacts/x86_oracle"
while [ $# -gt 0 ]; do case $1 in
  --filter) filters+=(--filter "$2"); shift 2;;
  --threads) threads=$2; shift 2;;
  --config) config=$2; shift 2;;
  --oracle-dir) oracle_dir=$2; shift 2;;
  --no-build) build=0; shift;;
  --keep) shift;;
  -h|--help) sed -n 2,13p "$0"; exit 0;;
  *) projects+=("$(basename "$1")"); shift;;
esac; done
[ ${#projects[@]} -eq 0 ] && projects=(BlitzCS.BinaryCompiler.Tests BlitzCS.BinaryCompiler.Win32.Tests BlitzCS.BinaryCompiler.Unpack.Tests BlitzCS.Linking.E2E.Tests)

command -v dotnet >/dev/null || { echo "dotnet not found in PATH" >&2; exit 3; }
# shellcheck disable=SC1091
. "$here/env.sh" || exit 3
out="$root/artifacts/wine"; mkdir -p "$out"

# Real-library tests read these variables; a Linux path is handed to the Windows process through Wine's Z: drive (Z:\var\home\...).
# Without them the real-library tests are reported SKIPPED (with the reason) by the runner: warn loudly so a green run is not mistaken for full coverage.
for v in BLITZCS_REAL_PE32_DIR BLITZCS_PACKED_SAMPLE; do
  val="${!v:-}"
  if [ -z "$val" ]; then
    echo "WARNING: $v is not set: real-library tests will be SKIPPED (not passed). Set $v to run them; see docs/wine-testing.md." >&2
  elif [[ "$val" == /* ]]; then
    [ -e "$val" ] || echo "WARNING: $v='$val' does not exist: the real-library tests that need it will be SKIPPED." >&2
    export "$v=Z:${val//\//\\}"
  fi
done

# The vorbisFile decode test compares against ffmpeg's decode of a generated stream; a Windows/Wine process has no ffmpeg, so prepare the stream here.
if [ -z "${BLITZCS_VORBIS_REFERENCE_DIR:-}" ] && [ -n "${BLITZCS_REAL_PE32_DIR:-}" ] && command -v ffmpeg >/dev/null 2>&1 \
   && ffmpeg -hide_banner -encoders 2>/dev/null | grep -q libvorbis; then
  ref="$out/vorbis-reference"; mkdir -p "$ref"
  if [ ! -f "$ref/tone.ogg" ] || [ ! -f "$ref/tone.s16" ]; then
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:duration=0.6:sample_rate=22050" -ac 2 -c:a libvorbis -q:a 3 "$ref/tone.ogg" \
      && ffmpeg -hide_banner -loglevel error -y -i "$ref/tone.ogg" -f s16le -acodec pcm_s16le "$ref/tone.s16" || rm -f "$ref/tone.ogg" "$ref/tone.s16"
  fi
  [ -f "$ref/tone.s16" ] && export BLITZCS_VORBIS_REFERENCE_DIR="Z:${ref//\//\\}"
fi

# The published test folders are copied away from the repository: tell tests that read repository files (samples/...) where it is.
export BLITZCS_REPO_ROOT="Z:${root//\//\\}"

# Prebuilt Windows oracle EXEs (the Windows process cannot run gcc); only the BinaryCompiler.Tests assembly uses them.
if [[ " ${projects[*]} " == *" BlitzCS.BinaryCompiler.Tests "* ]]; then   # the other assemblies do not use the oracle
  if ! ls "$oracle_dir"/*.oracle.exe >/dev/null 2>&1; then
    echo "== building oracle EXEs into $oracle_dir"; ORACLE_EXE=1 "$root/tools/x86_oracle/build_all_exe.sh" "$oracle_dir" >/dev/null || echo "WARNING: oracle build failed; oracle tests will skip" >&2
  fi
  work_oracle="$BLITZCS_WINE_WORK/oracle"; rm -rf "$work_oracle"; mkdir -p "$work_oracle"; cp "$oracle_dir"/*.oracle.exe "$oracle_dir"/*.json "$oracle_dir"/*.bin "$work_oracle"/ 2>/dev/null
  export BLITZCS_ORACLE_DIR="$(winepath -w "$work_oracle" 2>/dev/null || echo "Z:${work_oracle//\//\\}")"
fi

rc=0; idx=0
for p in "${projects[@]}"; do
  csproj="$root/tests/$p/$p.csproj"; [ -f "$csproj" ] || { echo "no such test project: $csproj" >&2; rc=3; continue; }
  app="$out/app/$p"
  if [ $build = 1 ]; then
    echo "== publishing $p (win-x64, $config)"
    rm -rf "$app"
    dotnet publish "$csproj" -c "$config" -r win-x64 --self-contained -o "$app" --artifacts-path "$out/ab" -p:UseAppHost=true -nologo -v q || { echo "publish failed: $p" >&2; rc=3; continue; }
    dotnet publish "$root/tools/WinTestRunner/WinTestRunner.csproj" -c "$config" -r win-x64 --self-contained -o "$app" --artifacts-path "$out/ab" -p:UseAppHost=true -nologo -v q || { echo "runner publish failed" >&2; rc=3; continue; }
  fi
  [ -f "$app/WinTestRunner.exe" ] || { echo "nothing published at $app (run without --no-build)" >&2; rc=3; continue; }
  idx=$((idx+1)); short="$BLITZCS_WINE_WORK/t$idx"; rm -rf "$short"; mkdir -p "$short"; cp -r "$app"/. "$short"/
  echo "== running $p under $(wine --version 2>/dev/null) from $short"
  ( cd "$short" && timeout 3600 wine WinTestRunner.exe --assembly "$p.dll" --threads "$threads" "${filters[@]}" 2>&1 | tee "$out/$p.log" | grep -v "^PASS" ; exit "${PIPESTATUS[0]}" )
  r=$?; echo "== $p exit code $r (full log: $out/$p.log)"; [ $r -ne 0 ] && rc=$r
done
wineserver -k 2>/dev/null || true
exit $rc
