# Windows runtime modernization

## Selected direction

The compiler selects compatible runtime implementations from the compilation target. BASIC command semantics remain the compatibility contract; the new Windows execution path must not require translating the legacy PE32 engine with the BRE.

| Target | Graphics | Keyboard/mouse input | Audio |
|---|---|---|---|
| Win32 | Direct3D 11, native x86 | SDL3, native x86 | FSOUND compatibility engine, SDL3 output and libsndfile x86 |
| Win64 | Direct3D 11, native x64 | SDL3, native x64 | FSOUND compatibility engine, SDL3 output and libsndfile x64 |

SDL is the input/windowing and audio-output dependency on both Windows targets. libsndfile supplies decoding; channel management and legacy playback semantics belong to the compatibility engine above those libraries. Other libraries retain their current bindings unless a matching-target equivalent is required. Native dependencies must have matching architecture and explicitly implemented semantic adapters where APIs differ.

The graphics change covers both Windows architectures. It does not select a Direct3D backend for Linux32/Linux64; those targets require their own platform implementations. The compiler's target-selection contract should support all four target names without claiming unavailable backend support.

## Compatibility boundary

Compilation binds typed BASIC calls to the runtime chosen for the target. Runtime implementations own scene state, resources, handles, rendering, input and audio semantics. A graphics API is not an implementation of the scene graph, collision queries, animation or mesh loaders.

Managed BASIC handles remain integer IDs into runtime-owned tables. Native pointers are pointer-sized and must never be truncated into BASIC integers. The runtime must preserve observable defaults, units, resource lifetime, errors and ordering; shared function names alone do not establish equivalence.

The SDL window is shared with the graphics backend. The renderer obtains its HWND from the active SDL window, and events are pumped on the owning thread. A second input window or competing event pump is not part of the design.

## Implementation and validation

This is an implementation direction, not a declaration that the full replacement engine exists. Validation proceeds through:

1. Target-specific binding selection with explicit diagnostics for missing profiles and wrong-architecture dependencies.
2. A real D3D11 device, swapchain, rendering/readback and shutdown on both Windows bitnesses, sharing the SDL window.
3. Semantic compatibility for the engine commands used by the application, including scene operations, loaders, animation and collision behavior.
4. Native input and audio tests for both architectures, comparing observable playback behavior with the legacy FMOD reference. Missing codecs, native runtimes and unsupported audio features must be explicit failures rather than silent omissions.
5. Unchanged application compilation, load, gameplay and shutdown, followed by the paired measurements described in the [BRE application cost baseline](binary-runtime-cost.md).

The existing SDL input and managed FSOUND implementation provide reusable pieces. Decoder/output availability alone does not implement spatial audio or all FMOD behavior. Likewise, a triangle smoke test does not establish complete Blitz3D rendering semantics.

Audio compatibility requires a feature-by-feature record and tests for simultaneous channels, per-channel volume and pan, loop boundaries, pause/resume/stop, sample-rate conversion, positional 3D audio, distance attenuation, Doppler, bounded-memory file streaming and any DSP behavior actually requested by the application. Full-file decoding must not be described as bounded-memory streaming. Unsupported DSP controls must not be accepted as no-ops. Native WAV/Vorbis playback must be verified against the packaged decoder on both architectures; a codec supported on the development host is not sufficient evidence for the Windows package.

The [audio compatibility contract](audio-runtime-compatibility.md) separates these
checks from decoder availability and records the limits of headless output tests.

Existing legacy packages remain available during validation. Integrating the replacement and deprecating the BRE require demonstrating the complete application path; neither follows merely from adding backend classes or changing a target label.

## Procedural texture migration boundary

The application's `CreateTexture` calls require a complete buffer-to-material
path. Creating an empty shader resource alone does not implement those calls.
The observed chains in `Main.bb`, `MapSystem.bb` and `AAText.bb` include:

- `TextureBuffer` and `SetBuffer` followed by `Cls` to initialize procedural
  textures, then `EntityTexture` to sample them on geometry.
- `CopyRect` from `BackBuffer` into a texture buffer for monitors and screen
  resizing. This should remain a GPU-to-GPU copy rather than introduce a
  synchronous readback and upload every frame.
- Locked texture buffers with `WritePixelFast` for the alpha font atlas.
- Texture blend modes 3 and 5 for the observed additive and ambient overlays.

