using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

/// <summary>
/// Hand-assembled host image obeying the TranslationArtifact contract, so the loader is testable without the translator.
/// Guest functions are "translated" x64 code that runs with RSP = guest ESP and jumps to the exit routine to finish.
/// Imports: stub i stores the continuation (ctx+8), switches to the native stack saved in ctx[0] and calls thunk i.
/// </summary>
internal sealed class FakeGuest
{
    public const long Ctx = 0x1000, Thunks = 0x1040;

    public required PeImage Image { get; init; }
    public required TranslationArtifact Artifact { get; init; }

    public static FakeGuest Create(bool dllMainReturnsTrue = true, bool withImport = true, bool withEntry = true)
    {
        var pe = new PeFixtureBuilder();
        if (withImport) pe.Import("host.dll", "hook");
        if (withEntry) pe.EntryPoint(0);
        var image = PeReader.Read(pe.Build());
        var (code, offsets) = AssembleHost(withImport, dllMainReturnsTrue);
        var host = code;
        var exports = new List<TranslatedExport>();
        void Export(string name, GuestCallingConvention conv, int? argBytes) =>
            exports.Add(new(name, (ushort)(exports.Count + 1), 0, offsets[name], conv, argBytes));
        Export("add", GuestCallingConvention.Cdecl, null);
        Export("add_std", GuestCallingConvention.Stdcall, 8);
        Export("getret", GuestCallingConvention.Cdecl, null);
        Export("writer", GuestCallingConvention.Cdecl, null);
        Export("overrun", GuestCallingConvention.Cdecl, null);
        Export("mystery", GuestCallingConvention.Unknown, null);
        Export("fast", GuestCallingConvention.Fastcall, 4);
        if (withImport)
        {
            Export("callimp", GuestCallingConvention.Cdecl, null);
            Export("callimp_twice", GuestCallingConvention.Cdecl, null);
        }
        var size = image.SizeOfImage;
        var artifact = new TranslationArtifact(1, image.Sha256, "fake", image.ImageBase, size, host, 0x1000, 0, (uint)Thunks, (uint)Ctx,
            offsets["enter"], size, size + 0x10,
            withEntry ? [new AddressMapEntry(image.EntryPointRva, offsets["dllmain"])] : [], exports, image.Imports, []);
        return new FakeGuest { Image = image, Artifact = artifact };
    }

    private static (byte[] Code, Dictionary<string, uint> Offsets) AssembleHost(bool withImport, bool dllMainOk)
    {
        var first = AssembleHost(withImport, dllMainOk, 0);
        return AssembleHost(withImport, dllMainOk, 0x1000 - (int)first.Offsets["ctx"]);
    }

    private static (byte[] Code, Dictionary<string, uint> Offsets) AssembleHost(bool withImport, bool dllMainOk, int pad)
    {
        var a = new Assembler(64);
        var labels = new Dictionary<string, Label>();
        Label L(string n) => labels.TryGetValue(n, out var l) ? l : labels[n] = a.CreateLabel(n);
        void Mark(string n) { var l = L(n); a.Label(ref l); labels[n] = l; }
        AssemblerMemoryOperand Mem(long off) => __qword_ptr[off == Ctx ? L("ctx") : off == Ctx + 8 ? L("ctxcont") : L("thunk0")];

        Mark("enter");
        a.push(rbx); a.push(rbp); a.push(r12); a.push(r13); a.push(r14); a.push(r15);
        a.push(Mem(Ctx));
        a.mov(Mem(Ctx), rsp);
        a.mov(esp, esi);
        a.jmp(rdi);

        Mark("exit");
        a.mov(rsp, Mem(Ctx));
        a.pop(Mem(Ctx));
        a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12); a.pop(rbp); a.pop(rbx);
        a.ret();

        void CallImport(string cont)
        {
            // ecx = argument; r14 = guest ESP at function entry
            a.lea(rsp, __[r14 - 8]);
            a.mov(__dword_ptr[rsp + 4], ecx);
            a.mov(__dword_ptr[rsp], 0);
            a.lea(rax, __[L(cont)]);
            a.mov(Mem(Ctx + 8), rax);
            a.jmp(L("stub0"));
        }

        Mark("add"); a.mov(eax, __dword_ptr[rsp + 4]); a.add(eax, __dword_ptr[rsp + 8]); a.jmp(L("exit"));
        Mark("add_std"); a.mov(eax, __dword_ptr[rsp + 4]); a.add(eax, __dword_ptr[rsp + 8]); a.jmp(L("exit"));
        Mark("getret"); a.mov(eax, __dword_ptr[rsp]); a.jmp(L("exit"));
        Mark("writer"); a.mov(ecx, __dword_ptr[rsp + 4]); a.mov(eax, __dword_ptr[rsp + 8]); a.mov(__dword_ptr[rcx], eax); a.jmp(L("exit"));
        Mark("overrun"); a.mov(__dword_ptr[rsp + 8], 0); a.xor(eax, eax); a.jmp(L("exit"));
        Mark("mystery"); a.xor(eax, eax); a.jmp(L("exit"));
        Mark("fast"); a.xor(eax, eax); a.jmp(L("exit"));

        if (withImport)
        {
            Mark("callimp");
            a.mov(r14, rsp); a.mov(ecx, __dword_ptr[r14 + 4]);
            CallImport("callimp_c1");
            Mark("callimp_c1"); a.jmp(L("exit"));

            Mark("callimp_twice");
            a.mov(r14, rsp); a.mov(ecx, __dword_ptr[r14 + 4]);
            CallImport("twice_c1");
            Mark("twice_c1"); a.mov(r15d, eax); a.mov(ecx, __dword_ptr[r14 + 4]);
            CallImport("twice_c2");
            Mark("twice_c2"); a.add(eax, r15d); a.jmp(L("exit"));

            // DllMain(hinst, reason, 0): reports the reason to import 0, returns TRUE/FALSE.
            Mark("dllmain");
            a.mov(r14, rsp); a.mov(ecx, __dword_ptr[r14 + 8]);
            CallImport("dllmain_c1");
            Mark("dllmain_c1"); a.mov(eax, dllMainOk ? 1 : 0); a.jmp(L("exit"));

            Mark("stub0");
            a.mov(r13, Mem(Ctx + 8)); a.mov(r12d, esp); a.mov(edi, esp);
            a.mov(rsp, Mem(Ctx));
            a.mov(rax, Mem(Thunks)); a.call(rax);
            a.mov(rcx, rax); a.shr(rcx, 32); a.mov(eax, eax);
            a.mov(esp, r12d); a.lea(rsp, __[rsp + rcx + 4]);
            a.jmp(r13);
        }
        else
        {
            Mark("dllmain"); a.mov(eax, dllMainOk ? 1 : 0); a.jmp(L("exit"));
        }

        a.db(new byte[pad]);
        Mark("ctx"); a.db(new byte[8]);
        Mark("ctxcont"); a.db(new byte[0x38]);
        Mark("thunk0"); a.db(new byte[0x2000 - 0x1040]);

        var stream = new MemoryStream();
        var result = a.Assemble(new StreamCodeWriter(stream), 0, BlockEncoderOptions.ReturnNewInstructionOffsets);
        var offsets = labels.ToDictionary(p => p.Key, p => (uint)result.GetLabelRIP(p.Value));
        return (stream.ToArray(), offsets);
    }
}
