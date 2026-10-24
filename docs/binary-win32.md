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
| Kernel32.Memory | VirtualAlloc, VirtualFree, VirtualProtect, VirtualQuery (protection semantics below) |
| Kernel32.Sync | InitializeCriticalSection, InitializeCriticalSectionAndSpinCount, DeleteCriticalSection, EnterCriticalSection, TryEnterCriticalSection, LeaveCriticalSection, InterlockedIncrement/Decrement/Exchange/ExchangeAdd/CompareExchange, CreateEventA, SetEvent, ResetEvent, CreateMutexA, ReleaseMutex, WaitForSingleObject |
| Kernel32.Tls | TlsAlloc, TlsFree, TlsGetValue, TlsSetValue |
| Kernel32.Process | GetLastError, SetLastError, GetCurrentProcess/Thread/ProcessId/ThreadId, Sleep, GetTickCount, QueryPerformanceCounter/Frequency, ExitProcess, TerminateProcess, GetVersion, GetVersionExA, GetCommandLineA, GetEnvironmentStrings(W), FreeEnvironmentStringsA/W, GetEnvironmentVariableA, GetStartupInfoA, GetModuleFileNameA, GetModuleHandleA, LoadLibraryA, FreeLibrary, GetProcAddress, SetUnhandledExceptionFilter, IsBadReadPtr/WritePtr/CodePtr |
| Kernel32.String | MultiByteToWideChar, WideCharToMultiByte, LCMapStringA/W, GetStringTypeA/W, GetACP, GetOEMCP, GetCPInfo, lstrlenA, lstrcpyA, lstrcpynA, lstrcatA |
| Kernel32.File | CreateFileA, ReadFile, WriteFile, CloseHandle, SetFilePointer, SetEndOfFile, FlushFileBuffers, GetFileSize, GetFileType, GetFileAttributesA, FindFirstFileA, FindNextFileA, FindClose, GetStdHandle, SetStdHandle, SetHandleCount |
| Msvcrt (cdecl) | malloc, calloc, realloc, free, memcpy, memmove, memset, memcmp, memchr, strlen, strcmp, _stricmp, strncmp, strcpy, strncpy, strcat, strchr, strrchr, strstr, abs, labs, atoi, toupper, tolower, sprintf, _snprintf |
| Msvcrt.Runtime (cdecl) | `_initterm`, `_initterm_e` (callbacks into guest code), `_errno`, `_ftol` (x87 argument, EDX:EAX result), `_CIpow` (ST1/ST0 arguments, ST0 result), sin, cos, atan, log, pow, sqrt, fabs, floor, fmod, ldexp, frexp (double stack arguments, ST0 result), clock, rand, srand, qsort, `_vsnprintf`, wcstombs, `operator new` / `operator delete`, fopen/fclose/fread/fseek/ftell (binary modes only), `_isctype`; DATA exports `__mb_cur_max` (1), `_adjust_fdiv` (0), `_pctype` ("C" locale class table) |
| Kernel32.Extra | CreateThread (real host thread running the guest routine), ResumeThread, GetExitCodeThread, Set/GetThreadPriority (recorded, host scheduling unchanged), DisableThreadLibraryCalls, GetSystemDirectoryA, GetWindowsDirectoryA, GetDriveTypeA (only C:), OutputDebugStringA (host sink or dropped) |
| Winmm | waveOutGetNumDevs / waveInGetNumDevs / mixerGetNumDevs = 0; every device-id call fails with MMSYSERR_BADDEVICEID (numbered device) or MMSYSERR_NODRIVER (WAVE_MAPPER); every handle call fails with MMSYSERR_INVALHANDLE (no open can succeed); timeGetTime; timeSetEvent (TIME_CALLBACK_FUNCTION, one-shot/periodic, real host timer thread) and timeKillEvent. No mci* |
| Winsock.Core | htonl, htons, ntohl, ntohs, WSAStartup, WSACleanup, WSAGetLastError, WSASetLastError, `__WSAFDIsSet` (WSOCK32 by ordinal 8, 9, 14, 15, 111, 112, 115, 116, 151, checked against a Windows-compatible export table; WS2_32 by name). No socket/connect/send/recv/select |
| Winsock | inet_addr, inet_ntoa for WSOCK32 (by name and ordinals 10/11, checked against a Windows export table) and WS2_32 (by name) |

