using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class WorkstreamCTests : IDisposable
{
    private static string SdkRuntimePath => Path.Combine(Environment.GetEnvironmentVariable(SdkRuntimeFactAttribute.Variable)
        ?? throw new InvalidOperationException(SdkRuntimeFactAttribute.Variable + " is not set"), "bin", "runtime.dll");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-ws-c-" + Guid.NewGuid().ToString("N"));

    public WorkstreamCTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    [Fact]
    public void UserLibResolver_ParsesUntaggedParametersAsIntegerNonPointer()
    {
        var declsPath = Path.Combine(directory, "test.decls");
        File.WriteAllText(declsPath, """
            .lib "user32.dll"
            SampleFunction%(a, b%, c$)
            """);

        var decls = UserLibResolver.ParseDeclsFile(declsPath, directory);
        Assert.Single(decls);
        var decl = decls[0];
        Assert.Equal("SampleFunction", decl.BasicName);
        Assert.Equal(ExternalValueType.Integer, decl.ReturnType);
        Assert.Equal([ExternalValueType.Integer, ExternalValueType.Integer, ExternalValueType.String], decl.ParameterTypes);
        Assert.Null(decl.PointerParameters);
        Assert.True(decl.IsWin32SystemDll);
        Assert.Equal("user32.dll", decl.Win32SystemDllName);
    }

    [Fact]
    public void UserLibResolver_ParsesPointerParametersAsIntegerPointer()
    {
        var declsPath = Path.Combine(directory, "ptr.decls");
        File.WriteAllText(declsPath, """
            .lib "kernel32.dll"
            RtlMoveMemory(dest*, src*, length%)
            """);

        var decls = UserLibResolver.ParseDeclsFile(declsPath, directory);
        Assert.Single(decls);
        var decl = decls[0];
        Assert.Equal("RtlMoveMemory", decl.BasicName);
        Assert.Equal(ExternalValueType.Void, decl.ReturnType);
        Assert.Equal([ExternalValueType.Integer, ExternalValueType.Integer, ExternalValueType.Integer], decl.ParameterTypes);
        Assert.Equal([true, true, false], decl.PointerParameters);
        Assert.True(decl.IsWin32SystemDll);
    }

    [Fact]
    public void UserLibResolver_GeneratesNativePointerLeaseOnlyWhenWin32RuntimeIsSelected()
    {
        var declsPath = Path.Combine(directory, "native-ptr.decls");
        File.WriteAllText(declsPath, """
            .lib "user32.dll"
            GetClientRect%(hwnd%, rect*)
            SetWindowLong%(hwnd%, index%, value%)
            """);
        var declarations = UserLibResolver.ParseDeclsFile(declsPath, directory);

        var nativeSource = UserLibResolver.GenerateDirectNativeBindingsSource(
            declarations,
            "runtime.dll",
            NativeBlitzCommandLibrary.SupportedRuntimeSha256);

        Assert.Contains("NativeBlitzCommandLibrary.For(typeof(DirectUserLibs).Assembly, \"runtime.dll\"", nativeSource);
        Assert.Contains("using var p1 = NativeLibrary.Pointer(a1);", nativeSource);
        Assert.Contains("PInvoke_0(a0, unchecked((int)p1.Address))", nativeSource);
        Assert.Contains("p1.Complete();", nativeSource);
        Assert.Contains("PInvoke_1(a0, a1, a2)", nativeSource);

        var ordinarySource = UserLibResolver.GenerateDirectNativeBindingsSource(declarations);
        Assert.DoesNotContain("NativeBlitzCommandLibrary", ordinarySource);
        Assert.DoesNotContain("NativeLibrary.Pointer", ordinarySource);
        Assert.Contains("PInvoke_0(a0, a1)", ordinarySource);
    }

    [Theory]
    [InlineData("user32.dll")]
    [InlineData("kernel32.dll")]
    [InlineData("gdi32.dll")]
    [InlineData("advapi32.dll")]
    [InlineData("winmm.dll")]
    public void UserLibResolver_RecognizesWin32SystemDllsWhenMissingLocally(string dllName)
    {
        var declsPath = Path.Combine(directory, "sys.decls");
        File.WriteAllText(declsPath, $"""
            .lib "{dllName}"
            SysFn%()
            """);

        var decls = UserLibResolver.ParseDeclsFile(declsPath, directory);
        Assert.Single(decls);
        Assert.True(decls[0].IsWin32SystemDll);
        Assert.Equal(dllName, decls[0].Win32SystemDllName);
    }

    [SdkRuntimeFact]
    public void Blitz3dRuntimeExtractor_ExtractsAllCommandsFromSdkRuntime()
    {
        var commands = Blitz3dRuntimeExtractor.ExtractCommands(SdkRuntimePath);
        Assert.True(commands.Count >= 521);

        var gfx = commands.FirstOrDefault(c => c.BasicName == "Graphics3D");
        Assert.NotNull(gfx);
        Assert.Equal(ExternalValueType.Void, gfx.ReturnType);
        Assert.Equal([ExternalValueType.Integer, ExternalValueType.Integer, ExternalValueType.Integer, ExternalValueType.Integer], gfx.ParameterTypes);
        Assert.Equal([0, 0], gfx.DefaultValues);

        var millisecs = commands.FirstOrDefault(c => c.BasicName == "MilliSecs");
        Assert.NotNull(millisecs);
        Assert.Equal(ExternalValueType.Integer, millisecs.ReturnType);
        Assert.Empty(millisecs.ParameterTypes);

        var sin = commands.FirstOrDefault(c => c.BasicName == "Sin");
        Assert.NotNull(sin);
        Assert.Equal(ExternalValueType.Float, sin.ReturnType);
        Assert.Equal([ExternalValueType.Float], sin.ParameterTypes);
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_LoadsBlitz3dRuntime_FiltersReservedKeywordsAndRegistersSignatures()
    {
        var user32Decls = Path.Combine(directory, "user32-x64.decls");
        File.WriteAllText(user32Decls, ".lib \"user32.dll\"\nGetClientRect%(hwnd%, lpRect*) : \"GetClientRect\"\n");

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            userlibs = new[] { user32Decls },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");

        // End and Print are filtered out, leaving 519 commands.
        Assert.DoesNotContain(result.Functions.Functions, f => f.Name.Equals("End", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Functions.Functions, f => f.Name.Equals("Print", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("Graphics3D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("CreateCamera", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("MilliSecs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("GetClientRect", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(result.BinaryBindings);
        var gfxBinding = result.BinaryBindings.FirstOrDefault(b => b.BasicName == "Graphics3D");
        Assert.NotNull(gfxBinding);
        Assert.NotNull(gfxBinding.CommandRva);
        Assert.True(gfxBinding.CommandRva > 0);
        Assert.Null(gfxBinding.NativeCommandSha256);
        Assert.Contains(result.BinaryLibraries!, path => Path.GetFullPath(path) == Path.GetFullPath(SdkRuntimePath));
        var generated = BinaryBindingGenerator.Source(result.BinaryBindings, result.LibrarySpecs!);
        Assert.Contains("BinaryLibrary.For(typeof(Registry).Assembly, \"runtime.dll\"", generated);
        Assert.Contains("BinaryLibrary.MarshalArgument(Lib, mem, global::BlitzCS.BinaryCompiler.Runtime.BinaryArgument.FromPointer(a1)", generated);
        Assert.DoesNotContain("NativeBlitzCommandLibrary.For", generated);
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_Win32RuntimeCommandsUseNativeRvaBindings()
    {
        var fmodPath = Path.Combine(directory, "fmod.dll");
        File.Copy(SdkRuntimePath, fmodPath);
        var fmodDecls = Path.Combine(directory, "fmod.decls");
        File.WriteAllText(fmodDecls, ".lib \"fmod.dll\"\nFmodInit%()\n");
        var user32Decls = Path.Combine(directory, "user32.decls");
        File.WriteAllText(user32Decls, ".lib \"user32.dll\"\nGetClientRect%(hwnd%, lpRect*) : \"GetClientRect\"\n");

        var catalogPath = Path.Combine(directory, "catalog-win32.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            userlibs = new[] { fmodDecls, user32Decls },
            binaryLibraries = new object[]
            {
                new { path = SdkRuntimePath, win32 = true },
                new { path = fmodPath, win32 = true, unpack = true }
            },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win32");

        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("Graphics3D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("FmodInit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("GetClientRect", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.NativeLibraries, path => Path.GetFullPath(path) == Path.GetFullPath(SdkRuntimePath));
        Assert.Contains(result.NativeLibraries, path => Path.GetFullPath(path) == Path.GetFullPath(fmodPath));
        Assert.Empty(result.BinaryLibraries ?? []);
        Assert.Empty(result.LibrarySpecs ?? []);
        var graphics = Assert.Single(result.BinaryBindings!, b => b.BasicName == "Graphics3D");
        Assert.NotNull(graphics.NativeCommandSha256);
        Assert.Equal(NativeBlitzCommandLibrary.SupportedRuntimeSha256, graphics.NativeCommandSha256);

        var generated = BinaryBindingGenerator.Source(result.BinaryBindings!, result.LibrarySpecs!);
        Assert.Contains("NativeBlitzCommandLibrary.For(typeof(Registry).Assembly, \"runtime.dll\"", generated);
        Assert.Contains("NativeBlitzCommandExport C", generated);
        Assert.DoesNotContain("BinaryLibrary.For", generated);
    }

    [Fact]
    public void BinaryBindingGeneratorKeepsTranslatedRuntimeRvaCallsForWin64()
    {
        var binding = new BinaryBinding("Graphics3D", "runtime.dll", "", GuestCallingConvention.Cdecl,
            [ExternalValueType.Integer], ExternalValueType.Void) { CommandRva = 0x1234 };
        var generated = BinaryBindingGenerator.Source([binding], [new BinaryLibrarySpec("runtime.dll")]);

        Assert.Contains("BinaryLibrary.For(typeof(Registry).Assembly, \"runtime.dll\"", generated);
        Assert.Contains("BinaryCommandExport C0 = Lib.Command(0x1234u, \"Graphics3D\", 1)", generated);
        Assert.DoesNotContain("NativeBlitzCommandLibrary.For", generated);
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_Win32RejectsUnverifiedNativeBlitzRuntime()
    {
        var alteredRuntime = Path.Combine(directory, "runtime-altered.dll");
        var image = File.ReadAllBytes(SdkRuntimePath);
        image[^1] ^= 1;
        File.WriteAllBytes(alteredRuntime, image);
        var catalogPath = Path.Combine(directory, "catalog-unverified.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = alteredRuntime,
            functions = Array.Empty<object>()
        }));

        var error = Assert.Throws<ArgumentException>(() => ExternalCatalog.Load(catalogPath, "Win32"));
        Assert.Contains("unsupported SHA-256", error.Message);
    }

    [Fact]
    public void ExternalCatalog_Win32SystemDllUserlib_PartitionsToDirectOnWin32()
    {
        var declsPath = Path.Combine(directory, "user32.decls");
        File.WriteAllText(declsPath, """
            .lib "user32.dll"
            MessageBoxA%(hwnd%, text$, title$, flags%) : "MessageBoxA"
            """);

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            userlibs = new[] { declsPath },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win32");

        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("MessageBoxA", StringComparison.OrdinalIgnoreCase));
        // user32.dll should not be tracked as a file in NativeLibraries
        Assert.DoesNotContain(result.NativeLibraries, lib => Path.GetFileName(lib).Equals("user32.dll", StringComparison.OrdinalIgnoreCase));
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_Win32SystemDllUserlib_BindsViaSharedWin32OnWin64()
    {
        var declsPath = Path.Combine(directory, "user32.decls");
        File.WriteAllText(declsPath, """
            .lib "user32.dll"
            MessageBoxA%(hwnd%, text$, title$, flags%) : "MessageBoxA"
            """);

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            userlibs = new[] { declsPath },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");

        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("MessageBoxA", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.BinaryBindings);
        var mb = result.BinaryBindings.FirstOrDefault(b => b.BasicName == "MessageBoxA");
        Assert.NotNull(mb);
        Assert.Equal("user32.dll", mb.Win32SystemDll);
        Assert.True(mb.FromUserLib);
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_UserlibStringReturn_DoesNotThrowUnsupported()
    {
        var declsPath = Path.Combine(directory, "str.decls");
        File.WriteAllText(declsPath, """
            .lib "user32.dll"
            GetSampleString$(id%) : "GetSampleString"
            """);

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            userlibs = new[] { declsPath },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");

        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("GetSampleString", StringComparison.OrdinalIgnoreCase));
        var binding = result.BinaryBindings?.FirstOrDefault(b => b.BasicName == "GetSampleString");
        Assert.NotNull(binding);
        Assert.Equal(ExternalValueType.String, binding.ReturnType);
        Assert.True(binding.FromUserLib);
    }

    [SdkRuntimeFact]
    public void BinaryTranslationStep_TranslatesBlitz3dRuntimeWithoutErrors()
    {
        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");
        var outDir = Path.Combine(directory, "out");
        Directory.CreateDirectory(outDir);
        var exePath = Path.Combine(outDir, "test.exe");

        using var warnings = new StringWriter();
        var prepared = BinaryTranslationStep.Prepare(result, exePath, warnings, "Win64");

        using var output = new StringWriter();
        prepared.Publish(output);

        var bcxPath = Path.Combine(outDir, "runtime.dll.bcx");
        Assert.True(File.Exists(bcxPath));
        var bytes = File.ReadAllBytes(bcxPath);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Win32Registry_NormalizesCrtdllToMsvcrt()
    {
        var normalized = BlitzCS.BinaryCompiler.Win32.Win32Registry.NormalizeDll("crtdll.dll");
        Assert.Equal("msvcrt.dll", normalized);
        var normalizedNoExt = BlitzCS.BinaryCompiler.Win32.Win32Registry.NormalizeDll("crtdll");
        Assert.Equal("msvcrt.dll", normalizedNoExt);
    }

    [SdkRuntimeFact]
    public void ExternalCatalog_ResolvesUndecoratedUserLibAsStdcall()
    {
        var declsPath = Path.Combine(directory, "undecorated.decls");
        File.WriteAllText(declsPath, $"""
            .lib "{SdkRuntimePath}"
            UndecoratedFunc%(a%) : "runtimeGetRuntime"
            """);

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            userlibs = new[] { declsPath },
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");
        var binding = result.BinaryBindings?.FirstOrDefault(b => b.BasicName == "UndecoratedFunc");
        Assert.NotNull(binding);
        Assert.Equal(BlitzCS.BinaryCompiler.GuestCallingConvention.Stdcall, binding.Convention);
    }
}

/// <summary>
/// [Fact] for a test that needs the extracted Blitz3D SDK: <c>BLITZCS_BLITZ3D_DIR</c> names the SDK root that contains <c>bin/runtime.dll</c>.
/// SKIPPED with the reason when it is not there, instead of returning early and reporting a meaningless PASS.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SdkRuntimeFactAttribute : FactAttribute
{
    public const string Variable = "BLITZCS_BLITZ3D_DIR";

    public SdkRuntimeFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(dir)) Skip = $"{Variable} is not set (point it at the extracted Blitz3D SDK root, the directory that contains bin/runtime.dll)";
        else if (!File.Exists(Path.Combine(dir, "bin", "runtime.dll"))) Skip = $"{Variable}='{dir}' has no bin/runtime.dll";
    }
}