Legacy flag 256 requests video memory; it does not mean render target. Relevant
observed combinations are 257 (RGB and video memory), 259 (RGB, alpha and video
memory), 3 (RGB and alpha), and 256 with clamp-U/clamp-V flags 16 and 32. Frames
default to one and frame indices start at zero. Texture values stored by a
brush retain the underlying resource after the caller frees its texture handle.

The renderer now provides mutable RGBA8 resources, explicit buffer selection,
retained material references, GPU clears and GPU copies. Pixel locking uses a
CPU shadow and explicit upload; ordinary clears and monitor copies stay on the
GPU. Direct scene rendering into texture buffers additionally needs
render-target and depth-state handling; the corresponding portal call observed
in the application's main path is commented out.

Distinct image and texture buffers can be locked simultaneously, including the
heightmap and mask used by forest generation. Buffer identity and dimension
queries remain available while locked; locking the same buffer twice fails.
Native tests exercise independent reads, writes and unlock order on both ABIs.

Texture values share their underlying state in the original engine. Changing
blend mode, coordinate selection or UV transformation therefore affects brushes
that already reference that texture. Brush painting itself copies brush values:
later replacement of a brush's texture stage does not replace an already painted
surface's stage. Both forms of sharing must retain resources after caller handles
are freed.

Black-key texture masking (flag 4) replaces decoded alpha with transparent black
and opaque nonblack pixels. Masked materials use alpha testing with depth writes;
the cutoff follows material alpha and the vertex-alpha effect. Native pixel
tests on Win32 and Win64 verify holes, occlusion and cutoff behavior.
Material blend selection follows the original distinction between a single
transparent texture, explicit alpha blending with multiple stages, and
vertex/material alpha. Masked textures alone do not request alpha blending;
explicit multiply/add modes retain priority.
Texture flag 8 creates a full GPU mip chain. Writes to mutable textures invalidate
the chain, which regenerates before sampling; mip-zero readback and copies remain
compatible with single-level staging resources. Native tests cover minification
and regeneration after clears, uploads, copies, draws and self-blits on both ABIs.

Texture stage blend 1 interpolates RGB by the texture alpha. Blend 6 interprets
RG as signed bump deltas and applies the configured matrix to the following
stage's UVs, without changing its own stage color or alpha. Native tests cover an
asymmetric matrix and the following stage's secondary UV set. The plain bump
mode does not use the luminance scale/offset controls.
Image color-key masking replaces the previous key instead of accumulating
transparent colors. Remasking a black-keyed image with yellow therefore restores
opaque black pixels, as required by the menu's dark button interiors. Regression
tests cover this sequence and the unchanged RGB values.
The image decoder also reads DXT1, DXT3 and DXT5 DDS textures, including the
1024-by-1024 forest texture exercised from the game assets. Tests cover block
alpha, partial edge blocks and malformed input bounds.

## Model and world integration

The managed B3D parser preserves node transforms, materials, both texture-coordinate
sets, animation tracks and bone weights. Static `LoadMesh` collapses the hierarchy
into mesh surfaces. `LoadAnimMesh`, playback commands and world updates use
reusable pose and skinning buffers. Skinned meshes keep per-instance dynamic GPU
vertex buffers: copied actors share base geometry while retaining independent
playback, deformed vertices and GPU buffers. Animation changes only keyed local
channels, preserving movement applied by the game to unkeyed roots.

The DirectX X reader supports text and binary MSZIP model data used by the game,
preserving frame transforms and hard-edge corner normals. Static loading ignores
animation templates as the original collapse path does; requesting keyed or
skinned X animation explicitly fails until that animation format is implemented.

Rendering implements legacy Gouraud lighting, the first eight visible lights in
hierarchy order, ambient light, directional/point/spot lights, shininess, linear
fog and the supported entity effects. Native pixel tests cover material lighting,
white specular highlights, fullbright, vertex colors, double-sided and flat
shading, fog and explicit multiply/add blending. Animated SCP-939 models and
independently timed copies have also passed native rendering tests on both
Windows architectures.

Collision rules support swept sphere, polygon and box targets and collision
responses. The Windows world update attaches mesh triangles when polygon rules
require them and refreshes that geometry after edits. Polygon targets use a
cached local-space BVH and conservative swept-volume queries; candidate triangle
order is preserved before narrow-phase tests. Static rendering also rejects
meshes outside the camera clip volume using cached local geometry bounds.

