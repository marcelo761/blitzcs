using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.Linking.E2E.Tests;

[Collection("LinkingE2E")]
public sealed class Win32HeapTests : E2EBase
{
    private byte[] HeapLibrary(string? missingImport = null)
    {
        var b = new PeFixtureBuilder();
        var gph = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "GetProcessHeap");
        var alloc = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "HeapAlloc");
        var free = PeFixtureBuilder.ImageBase + b.Import("kernel32.dll", "HeapFree");
        var gone = missingImport is null ? 0 : PeFixtureBuilder.ImageBase + b.Import("nosuch.dll", missingImport);
        // cdecl int heap_add(int x): p = HeapAlloc(GetProcessHeap(), 0, 16); *p = x + 1000; v = *p; HeapFree(heap, 0, p); return v;
        var code = TranslatorHarness.Asm(a =>
        {
            a.push(ebx); a.push(esi); a.push(edi);
            a.call(__dword_ptr[(int)gph]); a.mov(ebx, eax);
            a.push(16); a.push(0); a.push(ebx); a.call(__dword_ptr[(int)alloc]); a.mov(esi, eax);
            a.mov(ecx, __dword_ptr[esp + 16]); a.add(ecx, 1000); a.mov(__dword_ptr[esi], ecx); a.mov(edi, __dword_ptr[esi]);
            a.push(esi); a.push(0); a.push(ebx); a.call(__dword_ptr[(int)free]);
            a.mov(eax, edi); a.pop(edi); a.pop(esi); a.pop(ebx); a.ret();
        });
        var pad = Enumerable.Repeat((byte)0x90, 0x80 - code.Length);
        var fault = TranslatorHarness.Asm(a => { if (gone != 0) a.call(__dword_ptr[(int)gone]); a.ret(); });
        return b.WithCode(code.Concat(pad).Concat(fault).ToArray()).Export("heap_add", 0).Export("calls_missing", 0x80).Build();
    }

    [Fact]
    public void BasicProgramCallsALibraryThatUsesWin32HeapApis()
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), HeapLibrary());
        Build("For i = 1 To 3\nPrint HeapAdd(i * 10)\nNext\n",
            "{\"functions\":[" + Entry("HeapAdd", ["Integer"], "Integer", "lib.dll", "heap_add", "cdecl") + "]}", out var exit, out var err);
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Equal("1010\n1020\n1030\n", Run());
    }

    [Fact]
    public void Win32CanBeDisabledAndThenTheImportIsReportedBeforeAndAtRunTime()
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), HeapLibrary());
        var catalog = "{\"binaryLibraries\":[{\"path\":\"lib.dll\",\"win32\":false}],\"functions\":[" + Entry("HeapAdd", ["Integer"], "Integer", "lib.dll", "heap_add", "cdecl") + "]}";
        Build("Print HeapAdd(1)\n", catalog, out var exit, out var err);
        Assert.Equal(0, exit);
        Assert.Contains("kernel32.dll!GetProcessHeap", err);
        Assert.Contains("warning", err);
        Assert.Contains("kernel32.dll!GetProcessHeap", Run());
        catalog = catalog.Replace("\"win32\":false", "\"win32\":false,\"unboundImports\":\"error\"");
        Build("Print HeapAdd(1)\n", catalog, out exit, out err);
        Assert.Equal(1, exit);
        Assert.Contains("unboundImports", err);
    }

    [Fact]
    public void AMissingImportWarnsAtBuildTimeAndFaultsNamingItAtCallTime()
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), HeapLibrary("NeverThere"));
        Build("Print HeapAdd(5)\nPrint CallsMissing()\n",
            "{\"functions\":[" + Entry("HeapAdd", ["Integer"], "Integer", "lib.dll", "heap_add", "cdecl") + "," + Entry("CallsMissing", [], "Integer", "lib.dll", "calls_missing", "cdecl") + "]}", out var exit, out var err);
        Assert.Equal(0, exit);
        Assert.Contains("nosuch.dll!NeverThere", err);
        var output = Run();
        Assert.StartsWith("1005\n", output);
        Assert.Contains("nosuch.dll!NeverThere", output);
    }

    [Fact]
    public void ADuplicateLibraryEntryWithOptionsIsRejected()
    {
        File.WriteAllBytes(Path.Combine(Root, "lib.dll"), HeapLibrary());
        Build("Print 1\n", "{\"binaryLibraries\":[\"lib.dll\",{\"path\":\"lib.dll\",\"win32\":true}],\"functions\":[]}", out var exit, out var err);
        Assert.Equal(1, exit);
        Assert.Contains("more than once", err);
        Build("Print 1\n", "{\"binaryLibraries\":[{\"path\":\"lib.dll\",\"bogus\":1}],\"functions\":[]}", out exit, out err);
        Assert.Equal(1, exit);
        Assert.Contains("unknown key 'bogus'", err);
    }
}
