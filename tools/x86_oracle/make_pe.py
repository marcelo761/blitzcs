#!/usr/bin/env python3
"""Converts the statically linked oracle ELF32 (linked with oracle_win.ld) into a PE32 console EXE (stdlib only).
usage: make_pe.py <in.elf> <out.exe>
Image base 0x10000000, no relocations, DYNAMIC_BASE off, so a loader that honours the preferred base maps every section at the
address the ELF was linked for. Imports (kernel32: GetStdHandle, WriteFile, ExitProcess) live in a generated .idata at VA 0x10008000
whose IAT starts at its first byte: slot 0 GetStdHandle, 1 WriteFile, 2 ExitProcess (common.h reads them at those literal addresses)."""
import struct, sys

BASE = 0x10000000; SA = 0x1000; FA = 0x200; IDATA_RVA = 0x8000
IMPORTS = ["GetStdHandle", "WriteFile", "ExitProcess"]

def align(v, a): return (v + a - 1) & ~(a - 1)

def read_elf(path):
    d = open(path, "rb").read()
    assert d[:4] == b"\x7fELF" and d[4] == 1, "ELF32 expected"
    e_entry = struct.unpack_from("<I", d, 24)[0]
    shoff, = struct.unpack_from("<I", d, 32); shentsize, shnum, shstrndx = struct.unpack_from("<HHH", d, 46)
    sh = [struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize) for i in range(shnum)]
    stroff = sh[shstrndx][4]
    secs = []
    for name, typ, flags, addr, off, size, *_ in sh:
        if not flags & 2 or size == 0: continue  # SHF_ALLOC
        n = d[stroff + name:d.index(b"\0", stroff + name)].decode()
        secs.append(dict(name=n, type=typ, flags=flags, addr=addr, size=size, data=(b"" if typ == 8 else d[off:off + size])))
    return e_entry, secs

def build_idata():
    dll = b"KERNEL32.dll\0"
    off_ilt = 0x60; off_names = 0x80
    names = b""; hint_rvas = []
    for n in IMPORTS:
        hint_rvas.append(IDATA_RVA + off_names + len(names)); names += b"\0\0" + n.encode() + b"\0"
        if len(names) % 2: names += b"\0"
    dll_rva = IDATA_RVA + off_names + len(names); names += dll
    buf = bytearray(align(off_names + len(names), 16))
    for i, r in enumerate(hint_rvas):
        struct.pack_into("<I", buf, i * 4, r)              # IAT (loader overwrites)
        struct.pack_into("<I", buf, off_ilt + i * 4, r)    # ILT
    struct.pack_into("<IIIII", buf, 0x20, IDATA_RVA + off_ilt, 0, 0, dll_rva, IDATA_RVA)  # descriptor; terminator already zero
    buf[off_names:off_names + len(names)] = names
    return bytes(buf)

def main(elf, out):
    entry, secs = read_elf(elf)
    parts = []  # (name, rva, vsize, data, characteristics)
    for s in secs:
        va = s["addr"]; page = va & ~(SA - 1); pre = va - page
        data = b"\0" * pre + s["data"]
        code = s["flags"] & 4
        ch = 0x60000020 if code and not s["flags"] & 1 else 0xC0000040 if not code else 0xE0000020
        if s["type"] == 8: ch = 0xC0000080
        parts.append([s["name"].lstrip(".")[:8], page - BASE, pre + s["size"], data, ch])
    idata = build_idata()
    parts.append(["idata", IDATA_RVA, len(idata), idata, 0xC0000040])
    parts.sort(key=lambda p: p[1])
    for a, b in zip(parts, parts[1:]):
        assert a[1] + align(a[2], SA) <= b[1], f"sections {a[0]} and {b[0]} overlap"
    nsec = len(parts); hdr_size = align(0x40 + 4 + 20 + 224 + 40 * nsec, FA)
    raw = hdr_size; rows = []; body = b""
    for name, rva, vsize, data, ch in parts:
        rsize = align(len(data), FA) if data else 0
        rows.append((name, vsize, rva, rsize, raw if rsize else 0, ch)); body += data.ljust(rsize, b"\0"); raw += rsize
    size_image = align(parts[-1][1] + parts[-1][2], SA)
    code_sz = sum(r[3] for r in rows if r[5] & 0x20); init_sz = sum(r[3] for r in rows if r[5] & 0x40)
    pe = bytearray(b"MZ" + b"\0" * 0x3A + struct.pack("<I", 0x40)) + b"PE\0\0"
    pe += struct.pack("<HHIIIHH", 0x14C, nsec, 0, 0, 0, 224, 0x0103)  # i386, EXECUTABLE_IMAGE | 32BIT_MACHINE | RELOCS_STRIPPED
    ep_rva = entry - BASE
    opt = struct.pack("<HBBIIIIII", 0x10B, 14, 0, code_sz, init_sz, 0, ep_rva, parts[0][1], parts[0][1])
    opt += struct.pack("<IIIHHHHHHIIIIHHIIIIII", BASE, SA, FA, 6, 0, 0, 0, 6, 0, 0, size_image, hdr_size, 0, 3, 0,  # subsystem=console, DllCharacteristics=0
                       0x100000, 0x1000, 0x100000, 0x1000, 0, 16)
    dd = [(0, 0)] * 16; dd[1] = (IDATA_RVA + 0x20, 40); dd[12] = (IDATA_RVA, 16)
    opt += b"".join(struct.pack("<II", *x) for x in dd)
    pe += opt
    for name, vsize, rva, rsize, rptr, ch in rows:
        pe += struct.pack("<8sIIIIIIHHI", name.encode(), vsize, rva, rsize, rptr, 0, 0, 0, 0, ch)
    pe = pe.ljust(hdr_size, b"\0") + body
    open(out, "wb").write(pe)

if __name__ == "__main__": main(*sys.argv[1:3])
