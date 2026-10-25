# Blitz3D compiler reference outputs (native Wine)

The original `blitzcc.exe` (Blitz3D V1.118) runs natively under Wine: a PE32 console program, no BCSBRE involved. Its outputs are the byte-level reference for comparing against blitzcc running inside BCSBRE.

## Environment
- SDK: `Blitz3D-V1.118.zip`, extracted to a fresh temp dir; a copy is the work dir (`$WORK`). `bin/blitzcc.exe` is used as-is.
- Wine: Proton 11.0 `files/` (wine-11.0), via `. scripts/wine/env.sh`.
- Dedicated prefix (`BLITZCS_WINE_PREFIX=$REFPFX`): created with `wine wineboot -i`, then the Proton `lib/vkd3d/x86_64-windows/libvkd3d*.dll` copied to `system32` and `lib/vkd3d/i386-windows/libvkd3d*.dll` to `syswow64`.
- Why: `runtime.dll` imports DDRAW.dll, which pulls `wined3d.dll`, which imports `libvkd3d-1.dll`. Without these DLLs in the prefix, `blitzcc` prints `Unable to open runtime.dll` and exits 255. Wine's `wineboot` does not install them; the Proton `default_pfx` copy does not work as a plain copy (its symlinks are relative).
- Run: `BLITZPATH='Z:\<work>'` (Wine maps Z: to /), then `wine bin/blitzcc.exe <args>` with the work dir as cwd. No window or `-q` flag was needed once the DLLs were present.

## Commands and observed output (verbatim; lines joined with `/` where noted)
```
$ wine bin/blitzcc.exe -h        (exit 0)
ntsync: up and running.
Usage: blitzcc [-h|-q|+q|-c|-d|-k|+k|-v|-o exefile] [sourcefile.bb]
...
$ wine bin/blitzcc.exe -v        (exit 0)
Compiler version:11.18:SoLoud
Runtime version:11.18:SoLoud
Debugger version:0.0:
Linker version:11.18:SoLoud
$ wine bin/blitzcc.exe -c hello.bb        (exit 0; no file written)
BlitzCC V1.118 / (C)opyright 2000-2003 Blitz Research Ltd / Compiling "hello.bb"
Parsing... Generating... Translating... Assembling...
$ wine bin/blitzcc.exe -o out_hello.exe hello.bb        (exit 0)
... Assembling...
Creating executable "out_hello.exe"...
$ wine bin/blitzcc.exe hello.bb        (no -o: exit 0, prints "Executing...", runs the program, writes no exe)
```
`-q` suppresses the banner. The `ntsync: up and running.` line is printed by Wine itself.

## Sources and reference executables
Sources (in this repo): `tools/survey/blitzcc_ref/` (`hello.bb`, `prog2.bb`, `inc_main.bb` with `Include "inc_lib.bb"`, `inc_lib.bb`). Each compiled with `-o`, two runs each, into the reference dir (`ref/`).

| file | bytes | sha256 |
|---|---|---|
| out_hello.exe | 1126912 | d7d54f539e62c7a1a978156a9bfb773e689d0ba52eb9eb159b83d90d4733146d |
| out_prog2.exe | 1128960 | 099a287816166d186c52f675310e130cec73b49449f1ef0f59a75a9ba3d74659 |
| out_inc.exe (from inc_main.bb) | 1126912 | ff7e647967cb464c66460aeaa7a81acd596d334149cdffa0e990f7f9b4c000d4 |

Determinism: both runs of each source are byte-identical (`cmp` reports no difference). The COFF TimeDateStamp is the constant `0x66d235d0` (2024-08-30), taken from the compiler, not the clock. Sections: `.text .rdata .data .rsrc .reloc`.

Notes: when `-o` is omitted, the compiler writes no exe; it runs the program ("Executing..."). Include was only tested with both files in the cwd.

## Comparing a BcRun-produced exe
```
python3 -I tools/survey/compare_blitzcc.py <reference.exe> <bcrun.exe>
python3 -I tools/survey/compare_blitzcc.py --ignore-timestamp <reference.exe> <bcrun.exe>
```
Exit 0 identical, 1 different (each differing range is printed with its offset, length and PE region), 2 error. `--ignore-timestamp` masks the COFF TimeDateStamp and the optional-header CheckSum.
