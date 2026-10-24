#!/usr/bin/env bash
# Builds one controlled x86 library from C twice from the SAME machine code:
#   <out>/<name>.code.bin / .data.bin / .exports.json : flat image for the PE32 fixture (code @0x10001000, data @0x10002000)
#       exports.json = {"exports":[{name,offset}], "relocs":[rva of every absolute 32-bit field (R_386_32)]}
#   <out>/<name>.oracle                               : static ELF32 driver that runs the code natively on x86 (the oracle),
#       with the library at the SAME absolute addresses (see oracle.ld) and an import slot area at 0x10003400.
# usage: build.sh <lib.c> <driver.c> <out-dir>   (driver.c may be '-' to skip the oracle)
# A first line of the form  // FLAGS: <gcc flags>  in lib.c adds compiler flags (used for both lib and driver).
set -euo pipefail
lib=$1; driver=$2; out=$3; name=$(basename "$lib" .c); here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$out"; tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
extra=$(sed -n '1s#^// FLAGS: *##p' "$lib")
CF="-m32 -O2 -ffreestanding -fno-pic -fno-pie -fno-stack-protector -fno-asynchronous-unwind-tables -fcf-protection=none -fno-builtin -nostdlib -Wall -Wextra -Werror $extra"
gcc $CF -I"$here/lib" -c "$lib" -o "$tmp/lib.o"
ld -m elf_i386 -q -T "$here/layout.ld" -e 0 -o "$tmp/lib.elf" "$tmp/lib.o"
objcopy -O binary -j .text "$tmp/lib.elf" "$out/$name.code.bin"
objcopy -O binary -j .data "$tmp/lib.elf" "$out/$name.data.bin" 2>/dev/null || : > "$out/$name.data.bin"
python3 - "$tmp/lib.elf" "$out/$name.exports.json" <<'P'
import subprocess,sys,json
syms=[]
for l in subprocess.check_output(['nm','-n',sys.argv[1]],text=True).splitlines():
    a,t,n=l.split()
    if t in 'T': syms.append({'name':n,'offset':int(a,16)-0x10001000})
relocs=[]
for l in subprocess.check_output(['readelf','-rW',sys.argv[1]],text=True).splitlines():
    p=l.split()
    if len(p)>=3 and p[2]=='R_386_32': relocs.append(int(p[0],16)-0x10000000)
json.dump({'exports':syms,'relocs':sorted(set(relocs))},open(sys.argv[2],'w'),indent=1)
P
if [ "$driver" != "-" ]; then
  gcc $CF -I"$here/lib" -I"$here/drivers" -c "$driver" -o "$tmp/drv.o"
  ld -m elf_i386 -static --no-warn-rwx-segments -T "$here/oracle.ld" -e _start -o "$out/$name.oracle" "$tmp/lib.o" "$tmp/drv.o"
fi
