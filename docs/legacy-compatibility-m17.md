# M17 — legacy contracts and SCP semantic progress

M17 adds external command (`Void`) support, typed declaration defaults and an executable `DebugLog` implementation. It resolves the `Windowed3D` **signature** barrier without claiming an executable graphics implementation. There is no renderer or window host in this milestone.

## Verified status

| Stage | Result |
| --- | --- |
| Original SCP sources | All 30 previously audited `.bb` hashes unchanged |
| Integrated syntax | `Main.bb` and its 23 Include targets validated |
| Integrated semantic analysis | Partial; now stops at `fullscreen_window_fix.bb:70:2`, `api_GetClientRect` |
| External bindings | Existing real runtime/audio bindings preserved; `DebugLog` added; 69 contracts remain declaration-only |
| Integrated game assembly | Not emitted |
| Game initialization/execution | Not performed |
| Isolated files producing assemblies | 3/30: `Difficulty.bb`, `FMod.bb`, `KeyName.bb` |

Before this milestone the integrated diagnostic was `Windowed3D` at `fullscreen_window_fix.bb:47:8`. Adding that verified declaration exposed `CountGfxModes3D` at `Main.bb:80:25`, followed by `DebugLog` at `Main.bb:116:3` and Win32 calls. A bounded source-driven discovery tool then added 69 declarations to the SCP catalog. This is progress in signature resolution, not 69 implemented runtime APIs.

The [SCP audit](evidence/m17-scp.json), [contract inventory](evidence/m17-contracts.json) and [validation summary](evidence/m17-validation.json) record the results. The source snapshot declares upstream revision `edb8fe0840b78f14d1aef3a0bf6174630e7be296`; unchanged hashes are verified, while independent provenance verification of that local snapshot remains outside the probe.

## Original graphics behavior

