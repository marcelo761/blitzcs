# Win32 compatibility component for translated PE32 libraries

`src/BlitzCS.BinaryCompiler.Win32` supplies the Win32 and C runtime services that real PE32 libraries import, so they can initialise and run on
Linux. It references only `BlitzCS.BinaryCompiler.Runtime`; nothing in the compiler, the code generator, the lifter or the CLI references it.
No third-party source was copied: every service is written from Microsoft's documented behaviour (Microsoft Learn) and from observing real
import tables. Wine was not consulted as a source of code.

## Composition

`Win32ImportResolver : IGuestImportResolver` is built from independent `IWin32Service` classes. Each service registers its APIs by
`(dll, name)` with an explicit calling convention (`Stdcall`: the callee pops `4 * argc`; `Cdecl`: C runtime) and an exact argument count.
An API that no service registers is absent: `Resolve` returns null, so `GuestModule.Load` fails with `GuestImportException` naming `DLL!name`
(or, with `AllowUnboundImports`, the guest faults at call time naming the same). There are no placeholder stubs.

- One resolver serves exactly one guest module (it owns heaps, handles, TLS, files and guest allocations); a second module is refused.
- `Win32ImportResolver.Coverage` (static) lists implemented APIs and a priority list taken from real binaries (ogg, vorbis, vorbisFile, eax and
  the UPX stub of fmod) with a "needed by N of 5" count. `Analyze(PeImage)` returns Implemented/Missing per import.
- Guest memory: all allocations are guest-visible low memory obtained through `GuestMemory.Allocate`; no host pointer is ever handed to the
  guest. Bad guest pointers surface as `GuestMemoryException`.
- Failures that Windows answers with a crash (leaving an unowned critical section, freeing a CRT block twice, unsupported printf conversion)
  raise `Win32UsageException`, which abandons the guest call. `ExitProcess`/`TerminateProcess(self)` raise `Win32ExitProcessException` and never
  end the host.
- Host policy lives in `Win32Options`: sandbox root, command line, main module, environment (the host environment is never leaked), code pages,
  reported Windows version (default XP SP3, 5.1.2600), standard streams.

## Implemented APIs

| Service | APIs |
|---|---|
| Kernel32.Heap | GetProcessHeap, HeapCreate, HeapDestroy, HeapAlloc, HeapReAlloc, HeapFree, HeapSize, HeapValidate, GlobalAlloc/GlobalFree and LocalAlloc/LocalFree (fixed memory only; `GMEM_MOVEABLE` fails with ERROR_NOT_SUPPORTED) |
| Kernel32.Memory | VirtualAlloc, VirtualFree |
| Kernel32.Sync | InitializeCriticalSection, InitializeCriticalSectionAndSpinCount, DeleteCriticalSection, EnterCriticalSection, TryEnterCriticalSection, LeaveCriticalSection, InterlockedIncrement/Decrement/Exchange/ExchangeAdd/CompareExchange, CreateEventA, SetEvent, ResetEvent, CreateMutexA, ReleaseMutex, WaitForSingleObject |
| Kernel32.Tls | TlsAlloc, TlsFree, TlsGetValue, TlsSetValue |
| Kernel32.Process | GetLastError, SetLastError, GetCurrentProcess/Thread/ProcessId/ThreadId, Sleep, GetTickCount, QueryPerformanceCounter/Frequency, ExitProcess, TerminateProcess, GetVersion, GetVersionExA, GetCommandLineA, GetEnvironmentStrings(W), FreeEnvironmentStringsA/W, GetEnvironmentVariableA, GetStartupInfoA, GetModuleFileNameA, GetModuleHandleA, LoadLibraryA, FreeLibrary, GetProcAddress, SetUnhandledExceptionFilter, IsBadReadPtr/WritePtr/CodePtr |
| Kernel32.String | MultiByteToWideChar, WideCharToMultiByte, LCMapStringA/W, GetStringTypeA/W, GetACP, GetOEMCP, GetCPInfo, lstrlenA, lstrcpyA, lstrcpynA, lstrcatA |
| Kernel32.File | CreateFileA, ReadFile, WriteFile, CloseHandle, SetFilePointer, SetEndOfFile, FlushFileBuffers, GetFileSize, GetFileType, GetFileAttributesA, FindFirstFileA, FindNextFileA, FindClose, GetStdHandle, SetStdHandle, SetHandleCount |
| Msvcrt (cdecl) | malloc, calloc, realloc, free, memcpy, memmove, memset, memcmp, memchr, strlen, strcmp, _stricmp, strncmp, strcpy, strncpy, strcat, strchr, strrchr, strstr, abs, labs, atoi, toupper, tolower, sprintf, _snprintf |
| Winsock | inet_addr, inet_ntoa for WSOCK32 (by name and ordinals 10/11, checked against a Windows export table) and WS2_32 (by name) |

