# M20 — native window hosting and keyboard/mouse input

M20 introduces an SDL3 desktop window and the 18 historical keyboard/mouse APIs through `Blitz3DCS.Windowing`. The compiler continues to resolve these calls through external binding metadata. Window creation is explicit: this milestone supplies input hosting without a renderer, `Graphics`, `Graphics3D`, drawing commands or `Flip`.

## Run the example

Build the solution in Release and run the [BASIC/hybrid example](../samples/LegacyInput/window-input.bb) with the input catalog:

```sh
dotnet build BlitzCS.slnx -c Release
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/LegacyInput/window-input.bb -o artifacts/m20/window-input.dll \
  --externals samples/LegacyInput/runtime.json
dotnet artifacts/m20/window-input.dll
```

The program requires an SDL3 native library and a desktop display. It opens a 640×480 window, positions the cursor, clears buffered input and prints subsequent input in the launching console. Focus the window before typing. Escape or closing the window exits. A C# statement increments the BASIC global `inputevents`; BASIC prints the same value, demonstrating shared BASIC/C# state without changing the input binding path.

## Bootstrap and ownership

These five extensions establish the native host independently of the historical graphics APIs:

| Extension | Contract |
| --- | --- |
| `Blitz3DCSOpenWindow(width,height,title$)` | Opens the single process input window; all three arguments are required. |
| `Blitz3DCSCloseWindow()` | Releases the window and its input host. |
| `Blitz3DCSPumpEvents()` | Processes queued native events. |
| `Blitz3DCSWindowOpen()` | Returns integer 1 while the window is open, otherwise 0. |
| `Blitz3DCSWindowFocused()` | Returns integer 1 while the window has keyboard focus, otherwise 0. |

Create, poll and close the host on its owning application thread. The backend permits one window event host per process and rejects event-pump reentry. It owns SDL video/event subsystem references separately from SDL audio. A missing native library or unavailable display is a real initialization failure; no replacement host is selected silently. Window close and quit requests become observable through `Blitz3DCSWindowOpen()`.

## Historical API contracts

| Family | APIs |
| --- | --- |
| Keyboard | `KeyDown`, `KeyHit`, `GetKey`, `WaitKey`, `FlushKeys` |
| Mouse buttons | `MouseDown`, `MouseHit`, `GetMouse`, `WaitMouse`, `MouseWait`, `FlushMouse` |
| Mouse position and motion | `MouseX`, `MouseY`, `MouseZ`, `MouseXSpeed`, `MouseYSpeed`, `MouseZSpeed`, `MoveMouse` |

Keyboard indexes are historical PC/DirectInput scan codes, rather than SDL scancode numbers. Escape is 1 and `a` is 30. Mouse buttons are left 1, right 2, middle 3. `KeyDown` and `MouseDown` return 0 or 1. A hit query returns the accumulated press count and clears that counter only. Releasing a key or button clears down state without erasing its hits.

Each device has a separate 32-entry FIFO. Keyboard FIFO entries remain scan codes until `GetKey` translates them; mouse entries are historical button numbers. Reading either FIFO leaves hit counters intact. When full, a FIFO drops newly arriving entries while still updating down and hit state. OS keyboard auto-repeat is ignored. Flush pumps pending input before clearing the device FIFO and hit counts; held state and mouse axes survive.

`GetKey` returns one legacy character code, or zero for an empty FIFO or an untranslatable scan. Translation uses modifier state at read time. Navigation codes are Home 1, End 2, Insert 3, Delete 4, PageUp 5, PageDown 6, Up 28, Down 29, Right 30 and Left 31. `WaitKey` continues past zero translations; `WaitMouse` waits for a button, and `MouseWait` is its alias. Waiting processes native events rather than requiring a render loop. Closing the window while waiting throws `OperationCanceledException`; managed callers may also supply a cancellation token. This explicit modern error contract replaces terminating the original process from its idle loop.

Position reads do not consume motion. Each speed function subtracts its own last-read baseline and updates only that baseline. `MoveMouse` sets X/Y and their speed baselines, so an immediate speed query is zero. Wheel motion accumulates in 120-unit increments per notch; `MouseZ` returns whole notches with truncation toward zero. `FlushMouse` preserves wheel position and every speed baseline.

The pure state machine supports a device reset that clears held state, hits, FIFOs and axes while preserving speed baselines. The state machine itself preserves input on focus notifications. The real SDL X11 backend synthesizes key-up events on focus loss: the captured held-key case therefore returns 0, while the original mode-2 runtime returns 1. This is an observed compatibility divergence. Filtering unknown/virtual releases would risk losing genuine input and creating duplicate hits on refocus, so M20 does not apply that heuristic. Focus status is separately available through the bootstrap extension.

## Compatibility boundary and deliberate safety choices

The [original contract report](m20-original-input.md) separates genuinely executed Blitz3D 1.118 observations from source-derived behavior. Its [capture](evidence/m20-original-input.json) and [owned reference fixture](../samples/LegacyInput/reference-input.bb) cover buffered hits, flush, navigation codes, modifier timing, repeated keydown suppression, focus retention, motion, wheel and a bounded wait. Original-runtime execution is evidence about those cases, not proof that every SDL layout or operating system behaves identically.

