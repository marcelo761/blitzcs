using System.Buffers.Binary;
using System.Text;
using BlitzCS.BinaryCompiler.Lifting;

namespace BlitzCS.BinaryCompiler.Unpack;

internal sealed record BuiltPe(byte[] Bytes, int ImportCount, int DescriptorCount, int CodePages, int DataPages);

/// <summary>
/// Turns the dumped memory of an unpacked image into a NEW PE32 file. The packer discards the original section table, so the sections of the packed
/// image are kept (their virtual layout is what the exports and the unpacked code assume) and every writable+executable one is split at page granularity
/// into an executable part (pages reachable as code from the OEP and the exports) and a writable data part, because the runtime loader never maps W+X.
/// The import directory is rebuilt from the thunk addresses the stub stored into the image.
/// </summary>
internal static class DerivedPeWriter
{
    private const uint Writable = 0x80000000, Executable = 0x20000000, CodeFlags = 0x60000020, DataFlags = 0xC0000040, ImportFlags = 0x40000040;

    private sealed record Sec(string Name, uint Va, uint VSize, uint Flags, byte[]? Own = null);
    private sealed record Group(string Dll, uint StartRva, List<string> Names);

    public static BuiltPe? Build(PeImage packed, byte[] snapshot, uint oepRva, uint loadBase, GuestLoaderSandbox loader, HashSet<uint> stubSlots, int entrySection, List<BinaryDiagnostic> diags)
    {
        var mem = (byte[])snapshot.Clone();
        var b = packed.Bytes;
        var pe = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x3C));
        var opt = pe + 24;
        var sizeOpt = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 20));
        var table = opt + sizeOpt;
        var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 32));
        var fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 36));
        var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 60));
        if (sectionAlignment != 0x1000 || fileAlignment == 0 || (fileAlignment & (fileAlignment - 1)) != 0 || sizeOfHeaders > b.Length)
        {
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, "UNPACK_UNSUPPORTED_ALIGNMENT", $"SectionAlignment 0x{sectionAlignment:X} / FileAlignment 0x{fileAlignment:X}: only page-aligned sections can be split by page."));
            return null;
        }

        uint Span(PeSection s) => s.VirtualSize != 0 ? s.VirtualSize : s.RawSize;

        // 1. Import slots: every aligned dword holding a thunk the sandbox handed out.
        var slots = new List<(uint Rva, int Index)>();
        for (var k = 0; k < packed.Sections.Count; k++)
        {
            var s = packed.Sections[k];
            var end = Math.Min((ulong)s.VirtualAddress + Span(s), (ulong)mem.Length) & ~3UL;
            for (var rva = s.VirtualAddress; rva < end; rva += 4)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan((int)rva));
                if (!GuestLoaderSandbox.IsThunk(v) || v % 16 != 0 || (v - GuestLoaderSandbox.ThunkBase) / 16 >= loader.Imports.Count) continue;
                if (k == entrySection) { BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan((int)rva), 0); continue; } // the stub's own table
                slots.Add((rva, (int)((v - GuestLoaderSandbox.ThunkBase) / 16)));
            }
        }

        // A thunk-valued dword is an import slot only if it sits in an import table: next to another slot or next to the zero dword that ends a DLL's group. A lone
        // thunk-valued dword is a coincidence of bytes inside code or data (for example `cmp ecx, 0x80` + `jge` = 81 F9 80 00 00 00 7D xx contains the aligned dword
        // 00 00 00 7D = 0x7D000000, the first thunk) and rewriting it would corrupt the program. Nothing has been rewritten yet, so a rejected candidate keeps its bytes.
        var slotRvas = slots.Select(x => x.Rva).ToHashSet();
        uint DwordAt(uint r) => r + 4 <= mem.Length ? BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan((int)r)) : 0xFFFFFFFF;
        var rejected = slots.Where(x => !(slotRvas.Contains(x.Rva - 4) || slotRvas.Contains(x.Rva + 4) || (x.Rva >= 4 && DwordAt(x.Rva - 4) == 0) || DwordAt(x.Rva + 4) == 0)).ToList();
        if (rejected.Count > 0)
        {
            slots = slots.Except(rejected).ToList();
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Info, "UNPACK_ISOLATED_THUNK_IGNORED",
                $"{rejected.Count} dword(s) equal to a loader thunk address sit outside any import table (RVAs {string.Join(", ", rejected.Take(6).Select(x => "0x" + x.Rva.ToString("X")))}{(rejected.Count > 6 ? ", ..." : "")}) and were left unchanged."));
        }

        var groups = new List<Group>();
        uint prev = 0;
        foreach (var (rva, idx) in slots)
        {
            var imp = loader.Imports[idx];
            if (groups.Count > 0 && groups[^1].Dll == imp.Dll && rva == prev + 4) groups[^1].Names.Add(imp.Name);
            else groups.Add(new Group(imp.Dll, rva, [imp.Name]));
            prev = rva;
        }
        var importCount = groups.Sum(g => g.Names.Count);
        if (slots.Count < loader.Imports.Count(i => i.ViaGetProcAddress))
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Warning, "UNPACK_UNSTORED_IMPORTS",
                $"{loader.Imports.Count(i => i.ViaGetProcAddress) - slots.Count} import(s) the stub resolved are not stored in the final image (kept only in registers/stack or in the stub section) and cannot be recovered."));

        // 2. New import section after the last section.
        var lastEnd = packed.Sections.Max(s => s.VirtualAddress + Math.Max(Span(s), 1u));
        var importVa = (lastEnd + sectionAlignment - 1) & ~(sectionAlignment - 1);
        var importBlob = BuildImportSection(groups, importVa, mem, out var importDirSize);

        // 3. Pass 1: executable-but-not-writable view of every W+X section, to discover code pages by static control flow.
        var original = packed.Sections.Select(s => new Sec(s.Name, s.VirtualAddress, Span(s), s.Characteristics)).ToList();
        var pass1 = original.Select(s => (s.Flags & Writable) != 0 && (s.Flags & Executable) != 0 ? s with { Flags = s.Flags & ~Writable } : s).ToList();
        if (importBlob.Length > 0) pass1.Add(new Sec(".impfix", importVa, (uint)importBlob.Length, ImportFlags, importBlob));
        var probeBytes = Assemble(b, pe, opt, table, sizeOfHeaders, fileAlignment, sectionAlignment, oepRva, loadBase, mem, pass1, importVa, importDirSize, diags);
        if (probeBytes is null) return null;
        var probe = PeReader.Read(probeBytes);
        var cfg = ControlFlowGraph.Build(probe, probe.MapImage());
        var codePages = new HashSet<uint>();
        foreach (var gi in cfg.Instructions.Values)
            for (var p = gi.Rva >> 12; p <= (gi.Rva + (uint)gi.Length - 1) >> 12; p++) codePages.Add(p);

        // 4. Pass 2: split W+X sections into code (R+X) and data (R+W) runs.
        var final = new List<Sec>();
        var codeCount = 0; var dataCount = 0;
        foreach (var s in original)
        {
            if ((s.Flags & Writable) == 0 || (s.Flags & Executable) == 0) { final.Add(s); continue; }
            var end = s.Va + s.VSize;
            var runs = new List<(uint Start, uint End, bool Code)>();
            for (var p = s.Va >> 12; p <= (end - 1) >> 12; p++)
            {
                var code = codePages.Contains(p);
                var ps = Math.Max(p << 12, s.Va); var pe2 = Math.Min((p + 1) << 12, end);
                if (code) codeCount++; else dataCount++;
                if (runs.Count > 0 && runs[^1].Code == code) runs[^1] = (runs[^1].Start, pe2, code);
                else runs.Add((ps, pe2, code));
            }
            var stem = s.Name.Length > 5 ? s.Name[..5] : s.Name;
            for (var k = 0; k < runs.Count; k++)
                final.Add(new Sec(runs.Count == 1 ? s.Name + (runs[k].Code ? "" : "") : $"{stem}{(runs[k].Code ? 'c' : 'd')}{k}", runs[k].Start, runs[k].End - runs[k].Start,
                    runs[k].Code ? CodeFlags : DataFlags));
        }
        if (importBlob.Length > 0) final.Add(new Sec(".impfix", importVa, (uint)importBlob.Length, ImportFlags, importBlob));
        var bytes = Assemble(b, pe, opt, table, sizeOfHeaders, fileAlignment, sectionAlignment, oepRva, loadBase, mem, final, importVa, importDirSize, diags);
        return bytes is null ? null : new BuiltPe(bytes, importCount, groups.Count, codeCount, dataCount);
    }

    private static byte[] BuildImportSection(List<Group> groups, uint va, byte[] mem, out uint dirSize)
    {
        dirSize = 0;
        if (groups.Count == 0) return [];
        var ms = new MemoryStream();
        var descriptors = (uint)(groups.Count + 1) * 20;
        dirSize = descriptors;
        var iltOffset = new List<uint>();
        var cursor = descriptors;
        foreach (var g in groups) { iltOffset.Add(cursor); cursor += (uint)(g.Names.Count + 1) * 4; }
        var strings = new MemoryStream();
        var dllNameRva = new List<uint>();
        var thunkValues = new List<uint[]>();
        foreach (var g in groups)
        {
            dllNameRva.Add(va + cursor + (uint)strings.Length);
            strings.Write(Encoding.ASCII.GetBytes(g.Dll + "\0"));
            if (strings.Length % 2 != 0) strings.WriteByte(0);
            var vals = new uint[g.Names.Count];
            for (var i = 0; i < vals.Length; i++)
            {
                var n = g.Names[i];
                if (n.StartsWith('#')) vals[i] = 0x80000000u | ushort.Parse(n.AsSpan(1));
                else
                {
                    vals[i] = va + cursor + (uint)strings.Length;
                    strings.Write([0, 0]);
                    strings.Write(Encoding.ASCII.GetBytes(n + "\0"));
                    if (strings.Length % 2 != 0) strings.WriteByte(0);
                }
            }
            thunkValues.Add(vals);
        }
        var blob = new byte[cursor + (uint)strings.Length];
        for (var g = 0; g < groups.Count; g++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(g * 20), va + iltOffset[g]);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(g * 20 + 12), dllNameRva[g]);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(g * 20 + 16), groups[g].StartRva);
            for (var i = 0; i < thunkValues[g].Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan((int)iltOffset[g] + 4 * i), thunkValues[g][i]);
                // On-disk form of an unbound IAT: the slot mirrors the lookup table.
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan((int)groups[g].StartRva + 4 * i), thunkValues[g][i]);
            }
        }
        strings.ToArray().CopyTo(blob, cursor);
        return blob;
    }

    private static byte[]? Assemble(byte[] orig, int pe, int opt, int table, uint sizeOfHeaders, uint fileAlignment, uint sectionAlignment, uint oep, uint loadBase,
        byte[] mem, List<Sec> sections, uint importVa, uint importDirSize, List<BinaryDiagnostic> diags)
    {
        if (table + sections.Count * 40L > sizeOfHeaders)
        {
            diags.Add(new BinaryDiagnostic(DiagnosticSeverity.Error, "UNPACK_NO_HEADER_ROOM", $"{sections.Count} sections do not fit the {sizeOfHeaders}-byte header area."));
            return null;
        }
        uint Align(uint v, uint a) => (v + a - 1) & ~(a - 1);
        var file = new MemoryStream();
        var headers = new byte[sizeOfHeaders];
        Array.Copy(orig, headers, sizeOfHeaders);
        Array.Clear(headers, table, (int)sizeOfHeaders - table);
        var raws = new List<byte[]>();
        var offset = sizeOfHeaders;
        for (var k = 0; k < sections.Count; k++)
        {
            var s = sections[k];
            var raw = new byte[Align(s.VSize, fileAlignment)];
            if (s.Own is not null) s.Own.CopyTo(raw, 0);
            else Array.Copy(mem, s.Va, raw, 0, Math.Min(s.VSize, (uint)mem.Length - s.Va));
            raws.Add(raw);
            var o = table + k * 40;
            Encoding.ASCII.GetBytes(s.Name).CopyTo(headers, o);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o + 8), s.VSize);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o + 12), s.Va);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o + 16), (uint)raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o + 20), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o + 36), s.Flags);
            offset += (uint)raw.Length;
        }
        var last = sections[^1];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(o), v);
        BinaryPrimitives.WriteUInt16LittleEndian(headers.AsSpan(pe + 6), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(headers.AsSpan(pe + 22), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 22)) | 0x0001)); // RELOCS_STRIPPED
        W32(opt + 16, oep);
        W32(opt + 28, loadBase);   // the dump is correct for the base the stub ran at; there is no relocation table, so this is also the only base it can be mapped at
        W32(opt + 56, Align(last.Va + last.VSize, sectionAlignment));
        W32(opt + 64, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(headers.AsSpan(opt + 70), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(opt + 70)) & ~0x0040)); // not DYNAMIC_BASE
        var nDirs = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(opt + 92));
        void Dir(int i, uint rva, uint size) { if (i < nDirs) { W32(opt + 96 + 8 * i, rva); W32(opt + 100 + 8 * i, size); } }
        Dir(1, importDirSize == 0 ? 0 : importVa, importDirSize);
        Dir(5, 0, 0); Dir(6, 0, 0); Dir(11, 0, 0); Dir(12, 0, 0);
        file.Write(headers);
        foreach (var r in raws) file.Write(r);
        return file.ToArray();
    }
}
