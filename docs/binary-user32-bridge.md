# Host bridge for user32, gdi32 and DirectX (Windows x64 target)

On the Windows x64 target the translated 32-bit program calls the host's own `user32.dll`, `gdi32.dll`, `ddraw.dll` and `dinput8.dll` (natively, or Wine's under Wine).
There is no replacement window system or graphics engine and no IPC: every call is an in-process call of the 64-bit host DLL through an ABI adapter that converts the
32-bit arguments. The Linux target does not get these APIs: they are not registered there, so a program that uses them faults naming `dll!name`.

Code: `src/BlitzCS.BinaryCompiler.Win32/HostBridge/` (native calls, layouts, message thunks, window procedures), `Services/HostUser32Service.cs`, `HostGdi32Service.cs`,
`HostDirectXService.cs`, `DirectX/` (COM tables), `Com/HostComProxy.cs` (identity).

## Selection

`Win32Options.HostWindowing`: `Default` = the bridge on a Windows host (including Wine), absent elsewhere; `None` = the previous sandbox subset (only the desktop window,
everything else absent); `Host` on a non-Windows host throws `Win32UsageException`. `Win32ImportResolver.DefaultServices(options)` adds `HostUser32Service`, `HostGdi32Service`
and `HostDirectXService`, and `User32BasicService` keeps only `MessageBoxA` (a modal box would block a headless run, so `Win32Options.MessageBoxHandler` stays the policy).

## Adapter model

* **Table rows** (`HostFunction`, flat exports; `HostComMethod`, COM slots): the host DLL and export (or vtable slot), one `ComArg` per guest stdcall argument, and the result
  kind. Argument kinds: `Value` (zero-extended), `Signed`, `HostArgs.Handle` (sign-extended), `WParam`/`LParam`, `AnsiString`, `AnsiOrId` (MAKEINTRESOURCE/atom below 0x10000
  passes as a value), `CountedAnsi`/`CountedArray` (length taken from another argument), `InStruct`/`OutBytes`/`InOutStruct` for structures with identical 32/64-bit layouts
  (RECT, POINT, SIZE, TEXTMETRICA, ABC, MINMAXINFO, DDCAPS, DDPIXELFORMAT, D3DDEVICEDESC7, DIDEVCAPS, DIDEVICEINSTANCEA), `MustBeNull`. Outputs are copied back only on success.
  Results: `Int32` (low 32 bits), `Handle` (checked), `Pointer` (must fit 32 bits). An argument that cannot be adapted answers the API's failure value with ERROR_INVALID_PARAMETER
  (E_INVALIDARG for COM) without reaching the host.
* **Hand-written adapters** for layouts that differ or carry guest code addresses: `Win32Layouts` (WNDCLASSA 40/72 bytes, MSG 28/48, CREATESTRUCTA 48/80, WINDOWPOS 28/40,
  NCCALCSIZE_PARAMS 52/56, PAINTSTRUCT 64/72), `DdLayouts` (DDSURFACEDESC2 124/136: `lpSurface` at 36/40, the tail moves by 8), `DirectDrawSurfaces.BltFxToHost` (DDBLTFX 100/128:
  five DWORD/pointer unions), `DirectInputAdapters.DataFormatToHost` (DIDATAFORMAT 24/32 with DIOBJECTDATAFORMAT 16/24 and copied GUIDs), DIDEVICEOBJECTDATA (20/24).
* **Calls**: `HostInvoke.Call` calls a native address with up to 14 pointer-sized integer arguments in the host convention (Win64). This is the path DirectSound already used
  (`HostComInterface.Invoke` now goes through it). The thread's last error travels with each bridged call (guest value in, host value out).
* **Never forwarded**: a guest pointer is never given to the host as-is and a host pointer never reaches guest memory. Strings and structures are native copies; host addresses
  that a structure would return (`lpSurface`, `uAppData`, a 64-bit `lParam`) are replaced, shadowed or refused by name.

## Handles

USER and GDI handles (HWND, HDC, HGDIOBJ, HCURSOR, HMENU, HMONITOR) are 32-bit significant on 64-bit Windows: the guest gets the truncated value, the host gets it sign-extended,
and `HostHandles.ToGuest` faults if a host value does not survive that round trip. Addresses that Win32 calls handles are mapped instead: a guest HINSTANCE stands for the host
executable's module handle (`HostExports.HostInstance`) and is remembered per window (`GWL_HINSTANCE`, `CREATESTRUCT.hInstance`); window procedures are guest addresses kept in
a table, never host pointers. With the bridge, DirectSound's `SetCooperativeLevel` takes these real HWNDs too.

## Window procedures (callbacks)

`RegisterClassA` registers the guest class on the host with one native procedure (`WindowProcBridge.NativeProcedure`, `[UnmanagedCallersOnly]`) and records the guest procedure by
class atom. For each message the native procedure finds the window (by HWND, or by class atom for the first messages of `CreateWindowExA`), converts it (`MessageThunks`) and enters
the guest with `GuestModule.CallAddress` on the calling thread: the nested-call path of `EnterGuest`, same TEB, the guest stack continues below the running import, so guest SEH
inside the procedure works as in `docs/binary-seh.md`. The guest's result is sign-extended.

