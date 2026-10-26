using System.Text.Json;
using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class WorkstreamCTests : IDisposable
{
    private const string SdkRuntimePath = "/tmp/claude-1000/dyn-sdk.9EHv0N/Blitz3D-V1.118/bin/runtime.dll";
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

    [Fact]
    public void Blitz3dRuntimeExtractor_ExtractsAllCommandsFromSdkRuntime()
    {
        if (!File.Exists(SdkRuntimePath)) return;

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

    [Fact]
    public void ExternalCatalog_LoadsBlitz3dRuntime_FiltersReservedKeywordsAndRegistersSignatures()
    {
        if (!File.Exists(SdkRuntimePath)) return;

        var catalogPath = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            blitz3dRuntime = SdkRuntimePath,
            binaryTarget = "windows-x64",
            functions = Array.Empty<object>()
        }));

        using var result = ExternalCatalog.Load(catalogPath, "Win64");

        // End and Print are filtered out, leaving 519 commands.
        Assert.DoesNotContain(result.Functions.Functions, f => f.Name.Equals("End", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Functions.Functions, f => f.Name.Equals("Print", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("Graphics3D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("CreateCamera", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Functions.Functions, f => f.Name.Equals("MilliSecs", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(result.BinaryBindings);
        var gfxBinding = result.BinaryBindings.FirstOrDefault(b => b.BasicName == "Graphics3D");
        Assert.NotNull(gfxBinding);
        Assert.NotNull(gfxBinding.CommandRva);
        Assert.True(gfxBinding.CommandRva > 0);
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

    [Fact]
    public void ExternalCatalog_Win32SystemDllUserlib_BindsViaSharedWin32OnWin64()
    {
        if (!File.Exists(SdkRuntimePath)) return;

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

    [Fact]
    public void ExternalCatalog_UserlibStringReturn_DoesNotThrowUnsupported()
    {
        if (!File.Exists(SdkRuntimePath)) return;

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

    [Fact]
    public void BinaryTranslationStep_TranslatesBlitz3dRuntimeWithoutErrors()
    {
        if (!File.Exists(SdkRuntimePath)) return;

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

    [Fact]
    public void ExternalCatalog_ResolvesUndecoratedUserLibAsStdcall()
    {
        if (!File.Exists(SdkRuntimePath)) return;

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
