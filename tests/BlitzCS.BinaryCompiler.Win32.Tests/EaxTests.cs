using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;
using BlitzCS.Testing;
using Xunit;
using Xunit.Abstractions;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>
/// The real eax.dll (COM-style wrapper) under the Win32 component: its DllRegisterServer/DllUnregisterServer registry traffic is checked against COM conventions
/// (independent expectations: CLSID text format, InprocServer32 default value = the module path answered by GetModuleFileNameA). Set BLITZCS_REAL_PE32_DIR; nothing is copied into the repository.
/// </summary>
[Collection("GuestRuntime")]
public sealed class EaxTests(ITestOutputHelper output)
{
    private const string Path = "C:\\WINDOWS\\system32\\eax.dll";

    private static (PeImage, TranslationArtifact) Load()
    {
        var file = RealLibraries.PathOf("eax.dll");   // the callers are [RealLibraryFact]s: skipped at discovery when the file is absent
        var original = PeReader.Read(File.ReadAllBytes(file));
        var (image, loadBase) = HostBase.Fit(original);   // Windows: rebased into the host's image window
        return (image, X64Translator.Translate(original, new BinaryCompilerOptions(OptimizationProfile.Release, LoadBase: loadBase), "real-eax"));
    }

    private static GuestModule Open((PeImage Image, TranslationArtifact Artifact) lib, Win32ImportResolver r)
    {
        var m = GuestModule.Load(lib.Artifact, lib.Image, r, new GuestModuleOptions { RunEntryPoint = true, AllowUnboundImports = true });
        r.Context.RegisterModule("eax.dll", m.ImageBase, Path);
        return m;
    }

    [RealLibraryFact("eax.dll")]
    public void RegisterAndUnregisterServerLeaveWellFormedKeysAndThenAnEmptyRegistry()
    {
        var lib = Load();
        using var resolver = new Win32ImportResolver();
        using var module = Open(lib, resolver);
        var reg = module.Bind("DllRegisterServer", GuestCallingConvention.Stdcall, 0);
        var unreg = module.Bind("DllUnregisterServer", GuestCallingConvention.Stdcall, 0);

        var hr = reg.Call();
        var entries = Advapi32RegistryService.Snapshot(resolver.Context);
        foreach (var e in entries) output.WriteLine($"{e.Path} | {e.ValueName ?? "<key>"} | {e.Type} | {System.Text.Encoding.Latin1.GetString(e.Data).Replace('\0', '|')}");
        Assert.Equal(0u, hr); // S_OK
        Assert.NotEmpty(entries);

        var guid = new System.Text.RegularExpressions.Regex(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$");
        var inproc = entries.Where(e => e.Path.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase) && e.ValueName == "").ToList();
        Assert.NotEmpty(inproc);
        foreach (var e in inproc)
        {
            var parts = e.Path.Split('\\');
            Assert.Equal("HKEY_CLASSES_ROOT", parts[0]);
            Assert.Equal("CLSID", parts[1], ignoreCase: true);
            Assert.Matches(guid, parts[2]);
            Assert.Equal(1u, e.Type); // REG_SZ
            Assert.Equal("C:\\WINDOWS\\system32\\eax.dll", System.Text.Encoding.Latin1.GetString(e.Data).TrimEnd('\0'), ignoreCase: true);
        }

        Assert.Equal(2, inproc.Count); // EAX Unified and EAX Unified 8
        Assert.All(inproc, e => Assert.Contains(entries, x => x.Path == e.Path && x.ValueName == "ThreadingModel" && System.Text.Encoding.Latin1.GetString(x.Data).TrimEnd('\0') == "Both"));
        Assert.Contains(entries, x => x.Path.EndsWith("4FF53B81-1CE0-11D3-AAB8-00A0C95949D5}") && x.ValueName == "" && System.Text.Encoding.Latin1.GetString(x.Data) == "EAX Unified Interface\0");

        Assert.Equal(0u, unreg.Call());
        // like any ATL server it removes its own CLSID keys but leaves the shared HKCR\CLSID parent (present on every real machine)
        var left = Advapi32RegistryService.Snapshot(resolver.Context);
        Assert.Equal(["HKEY_CLASSES_ROOT\\CLSID"], left.Select(e => e.Path).ToArray());
        Assert.All(left, e => Assert.Null(e.ValueName));
    }

    [RealLibraryFact("eax.dll")]
    public void DllCanUnloadNowAndGetClassObjectBehaveLikeComServers()
    {
        var lib = Load();
        using var resolver = new Win32ImportResolver();
        using var module = Open(lib, resolver);
        var canUnload = module.Bind("DllCanUnloadNow", GuestCallingConvention.Stdcall, 0);
        var hr = canUnload.Call();
        output.WriteLine($"DllCanUnloadNow = 0x{hr:X8}");
        Assert.True(hr is 0 or 1); // S_OK / S_FALSE
        // an unknown CLSID must be refused with CLASS_E_CLASSNOTAVAILABLE and a NULL out pointer
        var get = module.Bind("DllGetClassObject", GuestCallingConvention.Stdcall, 3);
        using var clsid = module.Memory.Allocate(16); using var iid = module.Memory.Allocate(16); using var ppv = module.Memory.Allocate(4);
        clsid.Write(0, Guid.Parse("DEADBEEF-0000-0000-0000-000000000001").ToByteArray());
        iid.Write(0, new byte[16]); ppv.Write(0, BitConverter.GetBytes(0x1234u));
        var r = get.Call(clsid.Address, iid.Address, ppv.Address);
        output.WriteLine($"DllGetClassObject(unknown) = 0x{r:X8}");
        Assert.Equal(0x80040111u, r);
    }
}