Reference: [Blitz3D source revision](https://github.com/blitz-research/blitz3d/tree/6beb288cb5962393684a59a4a44ac11524894939), especially `bbruntime/bbgraphics.cpp` and `gxruntime/gxruntime.cpp`.

| API | Historical contract | M17 managed status |
| --- | --- | --- |
| `Windowed3D()` | Integer 0/1; primary driver's supported Direct3D render depths intersect current desktop bit depth | Declaration only |
| `CountGfxModes3D()` | Integer count of 3D-capable modes | Declaration only |
| `Graphics3D width,height[,depth=0,mode=0]` | Void command; modes 0,1,2,3,6,7; other modes fail | Declaration only |
| `EndGraphics` | Void command; returns to 400×300 windowed 2D and selects front buffer | Declaration only |
| `Flip [vwait=1]` | Void command; booleanizes vwait, presents and services runtime idle | Declaration only |
| `SystemProperty("AppHWND")` | String form of the original process's actual Win32 window handle | Existing explicit unsupported-capability error without a native host |
| `DebugLog text$` | Void command; delivers a line to an attached debugger; release without a debugger produces no console message | Implemented through .NET trace listeners |

`Graphics3D` mode 0 depends on debug mode and `Windowed3D`. Modes 2/3 are windowed, 3 adds scaling; modes 6/7 add autosuspend. Only the mode-2 lifecycle and invalid mode 4 were executed here; the remaining mode behavior is source-inspected.

A fresh original V1.118 run under Proton/x86 and an isolated Xvfb display observed:

- `Windowed3D=1`, repeat stability, and the same result after graphics shutdown.
- A 160×120 mode-2 window with the back buffer selected.
- Completion of `Flip 0` and default `Flip`.
- `EndGraphics` returning 400×300 with the front buffer selected.
- Compile rejection of `Windowed3D(1)` and `DebugLog` used as an expression.
- Invalid mode 4 reporting `Illegal Graphics3D mode`, **exit code 0**, and no completion marker.
- A release program continuing before/after `DebugLog`, without a diagnostic in its normal output.

[Original evidence](evidence/m17-original.json) retains hashes and observed output. Capability 1 is a property of that observed environment, not a constant for other machines. These measurements do not verify pixel presentation, vsync timing, fullscreen modes, or a managed renderer.

The original runtime uses Win32/DirectDraw7/Direct3D7. Loading its x86 binary into the verified .NET x64 process is incompatible. A separate original-process worker would require an explicit command/resource protocol and separate license review of its dependencies. It was not introduced. A future in-process compatibility implementation must preserve the API while measuring real host capabilities; no backend is selected by M17.

## Implemented infrastructure and diagnostics

The generic external registry now accepts `Void` **results only**. Managed `void` methods emit direct calls; treating them as scalar values produces a BASIC source diagnostic before Roslyn. This enables historical commands without inventing Integer return values. The existing primitive C# bridge keeps its prior restrictions.

Signatures and JSON catalogs also accept typed trailing defaults, independently of executable bindings. Defaults are immutable and validated. Explicit defaults define BASIC call behavior; absent defaults preserve existing CLR optional metadata. The original three-argument signature constructor remains available. See [external signatures](external-signatures-m14.md).

`Blitz3DCS.Runtime.LegacyDiagnostics.DebugLog` sends a diagnostic line to the host's .NET trace listeners and truncates at embedded NUL, matching the original C-string boundary. It does not implement a debugger UI. The host controls listeners and their resources; listener rendering and encoding are not claimed equivalent to the original MFC debugger. The [hybrid example](../samples/Native/debug-log.bb) explicitly attaches a stderr listener and exercises BASIC→C# formatting, C#→BASIC calls and shared state. It prints `2` to stdout and two diagnostic lines to stderr.

The diagnostic command belongs to Blitz3DCS.Runtime. The compiler gained only language-independent external-call capabilities and has no engine dependency. Audio, managed object lifetime, handles, Includes and native interoperability keep their existing architecture.

## Declaration discovery and the next dependency

[scp_semantic_progress.py](../tools/scp_semantic_progress.py) advances the first semantic error using original declarations. It:

- Verifies original runtime source files against the existing hash catalog.
- Ignores commented registrations and rejects unsupported/conflicting contracts rather than guessing them.
- Preserves return types and trailing defaults, including literal BASIC backslashes.
- Reads fully typed scalar `.decls` signatures; missing result tags mean `Void`, as in the original user-library loader.
- Leaves pointer parameters pending and never generates executable placeholder methods.
- Bounds iterations and subprocess timeouts; protects inputs and native/managed libraries from output-path collisions.

The next unresolved call is `api_GetClientRect(hwnd%, lpRect*)`. It is a **native pointer/bank bridge dependency**, not a new grammar requirement. The wrapper creates a 16-byte bank and expects the API to fill a rectangle, then reads its fields. Passing a managed bank token as an arbitrary pointer would be incorrect. `api_GetDesktopWindow`, `AppHWND` and later window-style calls also require real host ownership and safe handle translation. No x64 pointers were truncated or exposed as BASIC Integer values.

The SCP catalog contains scalar Win32 declarations for analysis, but those declarations are not an ABI binding and cannot execute. Even resolving the rectangle bridge alone will not provide graphics initialization or complete game execution.

## Validation and reproduction

- **662 .NET tests** pass in both Release and Debug; builds have zero warnings/errors and no skipped tests.
- **53 Python tests** pass; **89 local fixtures** pass (86 previous plus three new).
- The previous **85/86** genuine differential results are preserved, including the known `upper-bound-error` exit-status divergence.
- **3/3 new bounded comparisons** match fresh original captures: two compile rejections and release `DebugLog` output. Combined coverage is 88/89; this does not include a managed graphics lifecycle comparison.
- Existing hybrid, native ABI/publication, memory/handle and SDL audio checks passed.

The original programs were run genuinely during this milestone. Regression replay reuses the earlier captures with source hashes checked. [New comparisons](evidence/m17-new-comparison.json) and [regression replay](evidence/m17-regression-differential.json) distinguish those scopes. Graphics capability/lifecycle evidence remains original-only.

```sh
bash scripts/verify.sh
dotnet build BlitzCS.slnx -c Debug --nologo
dotnet test BlitzCS.slnx -c Debug --no-build --nologo
python3 -m unittest discover -s tests/differential -v
```

The full Release pipeline and its initial M17 graphics/diagnostic checks passed. The subsequently appended three-fixture and comparison commands were executed separately; unchanged broad tests were not repeated solely for that script addition.

For the unchanged game snapshot:

```sh
python3 tools/scp_compile_probe.py --source-dir "$SCP_SOURCE" \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Audio/scp.json --check-syntax \
  --report artifacts/m17/scp-integrated.json
```

To reproduce declaration discovery from executable bindings rather than silently treating catalog declarations as implementations:

```sh
python3 tools/scp_semantic_progress.py --entry "$SCP_SOURCE/Main.bb" \
  --runtime-source-dir "$BLITZ3D_SOURCE/bbruntime" \
  --bindings samples/Audio/scp.json --only-bound --max-additions 256 \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --catalog-output artifacts/m17/semantic-catalog.json \
  --report artifacts/m17/semantic-progress.json
```

Set these variables to separately obtained legitimate sources. Missing originals are not replaced with synthetic results. For the native probe, set `BLITZ3D_HOME`, `BLITZ3D_WINE`, and `BLITZ3D_WINEPREFIX` and run `tools/probe_windowed3d_original.py --report artifacts/m17-original/report.json --evidence-dir artifacts/m17-original/raw`. It needs Xvfb and xdotool; it uses an isolated SDK copy and its own display, not the user's desktop.

## Remaining priorities

1. Design and test a safe managed-bank/native-rectangle bridge, including invalid handles, bounds, disposal and source diagnostics.
2. Provide real desktop/window ownership and a compatible solution for legacy Win32 consumers; preserve explicit unsupported errors where no equivalent exists.
3. Implement and differentially validate the minimum historical graphics lifecycle only when its host dependencies are ready.
4. Continue unchanged SCP compilation from each observed error, then distinguish final binding, assembly emission and functional execution.

Legacy behavior comes first. Vulkan, DirectX 12, PBR, RTGI and other modernization are outside this milestone and remain deferred until a functional legacy baseline exists.
