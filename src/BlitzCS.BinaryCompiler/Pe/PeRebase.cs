using System.Buffers.Binary;

namespace BlitzCS.BinaryCompiler;

/// <summary>A rebase or base-allocation request that cannot be honoured. <see cref="Code"/> is stable (PE_REBASE_*).</summary>
public sealed class PeRebaseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Rebasing of PE32 images at translation time (see <see cref="PeImage.Rebase"/>).</summary>
internal static class PeRebase
{
    internal const uint BaseAlignment = 0x10000;

    internal static PeImage Rebase(PeImage image, uint newBase)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (newBase == image.ImageBase) return image;
        if (image.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code.StartsWith("PE03", StringComparison.Ordinal)))
            throw new PeRebaseException("PE_REBASE_BAD_RELOCS", "The image's base relocation directory is malformed or uses unsupported relocation types; it cannot be moved.");
        if (image.Relocations.Count == 0)
            throw new PeRebaseException("PE_REBASE_NO_RELOCS",
                $"The image has no base relocations (stripped), so it cannot be moved from its base 0x{image.ImageBase:X8} to 0x{newBase:X8}.");
        if (newBase == 0 || newBase % BaseAlignment != 0)
            throw new PeRebaseException("PE_REBASE_BAD_BASE", $"Base 0x{newBase:X8} must be non-zero and 64 KiB aligned.");
        if ((ulong)newBase + ModuleBaseAllocator.Footprint(image) > 0x1_0000_0000UL)
            throw new PeRebaseException("PE_REBASE_BAD_BASE", $"Base 0x{newBase:X8} plus the image footprint does not fit below 4 GiB.");

        var delta = unchecked(newBase - image.ImageBase);
        var bytes = (byte[])image.Bytes.Clone();
        var firstSection = image.Sections.Count == 0 ? image.SizeOfImage : image.Sections.Min(s => s.VirtualAddress);
        var headerBytes = (long)Math.Min(Math.Min(firstSection, image.SizeOfImage), (uint)bytes.Length);

        // Same RVA -> file offset rule as PeMapping.Map, so the relocated file maps to exactly "mapped image + delta at each fixup".
        long FileOffset(uint rva)
        {
            if (rva + 4UL <= (ulong)headerBytes) return rva;
            foreach (var s in image.Sections)
            {
                var size = s.VirtualSize == 0 ? s.RawSize : Math.Min(s.VirtualSize, s.RawSize);
                if (rva < s.VirtualAddress || (ulong)rva + 4 > (ulong)s.VirtualAddress + size) continue;
                var off = (long)s.RawOffset + (rva - s.VirtualAddress);
                if (off + 4 <= bytes.Length) return off;
            }
            return -1;
        }

        foreach (var r in image.Relocations)
        {
            if (r.Type != 3) continue;
            var off = FileOffset(r.Rva);
            if (off < 0)
                throw new PeRebaseException("PE_REBASE_UNBACKED_RELOC", $"Relocation at RVA 0x{r.Rva:X} is not backed by file data (zero-fill or header gap), so it cannot be patched in the file image.");
            var span = bytes.AsSpan((int)off, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked(BinaryPrimitives.ReadUInt32LittleEndian(span) + delta));
        }

        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(peOffset + 24 + 28), newBase);

        // Everything derived (TLS VAs, import/export tables, relocation list) comes from re-parsing the relocated bytes: consistent by construction.
        var parsed = PeReader.Read(bytes);
        if (parsed.ImageBase != newBase || parsed.Relocations.Count != image.Relocations.Count || parsed.Imports.Count != image.Imports.Count)
            throw new PeRebaseException("PE_REBASE_INCONSISTENT", "The relocated image does not re-parse to the same structure.");
        return new PeImage
        {
            Bytes = parsed.Bytes, Sha256 = image.Sha256, Machine = parsed.Machine, IsDll = parsed.IsDll, ImageBase = parsed.ImageBase,
            SizeOfImage = parsed.SizeOfImage, EntryPointRva = parsed.EntryPointRva, Sections = parsed.Sections, Imports = parsed.Imports,
            Exports = parsed.Exports, Relocations = parsed.Relocations, Tls = parsed.Tls, Diagnostics = parsed.Diagnostics,
            RebasedFrom = new PeRebaseInfo(image.RebasedFrom?.OriginalImageBase ?? image.ImageBase),
        };
    }
}

