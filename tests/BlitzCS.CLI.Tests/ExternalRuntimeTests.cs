using System.Diagnostics;
using System.Text.Json;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class ExternalRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-external-" + Guid.NewGuid());
    public ExternalRuntimeTests() => Directory.CreateDirectory(directory);

    private string Catalog(string assembly, string type, params (string Basic, string Method, string[] Types)[] functions)
    {
        return WriteCatalog(assembly, type, functions, null);
    }

    private string CatalogWithNativeLibraries(string assembly, string type, string[] nativeLibraries,
        params (string Basic, string Method, string[] Types)[] functions) =>
        WriteCatalog(assembly, type, functions, nativeLibraries);

    private string WriteCatalog(string assembly, string type,
        (string Basic, string Method, string[] Types)[] functions, string[]? nativeLibraries)
    {
        var path = Path.Combine(directory, "externals.json");
        var payload = new
        {
            functions = functions.Select(f => new
            {
                name = f.Basic, assembly, type, method = f.Method, parameters = f.Types
            }),
            nativeLibraries
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        return path;
    }

    private string Fixture()
    {
        var path = Path.Combine(directory, "Fixture.dll");
        DotNetEmitter.Emit("""
            public static class Fixture {
                public static int Add(int n, int delta=2) => n+delta;
                public static float Scale(float n) => n*2;
                public static string Wrap(string text) => "["+text+"]";
                public static void Main() {}
            }
            """, path, "Fixture");
        return path;
    }

    [Fact]
    public async Task ExternalModuleRunsWithDefaultsConversionsIncludesAndBidirectionalHybridCalls()
    {
        var assembly = Fixture();
        var catalog = Catalog(assembly, "Fixture", ("NativeAdd", "Add", ["Integer", "Integer"]),
            ("NativeScale", "Scale", ["Float"]), ("NativeWrap", "Wrap", ["String"]));
        File.WriteAllText(Path.Combine(directory, "functions.bb"), """
            Function RoundTrip%(n%)
              Return NativeAdd(n)
            End Function
            <?cs module
            public static class Hybrid {
                public static int Call(int n) => BB.roundtrip(n);
            }
            ?>
            """);
        var result = await BuildAndRun("""
            Include "functions.bb"
            Print CS::Hybrid.Call(40)
            Print NativeScale(1.75)
            Print CS::Fixture.Scale(1.75)
            Print NativeWrap$(42)
            NativeAdd 3.6,1
            """, catalog);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42", "3.5", "3.5", "[42]"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task CatalogPublishesAndRunsTransitiveSameDirectoryDependencies()
    {
        var dependencies = Path.Combine(directory, "dependencies");
        Directory.CreateDirectory(dependencies);
        var leaf = Path.Combine(dependencies, "Leaf.dll");
        var middle = Path.Combine(dependencies, "Middle.dll");
        var root = Path.Combine(dependencies, "Root.dll");
        DotNetEmitter.Emit("public static class Leaf { public static int Value() => 5; public static void Main() {} }", leaf, "Leaf");
        DotNetEmitter.Emit("public static class Middle { public static int Value() => Leaf.Value() + 7; public static void Main() {} }", middle, "Middle",
            referencePaths: [leaf]);
        DotNetEmitter.Emit("public static class Root { public static int Add(int value) => Middle.Value() + value; public static void Main() {} }", root, "Root",
            referencePaths: [middle]);
        var catalog = Catalog(root, "Root", ("NativeAdd", "Add", ["Integer"]));

        var result = await BuildAndRun("Print NativeAdd(30)", catalog, runInIsolatedDirectory: true);

        Assert.True(File.Exists(Path.Combine(directory, "out", "Root.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "out", "Middle.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "out", "Leaf.dll")));
        Assert.False(File.Exists(Path.Combine(directory, "out", "System.Runtime.dll")));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task CatalogPublishesNativeLibraryBesideManagedWrapperAndRunsFromIsolatedDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        var nativeSource = Path.Combine(directory, "native.c");
        var nativeLibrary = Path.Combine(directory, "libblitzcs_publish_fixture.so");
        File.WriteAllText(nativeSource, "#include <stdint.h>\nint32_t blitz_add(int32_t left, int32_t right) { return left + right; }\n");
        var cc = new ProcessStartInfo("cc") { RedirectStandardError = true, UseShellExecute = false };
        cc.ArgumentList.Add("-shared");
        cc.ArgumentList.Add("-fPIC");
        cc.ArgumentList.Add("-o");
        cc.ArgumentList.Add(nativeLibrary);
        cc.ArgumentList.Add(nativeSource);
        using (var compiler = Process.Start(cc)!)
        {
            var compileError = await compiler.StandardError.ReadToEndAsync();
            await compiler.WaitForExitAsync();
            Assert.True(compiler.ExitCode == 0, compileError);
        }

        var helper = Path.Combine(directory, "NativeHelper.dll");
        DotNetEmitter.Emit("""
            using System.Runtime.InteropServices;
            public static class NativeHelper {
                [DllImport("libblitzcs_publish_fixture.so", EntryPoint="blitz_add", CallingConvention=CallingConvention.Cdecl)]
                private static extern int Add(int left, int right);
                public static int Call(int left, int right) => Add(left, right);
                public static void Main() {}
            }
            """, helper, "NativeHelper");
        var wrapper = Path.Combine(directory, "NativeWrapper.dll");
        DotNetEmitter.Emit("public static class NativeWrapper { public static int Add(int a, int b) => NativeHelper.Call(a, b); public static void Main() {} }",
            wrapper, "NativeWrapper", [helper]);
        var catalog = CatalogWithNativeLibraries(wrapper, "NativeWrapper",
            [Path.GetFileName(nativeLibrary)], ("NativeAdd", "Add", ["Integer", "Integer"]));

        var result = await BuildAndRun("Print NativeAdd(19, 23)", catalog, runInIsolatedDirectory: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Lines("42"), result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void MissingNativeLibraryPreservesExistingOutputFiles()
    {
        var catalog = CatalogWithNativeLibraries(Fixture(), "Fixture", ["missing-native.so"],
            ("NativeAdd", "Add", ["Integer", "Integer"]));
        var source = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "out", "program.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(source, "Print NativeAdd(1, 2)");
        File.WriteAllText(output, "keep assembly");
        File.WriteAllText(Path.ChangeExtension(output, ".generated.cs"), "keep source");
        using var error = new StringWriter();

        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, error));

        Assert.Contains("missing-native.so", error.ToString());
        Assert.Equal("keep assembly", File.ReadAllText(output));
        Assert.Equal("keep source", File.ReadAllText(Path.ChangeExtension(output, ".generated.cs")));
    }

    [Fact]
    public void NativeLibraryBasenameCollisionWithManagedAssemblyPreservesOutputs()
    {
        var managedDirectory = Path.Combine(directory, "managed");
        var nativeDirectory = Path.Combine(directory, "native");
        Directory.CreateDirectory(managedDirectory);
        Directory.CreateDirectory(nativeDirectory);
        var managed = Path.Combine(managedDirectory, "Fixture.dll");
        DotNetEmitter.Emit("public static class Fixture { public static void Main() {} }", managed, "Fixture");
        var native = Path.Combine(nativeDirectory, "Fixture.dll");
        File.WriteAllText(native, "native");
        var output = Path.Combine(directory, "out", "program.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep assembly");
        File.WriteAllText(Path.ChangeExtension(output, ".generated.cs"), "keep source");

        var error = Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit(
            "internal static class App { public static void Main() {} }", output, "App", [managed], [native]));

        Assert.Contains("same output filename", error.Message);
        Assert.Equal("keep assembly", File.ReadAllText(output));
        Assert.Equal("keep source", File.ReadAllText(Path.ChangeExtension(output, ".generated.cs")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "Fixture.dll")));
    }

    [Fact]
    public void NativeLibraryBasenameCollisionWithGeneratedOutputIsRejectedBeforeWrites()
    {
        var directory = Path.Combine(this.directory, "native-output-collision");
        Directory.CreateDirectory(directory);
        var native = Path.Combine(directory, "program.generated.cs");
        File.WriteAllText(native, "native asset");
        var output = Path.Combine(directory, "program.dll");
        File.WriteAllText(output, "keep assembly");
        File.WriteAllText(Path.ChangeExtension(output, ".generated.cs"), "keep source");

        var error = Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit(
            "internal static class App { public static void Main() {} }", output, "App", null, [native]));

        Assert.Contains("output file", error.Message);
        Assert.Equal("keep assembly", File.ReadAllText(output));
        Assert.Equal("keep source", File.ReadAllText(Path.ChangeExtension(output, ".generated.cs")));
    }

    [Fact]
    public void CatalogWithMissingTransitiveDependencyPreservesExistingOutputFiles()
    {
        var dependencies = Path.Combine(directory, "dependencies");
        Directory.CreateDirectory(dependencies);
        var leaf = Path.Combine(dependencies, "Leaf.dll");
        var root = Path.Combine(dependencies, "Root.dll");
        DotNetEmitter.Emit("public static class Leaf { public static int Value() => 5; public static void Main() {} }", leaf, "Leaf");
        DotNetEmitter.Emit("public static class Root { public static int Add(int value) => Leaf.Value() + value; public static void Main() {} }", root, "Root",
            referencePaths: [leaf]);
        File.Delete(leaf);
        var catalog = Catalog(root, "Root", ("NativeAdd", "Add", ["Integer"]));
        var source = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "out", "program.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(source, "Print NativeAdd(30)");
        File.WriteAllText(output, "keep assembly");
        File.WriteAllText(Path.ChangeExtension(output, ".generated.cs"), "keep source");
        using var error = new StringWriter();

        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, error));

        Assert.Contains("Leaf", error.ToString());
        Assert.Equal("keep assembly", File.ReadAllText(output));
        Assert.Equal("keep source", File.ReadAllText(Path.ChangeExtension(output, ".generated.cs")));
    }

    [Fact]
    public async Task RuntimeErrorIsImplementedInSeparateLibraryAndStopsWithMessage()
    {
        var catalog = Catalog(typeof(BlitzCS.LegacyRuntime.LegacyErrors).Assembly.Location,
            "BlitzCS.LegacyRuntime.LegacyErrors", ("RuntimeError", "RuntimeError", ["String"]));
        var result = await BuildAndRun("Print \"before\"\nRuntimeError \"native failure\"\nPrint \"unreachable\"", catalog);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(Lines("before"), result.Stdout);
        Assert.Contains("native failure", result.Stderr);
        Assert.Contains("LegacyErrors.RuntimeError", result.Stderr);
        Assert.Contains("main.bb", result.Stderr);
    }

    [Fact]
    public void CatalogWithMissingMethodProducesDiagnosticAndNoOutputAssembly()
    {
        var catalog = Catalog(Fixture(), "Fixture", ("Missing", "NotThere", ["Integer"]));
        var source = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "out", "program.dll");
        File.WriteAllText(source, "Print Missing(1)");
        using var error = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", source, "-o", output, "--externals", catalog], TextWriter.Null, error));
        Assert.Contains("does not exist", error.ToString());
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void RuntimeIsOptInAndCompilerHasNoEngineDependency()
    {
        var error = Assert.Throws<BlitzCS.Compiler.BasicDiagnosticException>(() =>
            BlitzCS.Compiler.BasicCompiler.Compile("RuntimeError \"failure\"", "main.bb"));
        Assert.Contains("Unknown BASIC function or external symbol", error.Message);
        Assert.DoesNotContain(typeof(BlitzCS.Compiler.BasicCompiler).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.Contains("LegacyRuntime") || a.Name.Contains("Blitz3DCS"));
    }

    [Fact]
    public void OutputCannotOverwriteAnExternalAssembly()
    {
        var module = Fixture();
        var original = File.ReadAllBytes(module);
        Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit(
            "internal static class App { public static void Main() {} }", module, "App", [module]));
        Assert.Equal(original, File.ReadAllBytes(module));
    }

    [Fact]
    public void ExternalAssembliesWithTheSameOutputFilenameAreRejectedBeforeCopying()
    {
        var module = Fixture();
        var otherDirectory = Path.Combine(directory, "other");
        Directory.CreateDirectory(otherDirectory);
        var second = Path.Combine(otherDirectory, Path.GetFileName(module));
        File.Copy(module, second);
        var output = Path.Combine(directory, "out", "program.dll");
        Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit(
            "internal static class App { public static void Main() {} }", output, "App", [module, second]));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Fact]
    public void FilenameCollisionPreservesExistingOutputFiles()
    {
        var firstDirectory = Path.Combine(directory, "first");
        var secondDirectory = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "Shared.dll");
        var second = Path.Combine(secondDirectory, "Shared.dll");
        DotNetEmitter.Emit("public static class First { public static int Value() => 1; public static void Main() {} }", first, "First");
        DotNetEmitter.Emit("public static class Second { public static int Value() => 2; public static void Main() {} }", second, "Second");
        var outputDirectory = Path.Combine(directory, "collision-output");
        Directory.CreateDirectory(outputDirectory);
        var output = Path.Combine(outputDirectory, "program.dll");
        File.WriteAllText(output, "keep assembly");
        File.WriteAllText(Path.ChangeExtension(output, ".generated.cs"), "keep source");
        File.WriteAllText(Path.ChangeExtension(output, ".pdb"), "keep symbols");

        var error = Assert.Throws<ArgumentException>(() => DotNetEmitter.Emit(
            "internal static class App { public static void Main() {} }", output, "App", [first, second]));

        Assert.Contains("same output filename", error.Message);
        Assert.Equal("keep assembly", File.ReadAllText(output));
        Assert.Equal("keep source", File.ReadAllText(Path.ChangeExtension(output, ".generated.cs")));
        Assert.Equal("keep symbols", File.ReadAllText(Path.ChangeExtension(output, ".pdb")));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BuildAndRun(
        string source, string catalog, bool runInIsolatedDirectory = false)
    {
        var input = Path.Combine(directory, "main.bb");
        var output = Path.Combine(directory, "out", "program.dll");
        File.WriteAllText(input, source);
        using var errors = new StringWriter();
        Assert.True(CliApplication.Run(["build", input, "-o", output, "--externals", catalog], TextWriter.Null, errors) == 0, errors.ToString());
        if (runInIsolatedDirectory)
        {
            var isolated = Path.Combine(directory, "isolated-run");
            Directory.CreateDirectory(isolated);
            foreach (var path in Directory.GetFiles(Path.GetDirectoryName(output)!))
                File.Copy(path, Path.Combine(isolated, Path.GetFileName(path)));
            output = Path.Combine(isolated, Path.GetFileName(output));
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
