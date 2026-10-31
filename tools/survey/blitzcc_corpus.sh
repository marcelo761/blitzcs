#!/usr/bin/env bash
# Corpus differential for the original Blitz3D compiler (blitzcc.exe, V1.118).
#
# For up to MAX real *.bb sources of an SDK tree, compile each one
#   (A) natively under Wine (reference), and
#   (B) with the same blitzcc.exe translated and run by BCSBRE (tools/BcRun, linux-x64),
# then compare the two produced executables with tools/survey/compare_blitzcc.py.
# A source that the native compiler rejects (-c fails) is compiled with -c under BcRun too, and
# the two runs must print the same stdout (the diagnostic) and end with the same exit status.
# Produced executables are never run.
#
# Usage:  tools/survey/blitzcc_corpus.sh <SDK dir> <max count> > corpus.tsv
#
# Layout: the native side uses the dedicated Wine prefix's drive_c as C:\ (the SDK is copied
# there), and the BcRun side uses its sandbox as C:\. Both see identical strings
# ("C:\<relative path>.bb"), so no path differences can come from the command line.
#
# Environment:
#   BLITZCS_WINE_PREFIX  dedicated prefix (must contain the Proton vkd3d DLLs, see docs/blitz3d-reference.md)
#   BCRUN_TIMEOUT        seconds per BcRun compile (default 900)
#   NATIVE_TIMEOUT       seconds per native wine compile (default 300)
#   KEEP_WORK=1          keep the work directory (default: kept; set to 0 to delete it on exit)
#
# TSV columns: source, size, native_status, bcrun_status, identical, differing_ranges,
#              native_seconds, bcrun_seconds
#   identical: for a compiled source, the two executables; for a native compile error, the
#   two stdout transcripts and exit statuses (Wine's own stderr lines are not compared).
# Progress and the work directory go to stderr.

set -u
export LC_NUMERIC=C   # EPOCHREALTIME and awk need '.' as the decimal separator

if [ $# -ne 2 ]; then
  echo "usage: $0 <SDK dir> <max count>" >&2
  exit 2
fi
SDK=$(cd "$1" && pwd) || exit 2
MAX=$2
case "$MAX" in ''|*[!0-9]*) echo "max count must be a number" >&2; exit 2 ;; esac

HERE=$(cd "$(dirname "$0")/../.." && pwd)
BCRUN="$HERE/tools/BcRun/bin/Release/net10.0/BcRun.dll"
CMP="$HERE/tools/survey/compare_blitzcc.py"
BCRUN_TIMEOUT=${BCRUN_TIMEOUT:-900}
NATIVE_TIMEOUT=${NATIVE_TIMEOUT:-300}

[ -f "$BCRUN" ] || { echo "missing $BCRUN (run: dotnet build tools/BcRun -c Release)" >&2; exit 2; }
[ -f "$SDK/bin/blitzcc.exe" ] || { echo "missing $SDK/bin/blitzcc.exe" >&2; exit 2; }

export BLITZCS_WINE_NO_XVFB=${BLITZCS_WINE_NO_XVFB:-1}
# shellcheck source=/dev/null
. "$HERE/scripts/wine/env.sh" || exit 2
trap 'wineserver -k >/dev/null 2>&1 || true' EXIT

if [ ! -f "$WINEPREFIX/drive_c/windows/system32/libvkd3d-1.dll" ]; then
  echo "prefix $WINEPREFIX lacks libvkd3d-1.dll; see docs/blitz3d-reference.md (Environment)" >&2
  exit 2
fi

BS=$(printf '\\')
WORK=$(mktemp -d "${TMPDIR:-/tmp}/blitzcc-corpus.XXXX")
NATIVE="$WINEPREFIX/drive_c"          # native sandbox: C:\ under Wine
BC="$WORK/bcrun"                      # BcRun sandbox: C:\ under BCSBRE
mkdir -p "$BC" "$WORK/ref" "$WORK/bc" "$WORK/logs" "$WORK/cache"
echo "work directory: $WORK" >&2
echo "native sandbox (C:\\ under Wine): $NATIVE" >&2

