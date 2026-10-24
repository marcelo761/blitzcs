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
            if (targetAt + 1 >= args.Length || BinaryTargets.TryNormalize(args[targetAt + 1]) is null)
            {
                error.WriteLine("--target expects linux-x64 or windows-x64.");
                return 2;
            }
            target = args[targetAt + 1];
            args = [.. args[..targetAt], .. args[(targetAt + 2)..]];
        }
        var syntaxOnly = args.Length == 2 && args[0] == "check-syntax";
        var semanticsOnly = args.Length is 2 or 4 && args[0] == "check-semantics" && (args.Length == 2 || args[2] == "--externals");
        if (!syntaxOnly && !semanticsOnly && (args.Length is not (4 or 6) || args[0] != "build" || args[2] != "-o" || args.Length == 6 && args[4] != "--externals"))
        {
            error.WriteLine("Usage: blitzcs build <source.bb> -o <output.dll> [--externals <catalog.json>] [--target linux-x64|windows-x64]");
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
            var outputPath = Path.GetFullPath(args[3]);
            using var catalog = args.Length == 6
                ? ExternalCatalog.Load(args[5], target)
                : new ExternalCatalogResult(ExternalFunctionRegistry.Empty, []);
            var binaries = BinaryTranslationStep.Prepare(catalog, outputPath, error, target);
            var generated = BasicCompiler.Compile(File.ReadAllText(sourcePath), sourcePath, catalog.Functions, out var sourceInputs);
            DotNetEmitter.Emit(generated, outputPath, entryPointTypeName: "BlitzProgram",
                referencePaths: catalog.Functions.ReferencePaths, nativeLibraryPaths: catalog.NativeLibraries,
                inputPaths: args.Length == 6 ? sourceInputs.Append(args[5]) : sourceInputs);
            binaries.Publish(output);
            output.WriteLine(outputPath);
            return 0;
        }
        catch (Exception exception) when (exception is BasicDiagnosticException
            or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException or KeyNotFoundException or TypeLoadException or BadImageFormatException
            or System.Reflection.AmbiguousMatchException or PeRebaseException)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }
}
