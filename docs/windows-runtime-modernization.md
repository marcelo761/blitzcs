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

## Multisampling (`AntiAlias 1`) design

`AntiAlias 1` still fails explicitly, and `AntiAlias 0` is accepted. Setting the swapchain's sample
count alone is not a working design. The renderer currently treats the swapchain buffer as the single
back buffer for every operation:

- 2D drawing
- `CopyRect` from and to `BackBuffer`
- Pixel locking and readback
- Blit scratch copies

D3D11 does not allow `CopyResource` or `CopySubresourceRegion` between textures with different sample
counts, so each of these operations needs its own MSAA-aware path.

The planned design:

1. **Sample count.** Query `CheckMultisampleQualityLevels(R8G8B8A8_UNorm, n)` and the D32 depth format
   for n = 4, then 2. Use the highest count supported by both. If neither is supported, keep the
   explicit failure.
2. **Render targets.** Keep the swapchain single-sampled, with flip-model compatibility. Render 3D and 2D
   into an MSAA color texture and an MSAA depth texture of the same count. Recreate both on resize and
   `Graphics3D`.
3. **Resolve.** Resolve the MSAA texture with `ResolveSubresource` into the swapchain buffer before
   `Present`. Also resolve before any operation that reads the back buffer: `CopyRect` sources, locks,
   `ReadPixel` and readback staging.
4. **Writes into the back buffer.** `CopyRect` destinations, `WritePixel` and unlock uploads must draw a
   textured quad into the MSAA target, because copying into a multisampled resource is invalid. Clears
   apply to the MSAA views.
5. **Texture targets.** `SetBuffer TextureBuffer(...)` stays single-sampled.
6. **Validation.** Before claiming support:
   - Compare edge-coverage pixel tests on Win32 and Win64 with `AntiAlias 0`.
   - Run readback and `CopyRect` round trips after a resolve.
   - Test resize, and the fallback when the hardware lacks support.

   The original D3D7 `AntiAlias` setting is a driver-dependent render state, so original captures should
   determine whether exact pixel parity is expected.

This touches every back-buffer path in `D3D11Renderer`, which is well over 150 lines. It is therefore
deferred rather than implemented in a minimal change.

## CreatePlane decision

Decision: implement `CreatePlane` next, as its own increment. It is not part of the struct-ABI change,
and it should not be added as a throwing catalog placeholder. `CreatePlane` is now the first
semantic blocker on both targets (`Main.bb:8592`, the dimension 1499 event).

That event depends on four plane behaviors: texturing (`EntityTexture`), placement
(`PositionEntity`) and polygon collision (`EntityType` with `HIT_MAP`). The plane must also be
effectively infinite.

The minimum faithful scope:

- **Entity.** A plane entity accepts the `segments` and `parent` arguments.
- **Rendering.** The plane renders as a camera-centred grid of segments, with legacy UVs that tile per world unit.
- **Collision.** Infinite-plane collision participates in swept-sphere responses and picking, without
  being added to the triangle BVH.

This is a scene and collision subsystem, not a catalog entry. Each part needs pixel and collision tests
before it is bound.

A scratch, declaration-only probe that did not change the tracked catalogs shows what follows. After
`CreatePlane`, the next blockers are:

- `ErrorLog()` (`Main.bb:11689`), a function of the modified game runtime.
- `BlitzMovie_*` (`Main.bb:11876`), video playback through a third-party x86 DLL.

`BlitzMovie_*` is another separate subsystem.
