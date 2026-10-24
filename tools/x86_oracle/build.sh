#!/usr/bin/env bash
# Builds one controlled x86 library from C twice from the SAME machine code:
#   <out>/<name>.code.bin / .data.bin / .exports.json : flat image for the PE32 fixture (code @0x10001000, data @0x10002000)
#   <out>/<name>.oracle                               : static ELF32 driver that runs the code natively on x86 (the oracle)
# usage: build.sh <lib.c> <driver.c> <out-dir>   (driver.c may be '-' to skip the oracle)
set -euo pipefail
lib=$1; driver=$2; out=$3; name=$(basename "$lib" .c)
mkdir -p "$out"; tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
CF="-m32 -O2 -ffreestanding -fno-pic -fno-pie -fno-stack-protector -fno-asynchronous-unwind-tables -fcf-protection=none -fno-builtin -nostdlib -Wall -Wextra -Werror"
gcc $CF -c "$lib" -o "$tmp/lib.o"
ld -m elf_i386 -T "$(dirname "$0")/layout.ld" -e 0 -o "$tmp/lib.elf" "$tmp/lib.o"
objcopy -O binary -j .text "$tmp/lib.elf" "$out/$name.code.bin"
objcopy -O binary -j .data "$tmp/lib.elf" "$out/$name.data.bin" 2>/dev/null || : > "$out/$name.data.bin"
python3 - "$tmp/lib.elf" "$out/$name.exports.json" <<'P'
import subprocess,sys,json
syms=[]
for l in subprocess.check_output(['nm','-n',sys.argv[1]],text=True).splitlines():
    a,t,n=l.split()
    if t in 'T': syms.append({'name':n,'offset':int(a,16)-0x10001000})
json.dump({'exports':syms},open(sys.argv[2],'w'),indent=1)
P
if [ "$driver" != "-" ]; then
  gcc $CF -c "$driver" -o "$tmp/drv.o"
  ld -m elf_i386 -static -e _start -o "$out/$name.oracle" "$tmp/lib.o" "$tmp/drv.o"
fi
