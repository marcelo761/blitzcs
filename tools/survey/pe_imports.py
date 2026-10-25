"""Static PE32 import table reader (read-only; never executes the input).

Usage: python3 -I tools/survey/pe_imports.py <file.exe|file.dll> [...]
Prints, per DLL, the imported names (or ord#N), one per line, as TSV:
    image<TAB>dll<TAB>name_or_ordinal<TAB>iat_rva
"""
import struct
import sys


def rva_to_off(sections, rva):
    for va, vsize, raw, rsize in sections:
        span = max(vsize, rsize)
        if va <= rva < va + span:
            return rva - va + raw
    return None


def read_imports(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"MZ":
        raise SystemExit(f"{path}: not an MZ image")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise SystemExit(f"{path}: no PE signature")
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    if magic != 0x10B:
        raise SystemExit(f"{path}: not PE32 (magic {magic:#x})")
    dd = opt + 96
    imp_rva, imp_size = struct.unpack_from("<II", data, dd + 8)
    sec_off = opt + opt_size
    sections = []
    for i in range(nsec):
        o = sec_off + i * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, o + 8)
        sections.append((va, vsize, raw, rsize))
    out = []
    if not imp_rva:
        return out
    off = rva_to_off(sections, imp_rva)
    while True:
        oft, _ts, _fc, name_rva, ft = struct.unpack_from("<IIIII", data, off)
        if not (oft or name_rva or ft):
            break
        name_off = rva_to_off(sections, name_rva)
        end = data.index(b"\0", name_off)
        dll = data[name_off:end].decode("ascii", "replace")
        lookup = oft or ft
        lk_off = rva_to_off(sections, lookup)
        idx = 0
        while True:
            entry = struct.unpack_from("<I", data, lk_off + idx * 4)[0]
            if entry == 0:
                break
            if entry & 0x80000000:
                name = f"ord#{entry & 0xFFFF}"
            else:
                h_off = rva_to_off(sections, entry & 0x7FFFFFFF)
                e = data.index(b"\0", h_off + 2)
                name = data[h_off + 2:e].decode("ascii", "replace")
            out.append((dll, name, ft + idx * 4))
            idx += 1
        off += 20
    return out


if __name__ == "__main__":
    for p in sys.argv[1:]:
        for dll, name, iat in read_imports(p):
            print(f"{p}\t{dll}\t{name}\t{iat:#x}")
