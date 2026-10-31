namespace BlitzCS.BinaryCompiler;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>A concrete, non-silent report. Unsupported input is always an Error diagnostic, never a fake success.</summary>
public sealed record BinaryDiagnostic(DiagnosticSeverity Severity, string Code, string Message, uint? GuestRva = null);

public enum GuestCallingConvention { Cdecl, Stdcall, Thiscall, Fastcall, Unknown }

public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics)
{
    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
    public bool IsWritable => (Characteristics & 0x80000000) != 0;
}

/// <summary>An imported function. <paramref name="IsDelay"/> marks delay-load imports (the IAT slot is bound at load time like an eager import).</summary>
public sealed record PeImport(string Dll, string? Name, ushort? Ordinal, uint IatRva, bool IsDelay = false);

/// <summary>ForwarderTarget is non-null for forwarded exports (RVA lies inside the export directory).</summary>
public sealed record PeExport(string? Name, ushort Ordinal, uint Rva, string? ForwarderTarget);

/// <summary>Only IMAGE_REL_BASED_HIGHLOW (3) and ABSOLUTE (0, skipped) are accepted for PE32; anything else is a diagnostic.</summary>
public sealed record PeRelocation(uint Rva, ushort Type);

public sealed record PeTls(uint StartRaw, uint EndRaw, uint IndexAddress, uint CallbacksVa, uint SizeOfZeroFill = 0);

/// <summary>Lineage of an image produced by <see cref="PeImage.Rebase"/>: the base the original file prefers.</summary>
public sealed record PeRebaseInfo(uint OriginalImageBase);

/// <summary>Parsed PE32 (x86). Produced by PeReader; never mutates input bytes.</summary>
public sealed class PeImage
{
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required ushort Machine { get; init; }
    public required bool IsDll { get; init; }
    public required uint ImageBase { get; init; }
    public required uint SizeOfImage { get; init; }
    public required uint EntryPointRva { get; init; }
    public required IReadOnlyList<PeSection> Sections { get; init; }
    public required IReadOnlyList<PeImport> Imports { get; init; }
    public required IReadOnlyList<PeExport> Exports { get; init; }
    public required IReadOnlyList<PeRelocation> Relocations { get; init; }
    public PeTls? Tls { get; init; }
    public required IReadOnlyList<BinaryDiagnostic> Diagnostics { get; init; }

    /// <summary>Non-null for an image returned by <see cref="Rebase"/>. <see cref="Sha256"/> then still identifies the ORIGINAL file.</summary>
    public PeRebaseInfo? RebasedFrom { get; init; }

    /// <summary>
    /// Returns a new image whose bytes were relocated (HIGHLOW fixups applied to the file image, ImageBase updated) to <paramref name="newBase"/>;
    /// this image and its bytes are untouched. Translated code embeds absolute guest addresses, so the base is a translation input.
    /// Throws <see cref="PeRebaseException"/> for images without relocations, an invalid base or a relocation that cannot be patched.
    /// </summary>
    public PeImage Rebase(uint newBase) => PeRebase.Rebase(this, newBase);

    /// <summary>Builds the mapped image (SizeOfImage bytes, sections at their RVAs, relocations NOT applied).</summary>
    public byte[] MapImage() => PeMapping.Map(this);
}

public sealed record TranslatedExport(string? Name, ushort Ordinal, uint GuestRva, uint HostOffset, GuestCallingConvention Convention, int? ArgBytes);

/// <summary>Maps one guest instruction/block-entry RVA to an offset inside <see cref="TranslationArtifact.HostImage"/>.</summary>
public readonly record struct AddressMapEntry(uint GuestRva, uint HostOffset);

