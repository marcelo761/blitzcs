using BlitzCS.Compiler;

namespace BlitzCS.CLI;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is not (4 or 6) || args[0] != "build" || args[2] != "-o" || args.Length == 6 && args[4] != "--externals")
        {
            error.WriteLine("Usage: blitzcs build <source.bb> -o <output.dll> [--externals <catalog.json>]");
            return 2;
        }

        try
        {
            var sourcePath = Path.GetFullPath(args[1]);
            var outputPath = Path.GetFullPath(args[3]);
            if (!string.Equals(Path.GetExtension(sourcePath), ".bb", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Source must have the .bb extension.");
            var externals = args.Length == 6 ? ExternalCatalog.Load(args[5]) : ExternalFunctionRegistry.Empty;
            var generated = BasicCompiler.Compile(File.ReadAllText(sourcePath), sourcePath, externals);
            DotNetEmitter.Emit(generated, outputPath, entryPointTypeName: "BlitzProgram", referencePaths: externals.ReferencePaths);
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
