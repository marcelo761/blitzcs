using System.Diagnostics;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>Builds tests/.../Native/w32.c with tools/x86_oracle/build.sh (real IA-32 machine code), skipping when the 32-bit toolchain is missing.</summary>
internal static class NativeLibrary
{
    private static readonly object Gate = new();
    private static (byte[] Code, byte[] Data, (string Name, uint Offset)[] Exports, uint[] Relocs)? built;
    private static string? unavailable;
    private static bool tried;

    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "x86_oracle", "build.sh"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static string? UnavailableReason { get { Ensure(); return unavailable; } }
    public static (byte[] Code, byte[] Data, (string Name, uint Offset)[] Exports, uint[] Relocs) Library { get { Ensure(); return built ?? throw new InvalidOperationException(unavailable); } }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (tried) return;
            tried = true;
            try
            {
                if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) { unavailable = "needs Linux x64"; return; }
                var build = Path.Combine(RepoRoot, "tools", "x86_oracle", "build.sh");
                var source = Path.Combine(RepoRoot, "tests", "BlitzCS.BinaryCompiler.Win32.Tests", "Native", "w32.c");
                if (!File.Exists(build) || !File.Exists(source)) { unavailable = "build.sh or w32.c not found"; return; }
                var outDir = Path.Combine(Path.GetTempPath(), "blitzcs-win32-native-" + Environment.UserName + "-" + Environment.ProcessId);
                Directory.CreateDirectory(outDir);
                var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(build); psi.ArgumentList.Add(source); psi.ArgumentList.Add("-"); psi.ArgumentList.Add(outDir);
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEndAsync();
                var o = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(120_000)) { p.Kill(true); unavailable = "build.sh timed out"; return; }
                if (p.ExitCode != 0) { unavailable = "32-bit toolchain unavailable: " + (err.Result + o.Result).Trim(); return; }
                var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "w32.exports.json"))).RootElement;
                var exports = json.GetProperty("exports").EnumerateArray().Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("offset").GetUInt32())).ToArray();
                var relocs = json.GetProperty("relocs").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
                built = (File.ReadAllBytes(Path.Combine(outDir, "w32.code.bin")), File.ReadAllBytes(Path.Combine(outDir, "w32.data.bin")), exports, relocs);
                Directory.Delete(outDir, true);
            }
            catch (Exception e) { unavailable = "native build failed: " + e.Message; }
        }
    }
}

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute() { Skip = NativeLibrary.UnavailableReason is { } r ? "native x86 library unavailable: " + r : null; }
}

/// <summary>
/// A real IA-32 library (compiled C, no Win32 headers) calls HeapAlloc/HeapReAlloc/HeapFree/EnterCriticalSection/TlsAlloc/MultiByteToWideChar and the
/// cdecl MSVCRT functions through its IAT, translated and executed by the compiler runtime with <see cref="Win32ImportResolver"/> as the import
/// resolver. Expected values are computed in C#: Win32 semantics cannot be observed on the Linux ELF oracle (it has no Windows), so this test compares
/// against the documented behaviour, not against native execution.
/// </summary>
[Collection("GuestRuntime")]
public sealed class Win32IntegrationTests
{
    internal static readonly (string Dll, string Name)[] Imports =
    [
        ("KERNEL32.dll", "GetProcessHeap"), ("KERNEL32.dll", "HeapAlloc"), ("KERNEL32.dll", "HeapReAlloc"), ("KERNEL32.dll", "HeapSize"), ("KERNEL32.dll", "HeapFree"),
        ("KERNEL32.dll", "InitializeCriticalSection"), ("KERNEL32.dll", "EnterCriticalSection"), ("KERNEL32.dll", "LeaveCriticalSection"), ("KERNEL32.dll", "GetLastError"),
        ("KERNEL32.dll", "TlsAlloc"), ("KERNEL32.dll", "TlsSetValue"), ("KERNEL32.dll", "TlsGetValue"), ("KERNEL32.dll", "WideCharToMultiByte"), ("KERNEL32.dll", "MultiByteToWideChar"),
        ("KERNEL32.dll", "InterlockedIncrement"),
        ("MSVCRT.dll", "memcpy"), ("MSVCRT.dll", "strlen"), ("MSVCRT.dll", "malloc"), ("MSVCRT.dll", "free"),
    ];

