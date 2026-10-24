"""Lists DLL names that appear as strings (ANSI or UTF-16) inside PE files: candidates for LoadLibrary at run time.
Reads files only, never executes anything. Run as:  python3 -I tools/survey/dynamic_strings.py <file> [<file> ...]
"""
import re
import sys

ANSI = re.compile(rb"[A-Za-z0-9_\-]{2,24}\.(?:dll|drv|ocx)", re.I)
WIDE = re.compile(rb"(?:[A-Za-z0-9_\-]\x00){2,24}\.\x00(?:[dD]\x00[lL]\x00[lL]\x00)")


def main(paths):
    for path in paths:
        with open(path, "rb") as f:
            data = f.read()
        names = {m.group().decode().lower() for m in ANSI.finditer(data)}
        names |= {m.group().decode("utf-16le").lower() for m in WIDE.finditer(data)}
        print(path, sorted(names))


if __name__ == "__main__":
    main(sys.argv[1:])
