using BlitzCS.BinaryCompiler;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? target = null;
        var targetAt = Array.IndexOf(args, "--target");
        if (targetAt >= 0)
        {
            if (targetAt + 1 >= args.Length || !TargetCapabilities.TryParseTarget(args[targetAt + 1], out _))
            {
                error.WriteLine("--target expects linux-x64 or windows-x64 (or Win32, Linux32, Win64, Linux64).");
                return 2;
            }
            target = args[targetAt + 1];
            args = [.. args[..targetAt], .. args[(targetAt + 2)..]];
        }
        string? packageOutput = null;
        string? assets = null;
        var graphical = args.Contains("--gui");
        args = args.Where(arg => arg != "--gui").ToArray();
        var assetsAt = Array.IndexOf(args, "--assets");
        if (assetsAt >= 0)
        {
            if (assetsAt + 1 >= args.Length) { error.WriteLine("--assets expects a directory."); return 2; }
            assets = args[assetsAt + 1];
            args = [.. args[..assetsAt], .. args[(assetsAt + 2)..]];
        }
        // Keep explicit DLL emission for existing tooling and compiler regression tests.
        var outputAt = Array.IndexOf(args, "-o");
        if (outputAt >= 0 && outputAt + 1 < args.Length && !args[outputAt + 1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            packageOutput = args[outputAt + 1];
            args = [.. args[..outputAt], .. args[(outputAt + 2)..]];
        }
        var packageBuild = args.Length is 2 or 4 && args[0] == "build" && (args.Length == 2 || args[2] == "--externals");
        var syntaxOnly = args.Length == 2 && args[0] == "check-syntax";
        var semanticsOnly = args.Length is 2 or 4 && args[0] == "check-semantics" && (args.Length == 2 || args[2] == "--externals");
        if (!packageBuild && !syntaxOnly && !semanticsOnly && (args.Length is not (4 or 6) || args[0] != "build" || args[2] != "-o" || args.Length == 6 && args[4] != "--externals"))
        {
            error.WriteLine("Usage: blitzcs build <source.bb> [-o <package-directory|output.dll>] [--externals <catalog.json>] [--target linux-x64|windows-x64] [--assets <directory>] [--gui]");
            error.WriteLine("       blitzcs check-syntax <source.bb>");
            error.WriteLine("       blitzcs check-semantics <source.bb> [--externals <catalog.json>]");
            return 2;
        }

        try
        {
            var sourcePath = Path.GetFullPath(args[1]);
            if (!string.Equals(Path.GetExtension(sourcePath), ".bb", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Source must have the .bb extension.");
            if (syntaxOnly)
            {
                BasicCompiler.ValidateSyntax(File.ReadAllText(sourcePath), sourcePath);
                output.WriteLine("Syntax validated; semantics, external binding and assembly emission were not checked.");
                return 0;
            }
            if (semanticsOnly)
            {
                using var semanticCatalog = args.Length == 4
                    ? ExternalCatalog.Load(args[3])
                    : new ExternalCatalogResult(ExternalFunctionRegistry.Empty, []);
                var unbound = BasicCompiler.CheckSemantics(File.ReadAllText(sourcePath), sourcePath, semanticCatalog.Functions);
                foreach (var call in unbound)
                    output.WriteLine($"{call.SourcePath}({call.Line},{call.Column}): external function '{call.Name}' has a declared signature but no implementation.");
                output.WriteLine($"Semantic validation succeeded; {unbound.Count} external call(s) lack implementations.");
                return 0;
            }
            if (packageBuild)
            {
                // Application and translated native code must always agree about the runtime target.
                var applicationTarget = target is null ? CompilationTarget.Win64 : TargetCapabilities.ParseTarget(target);
                using var packageCatalog = args.Length == 4
                    ? ExternalCatalog.Load(args[3], target, isPackageBuild: true)
                    : new ExternalCatalogResult(ExternalFunctionRegistry.Empty, []);
                var code = BasicCompiler.Compile(File.ReadAllText(sourcePath), sourcePath, packageCatalog.Functions, out var inputs);
                var name = Path.GetFileNameWithoutExtension(sourcePath);
                var destination = packageOutput ?? Path.Combine("dist", name);
                if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(destination);
                    destination = Path.Combine(Path.GetDirectoryName(destination) ?? ".", name);
                }
                ApplicationPublisher.Build(code, destination, name, applicationTarget, packageCatalog,
                    inputs.Concat(packageCatalog.CatalogInputs ?? []), output, error, assets, graphical);
                return 0;
            }
            if (packageOutput is not null || assets is not null || graphical)
                throw new ArgumentException("--assets and --gui require an application package build.");
            var outputPath = Path.GetFullPath(args[3]);
            using var catalog = args.Length == 6
                ? ExternalCatalog.Load(args[5], target, isPackageBuild: false)
                : new ExternalCatalogResult(ExternalFunctionRegistry.Empty, []);
            if (target is not null)
            {
                var explicitTarget = TargetCapabilities.ParseTarget(target);
                foreach (var native in catalog.NativeLibraries.Except(catalog.BinaryLibraries ?? []))
                    TargetCapabilities.ValidateNativeLibrary(native, explicitTarget);
            }
            var binaries = BinaryTranslationStep.Prepare(catalog, outputPath, error, target);
            var generated = BasicCompiler.Compile(File.ReadAllText(sourcePath), sourcePath, catalog.Functions, out var sourceInputs);
            DotNetEmitter.Emit(generated, outputPath, entryPointTypeName: "BlitzProgram",
                referencePaths: catalog.Functions.ReferencePaths, nativeLibraryPaths: catalog.NativeLibraries,
                inputPaths: sourceInputs.Concat(catalog.CatalogInputs ?? []));
            binaries.Publish(output);
            output.WriteLine(outputPath);
            return 0;
        }
        catch (Exception exception) when (exception is BasicDiagnosticException
            or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException or KeyNotFoundException or TypeLoadException or BadImageFormatException
            or System.Reflection.AmbiguousMatchException or PeRebaseException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }
}