## Documented behaviour relied on, and approximations

Error codes are the real `winerror.h` values and `SetLastError` semantics follow the documentation (for example `TlsGetValue` and `GetFileSize`
reset the last error to ERROR_SUCCESS on success; `CreateFile` with `CREATE_ALWAYS`/`OPEN_ALWAYS` on an existing file succeeds with
ERROR_ALREADY_EXISTS).

- **Heap**: 8-byte alignment; `HeapSize` returns the requested size; `HEAP_ZERO_MEMORY`, `HEAP_REALLOC_IN_PLACE_ONLY`, `HEAP_GENERATE_EXCEPTIONS`
  honoured; a fixed-size heap (non-zero maximum) enforces its limit; `HeapFree(NULL)` succeeds; freeing a foreign or already freed pointer, which
  is undefined on Windows, fails with ERROR_INVALID_PARAMETER; blocks of 256 KiB or more get their own region which is unmapped when freed.
  Approximation: every heap is serialised, `HEAP_NO_SERIALIZE` is accepted but ignored.
- **VirtualAlloc/VirtualFree**: reserve and commit happen together (reserved pages are accessible); `MEM_DECOMMIT` zero-fills (and makes the pages read-write
  again); granularity is 4 KiB; a non-NULL address is only honoured inside a region this service allocated. The initial protection is applied to the pages.
- **VirtualProtect / VirtualQuery** (truthful, per page; `GuestMemory.TryQueryPage/TryProtect` over `IPlatformMemory.Protect`):
  * Guest data (VirtualAlloc, heap and other scratch buffers, and the non-code pages of the translated image such as `.data`, `.rdata`, headers): `PAGE_NOACCESS`,
    `READONLY`, `READWRITE` and `EXECUTE_*` are honoured; the old protection of the first page is returned through `lpflOldProtect`; `PAGE_WRITECOPY` behaves as `READWRITE`
    (the memory is private, so no copy ever happens; VirtualQuery reports what was requested); `PAGE_NOCACHE`/`PAGE_WRITECOMBINE` are accepted and have no effect.
    The managed accessors (`GuestMemory.Read/Write`) apply the same rules, so a host write into a read-only page throws `GuestMemoryException`; a guest write is a hardware fault.
  * `PAGE_EXECUTE*` on guest data is recorded (VirtualQuery reports it) but the host never maps it executable (W^X): the page stays readable, and writable when requested.
    Jumping into it ends in the ordinary "untranslated target" trap (`GuestTrapException`, kind 1, with the address), never in native execution.
  * Pages of translated code (every page of an executable section of a translated image): a request that makes them writable (`READWRITE`, `WRITECOPY`, `EXECUTE_READWRITE`,
    `EXECUTE_WRITECOPY`) throws `Win32SelfModifyingCodeException` (a `Win32UsageException`; the guest call fails with `GuestImportException` whose inner exception it is) naming the
    module, the page range and the requested protection. `READONLY`, `EXECUTE` and `EXECUTE_READ` are no-ops that report `PAGE_EXECUTE_READ` as the old protection; `PAGE_NOACCESS`
    on code is refused with `Win32UsageException` because it cannot be enforced.
  * `PAGE_GUARD` is refused with `Win32UsageException` (a guard-page violation cannot be delivered to guest exception handling). Runtime-owned memory (stacks, TEB/PEB) is refused likewise.
  * Failures like Windows, with nothing changed: size 0, unknown protection bits or invalid combinations -> `ERROR_INVALID_PARAMETER` (87); an unmapped page, a range past 4 GiB or a
    range spanning two allocations -> `ERROR_INVALID_ADDRESS` (487); `lpflOldProtect` NULL or unwritable -> `ERROR_NOACCESS` (998). The range is widened to whole pages.
  * `VirtualQuery` fills the 28-byte 32-bit `MEMORY_BASIC_INFORMATION`: `BaseAddress` is the page, `AllocationBase` the start of the allocation, `RegionSize` the run of pages with the same
    protection, `State` `MEM_COMMIT` or `MEM_FREE`, `Type` `MEM_IMAGE` for the translated image else `MEM_PRIVATE`. `AllocationProtect` is the protection of the allocation's first page
    now, not at allocation time (approximation). Length < 28 -> `ERROR_BAD_LENGTH` (24).
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