The Windows adapter caches which meshes require polygon geometry, invalidating
membership when collision rules, pick modes, entity types or mesh membership
change. Repeated queries check geometry versions only for those selected meshes.
Regression tests cover copying, freeing, same-count replacement and vertex edits.
The scene graph's own query traversal remains separate from this cache.

The warmed collision-update fixture (one moving source, 32 targets, 256 updates)
previously allocated 13,752 bytes per update; reusable traversal/type buffers
reduce that fixture to zero managed allocations. Regression tests also check
BVH results against linear collision tests and verify allocation-free warmed
queries, pose sampling and skinning. These isolated results are not game FPS or
loading-time measurements.

These components do not establish full game compatibility. Unsupported material
effects, remaining loaders/commands and complete application startup
and gameplay must be validated before replacing the legacy packages or claiming
a performance improvement.

## Multisampling (`AntiAlias 1`)

The renderer negotiates a common 4x or 2x sample count for RGBA8 color and D32
depth. It reports an explicit failure if the device supports neither combination.
The swapchain remains single-sampled; optional multisampled color/depth targets
receive both 3D and backbuffer 2D drawing.

The color target resolves before presentation, readback and backbuffer-source
copies. Backbuffer writes use textured GPU draws to avoid copying between
incompatible sample counts. Texture buffers remain single-sampled. Resize
recreates the targets. Switching anti-aliasing preserves the full current frame,
including when a custom viewport is active, using a GPU snapshot and restore.

Native Win32 and Win64 tests compare edge coverage with AA disabled, exercise
readback, backbuffer/texture copy round trips, pixel uploads, presentation and
resize, and verify exact color preservation across both toggle directions.
The original D3D7 anti-alias state was driver dependent; these checks establish
working multisampling rather than exact pixel parity with every legacy driver.

## Infinite planes and remaining startup bindings

`CreatePlane(segments, parent)` now creates an effectively infinite plane. Rendering
clips the camera's far-frustum grid against the local plane and projects it onto
local Y=0, with local X/Z texture coordinates. Reusable CPU vertex arrays and a
persistent dynamic GPU buffer keep geometry updates bounded. Cameras on or below
the plane see no plane geometry, matching the original one-sided implementation.

Polygon collision and picking use an analytic infinite-plane intersection instead
of the generated render triangles or a BVH. Copies retain the plane behavior and
have independent render buffers; parent transforms and swept sphere responses
are covered by focused tests. Exact endpoint/tie intersections follow the original
plane's strict collision-time comparison.

Native Win32 and Win64 pixel tests cover texture binding, camera movement 50,000
units from the origin, and the invisible underside. CPU tests cover horizon
clipping, planar UVs, allocation-free warmed grid generation, transformed collision,
and copied-plane picking without a world/render step.

`ErrorLog()` now drains a FIFO of recoverable error messages and returns an empty
string when drained. Fatal `RuntimeError` remains fatal. Image/model load misses
retain their original silent zero return and do not enqueue invented diagnostics.

The movie bindings now use a bounded-memory Media Foundation Source Reader and
update the target image as the application requests its buffer. Playback timing,
pause/stop and looping are independent of the game frame rate. The AVI soundtracks
used by the application are separate OGG streams. The two packaged AVIs use
MPEG-4 Part 2; no legacy x86 movie DLL is loaded by this implementation.
Native tests with an injected decoder verify image upload, timed frame changes
and decoder disposal on Win32 and Win64; these tests do not validate a codec.

The unchanged application passes semantic validation and builds for Win64 without
BRE dependencies. With startup movies disabled in the application configuration,
the Win64 package has rendered the menu and entered procedural map generation,
including forest terrain. Full gameplay remains unverified.
The decoder opens the actual AVI files under the tested Proton
runtime, but RGB output negotiation fails with `MF_E_TOPO_CODEC_NOT_FOUND` on both
architectures. Frame decoding remains unverified on native Windows. Successful
compilation and isolated graphics tests do not establish successful full game
startup or a gameplay performance result.
- `Blitz3DCS.Engine.Windows.Tests --instancing-smoke` renders additive sprites, lit instanced mesh runs and a blended mesh run with structural pixel checks. Its log (`BLITZCS_ENGINE_WINDOWS_SMOKE_LOG`) holds `passed`, a `hash=` line and per-pass `pass.*`/`regions.*` lines; run it by default, with `BLITZCS_D3D11_NOINSTANCING=1` and with `BLITZCS_D3D11_NOOPT=1` and compare the logs byte for byte.