    private static (PeImage Image, TranslationArtifact Artifact) Build(OptimizationProfile profile)
    {
        var lib = NativeLibrary.Library;
        var fx = new Win32PeBuilder().WithCode(lib.Code).WithData(lib.Data.Length >= 16 ? lib.Data : new byte[0x100]);
        foreach (var (name, off) in lib.Exports) fx.Export(name, off);
        foreach (var (dll, name) in Imports) fx.Import(dll, name);
        foreach (var r in lib.Relocs) fx.Relocate(r);
        var image = PeReader.Read(fx.Build());
        var art = X64Translator.Translate(image, new BinaryCompilerOptions(profile), "win32-" + profile);
        var errors = art.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Message)));
        return (image, art);
    }

    public static IEnumerable<object[]> Profiles => [[OptimizationProfile.Debug], [OptimizationProfile.Release]];

    private static uint Out(GuestModule m, int index) => m.Bind("t_out", GuestCallingConvention.Cdecl, 1).Call((uint)index);

    [NativeFact]
    public void ImportsOfTheLibraryAreAllImplementedAndAnalysisSaysSo()
    {
        var (image, _) = Build(OptimizationProfile.Release);
        using var r = new Win32ImportResolver();
        Assert.All(r.Analyze(image), v => Assert.Equal(Win32ImportStatus.Implemented, v.Status));
        Assert.Equal(Imports.Length, r.Analyze(image).Count);
    }

    [NativeFact]
    public void HeapCrtSyncTlsAndNlsWorkThroughTheIat()
    {
        foreach (var profile in new[] { OptimizationProfile.Debug, OptimizationProfile.Release })
        {
            var (image, art) = Build(profile);
            using var resolver = new Win32ImportResolver();
            using var module = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
            var label = profile.ToString();

            // heap: documented HeapAlloc/HeapReAlloc/HeapSize/HeapFree behaviour; second HeapFree fails with ERROR_INVALID_PARAMETER
            const int n = 300;
            var sum = module.Bind("t_heap", GuestCallingConvention.Cdecl, 1).Call(n);
            Assert.Equal((uint)Enumerable.Range(0, n).Sum(i => (byte)(i * 7 + 1)), sum);
            Assert.Equal(0u, Out(module, 0));                   // HEAP_ZERO_MEMORY
            Assert.Equal((uint)(n * 3), Out(module, 1));        // HeapSize = requested size after growth
            Assert.Equal(1u, Out(module, 2));                   // first HeapFree
            Assert.Equal(0u, Out(module, 3));                   // second HeapFree fails
            Assert.Equal(Win32Error.InvalidParameter, Out(module, 4));
            Assert.Equal(1u, Out(module, 5));

            // cdecl C runtime
            var text = "hello, translated world"u8.ToArray();
            using var src = module.Memory.Allocate(64);
            src.Write(0, text);
            Assert.Equal((uint)(text.Length * 3 + text.Length), module.Bind("t_crt", GuestCallingConvention.Cdecl, 2).Call(src.Address, (uint)text.Length));
            Assert.Equal((uint)text.Length, Out(module, 6));

            // critical section: recursive, balanced, LockCount mirrored back to -1
            Assert.Equal(7u + 25, module.Bind("t_cs", GuestCallingConvention.Cdecl, 1).Call(25));
            Assert.Equal(0xFFFFFFFFu, Out(module, 7));

            // TLS: new slot reads 0, then the stored value
            var idx = module.Bind("t_tls", GuestCallingConvention.Cdecl, 1).Call(0xBEEF);
            Assert.True(idx < 64, label);
            Assert.Equal(0u, Out(module, 8));
            Assert.Equal(0xBEEFu, Out(module, 9));

            // NLS: "caf\xE9 \x80" in CP1252 -> UTF-16 (7 units incl. NUL) -> UTF-8
            using var cp1252 = module.Memory.Allocate(32); cp1252.Write(0, [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x80, 0]);
            using var wide = module.Memory.Allocate(256); using var utf8 = module.Memory.Allocate(256);
            var bytes = module.Bind("t_nls", GuestCallingConvention.Cdecl, 3).Call(cp1252.Address, wide.Address, utf8.Address);
            var expected = System.Text.Encoding.UTF8.GetBytes("café €\0");
            Assert.Equal((uint)expected.Length, bytes);
            Assert.Equal(7u, Out(module, 10));
            Assert.Equal(expected, utf8.ToArray().AsSpan(0, expected.Length).ToArray());

            Assert.Equal(7u + 25 + 100, module.Bind("t_interlocked", GuestCallingConvention.Cdecl, 1).Call(100));
        }
    }

    [NativeFact]
    public void ErrorsInsideTheEmulatedServiceSurfaceAsImportFaultsNamingTheCause()
    {
        var (image, art) = Build(OptimizationProfile.Release);
        using var resolver = new Win32ImportResolver();
        using var module = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        // t_crt with a source pointer into unmapped memory: memcpy raises a guest-memory fault, not a host crash
        var fault = Assert.Throws<GuestImportException>(() => module.Bind("t_crt", GuestCallingConvention.Cdecl, 2).Call(0x10, 8));
        Assert.Contains("GuestMemoryException", fault.Message);
        // the module remains usable after the fault
        using var src = module.Memory.Allocate(16); src.Write(0, "abc"u8.ToArray());
        Assert.Equal(3u * 3 + 3, module.Bind("t_crt", GuestCallingConvention.Cdecl, 2).Call(src.Address, 3));
    }

    [Fact]
    public async Task ConditionVariableWaitTemporarilyAdmitsStableHostReads()
    {
        var b = new Win32PeBuilder().WithData(new byte[0x100]);
        var acquire = b.Import("KERNEL32.dll", "AcquireSRWLockExclusive");
        var sleep = b.Import("KERNEL32.dll", "SleepConditionVariableSRW");
        var release = b.Import("KERNEL32.dll", "ReleaseSRWLockExclusive");
        var wake = b.Import("KERNEL32.dll", "WakeConditionVariable");
        var baseVa = Win32PeBuilder.ImageBase;
        var lockVa = baseVa + Win32PeBuilder.DataRva + 0x20;
        var conditionVa = baseVa + Win32PeBuilder.DataRva + 0x24;
        var resultVa = baseVa + Win32PeBuilder.DataRva + 0x28;
        var code = new List<byte>();
        void Abs(uint value)
        {
            b.Relocate(Win32PeBuilder.TextRva + (uint)code.Count);
            code.AddRange(BitConverter.GetBytes(value));
        }
        void Push(uint value) { code.Add(0x68); Abs(value); }
        void Call(uint iat) { code.AddRange([0xFF, 0x15]); Abs(baseVa + iat); }

        var waitOffset = (uint)code.Count;
        Push(lockVa); Call(acquire);
        Push(0); Push(5_000); Push(lockVa); Push(conditionVa); Call(sleep);
        code.Add(0xA3); Abs(resultVa);
        Push(lockVa); Call(release);
        code.Add(0xA1); Abs(resultVa); code.Add(0xC3);
        var wakeOffset = (uint)code.Count;
        Push(conditionVa); Call(wake); code.Add(0xC3);

        b.WithCode([.. code]).Export("wait", waitOffset).Export("wake", wakeOffset);
        var image = PeReader.Read(b.Build());
        var artifact = X64Translator.Translate(image, new BinaryCompilerOptions(OptimizationProfile.Release), "condition-wait-stable-read");
        using var resolver = new Win32ImportResolver();
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(VerifiedBankReader.RuntimeSha256);
        using var module = GuestModule.Load(artifact, image, resolver, new GuestModuleOptions { RunEntryPoint = false }, environment, "condition-wait-stable-read");
        var gate = Assert.IsType<StableMemoryReadGate>(environment.StableReads);

        var before = gate.Epoch;
        var waiting = Task.Run(() => module.Bind("wait", GuestCallingConvention.Cdecl, 0).Call());
        StableMemoryReadGate.ReadLease reader = default;
        Assert.True(SpinWait.SpinUntil(() => gate.Epoch >= before + 2 && gate.TryEnterRead(out reader), TimeSpan.FromSeconds(5)));
        using (reader) Thread.Sleep(50); // The guest still owns the SRW lock, so wake waits until Monitor.Wait releases it.

        Assert.False(waiting.IsCompleted);
        Assert.Equal(0u, await Task.Run(() => module.Bind("wake", GuestCallingConvention.Cdecl, 0).Call()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1u, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [NativeFact]
    public void MissingImportsFailLoadByDefaultAndNameTheApi()
    {
        var (image, art) = Build(OptimizationProfile.Release);
        using var partial = new Win32ImportResolver(services: [new HeapService()]);
        var load = Assert.Throws<GuestImportException>(() => GuestModule.Load(art, image, partial, new GuestModuleOptions { RunEntryPoint = false }));
        Assert.Contains("KERNEL32.dll!EnterCriticalSection", load.Message);
        using var module = GuestModule.Load(art, image, partial, new GuestModuleOptions { RunEntryPoint = false, AllowUnboundImports = true });
        var fault = Assert.Throws<GuestImportException>(() => module.Bind("t_cs", GuestCallingConvention.Cdecl, 1).Call(1));
        Assert.Contains("InitializeCriticalSection", fault.Message);
    }

    [NativeFact]
    public void OneResolverCannotServeTwoModules()
    {
        var (image, art) = Build(OptimizationProfile.Release);
        using var resolver = new Win32ImportResolver();
        using var first = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        first.Bind("t_heap", GuestCallingConvention.Cdecl, 1).Call(4);
        first.Dispose();
        using var second = GuestModule.Load(art, image, resolver, new GuestModuleOptions { RunEntryPoint = false });
        var fault = Assert.Throws<GuestImportException>(() => second.Bind("t_heap", GuestCallingConvention.Cdecl, 1).Call(4));
        Assert.Contains("another guest module", fault.Message);
    }
}
