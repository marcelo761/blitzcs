#!/usr/bin/env bash
# Builds the M21 benchmark library once and links it twice (flat PE image + static ELF32 oracle), plus relocation RVAs
# for absolute pointers (function-pointer tables) so the translator can find indirect-call targets.
# usage: build.sh <out-dir>
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd); out=${1:?out dir}; mkdir -p "$out"; tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
CF="-m32 -O2 -ffreestanding -fno-pic -fno-pie -fno-stack-protector -fno-asynchronous-unwind-tables -fcf-protection=none -fno-builtin -nostdlib -Wall -Wextra -Werror"
gcc $CF -c "$here/bench_lib.c" -o "$tmp/lib.o"
ld -m elf_i386 -T "$here/../layout.ld" -e 0 --emit-relocs -o "$tmp/lib.elf" "$tmp/lib.o"
objcopy -O binary -j .text "$tmp/lib.elf" "$out/bench.code.bin"
objcopy -O binary -j .data "$tmp/lib.elf" "$out/bench.data.bin"
python3 - "$tmp/lib.elf" "$out/bench.exports.json" <<'P'
import subprocess,sys,json,re
elf=sys.argv[1]
syms=[]
for l in subprocess.check_output(['nm','-n',elf],text=True).splitlines():
    a,t,n=l.split()
    if t in 'T': syms.append({'name':n,'offset':int(a,16)-0x10001000})
relocs=[]
for l in subprocess.check_output(['readelf','-rW',elf],text=True).splitlines():
    m=re.match(r'^([0-9a-f]{8})\s+[0-9a-f]+\s+R_386_32\s',l)
    if m: relocs.append(int(m.group(1),16)-0x10000000)
json.dump({'exports':syms,'relocRvas':sorted(set(relocs))},open(sys.argv[2],'w'),indent=1)
P
gcc $CF -c "$here/bench_driver.c" -o "$tmp/drv.o"
ld -m elf_i386 -static -e _start -o "$out/bench.oracle" "$tmp/lib.o" "$tmp/drv.o"