/// <summary>A module's chosen guest base (64 KiB aligned).</summary>
public sealed record ModuleBase(string Name, uint Base, uint Footprint, bool Moved);

/// <summary>
/// Deterministic base allocation for a program's modules. Images without relocations keep their preferred base (a conflict among them is an
/// error); relocatable images keep their preferred base when free, else take the first free 64 KiB-aligned range found by a fixed scan.
/// Ranges the runtime itself uses (the MAP_32BIT zone 0x40000000-0x80000000 where guest stacks, TEBs and scratch buffers live, and the
/// first 16 MiB) are never handed out. The allocator cannot see the live address space: the loader maps with MAP_FIXED_NOREPLACE and
/// reports any conflict by name, it never overwrites.
/// </summary>
public static class ModuleBaseAllocator
{
    public static readonly IReadOnlyList<(uint Start, uint End)> DefaultReserved = [(0, 0x01000000), (0x40000000, 0x80000000)];
    private static readonly (uint Start, uint End)[] SearchWindows = [(0x10000000, 0x40000000), (0x80000000, 0xE0000000)];
    private const uint Gap = 0x10000;

    /// <summary>Bytes the loader maps for the image: SizeOfImage plus the synthetic exit/import-stub area, rounded up to 4 KiB.</summary>
    public static uint Footprint(PeImage image)
    {
        var exit = (image.SizeOfImage + 15u) & ~15u;
        var end = Math.Max((ulong)image.SizeOfImage, exit + 16UL + 16UL * (uint)Math.Max(1, image.Imports.Count) + 1);
        return (uint)((end + 0xFFF) & ~0xFFFUL);
    }

    public static IReadOnlyList<ModuleBase> Allocate(IReadOnlyList<(string Name, PeImage Image)> modules, IEnumerable<(uint Start, uint End)>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var taken = new List<(ulong Start, ulong End, string Owner)>();
        foreach (var (s, e) in reserved ?? DefaultReserved) taken.Add((s, e, "reserved range"));
        var result = new ModuleBase?[modules.Count];

        bool Fits(ulong start, ulong size, out ulong blockedEnd, out string owner)
        {
            blockedEnd = 0; owner = "";
            if (start + size > 0x1_0000_0000UL) { blockedEnd = ulong.MaxValue; owner = "the 4 GiB limit"; return false; }
            foreach (var t in taken)
                if (start < t.End + Gap && t.Start < start + size + Gap) { blockedEnd = t.End + Gap; owner = t.Owner; return false; }
            return true;
        }

        for (var i = 0; i < modules.Count; i++)
        {
            var (name, image) = modules[i];
            if (image.Relocations.Count != 0) continue;
            var size = Footprint(image);
            var owner = "";
            if (image.ImageBase % BaseAlign != 0 || !Fits(image.ImageBase, size, out _, out owner))
                throw new PeRebaseException("PE_REBASE_NO_RELOCS",
                    $"Module '{name}' has no relocations and must stay at 0x{image.ImageBase:X8}, which is not available ({(image.ImageBase % BaseAlign != 0 ? "not 64 KiB aligned" : "conflicts with " + owner)}).");
            taken.Add((image.ImageBase, (ulong)image.ImageBase + size, name));
            result[i] = new ModuleBase(name, image.ImageBase, size, false);
        }

        for (var i = 0; i < modules.Count; i++)
        {
            if (result[i] is not null) continue;
            var (name, image) = modules[i];
            var size = Footprint(image);
            ulong chosen = 0; var found = false;
            if (image.ImageBase % BaseAlign == 0 && image.ImageBase != 0 && Fits(image.ImageBase, size, out _, out _)) { chosen = image.ImageBase; found = true; }
            for (var w = 0; !found && w < SearchWindows.Length; w++)
            {
                var cursor = (ulong)SearchWindows[w].Start;
                while (cursor + size <= SearchWindows[w].End)
                {
                    if (Fits(cursor, size, out var blockedEnd, out _)) { chosen = cursor; found = true; break; }
                    cursor = (blockedEnd + BaseAlign - 1) / BaseAlign * BaseAlign;
                }
            }
            if (!found) throw new PeRebaseException("PE_REBASE_NO_SPACE", $"No free 64 KiB-aligned range of 0x{size:X} bytes for module '{name}'.");
            taken.Add((chosen, chosen + size, name));
            result[i] = new ModuleBase(name, (uint)chosen, size, chosen != image.ImageBase);
        }
        return result!;
    }

    private const uint BaseAlign = PeRebase.BaseAlignment;
}
