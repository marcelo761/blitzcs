using BlitzCS.Compiler;

namespace BlitzCS.CLI;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var syntaxOnly = args.Length == 2 && args[0] == "check-syntax";
        if (!syntaxOnly && (args.Length is not (4 or 6) || args[0] != "build" || args[2] != "-o" || args.Length == 6 && args[4] != "--externals"))
        {
            error.WriteLine("Usage: blitzcs build <source.bb> -o <output.dll> [--externals <catalog.json>]");
            error.WriteLine("       blitzcs check-syntax <source.bb>");
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
            var outputPath = Path.GetFullPath(args[3]);
            var catalog = args.Length == 6
                ? ExternalCatalog.Load(args[5])
                : new ExternalCatalogResult(ExternalFunctionRegistry.Empty, []);
            var generated = BasicCompiler.Compile(File.ReadAllText(sourcePath), sourcePath, catalog.Functions);
            DotNetEmitter.Emit(generated, outputPath, entryPointTypeName: "BlitzProgram",
                referencePaths: catalog.Functions.ReferencePaths, nativeLibraryPaths: catalog.NativeLibraries);
            output.WriteLine(outputPath);
            return 0;
        }
        catch (Exception exception) when (exception is BasicDiagnosticException
            or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException or KeyNotFoundException or TypeLoadException or BadImageFormatException
            or System.Reflection.AmbiguousMatchException)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }
}