# Both sandboxes are fresh copies of the same SDK tree. The native copy overwrites
# previous SDK files there; the prefix is dedicated to this differential.
cp -a "$SDK/." "$NATIVE/" && cp -a "$SDK/." "$BC/" || { echo "copy of SDK failed" >&2; exit 2; }

# Evenly spaced sample across the size-sorted list (small first, largest last).
mapfile -t ALL < <(find "$SDK" -name '*.bb' -type f -printf '%s\t%P\n' | sort -n -k1,1 -k2,2)
N=${#ALL[@]}
[ "$N" -gt 0 ] || { echo "no .bb sources under $SDK" >&2; exit 2; }
if [ "$MAX" -ge "$N" ]; then
  SEL=("${ALL[@]}")
else
  SEL=()
  for ((k = 0; k < MAX; k++)); do SEL+=("${ALL[$((k * N / MAX))]}"); done
fi
echo "sources: $N found, $MAX selected" >&2

printf 'source\tsize\tnative_status\tbcrun_status\tidentical\tdiffering_ranges\tnative_seconds\tbcrun_seconds\n'

clean() { printf '%s' "$1" | tr '\t\r\n' '   ' | cut -c1-200; }
first_error() {
  # first line naming an error, else the last non-empty line, of a blitzcc log
  local f=$1 line
  line=$(grep -v -e '^ntsync' -e '^$' "$f" | grep -i -m1 -e 'error' -e 'unable' -e 'cannot' -e 'exception')
  [ -z "$line" ] && line=$(grep -v -e '^ntsync' -e '^$' "$f" | tail -1)
  clean "${line:-no output}"
}
elapsed() { awk -v a="$1" -v b="$2" 'BEGIN { printf "%.2f", b - a }'; }

i=0
for entry in "${SEL[@]}"; do
  i=$((i + 1))
  size=${entry%%$'\t'*}
  rel=${entry#*$'\t'}
  win="C:${BS}${rel//\//$BS}"
  tag=$(printf '%03d' "$i")
  nstat="ok"; bstat="skipped (native compile error)"; ident="n/a"; ranges=""
  nsec="n/a"; bsec="n/a"

  # 1. native check only
  (cd "$NATIVE" && timeout "$NATIVE_TIMEOUT" env BLITZPATH="C:${BS}" wine bin/blitzcc.exe -c "$win") \
    >"$WORK/logs/$tag.native-c.out" 2>"$WORK/logs/$tag.native-c.err"
  rc=$?
  cat "$WORK/logs/$tag.native-c.out" "$WORK/logs/$tag.native-c.err" >"$WORK/logs/$tag.native-c.log"
  if [ "$rc" -ne 0 ]; then
    nstat="native compile error (exit $rc): $(first_error "$WORK/logs/$tag.native-c.log")"
    # 1b. the same check under BcRun: same diagnostic on stdout, same exit status
    t0=$EPOCHREALTIME
    timeout "$BCRUN_TIMEOUT" dotnet "$BCRUN" "$BC/bin/blitzcc.exe" --target linux-x64 \
      --env "blitzpath=C:${BS}" --sandbox "$BC" --cache-dir "$WORK/cache" -- \
      -c "$win" >"$WORK/logs/$tag.bcrun-c.out" 2>"$WORK/logs/$tag.bcrun-c.err"
    brc=$?
    t1=$EPOCHREALTIME
    bsec=$(elapsed "$t0" "$t1")
    if [ "$brc" -eq "$rc" ] && cmp -s "$WORK/logs/$tag.native-c.out" "$WORK/logs/$tag.bcrun-c.out"; then
      bstat="same compile error (exit $brc)"; ident=yes
    else
      bstat="compile error differs (exit $brc): $(first_error "$WORK/logs/$tag.bcrun-c.out") / $(clean "$(tail -1 "$WORK/logs/$tag.bcrun-c.err")")"; ident=no
      ranges=$(clean "$(diff "$WORK/logs/$tag.native-c.out" "$WORK/logs/$tag.bcrun-c.out" | head -4 | paste -sd ";" -)")
    fi
  else
    # 2. native executable
    rm -f "$NATIVE/_corpus.exe"
    t0=$EPOCHREALTIME
    (cd "$NATIVE" && timeout "$NATIVE_TIMEOUT" env BLITZPATH="C:${BS}" wine bin/blitzcc.exe -o "C:${BS}_corpus.exe" "$win") \
      >"$WORK/logs/$tag.native-o.log" 2>&1
    rc=$?
    t1=$EPOCHREALTIME
    nsec=$(elapsed "$t0" "$t1")
    if [ "$rc" -eq 0 ] && [ -f "$NATIVE/_corpus.exe" ]; then
      mv "$NATIVE/_corpus.exe" "$WORK/ref/$tag.exe"
    else
      nstat="native -o error (exit $rc): $(first_error "$WORK/logs/$tag.native-o.log")"
    fi
  fi

  if [ "$nstat" = ok ]; then
    # 3. BcRun executable
    rm -f "$BC/_corpus.exe"
    t0=$EPOCHREALTIME
    timeout "$BCRUN_TIMEOUT" dotnet "$BCRUN" "$BC/bin/blitzcc.exe" --target linux-x64 \
      --env "blitzpath=C:${BS}" --sandbox "$BC" --cache-dir "$WORK/cache" -- \
      -o "C:${BS}_corpus.exe" "$win" >"$WORK/logs/$tag.bcrun.log" 2>&1
    rc=$?
    t1=$EPOCHREALTIME
    bsec=$(elapsed "$t0" "$t1")
    if [ "$rc" -eq 124 ]; then
      bstat="bcrun timeout after ${BCRUN_TIMEOUT}s"
    elif [ "$rc" -eq 0 ] && [ -f "$BC/_corpus.exe" ]; then
      mv "$BC/_corpus.exe" "$WORK/bc/$tag.exe"
      bstat="ok"
    else
      bstat="bcrun error (exit $rc): $(first_error "$WORK/logs/$tag.bcrun.log")"
    fi

    # 4. compare
    if [ "$bstat" = ok ]; then
      python3 -I "$CMP" "$WORK/ref/$tag.exe" "$WORK/bc/$tag.exe" >"$WORK/logs/$tag.compare.txt" 2>&1
      crc=$?
      case "$crc" in
        0) ident=yes ;;
        1) ident=no ;;
        *) ident="error" ;;
      esac
      if [ "$ident" = no ]; then
        ranges=$(grep -e '^  offset=' "$WORK/logs/$tag.compare.txt" | sed -e 's/^ *//' -e 's/  */ /g' | paste -sd ';' -)
      elif [ "$ident" = error ]; then
        ranges=$(clean "$(head -1 "$WORK/logs/$tag.compare.txt")")
      fi
      cmp -s "$WORK/ref/$tag.exe" "$WORK/bc/$tag.exe" && cmpb=same || cmpb=differ
      [ "$ident" = yes ] && [ "$cmpb" != same ] && echo "warning: $rel: compare says identical, cmp says $cmpb" >&2
      [ "$ident" = no ] && [ "$cmpb" = same ] && echo "warning: $rel: compare says different, cmp says same" >&2
    fi
  fi

  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(clean "$rel")" "$size" "$(clean "$nstat")" \
    "$(clean "$bstat")" "$ident" "$ranges" "$nsec" "$bsec"
  echo "[$tag/$MAX] $rel: native=${nstat%% *} bcrun=${bstat%% *} identical=$ident" >&2
done

if [ "${KEEP_WORK:-1}" = 0 ]; then
  rm -rf "$WORK"
fi
