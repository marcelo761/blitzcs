# Native `fs:` support in the x86 oracle

The differential harness (`docs/binary-differential.md`) runs controlled C libraries natively as ELF32 programs and compares the
transcript with the translated code run through `GuestModule`. Code that touches `fs:` needs a real segment on the oracle side,
laid out like the guest TEB (`docs/binary-teb-contract.md`). `tools/x86_oracle/drivers/fs_teb.h` provides it.

## How it works

`teb_install()` fills a page-aligned 4 KiB block (plus a PEB block in the next page), calls `set_thread_area` (`int 0x80`, NR 243,
`user_desc` with base = TEB, limit = 0xFFF, 32-bit, byte granularity) and loads `%fs` with the returned selector. From then on the
library's inline-asm `%fs:` accesses are genuine hardware segment accesses (kernel IA-32 compat mode), not an emulation.

| Offset | Value installed |
|---|---|
| 0x00 ExceptionList | 0xFFFFFFFF |
| 0x04 / 0x08 StackBase / StackLimit | rounded-up address above the driver stack / base - 4 MiB (the live `esp` lies inside) |
| 0x18 Self | address of the block |
| 0x20 / 0x24 | 0x1234 / 0x5678 (only "non-zero and different" is observable) |
| 0x2C TLS pointer | 0 (`teb_set_tls(array)` to publish one) |
| 0x30 PEB | PEB block: +2 BeingDebugged = 0, +8 ImageBaseAddress = 0x10000000 (the library's link address) |
| 0x34 LastError | 0 |
| 0xE10.. TlsSlots | 0 |

API: `teb_install()`, `teb_block()`, `peb_block()`, `teb_get(off)`, `teb_put(off, v)`, `teb_set_tls(p)`, and the driver entry macro
`ORACLE_MAIN_FS(table, nrandom)` (same as `ORACLE_MAIN` but installs the segment first). The header is include-only and is included
after `common.h`; existing drivers are unchanged.

## Writing comparable libraries

Absolute addresses differ between oracle and guest (stack, TEB, PEB). A library must therefore return only address-independent facts:
equalities (`Self` read through `fs:` equals the pointer used to reach LastError), ranges (an RVA computed as `address - PEB.ImageBase`
lies in the text section), differences (`StackBase > StackLimit`, the local address lies between them), and values written then read
back (TLS slots, LastError, the chain length and handler sum of an exception-registration list).

## Libraries shipped

| Library / driver | Covers |
|---|---|
| `lib/fs_seh.c`, `drivers/fs_seh.c` | SEH-style push/pop of an `EXCEPTION_REGISTRATION` on `fs:[0]` with previous-link semantics (nested, depth 0..7), chain walk to 0xFFFFFFFF, multi-frame unwind by restoring an older link, StackBase/StackLimit bounds, Self pointer and LastError through Self |
| `lib/fs_tls.c`, `drivers/fs_tls.c` | TlsSlots with static offsets (slot 0, 1, 63), indexed offsets `fs:0xE10(,%idx,4)` and register-only `fs:(%reg)`, LastError read/modify/write (`addl`, `xchgl` on `fs:`), ClientId, TLS pointer read/write, PEB BeingDebugged/ImageBaseAddress |

Build and run natively: `tools/x86_oracle/build.sh tools/x86_oracle/lib/fs_seh.c tools/x86_oracle/drivers/fs_seh.c <out>` then
`<out>/fs_seh.oracle`. About 1300 and 900 vectors, deterministic. The test harness picks libraries by name (`OracleRunner.Build("fs_seh")`),
so enabling them is adding `"fs_seh"`/`"fs_tls"` to the library list in `OracleDifferentialTests.Cases`; they are meant to be enabled
together with the translator's TEB support (before it exists they fail with `BC_FS_UNSUPPORTED_OFFSET`, which is the intended
"unsupported, not silently zero" behaviour).

## Limits

x86-64 Linux with IA-32 emulation only (as the rest of the oracle). `gs:` is not installed. Thread-per-TEB behaviour is not exercised
(the oracle is single threaded).

## Windows variant (PE32 oracle, real TEB)

`drivers/fs_teb.h` includes `drivers/fs_teb_win.h` under `-DORACLE_WIN32` (see `docs/binary-differential.md` for the EXE). A Windows process already has a
genuine TEB at `fs:`, so nothing is installed; `teb_install()` only normalises what a real process initialises differently from the contract, restores the
real values around every OS call (`WriteFile`, `ExitProcess`) and at exit, and the libraries run unchanged. All three fs libraries (`fs_seh`, `fs_tls`, `fs_ops`)
produce byte-identical transcripts to the Linux oracle under Wine (Proton 11, WoW64), including `Self` page alignment, `PEB.ImageBaseAddress` = 0x10000000 and
the stack-bound relations.

Measured initial TEB/PEB of a PE32 console process under Wine, and how the contract deviates (`docs/binary-teb-contract.md` is the guest model):

| Field | Contract / Linux oracle | Real Windows (Wine) | Handling in the Windows driver |
|---|---|---|---|
| fs:[0] ExceptionList | 0xFFFFFFFF (empty chain) | one ntdll record whose `Next` is 0xFFFFFFFF (chain length 1) | driver sets 0xFFFFFFFF while library code runs; real head restored for OS calls and at exit. Without it `f_seh_nest` reports a different chain length and the "initial head is 0xFFFFFFFF" bit differs |
| fs:[4]/[8] StackBase/StackLimit | rounded address above the stack / base - 4 MiB | real stack: base above `esp`, limit = start of the reserved stack (here about 1 MiB below) | none needed: libraries print only `base > limit`, `limit <= local < base`, `base - limit >= 0x1000` |
| fs:[0x18] Self | address of the block | address of the TEB, page aligned (WoW64 32-bit TEB) | none (equality and page alignment hold) |
| fs:[0x20]/[0x24] ids | 0x1234 / 0x5678 | small distinct values (pid 0x20, tid 0x24 observed) | none (only non-zero and different are observable) |
| fs:[0x2C] TLS pointer | 0 | non-NULL (points to the loader's implicit-TLS array) | driver sets 0 for the library (`f_ids` expects NULL initially) and restores the real pointer for OS calls and at exit |
| fs:[0x30] PEB | PEB block, ImageBase = 0x10000000, BeingDebugged 0 | real PEB, ImageBase = 0x10000000 (preferred base honoured), BeingDebugged 0 | none |
| fs:[0x34] LastError | 0 | 0 at start, but OS calls overwrite it | none (the libraries only observe values they wrote; `f_lasterror` swaps back the previous value) |
| fs:[0xE10..] TlsSlots | 0 | 0 at start; owned by the OS (`TlsAlloc`) | driver saves 64 slots, zeroes them, and restores them at exit |

Deviations that remain to keep in mind when modelling real Windows binaries: a real process has pre-existing SEH records on the chain (a guest that walks
the whole chain must tolerate non-contract tails), the TLS pointer is normally non-NULL, `LastError` is clobbered by every API call, and the TEB lives
at a loader-chosen high address. None of these changes the instruction semantics of `fs:` accesses.
