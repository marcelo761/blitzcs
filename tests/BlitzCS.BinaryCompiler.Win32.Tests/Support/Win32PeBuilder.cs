using System.Buffers.Binary;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// Builds minimal, valid PE32 (i386) DLLs for tests. Derived from the compiler test project's fixture builder (same repository) with support for several imported DLLs: each DLL's thunk array is NUL-terminated, so after every group one empty IAT slot follows. Layout: headers at 0, .text at RVA 0x1000 (code),
/// .data at 0x2000 (writable), .idata at 0x3000 (imports; IAT slots), .edata at 0x4000, .reloc at 0x5000 (when relocs requested).
/// </summary>
public sealed class Win32PeBuilder
{
    public const uint ImageBase = 0x10000000;
    public const uint TextRva = 0x1000, DataRva = 0x2000, IdataRva = 0x3000, EdataRva = 0x4000, RelocRva = 0x5000;

    private byte[] code = [0xC3];
    private byte[] data = new byte[0x100];
    private readonly List<(string Name, uint CodeOffset)> exports = [];
    private readonly List<(string Dll, string Name)> imports = [];
    private readonly List<uint> relocRvas = [];
    private uint entryRva;

    public Win32PeBuilder WithCode(byte[] bytes) { code = bytes; return this; }
    public Win32PeBuilder WithData(byte[] bytes) { data = bytes; return this; }
    public Win32PeBuilder Export(string name, uint codeOffset) { exports.Add((name, codeOffset)); return this; }
    /// <summary>Adds an import; returns the RVA of its IAT slot (valid after any number of imports: slots are 4 bytes in declaration order).</summary>
    public uint Import(string dll, string name) { imports.Add((dll, name)); return IdataRva + 0x400 + (uint)(imports.Count - 1 + imports.Select(x => x.Dll).Distinct().Count() - 1) * 4; }
    /// <summary>Registers an absolute 32-bit field (HIGHLOW) at the given RVA.</summary>
    public Win32PeBuilder Relocate(uint rva) { relocRvas.Add(rva); return this; }
    public Win32PeBuilder EntryPoint(uint codeOffset) { entryRva = TextRva + codeOffset; return this; }

