namespace BlitzCS.BinaryCompiler;

internal static class PeMapping
{
    internal const uint MaxImageSize = 0x20000000;

    internal static byte[] Map(PeImage image)
    {
        if (image.SizeOfImage == 0 || image.SizeOfImage > MaxImageSize) throw new InvalidOperationException($"Invalid SizeOfImage 0x{image.SizeOfImage:X}.");
        var mapped = new byte[image.SizeOfImage];
        var src = image.Bytes;
        var first = image.Sections.Count == 0 ? image.SizeOfImage : image.Sections.Min(s => s.VirtualAddress);
        var headers = (int)Math.Min(Math.Min(first, image.SizeOfImage), (uint)src.Length);
        Array.Copy(src, mapped, headers);
        foreach (var s in image.Sections)
        {
            if (s.VirtualAddress >= mapped.Length || s.RawOffset >= src.Length) continue;
            var size = s.VirtualSize == 0 ? s.RawSize : Math.Min(s.VirtualSize, s.RawSize);
            size = (uint)Math.Min(Math.Min(size, (long)src.Length - s.RawOffset), (long)mapped.Length - s.VirtualAddress);
            Array.Copy(src, s.RawOffset, mapped, s.VirtualAddress, size);
        }
        return mapped;
    }
}

public static class PeRelocator
{
    /// <summary>Applies HIGHLOW deltas (newBase - ImageBase) to a mapped image in place.</summary>
    public static void Apply(PeImage image, byte[] mapped, uint newBase)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(mapped);
        var delta = unchecked(newBase - image.ImageBase);
        foreach (var r in image.Relocations)
        {
            if (r.Type != 3) continue;
            if ((ulong)r.Rva + 4 > (ulong)mapped.Length) throw new ArgumentOutOfRangeException(nameof(image), $"Relocation RVA 0x{r.Rva:X} outside mapped image.");
            var span = mapped.AsSpan((int)r.Rva, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span) + delta));
        }
    }
}

public static class ExportAbiHints
{
    /// <summary>Infers a convention only from unambiguous MSVC C decoration; everything else is Unknown.</summary>
    public static (GuestCallingConvention Convention, int? ArgBytes) Infer(string? exportName)
    {
        if (string.IsNullOrEmpty(exportName)) return (GuestCallingConvention.Unknown, null);
        var n = exportName;
        if (n[0] == '?' || n.Any(c => c < 0x21 || c > 0x7E)) return (GuestCallingConvention.Unknown, null);
        var at = n.LastIndexOf('@');
        if (n[0] == '_' && n.Length > 1)
        {
            if (at < 0) return (GuestCallingConvention.Cdecl, null);
            if (at > 1 && TryBytes(n, at, out var b)) return (GuestCallingConvention.Stdcall, b);
            return (GuestCallingConvention.Unknown, null);
        }
        if (n[0] == '@' && at > 1 && TryBytes(n, at, out var fb) && n.IndexOf('@', 1) == at) return (GuestCallingConvention.Fastcall, fb);
        return (GuestCallingConvention.Unknown, null);
    }

    private static bool TryBytes(string n, int at, out int bytes)
    {
        bytes = 0;
        var digits = n.AsSpan(at + 1);
        if (digits.Length == 0 || digits.Length > 5 || !digits.ToString().All(char.IsAsciiDigit)) return false;
        bytes = int.Parse(digits);
        return bytes % 4 == 0;
    }
}
