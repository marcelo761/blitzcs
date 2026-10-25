#!/usr/bin/env python3
"""Byte-level comparison of two PE executables, with each differing range
attributed to a PE structure (DOS header, PE header timestamp, section table,
.text, .data, .rdata, resources, overlay, ...).

Usage:
    python3 -I compare_blitzcc.py <a.exe> <b.exe> [--ignore-timestamp]

--ignore-timestamp masks the COFF TimeDateStamp and the optional-header
CheckSum in both files before comparing (they are reported as ignored).

Exit codes: 0 identical (after ignores), 1 different, 2 error.
Standard library only.
"""

import struct
import sys

ERROR = 2


class PEError(Exception):
    pass


def _u16(d, o):
    return struct.unpack_from("<H", d, o)[0]


def _u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def parse_pe(data):
    """Return a layout dict describing the PE structure of `data`."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise PEError("not an MZ/PE file (missing DOS signature)")
    lfanew = _u32(data, 0x3C)
    if lfanew + 24 > len(data) or data[lfanew:lfanew + 4] != b"PE\0\0":
        raise PEError("missing PE signature at e_lfanew=%#x" % lfanew)
    coff = lfanew + 4
    nsec = _u16(data, coff + 2)
    time_date_stamp = coff + 4
    opt_size = _u16(data, coff + 16)
    opt = coff + 20
    if opt + opt_size > len(data):
        raise PEError("optional header runs past end of file")
    magic = _u16(data, opt)
    if magic not in (0x10B, 0x20B):
        raise PEError("unknown optional header magic %#x" % magic)
    checksum = opt + 64
    sec_table = opt + opt_size
    sections = []
    for i in range(nsec):
        o = sec_table + 40 * i
        if o + 40 > len(data):
            raise PEError("section table runs past end of file")
        name = data[o:o + 8].rstrip(b"\0").decode("latin-1")
        vsize, vaddr, rsize, rptr = struct.unpack_from("<IIII", data, o + 8)
        sections.append({"name": name, "raw": rptr, "rawsize": rsize,
                         "vaddr": vaddr, "vsize": vsize})
    headers_end = sec_table + 40 * nsec
    raw_end = max([s["raw"] + s["rawsize"] for s in sections] or [headers_end])
    return {
        "lfanew": lfanew,
        "stub_start": 0x40,
        "coff": coff,
        "timestamp": time_date_stamp,
        "opt": opt,
        "opt_size": opt_size,
        "checksum": checksum,
        "sec_table": sec_table,
        "headers_end": headers_end,
        "sections": sections,
        "raw_end": raw_end,
        "size": len(data),
    }


def classify(layout, off):
    """Name the PE structure that owns file offset `off`."""
    if off < 0x40:
        return "DOS header"
    if off < layout["lfanew"]:
        return "DOS stub"
    if layout["lfanew"] <= off < layout["lfanew"] + 4:
        return "PE signature"
    t = layout["timestamp"]
    if t <= off < t + 4:
        return "PE header TimeDateStamp (COFF)"
    if layout["coff"] <= off < layout["opt"]:
        return "COFF header"
    c = layout["checksum"]
    if c <= off < c + 4:
        return "optional header CheckSum"
    if layout["opt"] <= off < layout["sec_table"]:
        return "optional header"
    if layout["sec_table"] <= off < layout["headers_end"]:
        return "section table"
    for s in layout["sections"]:
        if s["raw"] <= off < s["raw"] + s["rawsize"]:
            if s["name"] == ".rsrc":
                return "resources (.rsrc)"
            return "section %s" % s["name"]
    if off >= layout["raw_end"]:
        return "overlay (after last section)"
    return "header padding"


def masked_copy(data, layout, ignore_timestamp):
    """Return (bytes, list of ignored (offset, length, label))."""
    if not ignore_timestamp:
        return bytes(data), []
    buf = bytearray(data)
    ignored = []
    for off, label in ((layout["timestamp"], "PE header TimeDateStamp (COFF)"),
                       (layout["checksum"], "optional header CheckSum")):
        buf[off:off + 4] = b"\0\0\0\0"
        ignored.append((off, 4, label))
    return bytes(buf), ignored


def diff_ranges(a, b):
    """Contiguous byte ranges where a and b differ, plus a length mismatch."""
    ranges = []
    n = min(len(a), len(b))
    i = 0
    while i < n:
        if a[i] != b[i]:
            j = i
            while j < n and a[j] != b[j]:
                j += 1
            ranges.append((i, j - i))
            i = j
        else:
            i += 1
    if len(a) != len(b):
        ranges.append((n, abs(len(a) - len(b))))
    return ranges


def _hex(data, off, length, limit=8):
    chunk = data[off:off + min(length, limit)]
    s = " ".join("%02x" % x for x in chunk)
    if length > limit:
        s += " ..."
    return s


def main(argv):
    ignore_ts = False
    paths = []
    for arg in argv:
        if arg == "--ignore-timestamp":
            ignore_ts = True
        elif arg in ("-h", "--help"):
            print(__doc__)
            return 0
        else:
            paths.append(arg)
    if len(paths) != 2:
        sys.stderr.write("usage: compare_blitzcc.py <a.exe> <b.exe> [--ignore-timestamp]\n")
        return ERROR
    try:
        blobs = []
        for p in paths:
            with open(p, "rb") as fh:
                blobs.append(fh.read())
        layouts = [parse_pe(b) for b in blobs]
    except (OSError, PEError, struct.error) as exc:
        sys.stderr.write("error: %s\n" % exc)
        return ERROR

    a, ign_a = masked_copy(blobs[0], layouts[0], ignore_ts)
    b, ign_b = masked_copy(blobs[1], layouts[1], ignore_ts)
    for p, ign in zip(paths, (ign_a, ign_b)):
        for off, length, label in ign:
            print("ignored in %s: offset=%#x length=%d (%s)" % (p, off, length, label))

    ranges = diff_ranges(a, b)
    if not ranges:
        print("identical")
        print("sizes: %d bytes" % len(a))
        return 0

    print("different")
    print("sizes: %s=%d %s=%d" % (paths[0], len(blobs[0]), paths[1], len(blobs[1])))
    print("%d differing range(s):" % len(ranges))
    for off, length in ranges:
        region = classify(layouts[0], off)
        if off >= min(len(a), len(b)):
            region = "size difference (length mismatch)"
        elif region != classify(layouts[1], off):
            region += " / " + classify(layouts[1], off)
        print("  offset=%#010x length=%-6d region=%s" % (off, length, region))
        if off < min(len(a), len(b)):
            print("      a: %s" % _hex(a, off, length))
            print("      b: %s" % _hex(b, off, length))
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
