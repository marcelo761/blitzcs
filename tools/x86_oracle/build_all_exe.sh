#!/usr/bin/env bash
# Builds every library that has a driver, ELF oracle + PE32 oracle EXE, into <out-dir> (default artifacts/x86_oracle).
# Point BLITZCS_ORACLE_DIR at <out-dir> to let the Windows/Wine test run use the prebuilt .oracle.exe files.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd); out=${1:-$here/../../artifacts/x86_oracle}
for d in "$here"/drivers/*.c; do n=$(basename "$d" .c); [ -f "$here/lib/$n.c" ] && ORACLE_EXE=1 bash "$here/build.sh" "$here/lib/$n.c" "$d" "$out"; done
# libraries without a driver still need their flat images (fixtures)
for l in "$here"/lib/*.c; do n=$(basename "$l" .c); [ -f "$here/drivers/$n.c" ] || bash "$here/build.sh" "$l" - "$out"; done
echo "oracle artifacts in $out"
