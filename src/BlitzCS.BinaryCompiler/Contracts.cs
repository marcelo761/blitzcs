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
    uint ContextOffset,         // legacy: offset of an unused 64-byte area in the RW data area (the live context is per call, see EnterGuest)
    uint EnterGuestOffset,      // host routine, SysV: ulong EnterGuest(ulong hostTarget /*rdi*/, uint guestEsp /*esi*/, ulong callContext /*rdx*/) -> eax. callContext points to a zeroed 128-byte caller-owned block: [0]=saved native RSP, [16]=trap kind (u32, 1=untranslated target, 2=rejected instruction, 3=fs: offset outside the TEB), [24]=trap guest VA, [32]=import fault flag, [40]=guest TEB base (u64, copied into R15 for the whole call; see docs/binary-teb-contract.md), [48]=EDX out, [56]=capture request (2=ST0, 4=XMM0), [64..74)=ST0 raw 80-bit, [80]=ST0 as double, [88]=ST0 as float, [96..112)=XMM0, [112]=status out (1=x87 stack not empty on entry, 2=unbalanced on return, 4=ST0 empty), [116..128)=saved/current x87 CW and MXCSR. It is kept in R14 for the whole call, so calls on different threads (and nested calls) never share state.
    uint ExitGuestRva,          // synthetic guest RVA (>= SizeOfImage, covered by the dispatch table); loader places it as the outermost return address
    uint ImportStubBaseRva,     // synthetic guest RVA of import stub 0; stub i is at ImportStubBaseRva + 16*i; the loader writes ImageBase+that into IAT slot i
    IReadOnlyList<AddressMapEntry> AddressMap,
    IReadOnlyList<TranslatedExport> Exports,
    IReadOnlyList<PeImport> Imports,
    IReadOnlyList<BinaryDiagnostic> Diagnostics,
    uint? RebasedFromImageBase = null);   // non-null when the translated image was rebased from this preferred base (SourceSha256 stays the original file hash)

public enum OptimizationProfile { Debug, Release, ReleasePgo }

/// <param name="LoadBase">Guest base address to translate for (64 KiB aligned, below 4 GiB). Null = the image's preferred base. Translated code embeds absolute addresses, so this is part of the cache key.</param>
public sealed record BinaryCompilerOptions(OptimizationProfile Profile = OptimizationProfile.Release, string TargetArchitecture = "x64", string TargetOs = "linux-sysv", uint? LoadBase = null)
{
    public const string CompilerVersion = "0.5.0";
}