## Documented behaviour relied on, and approximations

Error codes are the real `winerror.h` values and `SetLastError` semantics follow the documentation (for example `TlsGetValue` and `GetFileSize`
reset the last error to ERROR_SUCCESS on success; `CreateFile` with `CREATE_ALWAYS`/`OPEN_ALWAYS` on an existing file succeeds with
ERROR_ALREADY_EXISTS).

- **Heap**: 8-byte alignment; `HeapSize` returns the requested size; `HEAP_ZERO_MEMORY`, `HEAP_REALLOC_IN_PLACE_ONLY`, `HEAP_GENERATE_EXCEPTIONS`
  honoured; a fixed-size heap (non-zero maximum) enforces its limit; `HeapFree(NULL)` succeeds; freeing a foreign or already freed pointer, which
  is undefined on Windows, fails with ERROR_INVALID_PARAMETER; blocks of 256 KiB or more get their own region which is unmapped when freed.
  Approximation: every heap is serialised, `HEAP_NO_SERIALIZE` is accepted but ignored.
- **VirtualAlloc/VirtualFree**: reserve and commit happen together (reserved pages are accessible); `MEM_DECOMMIT` zero-fills; granularity is
  4 KiB; a non-NULL address is only honoured inside a region this service allocated; page protections are validated but not enforced.
  `VirtualProtect` is deliberately not implemented because it cannot be enforced without a runtime hook.
- **Critical sections**: the 24-byte structure stays in guest memory (LockCount/RecursionCount/OwningThread are mirrored); the lock itself is a
  host monitor keyed by the structure address. **Interlocked** operations are atomic with respect to each other only, not to guest `LOCK`
  instructions on the same word.
- **TLS**: 64 slots (the guaranteed minimum) in a host per-thread table; `TlsFree` invalidates every thread's value. The TEB contract places the
  same slots at `TEB+0xE10`; once guest code reads `FS:[0xE10+4*i]` this table must become that array so inlined TLS access and these APIs share
  one storage. Exhaustion sets ERROR_NO_MORE_ITEMS (Microsoft does not document the code).
- **Modules**: emulated DLLs are opaque module tokens; host-registered modules (`Win32Context.RegisterModule`) carry explicit export tables
  (names or `#ordinal`). `GetProcAddress` on an emulated DLL returns NULL/ERROR_PROC_NOT_FOUND until the runtime hook below exists.
- **NLS**: code pages 437, 850, 1252, 65001; WideCharToMultiByte never best-fit maps and maps a surrogate pair to one default character in
  single-byte pages; `MB_COMPOSITE` fails with ERROR_NOT_SUPPORTED; LCMapString supports lower/upper case (and "no flag" = copy) with invariant
  mapping; GetStringType supports `CT_CTYPE1` only (the other types fail with ERROR_NOT_SUPPORTED/ERROR_INVALID_PARAMETER); the locale is ignored.