SDL layout translation and a legacy one-byte return value have a narrower contract than Unicode text input. Less common Ctrl combinations, IME composition, arbitrary Unicode, dead keys, every locale and fullscreen DirectInput acquisition are outside this milestone's parity claim. Joystick APIs, renderer-dependent input modes and autosuspend graphics modes are not implemented here.

Invalid scan codes outside 1–255 and mouse buttons outside 1–3 return zero or are ignored. The historical native implementation used unchecked array indexes; reproducing unsafe memory access is intentionally excluded. Hit counters saturate at `Int32.MaxValue` rather than entering native signed-overflow behavior. Raw wheel accumulation and speed subtraction use explicit managed integer wraparound at their representable limits; this does not claim the original floating-point axis precision for extreme accumulated values.

`SystemProperty("AppHWND")` remains unsupported. An SDL window is not a portable Win32 HWND, and this milestone does not provide `GetClientRect` or a fabricated handle to satisfy native Win32 adapters. Games that depend on those adapters or on rendering still require the corresponding real subsystems.

## Verification scope

The [pure state tests](../tests/Blitz3DCS.Windowing.Tests/LegacyInputStateTests.cs) cover FIFO ordering and overflow, independent hit consumption, flush preservation, wheel scaling, speed baselines, reset behavior, invalid indexes and allocation-free event/poll loops. They run without native window creation:

```sh
dotnet test tests/Blitz3DCS.Windowing.Tests/Blitz3DCS.Windowing.Tests.csproj \
  --filter FullyQualifiedName~LegacyInputStateTests
```

That focused suite passed 19 cases after first failing because the state type did not exist. Its allocation check warms the operations and measures 1,000 event/poll cycles using `GC.GetAllocatedBytesForCurrentThread`; the measured delta is zero. This establishes the pure state path's allocation behavior, not whole-application input latency or native event-pump allocation cost. Console formatting and text input decoding may allocate.

The [input benchmark](../benchmarks/Blitz3DCS.Input/Program.cs) records five rounds after 20,000 warmup calls, validates deterministic checksums and emits median nanoseconds and calling-thread managed allocation bytes as JSON. The pure state cycle includes key/button events, FIFO and hit reads, movement and wheel speed queries. `--native` additionally initializes the real SDL dummy video driver and measures an empty event pump and a `KeyDown` poll. The dummy driver requires SDL3 but no desktop display; it does not establish desktop-driver throughput or end-to-end input latency.

```sh
dotnet run --project benchmarks/Blitz3DCS.Input -c Release -- 500000
dotnet run --project benchmarks/Blitz3DCS.Input -c Release -- 500000 --native
```

The benchmark creates no timing acceptance threshold and measures no native allocation totals. Run with `--no-build` after building if its JSON output is redirected to an evidence file.

Native host integration, generated BASIC program execution, unchanged-project regression checks and the SCP audit require their own executed evidence. Their final counts and next blocker belong to the milestone validation report; the focused state result does not establish game initialization, frame-rate performance or complete Blitz3D compatibility.

## Win32 integration still required

The SCP wrapper calls `SystemProperty("AppHWND")` and user-library Win32 APIs such as `GetClientRect`; these are distinct from historical Blitz3D commands. The existing RECT adapter contract passes a checked bank token, not a raw BASIC pointer. A future Windows implementation must obtain a genuine HWND from its Windows host, retain pointer-sized native values internally and resolve any legacy Integer tokens through an owned handle table before invoking Win32. Its ownership and invalidation must follow the host lifetime. An X11/Wayland window identifier cannot satisfy that contract. No such adapter is claimed here, and the compiler remains unchanged.

## Reproduced measurements

The [input benchmark capture](evidence/m20-input-benchmark.json) records 500,000 operations per round after 20,000 warmup operations, with five measured rounds and checked results. On this Linux x64 host, the pure event/poll cycle measured a median 27.74 ns; an empty SDL dummy pump measured 1.16 µs and a complete dummy `KeyDown` poll 1.13 µs. All four measured paths allocated zero managed bytes on the calling thread. Native allocations, physical input latency and renderer performance are not measured. These are local measurements rather than CI timing thresholds or a comparison against original Blitz3D speed.

The [interactive demonstration capture](evidence/m20-demo.json) records a successfully emitted assembly, an actual SDL X11 window on an owned Xvfb display, XTest movement to 100,120, `GetKey` returning 97 for `a`, a left click returning 1, and Escape exiting with code 0. The printed shared BASIC/C# counter reached 2. This proves native input and hybrid state in that program, without a rendered frame or physical desktop/hardware claim.

## Integrated SCP progress

The [before/after audit](evidence/m20-scp.json) records unchanged SHA-256 hashes for all 30 original `.bb` files. `Main.bb` and 23 Include targets pass syntax analysis. The integrated semantic check and build both stop at `fullscreen_window_fix.bb:95:2`, unknown external symbol `RenderWorld`, after the previous `MoveMouse` failure at line 84. This is an engine dependency. Standalone compilation remains 5/30; the other first-error categories remain two syntactic, nine semantic/type, twelve unregistered native symbols and two external dependencies. These are first errors per file, not an exhaustive count of all project errors.

No integrated game assembly was emitted, no integrated dependencies were linked and no game initialization or execution was attempted. Known graphics declarations, executable input bindings, native host functionality and scoped historical comparisons are separate coverage claims. The five bootstrap extensions are deliberately absent from the historical registration count.
