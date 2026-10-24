#!/usr/bin/env bash
# Runs xunit v2 test projects as win-x64 under Wine.
#   scripts/wine/run-tests.sh [options] [project ...]
#   project: a directory/name below tests/ (default: BlitzCS.BinaryCompiler.Win32.Tests), e.g. BlitzCS.BinaryCompiler.Tests
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
[ ${#projects[@]} -eq 0 ] && projects=(BlitzCS.BinaryCompiler.Win32.Tests)

command -v dotnet >/dev/null || { echo "dotnet not found in PATH" >&2; exit 3; }
# shellcheck disable=SC1091
. "$here/env.sh" || exit 3
out="$root/artifacts/wine"; mkdir -p "$out"

# Prebuilt Windows oracle EXEs (the Windows process cannot run gcc); only the BinaryCompiler.Tests assembly uses them.
if [[ " ${projects[*]} " == *" BlitzCS.BinaryCompiler.Tests "* ]]; then
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
    dotnet publish "$csproj" -c "$config" -r win-x64 --self-contained -o "$app" --artifacts-path "$out/ab" -nologo -v q || { echo "publish failed: $p" >&2; rc=3; continue; }
    dotnet publish "$root/tools/WinTestRunner/WinTestRunner.csproj" -c "$config" -r win-x64 --self-contained -o "$app" --artifacts-path "$out/ab" -nologo -v q || { echo "runner publish failed" >&2; rc=3; continue; }
  fi
  [ -f "$app/WinTestRunner.exe" ] || { echo "nothing published at $app (run without --no-build)" >&2; rc=3; continue; }
  idx=$((idx+1)); short="$BLITZCS_WINE_WORK/t$idx"; rm -rf "$short"; mkdir -p "$short"; cp -r "$app"/. "$short"/
  echo "== running $p under $(wine --version 2>/dev/null) from $short"
  ( cd "$short" && timeout 3600 wine WinTestRunner.exe --assembly "$p.dll" --threads "$threads" "${filters[@]}" 2>&1 | tee "$out/$p.log" | grep -v "^PASS" ; exit "${PIPESTATUS[0]}" )
  r=$?; echo "== $p exit code $r (full log: $out/$p.log)"; [ $r -ne 0 ] && rc=$r
done
wineserver -k 2>/dev/null || true
exit $rc
