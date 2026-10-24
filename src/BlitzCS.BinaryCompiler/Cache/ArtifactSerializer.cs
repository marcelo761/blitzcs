using System.Security.Cryptography;
using System.Text;

namespace BlitzCS.BinaryCompiler;

public sealed class ArtifactFormatException(string message) : Exception(message);

public sealed class ArtifactSerializer
{
    public const int CurrentVersion = 1;
    private static readonly byte[] Magic = "BCSA"u8.ToArray();
    private const int MaxString = 1 << 20;

    public byte[] Serialize(TranslationArtifact a)
    {
        ArgumentNullException.ThrowIfNull(a);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Magic); w.Write(CurrentVersion);
            w.Write(a.FormatVersion); Str(w, a.SourceSha256); Str(w, a.CacheKey); w.Write(a.GuestImageBase); w.Write(a.GuestImageSize);
            w.Write(a.HostImage.Length); w.Write(a.HostImage); w.Write(a.CodeSize);
            w.Write(a.DispatchTableOffset); w.Write(a.ImportThunkTableOffset); w.Write(a.ContextOffset); w.Write(a.EnterGuestOffset); w.Write(a.ExitGuestRva); w.Write(a.ImportStubBaseRva);
            w.Write(a.AddressMap.Count); foreach (var m in a.AddressMap) { w.Write(m.GuestRva); w.Write(m.HostOffset); }
            w.Write(a.Exports.Count);
            foreach (var e in a.Exports) { OptStr(w, e.Name); w.Write(e.Ordinal); w.Write(e.GuestRva); w.Write(e.HostOffset); w.Write((int)e.Convention); w.Write(e.ArgBytes.HasValue); w.Write(e.ArgBytes ?? 0); }
            w.Write(a.Imports.Count);
            foreach (var i in a.Imports) { Str(w, i.Dll); OptStr(w, i.Name); w.Write(i.Ordinal.HasValue); w.Write(i.Ordinal ?? (ushort)0); w.Write(i.IatRva); }
            w.Write(a.Diagnostics.Count);
            foreach (var d in a.Diagnostics) { w.Write((int)d.Severity); Str(w, d.Code); Str(w, d.Message); w.Write(d.GuestRva.HasValue); w.Write(d.GuestRva ?? 0u); }
        }
        var body = ms.ToArray();
        var res = new byte[body.Length + 32];
        body.CopyTo(res, 0); SHA256.HashData(body).CopyTo(res, body.Length);
        return res;
    }

    public TranslationArtifact Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + 4 + 32) throw new ArtifactFormatException("Artifact truncated.");
        var body = data[..^32];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(data[^32..])) throw new ArtifactFormatException("Artifact checksum mismatch.");
        try { return Parse(body.ToArray()); }
        catch (ArtifactFormatException) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or OverflowException or InvalidOperationException or FormatException)
        { throw new ArtifactFormatException("Artifact corrupt: " + ex.Message); }
    }

    private static TranslationArtifact Parse(byte[] body)
    {
        using var ms = new MemoryStream(body, false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new ArtifactFormatException("Bad artifact magic.");
        var ver = r.ReadInt32();
        if (ver != CurrentVersion) throw new ArtifactFormatException($"Unsupported artifact version {ver}.");
        int Remaining() => (int)(ms.Length - ms.Position);
        int Count(int minElem) { var n = r.ReadInt32(); if (n < 0 || (long)n * minElem > Remaining()) throw new ArtifactFormatException("Bad element count."); return n; }
        string ReadStr() { var n = r.ReadInt32(); if (n < 0 || n > MaxString || n > Remaining()) throw new ArtifactFormatException("Bad string length."); return Encoding.UTF8.GetString(r.ReadBytes(n)); }
        string? ReadOpt() => r.ReadBoolean() ? ReadStr() : null;

        var fv = r.ReadInt32(); var src = ReadStr(); var key = ReadStr(); var gb = r.ReadUInt32(); var gs = r.ReadUInt32();
        var hl = r.ReadInt32(); if (hl < 0 || hl > Remaining()) throw new ArtifactFormatException("Bad host image length.");
        var host = r.ReadBytes(hl); var code = r.ReadInt32();
        if (code < 0 || code > hl) throw new ArtifactFormatException("Bad code size.");
        uint disp = r.ReadUInt32(), thunk = r.ReadUInt32(), ctx = r.ReadUInt32(), enter = r.ReadUInt32(), exit = r.ReadUInt32(), stubs = r.ReadUInt32();
        var map = new List<AddressMapEntry>(); for (var n = Count(8); n > 0; n--) map.Add(new AddressMapEntry(r.ReadUInt32(), r.ReadUInt32()));
        var exports = new List<TranslatedExport>();
        for (var n = Count(20); n > 0; n--)
        {
            var name = ReadOpt(); var ord = r.ReadUInt16(); var rva = r.ReadUInt32(); var off = r.ReadUInt32(); var cc = r.ReadInt32();
            if (!Enum.IsDefined((GuestCallingConvention)cc)) throw new ArtifactFormatException("Bad calling convention.");
            var has = r.ReadBoolean(); var ab = r.ReadInt32();
            exports.Add(new TranslatedExport(name, ord, rva, off, (GuestCallingConvention)cc, has ? ab : null));
        }
        var imports = new List<PeImport>();
        for (var n = Count(12); n > 0; n--)
        {
            var dll = ReadStr(); var name = ReadOpt(); var has = r.ReadBoolean(); var ord = r.ReadUInt16(); var iat = r.ReadUInt32();
            imports.Add(new PeImport(dll, name, has ? ord : null, iat));
        }
        var diags = new List<BinaryDiagnostic>();
        for (var n = Count(17); n > 0; n--)
        {
            var sev = r.ReadInt32();
            if (!Enum.IsDefined((DiagnosticSeverity)sev)) throw new ArtifactFormatException("Bad severity.");
            var code2 = ReadStr(); var msg = ReadStr(); var has = r.ReadBoolean(); var rva = r.ReadUInt32();
            diags.Add(new BinaryDiagnostic((DiagnosticSeverity)sev, code2, msg, has ? rva : null));
        }
        if (Remaining() != 0) throw new ArtifactFormatException("Trailing bytes in artifact.");
        return new TranslationArtifact(fv, src, key, gb, gs, host, code, disp, thunk, ctx, enter, exit, stubs, map, exports, imports, diags);
    }

    private static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b.Length); w.Write(b); }
    private static void OptStr(BinaryWriter w, string? s) { w.Write(s is not null); if (s is not null) Str(w, s); }
}
