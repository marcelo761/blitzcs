# Binary compiler differential testing against real x86

The PE32 -> x64 translator is validated against **real IA-32 execution**, never against itself.

## Oracle design

`tools/x86_oracle/build.sh <lib.c> <driver.c> <out>` compiles one controlled C library once (`gcc -m32 -ffreestanding`, no libc, no libgcc calls) and produces:

* a flat image (`<lib>.code.bin` at RVA 0x1000, `<lib>.data.bin` at RVA 0x2000, `<lib>.exports.json` with function offsets and every absolute 32-bit field (`R_386_32`) as a list of relocation RVAs), from which the tests build a PE32 fixture (`PeFixtureBuilder`, ImageBase 0x10000000, HIGHLOW relocations for jump tables, function-pointer tables and absolute data/FP-constant references);
* a static ELF32 oracle (`<lib>.oracle`) that links the **same object code at the same absolute addresses** (`oracle.ld`) and runs natively through the kernel's IA-32 compat mode. Import slots live at 0x10003400, matching the fixture's IAT.

The driver (`drivers/common.h`) calls each exported function with identical vectors (edge values x edge values on the first two arguments, then fixed-seed LCG random vectors) and prints a deterministic text transcript: return value, FNV hash of the whole data section after every call, callback trace lines, and a final data dump. The xunit tests replay the oracle's own vectors on `GuestModule` and compare return value, per-vector data hash, final data bytes and callback trace; the first divergence is reported with its vector index. `// FLAGS:` on the first line of a library adds compiler flags (SSE, MMX).

Failure classes in assertion messages: `TRANSLATION` (guest diverges from the oracle), `ENVIRONMENT` (oracle could not be built or run), `UNSUPPORTED` (translator reported an Error diagnostic). If `gcc -m32`, `ld` or IA-32 emulation is missing, the tests are skipped with an explicit reason (`OracleFact` / `OracleTheory`).

## Libraries (lib/ and drivers/)

| lib | covers |
|---|---|
| arith | add, mulhi, signed div, FNV over a buffer, recursion, global counter, switch, overflow builtin |
| intops1 | 64-bit add/sub/neg/mul, manual 64-bit divide (variable shifts), div/idiv, cmp/setcc packs, `__builtin_*_overflow`, cmov, min/max |
| intops2 | variable shifts/rotates (8/32/64 bit, shld/shrd, rcl/rcr), adc/sbb, bsf/bsr, bt/bts/btr/btc, bswap, partial registers, mul/imul 8/16, xchg/xadd/cmpxchg, loop/jecxz, flags captured with `pushfl` after add/sub/logic/inc/dec/neg/adc/sbb/shifts/mul, lahf/sahf, setcc |
| memops | `rep movsb/w/d`, backwards movs (`std`), `rep stosb/w/d`, `repne scasb`, `repe cmpsb`, `lodsb`, hashes, CRC, string scans, in-place reverse |
| state | data-section counters and structs, recursion (fib, Ackermann, gcd, depth 3000), dense/sparse/fall-through switch jump tables, writable and const function-pointer tables (relocated), indirect tail calls, sorting, binary search, matrix multiply |
| conv | cdecl and stdcall (0-6 args), mixed nested calls, callee-saved register pressure |
| cb | guest calls host imports through IAT slots (stdcall and cdecl, nested, loops); callback traces are compared |
| fp | x87: fild/fistp, fmul/fdiv/fadd, fsqrt, fsin, frndint, fldpi, float32 stores, fnstcw; results via integer returns and data |
| sse | SSE/SSE2 scalar float/double, packed int/float/double, shuffles, compares, movmskps |
| exotic | memory-form `bt*` with bit-string offsets, pushal/popal, leave, xlat, cmpxchg8b, high-byte registers, 3-operand imul, cmov/shift immediates, MMX, fprem, fistp, cpuid, rdtsc, lodsl, repne scasl, pushf/popf with DF |
| unsup | fastcall and thiscall: must be refused, never mistranslated |
| bcd | daa/das/aam: must be reported as UNSUPPORTED |

## Harness self-check

`HarnessSanityTests` proves the comparison is not vacuous: a mutated opcode (`add` -> `sub`) and a mutated initial data byte in the fixture are detected, and a corrupted oracle transcript (return value, data hash, final data, callback trace) is detected at the right vector.

## Known gaps

* `GuestFunction.Call` returns EAX only, so values returned in st0/xmm0 or EDX:EAX are not observable; float math is covered through integer-returning functions and data-section comparison.
* fastcall/thiscall exports are classified `Unknown` and `Bind` refuses them.
* daa/das/aam (and other opcodes invalid in 64-bit mode) are reported as `BC_UNSUPPORTED_INSTRUCTION` errors.
* A translator bug that crashes the guest with a native fault would take the test host down (the guest runs in-process).
* A library's code+rodata must stay below 4 KiB (the oracle linker script places data at 0x10002000).

## Running

```
dotnet test tests/BlitzCS.BinaryCompiler.Tests -c Debug   --filter "FullyQualifiedName~Differential"
dotnet test tests/BlitzCS.BinaryCompiler.Tests -c Release --filter "FullyQualifiedName~Differential"
# a single oracle by hand
tools/x86_oracle/build.sh tools/x86_oracle/lib/intops2.c tools/x86_oracle/drivers/intops2.c /tmp/o && /tmp/o/intops2.oracle | head
```
Built artifacts are cached under the test output directory (`x86_oracle-cache`) and rebuilt when sources change.
