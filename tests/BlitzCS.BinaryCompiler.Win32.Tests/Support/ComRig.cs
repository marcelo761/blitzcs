using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// A hand-assembled IA-32 guest that calls function pointers and COM vtable slots with exact stdcall frames, translated with a dynamic stub pool.
/// Exports (all cdecl wrappers, so a wrong stdcall pop count of the callee corrupts the wrapper's own return and fails loudly):
/// callvt0..7(obj, slot, a1.. ) = obj->vtbl[slot](obj, a1..); callptr0..4(fn, a1..) = fn(a1..) (stdcall); loadlib(name) / getproc(hmod, name) = the imported KERNEL32 functions; hammer(obj, n) = n x { AddRef; Release }.
/// </summary>
public sealed class ComRig : IDisposable
{
    public static readonly uint Base = Win32PeBuilder.ImageBase;
    public static uint Data(uint offset) => Base + Win32PeBuilder.DataRva + offset;

    public readonly GuestModule Module;
    public readonly Win32ImportResolver Resolver;

    public ComRig(int dynamicStubs = 64, Win32Options? options = null)
    {
        Resolver = new Win32ImportResolver(options);
        var pe = new Win32PeBuilder();
        var loadLibrary = pe.Import("KERNEL32.dll", "LoadLibraryA");
        var getProc = pe.Import("KERNEL32.dll", "GetProcAddress");
        var code = new List<byte>();
        var labels = new Dictionary<string, uint>();
        void B(params byte[] b) => code.AddRange(b);
        void Label(string n) => labels[n] = (uint)code.Count;
        for (var n = 0; n <= 7; n++)
        {
            Label("callvt" + n);
            B(0x8B, 0x44, 0x24, 0x04, 0x8B, 0x54, 0x24, 0x08, 0x8B, 0x08);             // mov eax,[esp+4]; mov edx,[esp+8]; mov ecx,[eax]
            for (var a = 0; a < n; a++) B(0xFF, 0x74, 0x24, (byte)(4 * (n + 2)));       // push dword [esp+4*(n+2)]: a(n) .. a1
            B(0x50, 0xFF, 0x14, 0x91, 0xC3);                                             // push eax; call [ecx+edx*4]; ret
        }
        for (var n = 0; n <= 4; n++)
        {
            Label("callptr" + n);
            B(0x8B, 0x44, 0x24, 0x04);                                                                      // mov eax,[esp+4]  (the function pointer)
            for (var a = 0; a < n; a++) B(0xFF, 0x74, 0x24, (byte)(4 * (n + 1)));                           // push dword [esp+4*(n+1)]: a(n) .. a1
            B(0xFF, 0xD0, 0xC3);                                                                            // call eax; ret
        }
        Label("loadlib");
        B(0xFF, 0x74, 0x24, 0x04, 0xFF, 0x15); code.AddRange(BitConverter.GetBytes(Base + loadLibrary)); B(0xC3);   // push name; call [LoadLibraryA]; ret
        Label("getproc");
        B(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08, 0xFF, 0x15); code.AddRange(BitConverter.GetBytes(Base + getProc)); B(0xC3); // push name; push hmod; call [GetProcAddress]; ret
        Label("hammer");
        B(0x53, 0x56, 0x8B, 0x74, 0x24, 0x0C, 0x8B, 0x5C, 0x24, 0x10);
        B(0x8B, 0x0E, 0x56, 0xFF, 0x51, 0x04, 0x8B, 0x0E, 0x56, 0xFF, 0x51, 0x08, 0x4B, 0x75, 0xF1);
        B(0x5E, 0x5B, 0xC3);

        pe.WithCode([.. code]).WithData(new byte[0x1000]);
        foreach (var (n, o) in labels) pe.Export(n, o);
        var image = PeReader.Read(pe.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release) { DynamicStubs = dynamicStubs }, "com-rig");
        Assert.DoesNotContain(artifact.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Module = GuestModule.Load(artifact, image, Resolver, new GuestModuleOptions { RunEntryPoint = false });
    }

    public uint F(string name, params uint[] args) => Module.Bind(name, GuestCallingConvention.Cdecl, args.Length).Call(args);
    public uint Vt(uint obj, int slot, params uint[] args) => F("callvt" + args.Length, [obj, (uint)slot, .. args]);
    public uint QueryInterface(uint obj, Guid iid, out uint result)
    {
        Module.Memory.Write(Data(0x100), iid.ToByteArray());
        Module.Memory.WriteUInt32(Data(0x120), 0xDEADBEEF);
        var hr = Vt(obj, 0, Data(0x100), Data(0x120));
        result = Module.Memory.ReadUInt32(Data(0x120));
        return hr;
    }
    /// <summary>Writes a NUL-terminated string into the data area and returns its guest address.</summary>
    public uint Str(uint dataOffset, string text) { Module.Memory.Write(Data(dataOffset), [.. System.Text.Encoding.ASCII.GetBytes(text), (byte)0]); return Data(dataOffset); }
    public uint GetProc(string dll, string name)
    {
        var h = F("loadlib", Str(0x400, dll));
        return h == 0 ? 0 : F("getproc", h, Str(0x440, name));
    }
    public uint AddRef(uint obj) => Vt(obj, 1);
    public uint Release(uint obj) => Vt(obj, 2);

    public void Dispose() { Module.Dispose(); Resolver.Dispose(); }
}
