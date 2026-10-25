# Legacy dependency discovery and resolution

BlitzCS can import historical `.decls` contracts and compose them with existing JSON profiles and real providers. Reading a declaration does not implement or execute its library. The compiler and publisher still refuse calls without executable bindings. Windows x64 remains the default application target.

## Configure a project

Keep the original BASIC source and libraries unchanged. Use the existing `--externals` mechanism with one project catalog:

```json
{
  "profiles": [
    "../BlitzCS/profiles/scpcb-mavless.json",
    "dependencies/fmod.json"
  ],
  "fallbackProfiles": ["../BlitzCS/samples/Audio/scp.json"],
  "declarations": ["original-source"]
}
```

`dependencies/fmod.json` can be an unchanged copy of `samples/FmodAudio/fmod.json`, with the separately supplied original `fmod.dll` beside it. This preserves the existing decorated exports, stdcall conventions, preprocessing and BCSBRE configuration. No per-function FMOD edits are needed. `samples/Audio/scp.json` is a different, optional collection of managed bindings and declarations; it is not the original FMOD binary provider.

`declarations` entries are files or directories, relative to their declaring catalog. Directories are scanned recursively for `.decls`, ordered by case-insensitive path followed by ordinal path. Every loaded file becomes a protected compiler input. Windows separators and uniquely matching filename casing are accepted on Linux. Ambiguous paths, duplicate declarations, cycles and malformed declarations produce errors. The existing 256-file catalog limit also bounds declaration inputs.

Multiple explicitly supplied catalogs can also be composed without creating a project file:

```sh
blitzcs check-semantics Main.bb --externals engine.json --externals bindings.json --externals user32.decls
```

There is no repository-wide automatic provider scan: unrelated catalogs may select incompatible runtimes. The project explicitly chooses catalogs; declaration directories and profile imports are then discovered deterministically.

## Historical declarations retain their contracts

The reader preserves `.lib` changes, BASIC names, return suffixes, all positional parameters (including unmarked Integer parameters), trailing defaults, aliases, stdcall metadata, source path/line and the original signature. It reads historical CP1252 text and reports unsupported syntax rather than dropping rows. Duplicate positional parameter names are retained because real historical declarations contain them.

For example:

```blitzbasic
.lib "user32.dll"
api_GetClientRect% (hwnd%, lpRect*) : "GetClientRect"
```

The second parameter remains a historical **data pointer**, with its original marker and provenance. Its Integer representation is a BASIC handle, not a native address. Contract analysis accepts either a bank handle or a BASIC object for `*`; it preserves an object expression without inventing a native conversion. Such unbound calls cannot reach emission. Float/String-to-pointer coercions are rejected.

An existing managed method with Integer parameters is not automatically a pointer adapter. `legacyAdapter: true` may designate an explicitly configured **managed** bank adapter; it never enables raw binary forwarding. An Integer bank adapter still cannot accept a BASIC object: object-data borrowing needs its own real adapter. No native layout, HWND ownership or object lifetime is inferred from a `.decls` file.

Blank `.lib` declarations can be satisfied by functions actually defined in the BASIC application. They do not manufacture functions or override explicitly selected executable providers. Ordinary conflicting function declarations remain errors.

The historical defaults are authoritative. An optional CLR parameter cannot widen a required historical signature. An actual Integer result may be discarded by a historical Void command; the real method/export is called and no result is fabricated. A Void provider cannot satisfy an Integer result, and floating-point results are not silently discarded. The original stdcall convention is supported by the [historical userlib code generator](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/compiler/codegen_x86/tile.cpp).

## Composition and provider selection

`profiles` retains strict contract composition: duplicate executable providers and conflicting signatures are errors. The existing declaration/default/contract checks remain in effect.

`fallbackProfiles` is an explicit ordered provider policy. It only completes an unbound contract. A previously selected primary provider wins. Catalog-compatible managed candidates undergo CLR signature checks; a candidate with a mismatched CLR return can fall through to a later compatible candidate. Rejected candidates remain diagnosable. Incompatible fallback contracts do not suppress unrelated sibling declarations.

Native identity and handle ownership also matter. When a historical DLL already selects a binary provider, a managed fallback is not silently mixed into that library's resource family. A matching-looking Integer handle is insufficient evidence of compatibility. Select an explicit compatible provider or adapter instead.

The historical `.decls` signature is preserved even when a configured provider disagrees. The disagreement is recorded as an ABI failure, and the unusable binding is withheld. A decorated export such as `_FSOUND_Stream_Open@16` paired with three four-byte arguments is diagnosed as a 16-versus-12-byte ABI disagreement. The compiler does not guess a missing argument.

## Trace and batch audit

```sh
blitzcs audit-dependencies Main.bb --externals project.json --trace-resolution resolution.json
blitzcs build Main.bb --externals project.json --trace-resolution build-resolution.json
```

`audit-dependencies` parses Main and its complete Include closure and collects every independently discoverable external call. Application functions, arrays, compiler intrinsics and C# bridge calls are excluded. It groups requested symbols by library/provider and status, retaining each call site and independently discoverable arity/value-use mismatches. It does not run semantic recovery, emit code or pretend an error has been repaired. Syntax/Include failures still stop parsing.

The report explicitly sets `semanticValidation: false` and `emissionAttempted: false`. Run `check-semantics` separately for full type/semantic validation. An audit returns nonzero while any requested dependency remains unresolved.

Each resolution records the requested name, declaration and catalog sources, selected provider, native export, convention, signature, preserved pointer metadata, executable-binding status, adaptations, rejected binding candidates and failure reason. Categories include:

| Status | Meaning |
|---|---|
| `declaration_missing` | No contract in the configured closure; does not mean no implementation exists elsewhere. |
| `provider_missing` | Contract loaded; no executable provider selected. |
| `adapter_missing` | A candidate exists but cannot marshal historical data pointers safely. |
| `abi_incompatible` | Signature, decorated byte count, convention or resource ownership conflicts. |
| `signature_mismatch` | A call's arity/value usage conflicts with the resolved signature. |
| `bound` | A concrete method/export binding was selected; runtime execution is not established by this status. |

Trace output is protected against source/catalog/declaration/provider/asset overwrites. Atomic replacement avoids truncating a hard-linked input. Failed builds may still produce a requested resolution report; they do not produce an executable package with missing required bindings.

## Runtime registration inventories

The experimental x86 host can inspect the **actual** runtime factory table without running BASIC:

```text
BlitzCS.OriginalRuntime.Host.exe --inspect-runtime runtime.dll new-snapshot.json
```

The original DLL and its native dependencies must be available. No IPC or engine replacement is involved. Snapshots contain version, DLL SHA-256 and descriptors, not callable native addresses.

Generate an advisory overlay using the existing base profile:

```sh
python3 tools/generate_runtime_profile.py --snapshot snapshot.json --base-profile profiles/blitz3d.json --provider scpcb.mavless-runtime --output overlay.json
```

The generator rejects signature/default conflicts, preserves actual runtime string text, excludes internal/reserved entries, and does not infer native ABI or executable implementations. The checked [600-registration snapshot](evidence/scpcb-runtime-registrations.json) reproduces `profiles/scpcb-mavless.json`: five public additions and two internal additions relative to the stock registration inventory.

See the [measured SCP results](scp-legacy-dependency-results.md) for source, binding, emission and execution stages.