/// <summary>
/// Output of the translator. HostImage = [code][padding][RW data]. Code is position independent (RIP-relative) and
/// Import protocol: stub i spills guest state, switches to the native stack saved in the context, calls the managed
/// `ulong Import(uint guestEsp)` (SysV: edi=guestEsp, guestEsp points at the guest return address; args at +4...),
/// where the low 32 bits are EAX and bits 32..62 are the bytes of arguments the callee pops (stdcall) or 0 (cdecl); bit 63 marks an extended return (EDX at CtxEdx and/or an 80-bit ST0 image at CtxSt0Raw, selected by the flags at CtxImportFlags) that the stub reloads before returning;
/// then it switches back and performs the guest `ret` (popping the extra bytes). Unbound imports are a load-time error.
/// Callee-saved SysV registers and the native stack pointer are preserved across EnterGuest.
/// references the data area at CodeSize.. via RIP-relative addressing, so the loader needs no fixups: it maps
/// [0,CodeSize) RX and [CodeSize,HostImage.Length) RW. Guest memory is the mapped PE image at its
/// (possibly relocated) base inside the low 4 GiB; guest address == host address.
/// </summary>
public sealed record TranslationArtifact(
    int FormatVersion,
    string SourceSha256,
    string CacheKey,
    uint GuestImageBase,
    uint GuestImageSize,
    byte[] HostImage,
    int CodeSize,
    uint DispatchTableOffset,   // offset (in HostImage) of uint32[] indexed by (guestRva) -> host offset, 0 = not translated
    uint ImportThunkTableOffset,// offset of uint64[] (one per PeImport, same order) the loader fills with managed function pointers
    uint ContextOffset,         // offset of the 64-byte module registry/cache area in RW data (the live context is per call, see EnterGuest)
    uint EnterGuestOffset,      // host routine, SysV: ulong EnterGuest(ulong hostTarget /*rdi*/, uint guestEsp /*esi*/, ulong callContext /*rdx*/) -> eax; Win64: the same with rcx/edx/r8 (and RDI, RSI, XMM6, XMM7, XMM15 preserved too). callContext points to a zeroed 128-byte caller-owned block: [0]=saved native RSP, [16]=trap kind (u32, 1=untranslated target, 2=rejected instruction, 3=fs: offset outside the TEB, 4=int 29h fast fail with the ECX code at [8], 5=named deferred fault of a segment-register or far-pointer load), [24]=trap guest VA, [32]=import fault flag, [40]=guest TEB base (u64, copied into R15 for the whole call; see docs/binary-teb-contract.md), [48]=EDX out, [56]=capture request (2=ST0, 4=XMM0), [64..74)=ST0 raw 80-bit, [80]=ST0 as double, [88]=ST0 as float, [96..112)=XMM0, [112]=status out (1=x87 stack not empty on entry, 2=unbalanced on return, 4=ST0 empty), [116..128)=saved/current x87 CW and MXCSR. It is kept in R14 for the whole call, so calls on different threads (and nested calls) never share state.
    uint ExitGuestRva,          // synthetic guest RVA (>= SizeOfImage, covered by the dispatch table); loader places it as the outermost return address
    uint ImportStubBaseRva,     // synthetic guest RVA of import stub 0; stub i is at ImportStubBaseRva + 16*i; the loader writes ImageBase+that into IAT slot i
    IReadOnlyList<AddressMapEntry> AddressMap,
    IReadOnlyList<TranslatedExport> Exports,
    IReadOnlyList<PeImport> Imports,
    IReadOnlyList<BinaryDiagnostic> Diagnostics,
    uint? RebasedFromImageBase = null,   // non-null when the translated image was rebased from this preferred base (SourceSha256 stays the original file hash)
    string? TargetOs = null,   // host OS/ABI the runtime routines were generated for (BinaryTargets); null = legacy artifact = linux-sysv. The loader refuses a mismatch.
    int DynamicStubs = 0,   // import-style stubs emitted after the real imports (index Imports.Count + j: stub at ImportStubBaseRva + 16*index, thunk slot 8*index): the pool behind GuestModule.AllocateStub. 0 = none.
    int RuntimeStubs = 0);  // import-style stubs after the dynamic pool (index Imports.Count + DynamicStubs + k) that the runtime binds to its own handlers (ImportStubLayout.RuntimeStubCount; the guest SEH continuations). 0 = none (hand-built artifacts).

