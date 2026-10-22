using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace BlitzCS.CLI;

/// <summary>Emits a framework-dependent .NET 10 console assembly and portable symbols.</summary>
public static class DotNetEmitter
{
    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName = null) =>
        Emit(generatedCode, outputPath, entryPointTypeName, null);

    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName, IEnumerable<string>? referencePaths) =>
        Emit(generatedCode, outputPath, entryPointTypeName, referencePaths, null);

    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName,
        IEnumerable<string>? referencePaths, IEnumerable<string>? nativeLibraryPaths)
    {
        if (!string.Equals(Path.GetExtension(outputPath), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must have the .dll extension.", nameof(outputPath));

        outputPath = Path.GetFullPath(outputPath);
        var generatedPath = Path.ChangeExtension(outputPath, ".generated.cs");
        var syntax = CSharpSyntaxTree.ParseText(
            SourceText.From(generatedCode, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.CSharp14), generatedPath);
        var platformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The host runtime does not expose platform references.");
        string[] externalPaths;
        using (var context = new ExternalAssemblyLoadContext())
            externalPaths = context.Discover(referencePaths ?? []).ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var nativePaths = (nativeLibraryPaths ?? []).Select(Path.GetFullPath).Distinct(comparer).ToArray();
        foreach (var path in nativePaths)
            if (!File.Exists(path) || Directory.Exists(path))
                throw new FileNotFoundException($"Native library '{path}' does not exist or is not a file.", path);

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var reservedTargets = new HashSet<string>(comparer)
        {
            outputPath,
            generatedPath,
            Path.ChangeExtension(outputPath, ".pdb"),
            Path.ChangeExtension(outputPath, ".runtimeconfig.json")
        };
        var targets = new Dictionary<string, string>(comparer);
        foreach (var path in externalPaths)
        {
            var target = Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileName(path)));
            if (reservedTargets.Contains(target))
                throw new ArgumentException("External assembly cannot overwrite the output assembly.");
            if (targets.TryGetValue(target, out var previous) && !comparer.Equals(previous, path))
                throw new ArgumentException($"External assemblies have the same output filename '{Path.GetFileName(path)}'.");
            targets[target] = path;
        }
        foreach (var path in nativePaths)
        {
            var target = Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileName(path)));
            if (reservedTargets.Contains(target))
                throw new ArgumentException($"Native library cannot overwrite an output file '{Path.GetFileName(path)}'.");
            if (targets.TryGetValue(target, out var previous) && !comparer.Equals(previous, path))
                throw new ArgumentException($"Managed and native assets have the same output filename '{Path.GetFileName(path)}'.");
            targets[target] = path;
        }
        var references = platformAssemblies.Split(Path.PathSeparator).Concat(externalPaths).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputPath), [syntax], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug, checkOverflow: false,
                deterministic: true, mainTypeName: entryPointTypeName));
        using var assembly = new MemoryStream();
        using var symbols = new MemoryStream();
        var result = compilation.Emit(assembly, symbols,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        // Compile before touching outputs, so diagnostics leave no partial assembly.
        Directory.CreateDirectory(outputDirectory);
        foreach (var (target, path) in targets)
        {
            if (comparer.Equals(path, target)) continue;
            File.Copy(path, target, overwrite: true);
        }
        File.WriteAllText(generatedPath, generatedCode, Encoding.UTF8);
        File.WriteAllBytes(outputPath, assembly.ToArray());
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".pdb"), symbols.ToArray());
        File.WriteAllText(Path.ChangeExtension(outputPath, ".runtimeconfig.json"),
            JsonSerializer.Serialize(new
            {
                runtimeOptions = new
                {
                    tfm = "net10.0",
                    framework = new { name = "Microsoft.NETCore.App", version = "10.0.0" }
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
