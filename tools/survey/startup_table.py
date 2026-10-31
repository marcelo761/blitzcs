"""Joins the static evidence of one SDK image into a start-up table (read-only; executes nothing).

Inputs:
  --imports  TSV from pe_imports.py (image, dll, name, iat)            for the image
  --survey   sdk-survey.json from `BcSurvey sdk <dir> --json`          (Win32 verdict per dll!name)
  --startup  TSV from `BcSurvey startup <pe> [exports]`                (direct-call closure per root)
  --image    image name as written in --imports (filter)
Output (stdout TSV): dll, name, implemented, group, direct_sites, upper_sites, roots
"""
import argparse
import json
import sys


def group_of(dll, name):
    d = dll.lower()
    n = name.lower()
    if d == "user32.dll":
        return "user32 window/message"
    if d in ("gdi32.dll", "gdiplus.dll", "msimg32.dll", "winspool.drv"):
        return "gdi32/gdiplus/printing"
    if d in ("ddraw.dll", "dinput8.dll", "d3dxof.dll"):
        return "DirectX/COM creation"
    if d == "winmm.dll":
        return "winmm timers/sound"
    if d == "ws2_32.dll":
        return "network (ws2_32)"
    if d in ("ole32.dll", "oleaut32.dll", "oleacc.dll", "oledlg.dll"):
        return "ole32/oleaut32 COM"
    if d in ("shell32.dll", "uxtheme.dll", "shlwapi.dll", "imm32.dll"):
        return "shell/theme/IME"
    if d in ("advapi32.dll",):
        return "advapi32 registry"
    if d == "kernel32.dll":
        if any(k in n for k in ("exception", "raise", "unhandled", "debugger", "rtlunwind", "setunhandled")):
            return "kernel32 SEH/diagnostics"
        if any(k in n for k in ("startup", "modulehandle", "getmodulefilename", "commandline", "encode", "slist", "processorfeature", "securitycookie", "systemtimeasfiletime", "getcurrentprocess", "getcurrentthreadid")):
            return "kernel32 CRT/startup"
        if any(k in n for k in ("file", "console", "locale", "codepage", "stringtype", "widechar", "multibyte", "compare", "env", "lcmap", "cpinfo", "acp", "oemcp", "wide", "localeinfo")):
            return "kernel32 file/console/locale"
        if any(k in n for k in ("resource", "heap", "virtual", "global", "local")):
            return "kernel32 memory/resources"
        if any(k in n for k in ("library", "procaddress", "module")):
            return "kernel32 module loading"
        if any(k in n for k in ("critical", "tls", "interlocked", "event", "mutex", "thread", "sleep", "wait")):
            return "kernel32 sync/threads/TLS"
        return "kernel32 other"
    return "other (" + d + ")"


def load(path):
    rows = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or line.startswith("dll\t"):
                continue
            p = line.rstrip("\n").split("\t")
            if len(p) >= 7:
                rows[(p[0].lower(), p[1])] = (int(p[4]), int(p[5]), p[6])
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--imports", required=True)
    ap.add_argument("--survey", required=True)
    ap.add_argument("--startup", action="append", default=[])
    ap.add_argument("--image", required=True)
    a = ap.parse_args()
    survey = json.load(open(a.survey, encoding="utf-8"))
    verdict = {}
    for imp in survey["imports"]:
        verdict[(imp["Dll"].lower(), imp["Name"])] = imp["Status"]
    startup = {}
    for path in a.startup:
        for key, (d, u, roots) in load(path).items():
            cur = startup.get(key, (0, 0, set()))
            startup[key] = (cur[0] + d, cur[1] + u, cur[2] | set(roots.split(",")))
    print("dll\tname\timplemented\tgroup\tdirect_sites\tupper_sites\troots")
    seen = set()
    for line in open(a.imports, encoding="utf-8"):
        img, dll, name, _iat = line.rstrip("\n").split("\t")
        if not img.endswith(a.image):
            continue
        key = (dll.lower(), name)
        if key in seen:
            continue
        seen.add(key)
        st = verdict.get(key, "unknown")
        d, u, roots = startup.get(key, (0, 0, set()))
        impl = "yes" if st == "implemented" else "no"
        print(f"{dll}\t{name}\t{impl}\t{group_of(dll, name)}\t{d}\t{u}\t{','.join(sorted(roots))}")


if __name__ == "__main__":
    sys.exit(main())