    public byte[] Build()
    {
        bool hasImports = imports.Count > 0, hasExports = exports.Count > 0, hasRelocs = relocRvas.Count > 0;
        var sections = new List<(string, uint rva, byte[] bytes, uint flags)>
        {
            (".text", TextRva, code, 0x60000020), (".data", DataRva, data, 0xC0000040),
        };
        var idata = BuildImports(); var edata = BuildExports(); var reloc = BuildRelocs();
        if (hasImports) sections.Add((".idata", IdataRva, idata, 0xC0000040));
        if (hasExports) sections.Add((".edata", EdataRva, edata, 0x40000040));
        if (hasRelocs) sections.Add((".reloc", RelocRva, reloc, 0x42000040));

        const int headerSize = 0x400; // DOS(0x40)+PE sig+COFF+optional(224)+sections, file-aligned
        var raw = new List<(int off, byte[] b)>(); int cursor = headerSize;
        foreach (var s in sections) { raw.Add((cursor, s.Item3)); cursor += (s.Item3.Length + 0x1FF) & ~0x1FF; }
        var file = new byte[cursor];
        file[0] = (byte)'M'; file[1] = (byte)'Z'; BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3C), 0x40);
        var pe = 0x40; Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, pe);
        var coff = pe + 4;
        W16(file, coff, 0x014C); W16(file, coff + 2, (ushort)sections.Count); W16(file, coff + 16, 224);
        W16(file, coff + 18, (ushort)0x2102); // DLL | executable | 32-bit machine
        var opt = coff + 20;
        W16(file, opt, 0x10B); W32(file, opt + 16, entryRva); W32(file, opt + 28, ImageBase); W32(file, opt + 32, 0x1000); W32(file, opt + 36, 0x200);
        W16(file, opt + 40, 4); W16(file, opt + 48, 4);
        var lastRva = sections[^1].rva + (uint)((sections[^1].Item3.Length + 0xFFF) & ~0xFFF);
        W32(file, opt + 56, lastRva); W32(file, opt + 60, headerSize); W16(file, opt + 68, 2); W32(file, opt + 92, 16);
        if (hasExports) { W32(file, opt + 96, EdataRva); W32(file, opt + 100, (uint)edata.Length); }
        if (hasImports) { W32(file, opt + 104, IdataRva); W32(file, opt + 108, (uint)idata.Length); }
        if (hasRelocs) { W32(file, opt + 96 + 5 * 8, RelocRva); W32(file, opt + 96 + 5 * 8 + 4, (uint)reloc.Length); }
        var sh = opt + 224;
        for (var i = 0; i < sections.Count; i++, sh += 40)
        {
            Encoding.ASCII.GetBytes(sections[i].Item1).CopyTo(file, sh);
            W32(file, sh + 8, (uint)sections[i].Item3.Length); W32(file, sh + 12, sections[i].rva);
            W32(file, sh + 16, (uint)((sections[i].Item3.Length + 0x1FF) & ~0x1FF)); W32(file, sh + 20, (uint)raw[i].off);
            W32(file, sh + 36, sections[i].flags);
            raw[i].b.CopyTo(file, raw[i].off);
        }
        return file;
    }

    private byte[] BuildImports()
    {
        if (imports.Count == 0) return [];
        var ng = imports.Select(x => x.Dll).Distinct().Count(); var buf = new byte[0x400 + (imports.Count + ng) * 8 + 0x400]; // descriptors+names (0..0x400) | IAT | ILT area
        var groups = imports.Select((x, i) => (x, i)).GroupBy(t => t.x.Dll).ToList();
        // Fixture limitation: IAT slots are laid out in declaration order, so imports must be declared grouped by DLL.
        var order = imports.Select(x => x.Dll).ToList();
        if (!order.SequenceEqual(order.OrderBy(d => order.IndexOf(d)))) throw new InvalidOperationException("declare imports grouped by dll");
        uint strings = IdataRva + 0x200; var stringCursor = 0x200; var iltBase = 0x400 + (imports.Count + ng) * 4; int Slot(int idx) => idx + imports.Take(idx + 1).Select(x => x.Dll).Distinct().Count() - 1;
        var d = 0;
        foreach (var g in groups)
        {
            var first = Slot(g.First().i); var ilt = (uint)(iltBase + first * 4);
            var nameRva = IdataRva + (uint)stringCursor; Encoding.ASCII.GetBytes(g.Key + "\0").CopyTo(buf, stringCursor); stringCursor += g.Key.Length + 1;
            W32(buf, d * 20, IdataRva + ilt); W32(buf, d * 20 + 12, nameRva); W32(buf, d * 20 + 16, IdataRva + 0x400 + (uint)first * 4);
            foreach (var (x, i) in g)
            {
                var hintRva = IdataRva + (uint)stringCursor; stringCursor += 2; Encoding.ASCII.GetBytes(x.Name + "\0").CopyTo(buf, stringCursor); stringCursor += x.Name.Length + 1;
                W32(buf, 0x400 + Slot(i) * 4, hintRva); W32(buf, iltBase + Slot(i) * 4, hintRva);
            }
            d++;
        }
        _ = strings;
        return buf;
    }

    private byte[] BuildExports()
    {
        if (exports.Count == 0) return [];
        var sorted = exports.Select((e, i) => (e, i)).OrderBy(t => t.e.Name, StringComparer.Ordinal).ToList();
        var buf = new byte[0x1000]; var n = exports.Count;
        var fn = 40u; var names = fn + (uint)n * 4; var ords = names + (uint)n * 4; var str = ords + (uint)n * 2;
        var cursor = (int)str; Encoding.ASCII.GetBytes("fixture.dll\0").CopyTo(buf, cursor); var dllName = EdataRva + (uint)cursor; cursor += 12;
        W32(buf, 12, dllName); W32(buf, 16, 1); W32(buf, 20, (uint)n); W32(buf, 24, (uint)n);
        W32(buf, 28, EdataRva + fn); W32(buf, 32, EdataRva + names); W32(buf, 36, EdataRva + ords);
        for (var i = 0; i < n; i++)
        {
            var (e, original) = sorted[i];
            W32(buf, (int)fn + i * 4, TextRva + e.CodeOffset);
            Encoding.ASCII.GetBytes(e.Name + "\0").CopyTo(buf, cursor); W32(buf, (int)names + i * 4, EdataRva + (uint)cursor); cursor += e.Name.Length + 1;
            W16(buf, (int)ords + i * 2, (ushort)i);
            _ = original;
        }
        return buf[..cursor];
    }

    private byte[] BuildRelocs()
    {
        if (relocRvas.Count == 0) return [];
        var blocks = relocRvas.GroupBy(r => r & ~0xFFFu).OrderBy(g => g.Key).ToList();
        using var ms = new MemoryStream();
        foreach (var g in blocks)
        {
            var entries = g.OrderBy(x => x).ToList(); if (entries.Count % 2 == 1) entries.Add(0xFFFFFFFF);
            var size = 8 + entries.Count * 2; var b = new byte[size];
            W32(b, 0, g.Key); W32(b, 4, (uint)size);
            for (var i = 0; i < entries.Count; i++) W16(b, 8 + i * 2, entries[i] == 0xFFFFFFFF ? (ushort)0 : (ushort)(0x3000 | (entries[i] & 0xFFF)));
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
}