## Registry, COM and user32 subsets

- **advapi32 registry** (`Advapi32RegistryService`): an in-memory virtual registry per resolver, empty except for the four predefined roots
  (HKCR, HKCU, HKLM, HKU). ANSI `RegOpenKey[Ex]`, `RegCreateKey[Ex]`, `RegCloseKey`, `RegSetValue[Ex]`, `RegQueryValue[Ex]`, `RegEnumKey[Ex]`,
  `RegEnumValue`, `RegDeleteKey`, `RegDeleteValue`, `RegFlushKey` return the LONG error code (SUCCESS, FILE_NOT_FOUND, MORE_DATA, NO_MORE_ITEMS,
  INVALID_HANDLE, KEY_DELETED, ACCESS_DENIED for deleting a key with sub-keys) and follow the documented size-in/size-out buffer rules. With
  `Win32Options.SandboxRoot` the tree is persisted to `.blitzcs-registry.json` in it (a corrupt file is a fault). Not modelled: W variants,
  merged HKCR view, security, volatile keys, HKEY_CURRENT_CONFIG. `Advapi32RegistryService.Snapshot` lets a host inspect what a guest wrote.
- **ole32** (`Ole32Service`): per-thread apartment bookkeeping (S_OK / S_FALSE / RPC_E_CHANGED_MODE), `CoTaskMemAlloc/Realloc/Free` on a checked guest
  heap, GUID text conversion (`StringFromCLSID`, `StringFromGUID2`, `CLSIDFromString` incl. ProgID lookup in the virtual registry, `IIDFromString`).
  `CoCreateInstance` never returns S_OK: no COM server is loaded by this component. It returns CO_E_NOTINITIALIZED before `CoInitialize`,
  REGDB_E_CLASSNOTREG for an unregistered class, CLASS_E_CLASSNOTAVAILABLE when the virtual registry names a server that cannot be hosted here, and
  always NULLs `*ppv`. Hosting an in-proc server through an explicit host registration API is out of scope.
- **user32** (`User32BasicService`): `GetDesktopWindow` returns the opaque sandbox handle `0x00010010`; `IsWindow`/`IsWindowVisible`/`MessageBoxA` validate
  HWNDs (ERROR_INVALID_WINDOW_HANDLE). `MessageBoxA` calls `Win32Options.MessageBoxHandler`; without one it faults naming `user32!MessageBoxA`. Every other
  window API is unresolved and faults by name.
- **eax.dll end to end** (`EaxTests`, needs `BLITZCS_REAL_PE32_DIR`): `DllRegisterServer` returns S_OK and writes two CLSIDs
  ({4FF53B81-1CE0-11D3-AAB8-00A0C95949D5} EAX Unified, {CA503B60-B176-11D4-A094-D0C0BF3A560C} EAX Unified 8) each with InprocServer32 (default =
  the module path `GetModuleFileNameA` answers, ThreadingModel=Both), ProgID and VersionIndependentProgID; the test checks the GUID format and values
  against COM conventions. `DllUnregisterServer` removes them, leaving only the shared `HKCR\CLSID` parent (as ATL servers do). `DllCanUnloadNow`
  returns S_OK with no objects; `DllGetClassObject` with an unknown CLSID returns CLASS_E_CLASSNOTAVAILABLE.

## Real imports (ogg.dll, vorbis.dll, vorbisFile.dll, eax.dll, UPX stub of fmod.dll)