/// <summary>
/// Layout of the process-wide module registry that translated code consults when a dispatch target lies outside its own module (docs/binary-multimodule.md,
/// "Cross-module dispatch"). The loader stores the registry's address in the 8-byte cell at <c>ContextOffset + CellOffset</c> of every module (0 = not part of a set).
/// Header: u32 count (release-published), u32 capacity; 32-byte records from <see cref="HeaderSize"/>: u32 guest base, u32 bound (valid table indices, 0 = retired),
/// u64 host code base (R12), u64 dispatch table address (R13, 0 = dead).
/// </summary>
/// <summary>
/// What every import stub records in the per-call context besides the protocol of <see cref="TranslationArtifact"/> (docs/binary-seh.md, "Import stub contract").
/// On entry each stub stores the guest's callee-saved registers EBX, EBP, ESI and EDI at the call into the context, so a managed import can build an exact
/// x86 CONTEXT; runtime stubs also store EAX (the value a guest function returned to them). An extended return (bit 63) whose flags at CtxImportFlags contain
/// <see cref="RedirectFlag"/> does not return to the caller: EAX of the import result is the guest address of a register image laid out like the integer part
/// of an x86 CONTEXT (from CONTEXT.Edi at +0x9C: Edi, Esi, Ebx, Edx, Ecx, Eax, Ebp, Eip, SegCs, EFlags, Esp), and the stub loads the eight general registers
/// and continues at Eip (through the normal dispatch, so the target must be translated code).
/// </summary>
public static class ImportStubLayout
{
    public const int CtxGuestEbx = 20, CtxGuestEbp = 36, CtxGuestEsi = 52, CtxGuestEdi = 92, CtxGuestEax = 76;
    public const uint RedirectFlag = 4;
    /// <summary>Offsets inside the register image (relative to CONTEXT.Edi).</summary>
    public const int ImageEdi = 0, ImageEsi = 4, ImageEbx = 8, ImageEdx = 12, ImageEcx = 16, ImageEax = 20, ImageEbp = 24, ImageEip = 28, ImageEsp = 40;
    /// <summary>Runtime stubs every translated artifact carries (after the dynamic pool): 0 = SEH handler-return continuation, 1 = SEH dispatcher/unwind frame handler.</summary>
    public const int RuntimeStubCount = 2;
}

public static class ModuleRegistryLayout
{
    public const uint CellOffset = 0;
    /// <summary>Per-artifact RW context cell containing the last matching registry record pointer (zero initially). The record's live bound and table are checked on every hit.</summary>
    public const uint CachedRecordCellOffset = 8;
    public const int HeaderSize = 16, RecordSize = 32, GuestBase = 0, Bound = 4, HostBase = 8, Table = 16;
    /// <summary>Header offset of the u64 miss handler (0 = none): native <c>uint handler(registry, guestVa, callContext)</c> that the miss routine calls when no record translates
    /// the target, before it traps (dynamic code, docs/binary-dynamic-code.md). Non-zero = a record that translates the address has been published; the walk is repeated.</summary>
    public const int MissHandler = 8;
}

public enum OptimizationProfile { Debug, Release, ReleasePgo }

/// <param name="LoadBase">Guest base address to translate for (64 KiB aligned, below 4 GiB). Null = the image's preferred base. Translated code embeds absolute addresses, so this is part of the cache key.</param>
public sealed record BinaryCompilerOptions(OptimizationProfile Profile = OptimizationProfile.Release, string TargetArchitecture = "x64", string? TargetOs = null, uint? LoadBase = null)
{
    /// <summary>The host OS/ABI the artifact is generated for (see <see cref="BinaryTargets"/>). Defaults to the OS of the running process; validated by the translator.</summary>
    public string TargetOs { get; init; } = TargetOs ?? BinaryTargets.Host;

    /// <summary>Import-style stubs the translator emits beyond the image's real imports (0 = none). The runtime hands them out with GuestModule.AllocateStub, so guest code can call managed code through function pointers and COM vtables it obtained at run time. Part of the cache key when non-zero.</summary>
    public int DynamicStubs { get; init; }

    /// <summary>Upper bound of <see cref="DynamicStubs"/> (each stub costs 16 bytes of guest address space and about 70 bytes of host code; 4096 stubs = 64 KiB stay inside the 64 KiB gap the module base allocator keeps between modules).</summary>
    public const int MaxDynamicStubs = 4096;

    public const string CompilerVersion = "0.14.2";
}

/// <summary>Target OS/ABI identifiers. Artifacts are target specific: the runtime routines (EnterGuest, import stubs) follow the host calling convention, and the loader refuses a mismatching artifact.</summary>
public static class BinaryTargets
{
    public const string LinuxSysV = "linux-sysv", WindowsX64 = "windows-x64";

    /// <summary>The target of the running process; PlatformNotSupportedException on any other OS.</summary>
    public static string Host => OperatingSystem.IsWindows() ? WindowsX64 : OperatingSystem.IsLinux() ? LinuxSysV
        : throw new PlatformNotSupportedException("The binary compiler runtime supports Linux x86-64 and Windows x86-64 only.");

    /// <summary>Maps user-facing aliases (linux-x64, win-x64 ...) to the canonical identifier; null when unknown.</summary>
    public static string? TryNormalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        LinuxSysV or "linux-x64" or "linux" => LinuxSysV,
        WindowsX64 or "win-x64" or "windows" or "win64" => WindowsX64,
        _ => null,
    };

    public static string Normalize(string? value) => TryNormalize(value)
        ?? throw new ArgumentException($"Unknown target OS '{value}'. Supported: {LinuxSysV} (alias linux-x64), {WindowsX64} (alias win-x64).", nameof(value));
}
