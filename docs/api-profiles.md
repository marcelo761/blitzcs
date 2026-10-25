# External API profiles

BlitzCS keeps language semantics, API declarations and executable providers separate. An API profile is an ordinary external catalog included through the existing `--externals` option. It declares source-level contracts; it does not install an engine, discover exports, generate stubs or enable native code execution.

## Declaration validation

```sh
dotnet run --project src/BlitzCS.CLI -c Release -- \
  check-semantics /path/to/original/Graphics3D.bb --externals profiles/blitz3d.json
```

A semantic check can succeed while reporting declared calls without implementations. It validates parsing, source types, argument counts and defaults. It does not emit an executable. `build` rejects a used unbound declaration with its original source location and required provider before emitting a package. Unused declarations do not require providers.

The stock profile is generated from the pinned source-backed [historical API inventory](blitz3d-api-inventory.md). It covers 534 public signatures: 536 decoded public registrations minus the language statements `Print` and `End`. The 57 internal symbols are excluded because their public callable signatures are unknown. The profile records provenance and exclusions; its source commit is not a claim that every SDK/runtime version exposes exactly the same commands.

```sh
python3 tools/generate_blitz3d_profile.py
python3 -m unittest discover -s tests/differential -p test_blitz3d_profile.py
```

`profiles/scpcb-mavless.json` adds three texture bump declarations recovered from descriptor strings in the exact SCP:CB patched runtime artifact. Its upstream identity, SHA-256 and descriptor offsets are recorded independently from stock Blitz3D. This remains declaration evidence, not a native ABI or runtime compatibility claim.

## Compose declarations and providers

```json
{
  "profiles": ["engine-api.json"],
  "functions": [
    {
      "name": "NativeAdd",
      "parameters": ["Integer", "Integer"],
      "assembly": "provider/MathProvider.dll",
      "type": "MathProvider",
      "method": "Add"
    }
  ]
}
```

The included profile could contain:

```json
{
  "functions": [
    {
      "name": "NativeAdd",
      "returnType": "Integer",
      "parameters": ["Integer", "Integer"],
      "defaults": [7],
      "contract": {
        "profile": "example-api",
        "provider": "example.runtime",
        "resolution": "runtime-registration",
        "symbol": "%NativeAdd%a%b=7",
        "callingConvention": null,
        "source": "runtime/registration.cpp:42"
      }
    }
  ]
}
```

Profiles load depth first. Paths resolve relative to the file declaring them, including assembly, native library and binary library paths. Repeated imports of the same full path are deduplicated; cycles and more than 256 distinct catalog files are rejected. All loaded catalogs become protected build inputs, so output cannot overwrite an included profile.

Names are case insensitive. BASIC has one external signature per name, not overload sets. `parameters` selects an exact public static managed overload. A declaration can be completed by one implementation with matching parameter and return types; another declaration, conflicting signature, second binding or changed historical defaults is rejected. An omitted `returnType` on a binding inherits the declaration. Declared defaults and contract metadata are preserved. Ordinary catalogs without profiles retain their existing CLR optional-parameter fallback; explicit `defaults: []` means every parameter is required.

An included catalog can also contain existing managed or `binary` bindings. The unchanged binary binding fields select actual PE exports, conventions and `.bcx` libraries; the [binary linking contract](binary-linking.md) still applies. No runtime-registration descriptor is automatically treated as a PE export. Native host architecture, relocations, instructions, import resolution and marshaling still require actual support from the native provider.

`nativeLibraries` and `binaryLibraries` are combined. Incompatible global `binaryTarget`/`binaryProfile` settings are rejected instead of silently selecting one file's settings. API `profiles` is catalog composition; `binaryProfile` retains its existing binary optimization meaning.

## Contract metadata

`contract` is optional and engine independent. `provider`, `resolution` and `symbol` are nonempty strings identifying the required provider and the source of symbol resolution. `profile` and `source` identify provenance. In generated historical profiles, `symbol` preserves the inventoried C++ string-literal body; its encoding is recorded in profile metadata, and it is not a ready-to-use runtime lookup key. String defaults are decoded separately to their actual BASIC values. `callingConvention` is null when unknown, or one of `cdecl`, `stdcall`, `thiscall`, `fastcall`, `managed`.

This metadata describes the declaration; it is not an executable resolver or a marshaling specification. The chosen provider binding supplies its actual implementation. In particular, primitive source-level types and an rtSym descriptor do not establish ownership of native strings, bank buffers, COM objects, callbacks or opaque handles. Unknown ABI facts must remain unknown. A runtime provider must implement the required conversion explicitly; casting a BASIC integer to a host pointer is not supplied by a profile.

The compiler library exposes `ExternalApiContract` on `ExternalFunctionSignature`; `CheckSemantics` returns it on each `UnboundExternalCall`. `MissingImplementationMessage` is shared by compiler and CLI diagnostics. The compiler and CLI acquire no dependency on Blitz3DCS.

## Existing string provider example

```sh
dotnet build src/Blitz3DCS.Runtime -c Release
dotnet run --project src/BlitzCS.CLI -c Release -- \
  build /path/to/original/Chr.bb --externals samples/Profiles/blitz3d-strings.json
```

This composes historical declarations with existing, explicitly registered string implementations. The normal output remains `dist/Chr/Chr.exe`, self-contained Windows x64 even when built from Linux. `--target linux-x64` and explicit managed DLL output retain their previous meanings. Other engine calls remain unbound, and graphical examples do not become runnable merely by selecting this catalog.

For measured source and execution coverage, see [legacy source compatibility](legacy-source-compatibility.md).

## Historical declaration discovery

Catalogs can now import `.decls` files/directories with `declarations`, select ordered secondary providers with `fallbackProfiles`, and expose complete call-resolution reports. Multiple `--externals` arguments compose through the same loader. See [legacy dependency resolution](legacy-dependency-resolution.md) for pointer ownership, conflicts, tracing and batch-audit semantics.
