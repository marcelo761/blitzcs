# Native struct pointer ABI

Original userlib declarations such as `GlobalMemoryStatus(buffer*)` pass a BASIC `Type` instance
to native code by pointer. The 32-bit original passed the address of the object's field memory: one
4-byte slot per field. This page describes how typed external bindings accept such arguments without
exposing native addresses to BASIC or tying the compiler to a particular library.

## Layers

| Layer | Owns | Does not own |
| --- | --- | --- |
| Compiler (`BlitzCS.Compiler`) | Accepting a `Type` instance for a pointer parameter; the legacy field image; copy-in/copy-back ordering; source diagnostics | Native layouts, pointer sizes, libraries |
| Binding (for example `Blitz3DCS.Engine.Windows`) | The target-specific native struct, narrowing rules, the native call, API-specific semantics | BASIC object storage |
| `BlitzCS.NativeInterop.NativeStructLayout` | Translating a field image to and from a naturally aligned native struct; pinning for one call | Choosing a layout for an API |

`.decls` files say only that a parameter is a pointer (`*`). They contain no direction or layout, so
they remain the BASIC signature contract, and a reviewed binding supplies the ABI. `.decls` pointer
contracts that have no binding stay unbound.

## Catalog and binding contract

| Catalog parameter | CLR parameter | Before the call | After the call |
| --- | --- | --- | --- |
| `PointerIn` | `ReadOnlySpan<byte>` | Fields copied into the image | Not copied back |
| `PointerOut` | `[Out] Span<byte>` | Image zero-filled | Copied back to the fields |
| `PointerInOut` | `Span<byte>` | Fields copied into the image | Copied back to the fields |

The generator for the Windows runtime catalog derives these names by reflection. The CLI checks
that each catalog entry matches its method. Pointer kinds cannot be return types and cannot have
defaults.

**Field image:** Fields are stored in declaration order. Each `Integer` field occupies a
little-endian Int32 slot, and each `Float` field occupies an IEEE single slot. This matches the
original 32-bit object layout.

**Accepted arguments:** A `Type` instance whose fields are all `Integer`/`Float`, or `Null`. `Null` reaches the binding as an empty span, which means
NULL.

**Source diagnostics:** Integer values are rejected at compile time, including bank handles. Bank
APIs keep Integer bank-token bindings over `LegacyBanks.WithPointer`, such as `api_GetClientRect`.
These shapes are also rejected at compile time:

- String, object and `Field x[n]` fields
- Field-less Types
- Missing arguments

**Ordering:** Object references are evaluated in normal argument order. The images are refreshed
from live fields after the last argument has been evaluated, so a nested call that changes the object
is observed, as with the original's live memory. Images are written back after the call returns, in
reverse order.

**Object lifetime:** A deleted object fails when its image is refreshed. If the object is deleted
during the call, copy-back fails rather than writing to a deleted object.

**Lifetime:** The span is a ref struct, so a binding cannot retain it after returning. Bindings that
call native code use `NativeStructLayout`. It pins a separate, correctly aligned native copy only
while its callback runs, through `NativeBuffer.WithPointer`.

## Layout (`NativeStructLayout`)

`NativeStructLayout.Sequential(fields)` uses natural alignment, matching MSVC defaults on Win32 and
Win64 and x86-64 System V. It does not model the i386 System V ABI, where 8-byte fields are 4-byte
aligned. An explicit pointer size can be given for computing a layout, but only a layout for the
current process can be invoked.

| Field kind | BASIC slots | Native size | Copy-back |
| --- | --- | --- | --- |
| `Int32`, `UInt32`, `Float32` | 1 | 4 | Bit copy |
| `UInt64` | 2 (low, high) | 8, 8-aligned | Bit copy |
| `UIntPtrSaturating` | 1 | Pointer size | Saturates to `0xFFFFFFFF` on 64-bit |
| `UIntPtrChecked` | 1 | Pointer size | Throws `OverflowException` on 64-bit, leaving the image unchanged |

Pointer-sized inputs are zero-extended. A too-short image fails with `ArgumentException`; extra bytes are preserved.

| Structure | Win32 | Win64 | BASIC image |
| --- | --- | --- | --- |
| `MEMORYSTATUS` (2 DWORD and 6 SIZE_T fields) | 32 bytes, 4-aligned | 56 bytes, 8-aligned | 32 bytes |
| `MEMORYSTATUSEX` (2 DWORD and 7 DWORDLONG fields) | 64 bytes, 8-aligned | 64 bytes, 8-aligned | 64 bytes (low/high pairs) |

`LegacyKernel32Bindings.GlobalMemoryStatus` uses the `MEMORYSTATUS` row. A Win64 value over 4 GiB
reads as -1, which is how the 32-bit API reports overflow. `dwLength` reports the legacy size of
32 bytes on both targets.

## Not supported

Strings, arrays, object references or pointers inside structs; callbacks that retain a buffer; bank
arguments to struct-pointer parameters; packing other than natural alignment; and generating bindings
automatically from `.decls` each need an explicit design. They are diagnosed, not passed as raw integers.

## Validation

- Compiler: the in, out and in/out directions, nested calls, single-line `If`, `Null`, deleted
  objects and rejected shapes are covered (`tests/BlitzCS.Compiler.Tests/StructPointerParameterTests.cs`).
- Linux C fixture: `tests/BlitzCS.NativeInterop.Tests/native/buffer.c` has a struct with 32-bit,
  `size_t` and `uint64_t` fields. The tests compare offsets with the C compiler, call it in all
  three directions and test saturation and checked overflow. They also run a generated BASIC program
  end to end.
- Windows: `LegacyKernel32BindingsTests` runs under `--system-smoke` and on Windows hosts. It has
  passed in x64 and x86 processes under Wine. Native Windows hardware runs are still pending.
