using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class CoverageTests(Xunit.Abstractions.ITestOutputHelper output)
{

    [Fact]
    public void EveryApiHasAConsistentDefinition()
    {
        var apis = Win32ImportResolver.Coverage.Implemented;
        Assert.True(apis.Count > 100);
        Assert.Equal(apis.Count, apis.Select(a => a.Dll.ToLowerInvariant() + "!" + a.Name).Distinct().Count());
        foreach (var a in apis)
        {
            Assert.InRange(a.ArgCount, 0, 9);
            Assert.False(string.IsNullOrEmpty(a.Service));
            if (a.Dll.Equals("MSVCRT.dll", StringComparison.OrdinalIgnoreCase)) Assert.Equal(Win32Convention.Cdecl, a.Convention);
            else Assert.Equal(Win32Convention.Stdcall, a.Convention);
        }
        var services = apis.Select(a => a.Service).Distinct().ToHashSet();
        foreach (var expected in new[] { "Kernel32.File", "Kernel32.Heap", "Kernel32.Memory", "Kernel32.Process", "Kernel32.String", "Kernel32.Sync", "Kernel32.Tls", "Msvcrt", "Winsock", "Advapi32.Registry", "Ole32.Com", "User32.Basic" })
            Assert.Contains(expected, services);
    }

    [Fact]
    public void CoverageSeparatesImplementedFromMissingPriorityEntries()
    {
        var cov = Win32ImportResolver.Coverage;
        Assert.Contains(cov.Priority, p => p is { Dll: "kernel32.dll", Name: "HeapAlloc", Implemented: true });
        var missing = cov.Missing.Select(m => m.Dll + "!" + m.Name).ToHashSet();
        Assert.Contains("user32.dll!ShowWindow", missing);
        Assert.Contains(cov.Priority, p => p is { Dll: "kernel32.dll", Name: "RtlUnwind", Implemented: true, Service: "Kernel32.Process" });       // guest SEH (docs/binary-seh.md)
        Assert.Contains(cov.Priority, p => p is { Dll: "kernel32.dll", Name: "RaiseException", Implemented: true, Service: "Kernel32.Process" });
        Assert.DoesNotContain("ole32.dll!CoCreateInstance", missing);
        Assert.DoesNotContain("wsock32.dll!#11", missing);
        Assert.DoesNotContain(missing, m => m.StartsWith("kernel32.dll!Heap"));
    }

    [Fact]
    public void ResolverBindsOnlyWhatIsImplemented()
    {
        using var r = new Win32ImportResolver();
        Assert.NotNull(r.Resolve("kernel32.dll", "HeapAlloc", null));
        Assert.NotNull(r.Resolve("KERNEL32", "GetVersion", null)); // dll name rules: case-insensitive, extension optional
        Assert.Null(r.Resolve("kernel32.dll", "heapalloc", null)); // function names are case-sensitive
        Assert.Null(r.Resolve("kernel32.dll", "CreateRemoteThread", null));
        Assert.NotNull(r.Resolve("user32.dll", "GetDesktopWindow", null));
        Assert.Null(r.Resolve("user32.dll", "ShowWindow", null));
        Assert.Null(r.Resolve("msvcrt.dll", "_not_a_real_crt_function", null));
        Assert.Null(r.Resolve("kernel32.dll", null, 7));
    }

    [Fact]
    public void ServicesCanBeComposedIndependently()
    {
        using var r = new Win32ImportResolver(services: [new HeapService(), new TlsService()]);
        Assert.NotNull(r.Resolve("kernel32.dll", "TlsAlloc", null));
        Assert.Null(r.Resolve("kernel32.dll", "EnterCriticalSection", null));
        Assert.Null(r.Resolve("msvcrt.dll", "malloc", null));
        Assert.Throws<InvalidOperationException>(() => new Win32ImportResolver(services: [new HeapService(), new HeapService()]));
    }

    [Fact]
    public void DirectCallValidatesNameAndArgumentCount()
    {
        using var w = new World();
        Assert.Throws<KeyNotFoundException>(() => w.Kernel("CreateRemoteThread"));
        Assert.Throws<ArgumentException>(() => w.Kernel("HeapAlloc", 1, 2));
        using var other = new World();
        Assert.NotEqual(0u, w.Kernel("GetProcessHeap"));
        Assert.Throws<Win32UsageException>(() => w.R.Call(other.Mem, World.K, "GetProcessHeap")); // one resolver serves one memory
    }

    private static PeImage Load(string file) => PeReader.Read(File.ReadAllBytes(RealLibraries.PathOf(file)));

    [RealLibraryFact("ogg.dll", "vorbis.dll", "vorbisFile.dll", "eax.dll")]
    public void RealLibrariesBlockersAreExactlyTheKnownGaps()
    {
        using var r = new Win32ImportResolver();
        foreach (var f in new[] { "ogg.dll", "vorbis.dll", "vorbisFile.dll", "eax.dll" })
        {
            var verdicts = r.Analyze(Load(f));
            output.WriteLine($"{f}: {verdicts.Count(v => v.Status == Win32ImportStatus.Implemented)}/{verdicts.Count} imports implemented; missing: {string.Join(", ", verdicts.Where(v => v.Status == Win32ImportStatus.Missing).Select(v => v.Label))}");
        }
        string[] Missing(string file, bool win32Only = true) => r.Analyze(Load(file)).Where(v => v.Status == Win32ImportStatus.Missing && (!win32Only || !v.Dll.StartsWith("ogg", StringComparison.OrdinalIgnoreCase) && !v.Dll.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase))).Select(v => v.Label).Order().ToArray();
        Assert.Equal(["KERNEL32.dll!RtlUnwind"], Missing("ogg.dll"));
        Assert.Equal(["KERNEL32.dll!RaiseException", "KERNEL32.dll!RtlUnwind"], Missing("vorbis.dll"));
        Assert.Equal(["KERNEL32.dll!RaiseException", "KERNEL32.dll!RtlUnwind"], Missing("vorbisFile.dll"));
        var eax = Missing("eax.dll");
        Assert.Contains("KERNEL32.dll!RtlUnwind", eax);
        Assert.Contains("KERNEL32.dll!RaiseException", eax);
        Assert.Equal(["KERNEL32.dll!RaiseException", "KERNEL32.dll!RtlUnwind"], eax);
        Assert.All(eax, m => Assert.DoesNotContain("Heap", m));
        // inter-module imports are reported as missing here and must come from the caller's own resolver
        Assert.Equal(11, r.Analyze(Load("vorbis.dll")).Count(v => v.Dll == "ogg.dll"));
    }
}
