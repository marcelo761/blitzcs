using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class CoverageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string? RealDir = Environment.GetEnvironmentVariable("BLITZCS_REAL_PE32_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EveryApiHasAConsistentDefinition()
    {
        var apis = Win32ImportResolver.Coverage.Implemented;
        Assert.True(apis.Count > 100);
        Assert.Equal(apis.Count, apis.Select(a => a.Dll.ToLowerInvariant() + "!" + a.Name).Distinct().Count());
        foreach (var a in apis)
        {
            Assert.InRange(a.ArgCount, 0, 8);
            Assert.False(string.IsNullOrEmpty(a.Service));
            if (a.Dll.Equals("MSVCRT.dll", StringComparison.OrdinalIgnoreCase)) Assert.Equal(Win32Convention.Cdecl, a.Convention);
            else Assert.Equal(Win32Convention.Stdcall, a.Convention);
        }
        Assert.Equal(["Kernel32.File", "Kernel32.Heap", "Kernel32.Memory", "Kernel32.Process", "Kernel32.String", "Kernel32.Sync", "Kernel32.Tls", "Msvcrt", "Winsock"],
            apis.Select(a => a.Service).Distinct().Order().ToArray());
    }

    [Fact]
    public void CoverageSeparatesImplementedFromMissingPriorityEntries()
    {
        var cov = Win32ImportResolver.Coverage;
        Assert.Contains(cov.Priority, p => p is { Dll: "kernel32.dll", Name: "HeapAlloc", Implemented: true });
        var missing = cov.Missing.Select(m => m.Dll + "!" + m.Name).ToHashSet();
        Assert.Contains("kernel32.dll!RtlUnwind", missing);
        Assert.Contains("kernel32.dll!RaiseException", missing);
        Assert.Contains("ole32.dll!CoCreateInstance", missing);
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
        Assert.Null(r.Resolve("kernel32.dll", "RtlUnwind", null));
        Assert.Null(r.Resolve("user32.dll", "GetDesktopWindow", null));
        Assert.Null(r.Resolve("msvcrt.dll", "_ftol", null));
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
        Assert.Throws<KeyNotFoundException>(() => w.Kernel("RtlUnwind"));
        Assert.Throws<ArgumentException>(() => w.Kernel("HeapAlloc", 1, 2));
        using var other = new World();
        Assert.NotEqual(0u, w.Kernel("GetProcessHeap"));
        Assert.Throws<Win32UsageException>(() => w.R.Call(other.Mem, World.K, "GetProcessHeap")); // one resolver serves one memory
    }

    private static PeImage Load(string file) => PeReader.Read(File.ReadAllBytes(Path.Combine(RealDir!, file)));

    [Fact]
    public void RealLibrariesBlockersAreExactlyTheKnownGaps()
    {
        if (RealDir is null || !new[] { "ogg.dll", "vorbis.dll", "vorbisFile.dll", "eax.dll" }.All(f => File.Exists(Path.Combine(RealDir, f)))) return; // BLITZCS_REAL_PE32_DIR not set (or incomplete): nothing to analyse
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
        Assert.Contains("USER32.dll!GetDesktopWindow", eax);
        Assert.Contains("ole32.dll!CoCreateInstance", eax);
        Assert.All(eax, m => Assert.DoesNotContain("Heap", m));
        // inter-module imports are reported as missing here and must come from the caller's own resolver
        Assert.Equal(11, r.Analyze(Load("vorbis.dll")).Count(v => v.Dll == "ogg.dll"));
    }
}