* **Messages**: values and handles cross as values (a host value that does not fit 32 bits faults naming the message); pointer messages have thunks: WM_(NC)CREATE, WM_GETMINMAXINFO,
  WM_NCCALCSIZE (both forms), WM_WINDOWPOSCHANGING/CHANGED, WM_STYLECHANGING/CHANGED, WM_SIZING/MOVING, WM_SETTEXT/SETTINGCHANGE/DEVMODECHANGE, WM_GETTEXT, WM_GETDLGCODE.
  Known pointer messages without a thunk (WM_NOTIFY, WM_DRAWITEM, WM_COPYDATA, ...) fault by name. The guest works on a copy; its changes go back to the host structure.
* **DefWindowProcA** on the message being handled gets the host's original structure (with the guest's edits applied) and the guest copy is refreshed afterwards, so default
  processing (WM_NCCALCSIZE, WM_GETMINMAXINFO ...) works on real data. `PostMessageA` refuses pointer messages with ERROR_MESSAGE_SYNC_ONLY, as Windows does.
* **Faults**: a managed exception never unwinds through user32's frames. A fault in a guest procedure is recorded per thread, the host gets DefWindowProcA's answer, and every
  bridged call rethrows it when the host returns (`HostCallbackException`), so the guest call that dispatched the message fails by name.
* **Window longs**: `GWL_WNDPROC`/`GWL_HINSTANCE` come from the table (subclassing replaces the guest procedure); `GWL_USERDATA`/`GWL_ID`/`GWL_HWNDPARENT` use the `LongPtr`
  exports; extra bytes and styles use `GetWindowLongA`. A window not created from a guest class has a host procedure: asking for it faults.
* **Lifetime**: the context's disposal destroys the windows it still owns (on the owning thread) and unregisters its classes; messages that arrive later go to DefWindowProcA.

## COM objects

The DirectX objects are guest COM objects (`docs/binary-com.md`) over the native ones. `HostComRegistry` keeps **one guest object per host object**: identity is the host's
`QueryInterface(IID_IUnknown)` pointer, so a host interface returned again (`GetDDInterface`, `GetAttachedSurface`) is the same guest object with an AddRef. `QueryInterface`
for a new IID asks the host: refused = E_NOINTERFACE, accepted = a new face of the same object, accepted without an adapter table = a named fault. Each face holds one native
reference; the guest's last Release releases them all. Slots that are not bridged are `Absent` rows that fault naming `Interface::Method` (never an invented HRESULT).

Tables: IDirectDraw7 (CreateSurface, CreateClipper, EnumDisplayModes with callbacks, GetCaps, GetDeviceIdentifier, SetCooperativeLevel, SetDisplayMode, ...), IDirectDraw (version 1,
value methods), IDirectDrawSurface7 (Blt, BltFast, Flip, GetAttachedSurface, GetDC/ReleaseDC, Lock/Unlock, color keys, ...), IDirectDrawClipper, IDirect3D7 (EnumDevices,
EnumZBufferFormats), IDirectInput8A (CreateDevice, EnumDevices), IDirectInputDevice8A (SetDataFormat, SetCooperativeLevel, Acquire, GetDeviceState, GetDeviceData, properties).
Not bridged (named faults): palettes, overlays, private data, `IDirect3D7::CreateDevice` (IDirect3DDevice7 passes `float` arguments that the Win64 convention puts in XMM
registers: it needs typed adapters), force feedback, action maps, `d3dxof.dll!DirectXFileCreate`.

**Surface Lock**: guest Lock = host Lock (held until the guest unlocks, as on Windows) + a shadow in guest memory with the host's pitch, filled from the locked rows unless
DDLOCK_WRITEONLY; guest Unlock = rows copied back unless DDLOCK_READONLY + host Unlock. Formats without whole bytes per pixel are refused by name.

## Other APIs added with the bridge

winmm `timeGetDevCaps`/`timeBeginPeriod`/`timeEndPeriod` (host winmm on a Windows host, so the period really changes the host timer resolution; documented range and matching
end elsewhere); WS2_32 by ordinal for the functions WSOCK32 already had (same ordinals, checked against Wine's ws2_32 export table).

## Tests

`tests/BlitzCS.BinaryCompiler.Win32.Tests/HostWindowBridgeTests.cs`: layout round trips and refusals (every host); composition (bridge present exactly on Windows, `None`, `Host`
refused on Linux); a hand-assembled guest window procedure re-entered from host messages under Wine (WM_NCCREATE/WM_CREATE with converted CREATESTRUCT, window longs, posted
MSG round trip, a fault inside a dispatched message ending the guest call by name, WM_NCDESTROY on destroy); DirectDraw identity, lifetime, Lock shadow and colour-fill Blt
(`RequiresHostDirectDrawFact`: skipped with the reason when the host's ddraw.dll cannot load).
Under Wine, ddraw needs the vkd3d DLLs in the prefix: `BLITZCS_WINE_GRAPHICS=1` (`scripts/wine/env.sh`), for example
`BLITZCS_WINE_GRAPHICS=1 BLITZCS_WINE_PREFIX=$HOME/.cache/blitzcs/wineprefix-graphics scripts/wine/run-tests.sh BlitzCS.BinaryCompiler.Win32.Tests --filter HostWindowBridge`.
