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

## Corpus differential
`tools/survey/blitzcc_corpus.sh <SDK dir> <max count>` compiles each selected `*.bb` of the SDK natively (Wine, `-c` then `-o`) and under BcRun (`--target linux-x64`), then compares the two exes with `compare_blitzcc.py`. A source the native compiler rejects is compiled with `-c` under BcRun too, and the two runs must print the same stdout (the diagnostic; Wine's own `ntsync` stderr line is not compared) and end with the same exit status. Native sandbox: the dedicated prefix's `drive_c`; BcRun sandbox: a copy of the SDK. Both use the string `C:\<relative path>`, so the command line is identical on both sides.

Command run (SDK = Blitz3D V1.118, 704 `.bb` files, 60 selected by evenly spaced size, smallest first):
```
BLITZCS_WINE_PREFIX=<prefix with vkd3d DLLs> tools/survey/blitzcc_corpus.sh <SDK dir> 60 > corpus.tsv
```

| metric | count |
|---|---|
| sources tried | 60 |
| native compile errors (`-c`) | 2 (`help/commands/2d_examples/StartNetGame.bb`: `Function 'startnetgame' not found`; `JoyUDir.bb`: `Expecting end-of-file`) |
| compile errors with the same stdout and exit status under BcRun (guest SEH, `docs/binary-seh.md`) | 2 of 2 (exit 255 both) |
| compared (both exes produced) | 58 |
| identical | 58 |
| different | 0 |
| BcRun failures | 0 |

Re-run after guest SEH (format 6 artifacts: every import stub changed): 60 of 60 `identical = yes` (58 executables byte-identical, 2 compile errors matching), native total 17.3 s, BcRun total 25.2 s.
A first run of that day reported `samples/birdie/Quick Deform/qd.bb` different in 4 bytes of `.rsrc` (offset 0x10B42C): the native exe had `3d275554` where BcRun wrote zeros. Three further
native compiles of the same source wrote zeros, like BcRun and like the earlier corpus run, so the native compiler leaves those 4 bytes nondeterministic (uninitialised); the full re-run was identical.

Timing (wall, per source): native `-o` total 17.2 s, average 0.30 s; BcRun `-o` total 23.2 s, average 0.40 s (BcRun cache shared across the run, so the first translation is included).

The 58 native exes are 1126912 to 1157120 bytes. Because nothing differed, no offset analysis or bisection was needed. The TSV is at `/tmp/claude-1000/j-corpus.tsv`.
