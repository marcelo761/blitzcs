using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlitzCS.BinaryCompiler;

public sealed class PeFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PeReader
{
    private const int MaxSections = 96, MaxImports = 65536, MaxExports = 65536, MaxName = 1024;

    public static PeImage Read(ReadOnlyMemory<byte> input)
    {
        var b = input.ToArray();
        var diags = new List<BinaryDiagnostic>();
        void Err(string code, string msg, uint? rva = null) => diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, code, msg, rva));

        if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') throw new PeFormatException("PE000", "Missing DOS header.");
        var lfanew = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x3C));
        if (lfanew > (uint)b.Length || lfanew + 24 > (uint)b.Length) throw new PeFormatException("PE000", "e_lfanew outside file.");
        var pe = (int)lfanew;
        if (b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0) throw new PeFormatException("PE000", "Missing PE signature.");
        var machine = U16(b, pe + 4); int nsec = U16(b, pe + 6); int sizeOpt = U16(b, pe + 20); var chars = U16(b, pe + 22);
        var opt = pe + 24;
        var sha = Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        PeImage Early() => new()
        {
            Bytes = b, Sha256 = sha, Machine = machine, IsDll = (chars & 0x2000) != 0, ImageBase = 0, SizeOfImage = 0, EntryPointRva = 0,
            Sections = [], Imports = [], Exports = [], Relocations = [], Diagnostics = diags,
        };

        if (machine != 0x14C) { Err("PE002", $"Unsupported machine 0x{machine:X4}; only x86 (0x014C) is supported."); return Early(); }
        if (sizeOpt < 2 || opt + 2 > b.Length) { Err("PE006", "Optional header truncated."); return Early(); }
        var magic = U16(b, opt);
        if (magic == 0x20B) { Err("PE001", "PE32+ (64-bit) images are not supported."); return Early(); }
        if (magic != 0x10B) { Err("PE001", $"Unknown optional header magic 0x{magic:X4}."); return Early(); }
        if (sizeOpt < 96 || (long)opt + sizeOpt > b.Length) { Err("PE006", "Optional header truncated."); return Early(); }

        var entry = U32(b, opt + 16); var imageBase = U32(b, opt + 28); var sizeOfImage = U32(b, opt + 56); var sizeOfHeaders = U32(b, opt + 60);
        var nDirs = (int)Math.Min(Math.Min(U32(b, opt + 92), 16u), (uint)((sizeOpt - 96) / 8));
        (uint Rva, uint Size) Dir(int i) => i < nDirs ? (U32(b, opt + 96 + i * 8), U32(b, opt + 100 + i * 8)) : (0, 0);

        if (sizeOfImage == 0 || sizeOfImage > PeMapping.MaxImageSize) Err("PE007", $"Invalid SizeOfImage 0x{sizeOfImage:X}.");
        if (entry >= sizeOfImage && entry != 0) Err("PE008", "Entry point outside image.", entry);

        var sections = new List<PeSection>();
        var table = (long)opt + sizeOpt;
        if (nsec > MaxSections) Err("PE006", $"Too many sections ({nsec}).");
        else if (table + (long)nsec * 40 > b.Length) Err("PE006", "Section table truncated.");
        else
            for (var i = 0; i < nsec; i++)
            {
                var o = (int)table + i * 40;
                var name = Encoding.ASCII.GetString(b, o, 8).TrimEnd('\0');
                var s = new PeSection(name, U32(b, o + 12), U32(b, o + 8), U32(b, o + 20), U32(b, o + 16), U32(b, o + 36));
                if (s.RawSize > 0 && (ulong)s.RawOffset + s.RawSize > (ulong)b.Length) Err("PE003", $"Section {name} raw data outside file.", s.VirtualAddress);
                if ((ulong)s.VirtualAddress + Math.Max(s.VirtualSize, 1) > sizeOfImage) Err("PE005", $"Section {name} outside SizeOfImage.", s.VirtualAddress);
                sections.Add(s);
            }
        for (var i = 0; i < sections.Count; i++)
            for (var j = i + 1; j < sections.Count; j++)
            {
                var x = sections[i]; var y = sections[j];
                if (Math.Max(x.VirtualSize, 1u) is var xs && Math.Max(y.VirtualSize, 1u) is var ys &&
                    (ulong)x.VirtualAddress < (ulong)y.VirtualAddress + ys && (ulong)y.VirtualAddress < (ulong)x.VirtualAddress + xs)
                    Err("PE004", $"Sections {x.Name} and {y.Name} overlap.", y.VirtualAddress);
            }

        bool Off(uint rva, int len, out int off)
        {
            off = 0;
            if (len < 0) return false;
            if (rva < sizeOfHeaders && (ulong)rva + (ulong)len <= Math.Min(sizeOfHeaders, (uint)b.Length)) { off = (int)rva; return true; }
            foreach (var s in sections)
            {
                if (rva < s.VirtualAddress) continue;
                var d = rva - s.VirtualAddress;
                if (d >= s.RawSize || (ulong)d + (ulong)len > s.RawSize) continue;
                var o = (ulong)s.RawOffset + d;
                if (o + (ulong)len > (ulong)b.Length) continue;
                off = (int)o; return true;
            }
            return false;
        }
        string? Str(uint rva)
        {
            if (!Off(rva, 1, out var o)) return null;
            var e = o; while (e < b.Length && b[e] != 0 && e - o < MaxName) e++;
            if (e >= b.Length || b[e] != 0) return null;
            return Encoding.Latin1.GetString(b, o, e - o);
        }
        bool Rd32(uint rva, out uint v) { v = 0; if (!Off(rva, 4, out var o)) return false; v = U32(b, o); return true; }

        // Delay-load directory (IMAGE_DELAYLOAD_DESCRIPTOR, 32 bytes). Each delay IAT slot becomes an ordinary import (PeImport.IsDelay): the loader binds
        // it like an eager import, so the slot never reaches the delay-load helper. Only the RVA form (Attributes bit 0) is supported.
        void ReadDelayImports(uint dirRva, List<PeImport> into)
        {
            if (dirRva == 0) return;
            for (uint d = 0; d < 4096; d++)
            {
                var drva = dirRva + d * 32;
                if (!Off(drva, 32, out var o)) { Err("PE050", "Delay import descriptor outside image.", drva); return; }
                uint attrs = U32(b, o), nameRva = U32(b, o + 4), iatRva = U32(b, o + 12), intRva = U32(b, o + 16);
                if (attrs == 0 && nameRva == 0 && iatRva == 0 && intRva == 0 && U32(b, o + 8) == 0) return; // terminator
                if ((attrs & 1) == 0)
                {
                    Err("PE052", "Delay import descriptor uses virtual addresses (Attributes bit 0 clear, pre-VC7 format); only the RVA form is supported.", drva);
                    continue;
                }
                var dll = nameRva == 0 ? null : Str(nameRva);
                if (dll is null) { Err("PE011", "Delay import DLL name unreadable.", drva); continue; }
                if (iatRva == 0 || intRva == 0) { Err("PE012", $"Delay import {dll} has no import address table or import name table.", drva); continue; }
                for (uint i = 0; i < MaxImports; i++)
                {
                    if (!Rd32(intRva + i * 4, out var th)) { Err("PE013", $"Delay import name table of {dll} runs outside image.", intRva + i * 4); break; }
                    if (th == 0) break;
                    var iat = iatRva + i * 4;
                    if (!Off(iat, 4, out _)) { Err("PE013", $"Delay import address table of {dll} runs outside image.", iat); break; }
                    if ((th & 0x80000000) != 0) into.Add(new PeImport(dll, null, (ushort)(th & 0xFFFF), iat, true));
                    else
                    {
                        var n = Str(th + 2);
                        if (n is null) { Err("PE014", $"Delay import name of {dll} unreadable.", th); continue; }
                        into.Add(new PeImport(dll, n, null, iat, true));
                    }
                    if (i == MaxImports - 1) Err("PE013", $"Too many delay imports in {dll}.", intRva);
                }
            }
            Err("PE050", "Delay import directory has no terminating descriptor.", dirRva);
        }

        var imports = new List<PeImport>();
        var (impRva, impSize) = Dir(1);
        if (impRva != 0)
        {
            for (uint d = 0; d < 65536; d++)
            {
                var drva = impRva + d * 20;
                if (!Off(drva, 20, out var o)) { Err("PE010", "Import descriptor outside image.", drva); break; }
                uint oft = U32(b, o), nameRva = U32(b, o + 12), ft = U32(b, o + 16);
                if (oft == 0 && nameRva == 0 && ft == 0) break;
                var dll = nameRva == 0 ? null : Str(nameRva);
                if (dll is null) { Err("PE011", "Import DLL name unreadable.", drva); continue; }
                if (ft == 0) { Err("PE012", $"Import {dll} has no FirstThunk.", drva); continue; }
                var ilt = oft != 0 ? oft : ft;
                for (uint i = 0; i < MaxImports; i++)
                {
                    if (!Rd32(ilt + i * 4, out var th)) { Err("PE013", $"Import thunk list of {dll} runs outside image.", ilt + i * 4); break; }
                    if (th == 0) break;
                    var iat = ft + i * 4;
                    if ((th & 0x80000000) != 0) imports.Add(new PeImport(dll, null, (ushort)(th & 0xFFFF), iat));
                    else
                    {
                        var n = Str(th + 2);
                        if (n is null) { Err("PE014", $"Import name of {dll} unreadable.", th); continue; }
                        imports.Add(new PeImport(dll, n, null, iat));
                    }
                    if (i == MaxImports - 1) Err("PE013", $"Too many imports in {dll}.", ilt);
                }
            }
        }

        var exports = new List<PeExport>();
        var (expRva, expSize) = Dir(0);
        if (expRva != 0)
        {
            if (!Off(expRva, 40, out var o) ) Err("PE020", "Export directory outside image.", expRva);
            else
            {
                uint nBase = U32(b, o + 16), nFuncs = U32(b, o + 20), nNames = U32(b, o + 24), aFuncs = U32(b, o + 28), aNames = U32(b, o + 32), aOrds = U32(b, o + 36);
                if (nFuncs > MaxExports || nNames > MaxExports) Err("PE021", "Export counts implausible.", expRva);
                else
                {
                    var names = new Dictionary<uint, string>();
                    for (uint i = 0; i < nNames; i++)
                    {
                        if (!Rd32(aNames + i * 4, out var nr) || !Off(aOrds + i * 2, 2, out var oo)) { Err("PE022", "Export name table outside image.", aNames); break; }
                        var idx = U16(b, oo); var nm = Str(nr);
                        if (nm is null || idx >= nFuncs) { Err("PE023", "Export name/ordinal invalid.", nr); continue; }
                        names[idx] = nm;
                    }
                    for (uint i = 0; i < nFuncs; i++)
                    {
                        if (!Rd32(aFuncs + i * 4, out var fr)) { Err("PE022", "Export address table outside image.", aFuncs); break; }
                        if (fr == 0) continue;
                        if ((ulong)nBase + i > ushort.MaxValue) { Err("PE024", "Export ordinal exceeds 65535.", fr); continue; }
                        string? fwd = null;
                        if (fr >= expRva && (ulong)fr < (ulong)expRva + expSize)
                        {
                            fwd = Str(fr);
                            if (fwd is null) { Err("PE025", "Export forwarder unreadable.", fr); continue; }
                        }
                        exports.Add(new PeExport(names.GetValueOrDefault(i), (ushort)(nBase + i), fr, fwd));
                    }
                }
            }
        }

        var relocs = new List<PeRelocation>();
        var (relRva, relSize) = Dir(5);
        if (relRva != 0 && relSize != 0)
        {
            if (!Off(relRva, (int)Math.Min(relSize, int.MaxValue), out var o)) Err("PE030", "Base relocation directory outside image.", relRva);
            else
            {
                var end = (long)o + relSize; var p = (long)o;
                while (p + 8 <= end)
                {
                    var page = U32(b, (int)p); var size = U32(b, (int)p + 4);
                    if (size < 8 || (size & 1) != 0 || p + size > end) { Err("PE030", "Malformed relocation block.", page); break; }
                    for (var e = p + 8; e < p + size; e += 2)
                    {
                        var entry16 = U16(b, (int)e); var type = entry16 >> 12; var rva = page + (uint)(entry16 & 0xFFF);
                        if (type == 0) continue;
                        if (type != 3) { Err("PE031", $"Unsupported relocation type {type}.", rva); continue; }
                        if (rva >= sizeOfImage) { Err("PE032", "Relocation outside image.", rva); continue; }
                        relocs.Add(new PeRelocation(rva, 3));
                    }
                    p += size;
                }
            }
        }

        PeTls? tls = null;
        var (tlsRva, _) = Dir(9);
        if (tlsRva != 0)
        {
            if (!Off(tlsRva, 16, out var o)) Err("PE040", "TLS directory outside image.", tlsRva);
            else tls = new PeTls(U32(b, o), U32(b, o + 4), U32(b, o + 8), U32(b, o + 12), Off(tlsRva + 16, 4, out var zo) ? U32(b, zo) : 0);
        }
        ReadDelayImports(Dir(13).Rva, imports);
        if (Dir(14).Rva != 0) Err("PE051", "Managed (CLR) images are not supported.", Dir(14).Rva);

        return new PeImage
        {
            Bytes = b, Sha256 = sha, Machine = machine, IsDll = (chars & 0x2000) != 0, ImageBase = imageBase, SizeOfImage = sizeOfImage,
            EntryPointRva = entry, Sections = sections, Imports = imports, Exports = exports, Relocations = relocs, Tls = tls, Diagnostics = diags,
        };
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
