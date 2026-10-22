# Blitz3DCS initial architecture

> Sequencing update (M17): this is the historical M11 architecture proposal.
> Current development prioritizes the original Blitz3D API and unchanged SCP
> compilation/execution. Modern backends and rendering features are deferred
> until that legacy baseline is demonstrated. See the [M17 report](legacy-compatibility-m17.md).

**Status:** proposed architecture; renderer and graphics runtime are not implemented.

M12 adds a real opt-in filesystem/system/string query module in
`Blitz3DCS.Runtime`, described in [native-runtime-m12.md](native-runtime-m12.md).
This establishes the generic static binding and managed publication path;
it does not implement any graphics backend or engine services proposed below.

M13 adds [generic native interop](native-interop-m13.md) outside the compiler:
explicit typed host bindings and native asset publication. The executed profile
is Linux/Cdecl; FMOD Win32 remains a separate compatibility/integration problem.
This is a reusable binding foundation, not a graphics backend.

Blitz3DCS is planned as a compatibility layer that preserves the historical public
Blitz3D programming API while supplying a modern managed runtime. Its first graphics
backend is planned for Vulkan 1.4, called from C#/.NET through native bindings where
needed. The design keeps the compiler independent from engine services and keeps
public BASIC commands independent from graphics API calls. D3D12 and Metal are
possible later backends behind the same engine-facing contract.

This document records intended boundaries. It does not claim that any listed engine,
physics, lighting, AI, or renderer feature exists today.

## Current repository boundary

BlitzCS is the BASIC language and compilation infrastructure. The compiler parses
source, performs semantic analysis, emits C#, and the CLI uses Roslyn to create a
.NET assembly. Blitz3DCS is intended to consume BlitzCS; the compiler must not depend
on Blitz3DCS, a renderer, Vulkan, or any other engine module.

The repository already has an opt-in `ExternalFunctionRegistry`. A host supplies
registered public static .NET methods, and compilation binds calls to direct,
typed calls. The current external contract is deliberately small: `Integer` maps to
`Int32`, `Float` to `Single`, and `String` to `String` for parameters and returns.
It does not currently pass engine objects, arrays, resource references, callbacks,
or native handles through the registry. The CLI's external catalog selects exact
methods explicitly; engine symbols are never invented or registered implicitly.
See [external-functions.md](external-functions.md) and
[external-functions-design.md](external-functions-design.md).

The generated program is a .NET assembly and does not need the BlitzCS CLI to run.
Programs that bind Blitz3DCS APIs will also need the corresponding runtime
assemblies available when they execute. An engine host can reference the existing
scalar registry contract, but a full Blitz3D-compatible runtime will need a
deliberate resource and lifetime contract before non-scalar values are exposed.

## Intended module boundaries

```text
BlitzBasic source
        │
        ▼
BlitzCS compiler ── emits ──> .NET program ──calls──> Blitz3DCS compatibility API
                                                  │                     │
                                                  │                     ▼
                                                  │               Engine runtime
                                                  │                 ├── graphics abstraction
                                                  │                 │     └── Vulkan 1.4 backend (first)
                                                  │                 ├── asset/resource lifetime service
                                                  │                 ├── planned physics and animation services
                                                  │                 └── planned AI and other engine services

Build host supplies the compiler with an explicit external-symbol registry and
assembly references as compilation inputs. The compiler consumes generic .NET
metadata; it has no project or runtime dependency on Blitz3DCS or engine modules.
```

Blitz3DCS owns the historical API surface and compatibility semantics. Its API
facade translates calls and arguments into engine-level operations. The engine
runtime owns application/session state, scheduling, resources, and service
composition. A graphics backend owns graphics-device objects and translates the
graphics abstraction into its native API. The backend must not redefine BASIC
syntax or embed legacy command names into Vulkan-facing code.

The compatibility facade should keep legacy names, signatures, defaults, and
observable behavior where they are established. New functionality may be offered
through explicit extensions, but extensions must not silently change existing
command behavior. Until a command has an implementation and a verified contract,
it is absent from the exposed registry/API rather than represented by a stub.

## Graphics backend contract

The engine-facing graphics contract should describe engine concepts and work
submission, not Vulkan types. It is a boundary for device discovery and selection,
window/surface presentation, resource creation and upload, frame execution,
synchronization, and device-loss reporting. The contract should be kept narrow enough
that a D3D12 or Metal implementation can satisfy it without emulating Vulkan
objects. Vulkan objects, extension names, queue-family indices, and Vulkan result
codes stay inside the Vulkan backend and diagnostic layer.

