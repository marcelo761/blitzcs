using System.Buffers.Binary;
using System.Text;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Unpack.Tests;

/// <summary>What the stub does before it decodes the payload. Every non-Normal variant must make the unpacker refuse with a specific code.</summary>
public enum StubVariant { Normal, AntiDebugPeb, CallsUnimplementedImport, Rdtsc, UsesFpu, ReturnsWithoutOep, SpinsForever, XorOnlyNoImports }

/// <summary>
/// A tiny test packer. The produced PE32 DLL has two sections: PK0 (RWX, VirtualSize 0x2000, RawSize 0: the original code and IAT live only after execution)
/// and PK1 (RWX, file backed: the stub, the XOR-0x5A encoded payload, the stub's import table kernel32!LoadLibraryA/GetProcAddress, the export directory).
/// The stub decodes the payload into PK0, resolves msvcrt!abs through LoadLibraryA/GetProcAddress into an IAT slot inside PK0, then jumps to the OEP.
/// </summary>
internal static class SyntheticPacker
{
    public const uint Base = 0x10000000, Pk0 = 0x1000, Pk1 = 0x3000, OepRva = 0x1000, AddRva = 0x1010, CallAbsRva = 0x1020, AbsSlotRva = 0x2100;
    private const uint PayloadOffset = 0x200, StringsOffset = 0x400, ImportOffset = 0x500, ExportOffset = 0x600;
    public const int PayloadLength = 0x200;
    public static byte[] OriginalCode()
    {
        var code = new byte[PayloadLength];
        // 0x1000: DllMain: mov eax,1 ; ret 0Ch
        new byte[] { 0xB8, 1, 0, 0, 0, 0xC2, 0x0C, 0 }.CopyTo(code, 0);
        // 0x1010: _add2@8: mov eax,[esp+4] ; add eax,[esp+8] ; ret 8
        new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x03, 0x44, 0x24, 0x08, 0xC2, 0x08, 0 }.CopyTo(code, 0x10);
        // 0x1020: _callabs@4: push [esp+4] ; call [AbsSlot] ; add esp,4 ; ret 4
        var at = 0x20;
        new byte[] { 0xFF, 0x74, 0x24, 0x04, 0xFF, 0x15 }.CopyTo(code, at); at += 6;
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(at), Base + AbsSlotRva); at += 4;
        new byte[] { 0x83, 0xC4, 0x04, 0xC2, 0x04, 0 }.CopyTo(code, at);
        return code;
    }

    public static byte[] Build(StubVariant variant = StubVariant.Normal)
    {
        var pk1 = new byte[0x800];
        var payload = OriginalCode();
        for (var i = 0; i < payload.Length; i++) pk1[PayloadOffset + i] = (byte)(payload[i] ^ 0x5A);

        void Str(uint off, string s) => Encoding.ASCII.GetBytes(s + "\0").CopyTo(pk1, off);
        Str(StringsOffset, "msvcrt.dll"); Str(StringsOffset + 0x20, "abs"); Str(StringsOffset + 0x40, "user32.dll"); Str(StringsOffset + 0x60, "MessageBoxA");

        // stub import table: kernel32.dll!LoadLibraryA, GetProcAddress
        uint idescrRva = Pk1 + ImportOffset, iltRva = Pk1 + ImportOffset + 0x40, iatRva = Pk1 + ImportOffset + 0x60, nameRva = Pk1 + ImportOffset + 0x80;
        void W32(uint off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(pk1.AsSpan((int)off), v);
        W32(ImportOffset, iltRva); W32(ImportOffset + 12, nameRva); W32(ImportOffset + 16, iatRva);
        Str(ImportOffset + 0x80, "KERNEL32.dll");
        Str(ImportOffset + 0xA0 + 2, "LoadLibraryA"); Str(ImportOffset + 0xC0 + 2, "GetProcAddress");
        W32(ImportOffset + 0x40, Pk1 + ImportOffset + 0xA0); W32(ImportOffset + 0x44, Pk1 + ImportOffset + 0xC0);
        W32(ImportOffset + 0x60, Pk1 + ImportOffset + 0xA0); W32(ImportOffset + 0x64, Pk1 + ImportOffset + 0xC0);
        uint loadLibrary = Base + iatRva, getProc = Base + iatRva + 4;

        // export directory: _add2@8, _callabs@4 (names sorted ordinally)
        var e = ExportOffset;
        W32(e + 12, Pk1 + e + 0x80); W32(e + 16, 1); W32(e + 20, 2); W32(e + 24, 2);
        W32(e + 28, Pk1 + e + 0x28); W32(e + 32, Pk1 + e + 0x30); W32(e + 36, Pk1 + e + 0x38);
        W32(e + 0x28, AddRva); W32(e + 0x2C, CallAbsRva);
        Str(e + 0x40, "_add2@8"); Str(e + 0x50, "_callabs@4"); Str(e + 0x80, "packed.dll");
        W32(e + 0x30, Pk1 + e + 0x40); W32(e + 0x34, Pk1 + e + 0x50);
        BinaryPrimitives.WriteUInt16LittleEndian(pk1.AsSpan((int)e + 0x38), 0); BinaryPrimitives.WriteUInt16LittleEndian(pk1.AsSpan((int)e + 0x3A), 1);

        // ---- the stub ----
        var a = new Assembler(32);
        switch (variant)
        {
            case StubVariant.ReturnsWithoutOep: a.mov(eax, 1); a.ret(0xC); break;
            case StubVariant.SpinsForever: { var l = a.CreateLabel(); a.Label(ref l); a.jmp(l); break; }
            case StubVariant.AntiDebugPeb: a.mov(eax, __dword_ptr.fs[0x30]); a.movzx(eax, __byte_ptr[eax + 2]); break;
            case StubVariant.Rdtsc: a.rdtsc(); break;
            case StubVariant.UsesFpu: a.fld1(); break;
        }
        if (variant is not (StubVariant.ReturnsWithoutOep or StubVariant.SpinsForever))
        {
            a.pushad();
            a.mov(esi, Base + Pk1 + PayloadOffset);
            a.mov(edi, Base + Pk0);
            a.mov(ecx, PayloadLength);
            var loop = a.CreateLabel(); a.Label(ref loop);
            a.mov(al, __byte_ptr[esi]); a.xor(al, 0x5A); a.mov(__byte_ptr[edi], al);
            a.inc(esi); a.inc(edi); a.dec(ecx); a.jnz(loop);
            if (variant != StubVariant.XorOnlyNoImports)
            {
                a.push(Base + Pk1 + StringsOffset); a.call(__dword_ptr[loadLibrary]);
                a.push(Base + Pk1 + StringsOffset + 0x20); a.push(eax); a.call(__dword_ptr[getProc]);
                a.mov(__dword_ptr[Base + AbsSlotRva], eax);
            }
            if (variant == StubVariant.CallsUnimplementedImport)
            {
                a.push(Base + Pk1 + StringsOffset + 0x40); a.call(__dword_ptr[loadLibrary]);
                a.push(Base + Pk1 + StringsOffset + 0x60); a.push(eax); a.call(__dword_ptr[getProc]);
                a.push(0); a.call(eax);
            }
            a.popad();
            a.jmp(Base + OepRva);
        }
        var ms = new MemoryStream();
        a.Assemble(new StreamCodeWriter(ms), Base + Pk1);
        var stub = ms.ToArray();
        Assert_(stub.Length < PayloadOffset, "stub too large");
        stub.CopyTo(pk1, 0);
        return Pe(pk1, idescrRva, ExportOffset);
    }

    private static void Assert_(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }

    private static byte[] Pe(byte[] pk1, uint importRva, uint exportOffset)
    {
        const int headers = 0x400;
        var file = new byte[headers + pk1.Length];
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(o), v);
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(o), v);
        file[0] = (byte)'M'; file[1] = (byte)'Z'; W32(0x3C, 0x40);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, 0x40);
        var coff = 0x44; W16(coff, 0x14C); W16(coff + 2, 2); W16(coff + 16, 224); W16(coff + 18, 0x2102);
        var opt = coff + 20;
        W16(opt, 0x10B); W32(opt + 16, Pk1); W32(opt + 28, Base); W32(opt + 32, 0x1000); W32(opt + 36, 0x200);
        W16(opt + 40, 4); W16(opt + 48, 4); W32(opt + 56, Pk1 + 0x1000); W32(opt + 60, headers); W16(opt + 68, 2); W32(opt + 92, 16);
        W32(opt + 96, Pk1 + exportOffset); W32(opt + 100, 0x100);
        W32(opt + 104, importRva); W32(opt + 108, 0x28);
        var sh = opt + 224;
        Encoding.ASCII.GetBytes("PK0").CopyTo(file, sh);
        W32(sh + 8, 0x2000); W32(sh + 12, Pk0); W32(sh + 16, 0); W32(sh + 20, 0); W32(sh + 36, 0xE0000080);
        sh += 40;
        Encoding.ASCII.GetBytes("PK1").CopyTo(file, sh);
        W32(sh + 8, (uint)pk1.Length); W32(sh + 12, Pk1); W32(sh + 16, (uint)pk1.Length); W32(sh + 20, headers); W32(sh + 36, 0xE0000060);
        pk1.CopyTo(file, headers);
        return file;
    }
}