81 distinct non-inter-module imports were tabulated (`PriorityList.cs`). Satisfied per binary (imports from `ogg.dll`/`vorbis.dll` are
inter-module links the caller's own resolver must provide):

| Binary | Win32 imports satisfied | Missing Win32 imports |
|---|---|---|
| ogg.dll | 42/43 | RtlUnwind |
| vorbis.dll | 54/56 | RtlUnwind, RaiseException |
| vorbisFile.dll | 52/54 | RtlUnwind, RaiseException |
| eax.dll | 71/73 | RtlUnwind, RaiseException |
| fmod.dll stub | LoadLibraryA, GetProcAddress, abs, wsock32 #11 | RegCloseKey, acmStreamOpen, CoInitialize, ShowWindow, mixerOpen |

`RtlUnwind` and `RaiseException` need structured exception handling in the runtime (frame chain walking and handler invocation), so they stay
missing; the CRT only reaches them on exception paths, so `AllowUnboundImports` lets start-up code run while any use faults naming the API.
The design for these two is in `binary-seh.md`. The registry, COM and basic user32 imports of eax.dll are implemented as truthful subsets (below);
the windowing and winmm imports of fmod.dll remain intentionally absent.

## Runtime additions made for the unpacked fmod.dll (all additive)

* `GuestModule.CallAddress` / `GuestCall.CallGuest` (hook 2 below, now present): an import can call guest code by address (the address must be a translated code entry: export,
  relocation/immediate-derived root; otherwise `GuestTrapException` names it). Used by `_initterm`, `qsort`, `CreateThread`, `timeSetEvent`.
* `GuestCall.TryPopSt0` / `PopSt0Double` / `SetReturnSt0(double)` and `GuestX87` (hook 6, now present): the guest x87 registers are the real ones of the executing thread and survive the
  managed transition, so a 27-byte helper mapped R+X pops ST0 (it checks FXAM for an empty stack). Linux x86-64 only, like the rest of the runtime.
* `IGuestDataImportResolver` / `GuestDataImport`: an import that is a VARIABLE (msvcrt `__mb_cur_max`, `_pctype`, `_adjust_fdiv`) gets the variable's guest address in its IAT slot, written before
  the IAT is made read-only. Before this, such an import would have been bound to a callable stub and silently read as data.

## Runtime hooks needed

1. (open) A guest-callable stub address for a bound import (so `GetProcAddress(kernel32, "HeapAlloc")` can answer; see `Win32Context.SetExportStubAddressProvider`).
2. (done, see above) Calling back into guest code by address. `atexit`/`_onexit` still need the process-exit sequence.
3. (done for VirtualProtect/VirtualQuery) Page protection control on `GuestMemory` regions. Real decommit (unmapping) is still open.
4. Atomic read-modify-write on guest words, so Interlocked APIs are atomic against guest `LOCK` instructions.
5. Structured exception support (`RtlUnwind`, `RaiseException`, `SetUnhandledExceptionFilter` delivery; study in `binary-seh.md`) and the TEB TLS array (see above).
6. (done, see above) x87 arguments. `_ftol2` / `_CIsin` family are not registered (not imported by the samples).
7. Relocation/data-pointer roots for UPX-unpacked images: the unpacker drops relocations, so code reachable only through data (the fmod output-driver function table) is not translated; `FSOUND_Init`, `FSOUND_SetDriver` and driver enumeration after `FSOUND_SetOutput` trap with `GuestTrapException` (see binary-unpack.md).

## Tests

`tests/BlitzCS.BinaryCompiler.Win32.Tests` unit-tests every API on a sparse fake address space (success, error returns with `GetLastError`,
argument validation, double free, lifetime, concurrency of heap, critical sections, interlocked and events). An integration test compiles
`Native/w32.c` with `tools/x86_oracle/build.sh` (real IA-32 code, skipped when the 32-bit toolchain is missing), translates it and runs it with
this resolver: it calls HeapAlloc/HeapReAlloc/HeapSize/HeapFree, critical sections, TLS, MultiByteToWideChar/WideCharToMultiByte, Interlocked
and the cdecl MSVCRT functions through its IAT. Win32 semantics cannot be observed on the Linux ELF oracle, so expected values are computed in C#
from the documentation rather than compared with native execution. Tests that analyse real binaries read the directory from
`BLITZCS_REAL_PE32_DIR` and do nothing when it is unset; no binary is copied into the repository. Guest-loading tests belong to the xunit
collection `GuestRuntime`.