Vulkan 1.4 is the initial backend target, not a claim that every 1.4 feature is
available on every device. Startup should query loader and physical-device versions,
extensions, properties, and feature support, then select a documented feature
profile. A feature is usable only when the backend enables the required core feature
or extension. Missing requirements produce a specific startup/capability error;
optional capabilities are reported to the engine so a higher-level feature can
choose a fallback or report that it is unavailable. The capability model belongs
to the engine/backend boundary and must not leak Vulkan feature structs into the
historical Blitz3D API.

Prefer Vulkan core functionality when it covers the need. Add an extension only
when a concrete feature requires it, record its required-versus-optional status,
and check both extension and feature support before enabling it. This follows the
Khronos Vulkan 1.4 guidance to query supported versions and features and replace
promoted extension paths with core functionality where applicable. The Vulkan
versions and porting guide also identifies timeline semaphores and dynamic rendering
as useful modern patterns; these remain backend implementation choices, not promises
of this engine until implemented. See the [Vulkan versions and porting guide](https://docs.vulkan.org/guide/latest/versions.html),
the [Vulkan 1.4 feature proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_VERSION_1_4.html),
and the [Vulkan API specification](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html).

The .NET integration should bind the C Vulkan API through a private backend layer,
using maintained or generated bindings and a small amount of handwritten native
interop only where required. Binding choice is an implementation decision, not a
public engine API decision. Native structs, pointers, dispatch handles, and allocator
callbacks must be contained there. The engine-facing boundary should use managed
types and opaque engine resource identifiers, with explicit conversion at the
backend boundary.

## Calls, threads, and resource lifetime

The compatibility API is expected to be called primarily from the BASIC program's
execution thread. It should preserve call order and synchronous results where the
historical API makes them observable. The renderer may own a dedicated thread for
window events and graphics queue submission; calls that touch thread-affine backend
state must be routed to that owner or recorded into a defined frame command stream.
Background asset loading and CPU preparation may run concurrently, but must publish
completed resources at explicit synchronization points. The API contract must state
which operations block, which may complete later, and how errors return to the
calling program.

Native resources require deterministic ownership. The runtime should own each
backend object, associate it with an opaque engine resource identifier, and release
it on explicit legacy deletion or runtime shutdown. If identifiers are exposed as
legacy `Integer` values, the internal table must validate them and reject stale or
wrong-kind identifiers; raw Vulkan handles or pointers must never be represented as
integers in BASIC. Resource destruction must wait until submitted GPU work no longer
uses the resource. Managed wrappers and native destruction should follow the same
owner and lifetime rules, including orderly device teardown.

These are planned contracts, not capabilities of the current external registry.
The existing registry cannot represent a resource lease, retained callback, or
engine-owned object. Before implementing renderer commands, decide whether the
legacy surface can express the command with scalar IDs and whether that preserves
the original API; otherwise extend the interop boundary explicitly in Blitz3DCS
without adding graphics-specific dependencies to BlitzCS.Compiler.

## Planned engine capabilities

Modern PBR lighting, advanced materials, physics, ragdolls, and advanced AI are
roadmap goals. Their APIs and implementation details remain open. They should be
introduced as engine services behind the same compatibility/runtime boundaries,
with capability checks where platform support varies. PBR and lighting may use
backend shader and resource facilities; physics, ragdolls, and AI remain engine
services rather than Vulkan responsibilities. No command names or behavior for
these planned systems are specified here.

Future D3D12 and Metal backends should implement the engine-facing graphics
contract and advertise their actual capabilities. The engine should express
requirements in backend-neutral terms and define fallbacks only where the feature's
meaning remains sound. If a backend cannot provide a required behavior, startup or
the affected operation should report the limitation explicitly rather than emulate
it inaccurately.

## Decisions to settle before renderer implementation

- Select the .NET Vulkan binding approach and supported deployment platforms.
- Define the minimum Vulkan 1.4 feature profile and which advanced capabilities are
  optional, including their fallback behavior.
- Define the windowing/surface boundary and ownership between the application host
  and Blitz3DCS.
- Specify which historical API commands are in the first compatibility slice and
  verify their signatures and behavior against the public Blitz3D contract.
- Decide how integer resource IDs map to legacy handle semantics and how the
  registry evolves if scalar calls cannot safely express an operation.
- Set renderer-thread ownership, blocking behavior, error delivery, and teardown
  rules before adding asynchronous work.