- **Files**: paths map into `SandboxRoot` (null: every path API fails with ERROR_ACCESS_DENIED). Drive letters are dropped, `..` clamps at the
  root, UNC/device paths give ERROR_BAD_PATHNAME, names match case-insensitively against existing entries, symbolic links are never followed,
  share modes and attribute flags are ignored, overlapped I/O and `FILE_FLAG_DELETE_ON_CLOSE` fail with ERROR_NOT_SUPPORTED. Handles are never
  reused, so a double `CloseHandle` is detected.
- **CRT**: `malloc` family uses a private heap; `free`/`realloc` of a pointer it does not own raise `Win32UsageException`; `memcpy` tolerates
  overlap; `sprintf`/`_snprintf` support `d i u x X o c s p f F %` with flags, width, precision, `*` and `h/l/ll/I64`; other conversions
  (`%e %g %n ...`) and NaN/Infinity raise `Win32UsageException` naming the specifier. `_snprintf` follows the MSVCRT truncation rule (returns -1,
  no terminator when the output does not fit).

## Real imports (ogg.dll, vorbis.dll, vorbisFile.dll, eax.dll, UPX stub of fmod.dll)

81 distinct non-inter-module imports were tabulated (`PriorityList.cs`). Satisfied per binary (imports from `ogg.dll`/`vorbis.dll` are
inter-module links the caller's own resolver must provide):

| Binary | Win32 imports satisfied | Missing Win32 imports |
|---|---|---|
| ogg.dll | 42/43 | RtlUnwind |
| vorbis.dll | 54/56 | RtlUnwind, RaiseException |
| vorbisFile.dll | 52/54 | RtlUnwind, RaiseException |
| eax.dll | 58/73 | RtlUnwind, RaiseException, GetDesktopWindow, Reg* (7), ole32 (5) |
| fmod.dll stub | LoadLibraryA, GetProcAddress, abs, wsock32 #11 | RegCloseKey, acmStreamOpen, CoInitialize, ShowWindow, mixerOpen |

`RtlUnwind` and `RaiseException` need structured exception handling in the runtime (frame chain walking and handler invocation), so they stay
missing; the CRT only reaches them on exception paths, so `AllowUnboundImports` lets start-up code run while any use faults naming the API.
The registry, COM, user32 and winmm imports of eax.dll/fmod.dll have no meaningful semantics without a registry, COM runtime and windowing
layer and are intentionally absent.

## Runtime hooks needed

1. A guest-callable stub address for a bound import (so `GetProcAddress(kernel32, "HeapAlloc")` can answer; see `Win32Context.SetExportStubAddressProvider`).
2. A way for imports to call back into guest code by guest address (needed for `qsort`, `bsearch`, `atexit`, window procedures).
3. Page protection control on `GuestMemory` regions (needed for `VirtualProtect` and real decommit).
4. Atomic read-modify-write on guest words, so Interlocked APIs are atomic against guest `LOCK` instructions.
5. Structured exception support (`RtlUnwind`, `RaiseException`, `SetUnhandledExceptionFilter` delivery) and the TEB TLS array (see above).
6. An x87-argument convention for `_ftol`/`_ftol2`.

## Tests

`tests/BlitzCS.BinaryCompiler.Win32.Tests` unit-tests every API on a sparse fake address space (success, error returns with `GetLastError`,
argument validation, double free, lifetime, concurrency of heap, critical sections, interlocked and events). An integration test compiles
`Native/w32.c` with `tools/x86_oracle/build.sh` (real IA-32 code, skipped when the 32-bit toolchain is missing), translates it and runs it with
this resolver: it calls HeapAlloc/HeapReAlloc/HeapSize/HeapFree, critical sections, TLS, MultiByteToWideChar/WideCharToMultiByte, Interlocked
and the cdecl MSVCRT functions through its IAT. Win32 semantics cannot be observed on the Linux ELF oracle, so expected values are computed in C#
from the documentation rather than compared with native execution. Tests that analyse real binaries read the directory from
`BLITZCS_REAL_PE32_DIR` and do nothing when it is unset; no binary is copied into the repository. Guest-loading tests belong to the xunit
collection `GuestRuntime`.
