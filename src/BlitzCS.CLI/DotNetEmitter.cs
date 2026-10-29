using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace BlitzCS.CLI;

/// <summary>Emits a framework-dependent .NET 10 console assembly and portable symbols.</summary>
/// <remarks>Artifacts are replaced individually, preserving other hard links to existing files.
/// Preflight assumes stable filesystem paths and does not defend against concurrent hostile changes.</remarks>
public static class DotNetEmitter
{
    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName = null,
        OptimizationLevel optimizationLevel = OptimizationLevel.Release) =>
        Emit(generatedCode, outputPath, entryPointTypeName, null, null, null, optimizationLevel);

    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName,
        IEnumerable<string>? referencePaths, OptimizationLevel optimizationLevel = OptimizationLevel.Release) =>
        Emit(generatedCode, outputPath, entryPointTypeName, referencePaths, null, null, optimizationLevel);

    public static void Emit(string generatedCode, string outputPath, string? entryPointTypeName,
        IEnumerable<string>? referencePaths, IEnumerable<string>? nativeLibraryPaths,
        OptimizationLevel optimizationLevel = OptimizationLevel.Release) =>
        Emit(generatedCode, outputPath, entryPointTypeName, referencePaths, nativeLibraryPaths, null, optimizationLevel);

    internal static void Emit(string generatedCode, string outputPath, string? entryPointTypeName,
        IEnumerable<string>? referencePaths, IEnumerable<string>? nativeLibraryPaths, IEnumerable<string>? inputPaths,
        OptimizationLevel optimizationLevel = OptimizationLevel.Release)
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
        var protectedPaths = new HashSet<string>((inputPaths ?? [])
            .Concat(syntax.GetLineMappings().Select(mapping => mapping.MappedSpan.Path)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path)))
            .Select(CanonicalPath), comparer);
        foreach (var path in platformAssemblies.Split(Path.PathSeparator)
                     .Concat(externalPaths).Concat(nativePaths))
            protectedPaths.Add(CanonicalPath(path));
        // The host also needs its app configuration and native/managed dependencies.
        foreach (var path in externalPaths)
            foreach (var extension in new[] { ".pdb", ".deps.json", ".runtimeconfig.json", ".generated.cs" })
                protectedPaths.Add(CanonicalPath(Path.ChangeExtension(path, extension)));
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(DotNetEmitter).Assembly.Location)! }
                     .Distinct(comparer))
            foreach (var path in Directory.EnumerateFiles(directory))
                protectedPaths.Add(CanonicalPath(path));
        var artifactPaths = reservedTargets.Select(CanonicalPath).ToArray();
        if (artifactPaths.Distinct(comparer).Count() != artifactPaths.Length || artifactPaths.Any(protectedPaths.Contains))
            throw new ArgumentException("Cannot overwrite compiler or external input files with an output file.");
        foreach (var target in reservedTargets)
            if (Directory.Exists(target))
                throw new ArgumentException("Output file cannot overwrite a directory.");
        reservedTargets = new HashSet<string>(artifactPaths, comparer);
        var targets = new Dictionary<string, string>(comparer);
        foreach (var path in externalPaths)
        {
            var target = Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileName(path)));
            if (reservedTargets.Contains(CanonicalPath(target)))
                throw new ArgumentException("External assembly cannot overwrite the output assembly.");
            if (targets.TryGetValue(target, out var previous) && !comparer.Equals(previous, path))
                throw new ArgumentException($"External assemblies have the same output filename '{Path.GetFileName(path)}'.");
            targets[target] = path;
        }
        foreach (var path in nativePaths)
        {
            var target = Path.GetFullPath(Path.Combine(outputDirectory, Path.GetFileName(path)));
            if (reservedTargets.Contains(CanonicalPath(target)))
                throw new ArgumentException($"Native library cannot overwrite an output file '{Path.GetFileName(path)}'.");
            if (targets.TryGetValue(target, out var previous) && !comparer.Equals(previous, path))
                throw new ArgumentException($"Managed and native assets have the same output filename '{Path.GetFileName(path)}'.");
            targets[target] = path;
        }
        var copiedTargets = new HashSet<string>(comparer);
        foreach (var (target, path) in targets)
        {
            var canonicalTarget = CanonicalPath(target);
            if (!copiedTargets.Add(canonicalTarget))
                throw new ArgumentException("Copied assets must have distinct output files.");
            if (!comparer.Equals(canonicalTarget, CanonicalPath(path)) && protectedPaths.Contains(canonicalTarget))
                throw new ArgumentException("Copied assets cannot overwrite compiler or external input files.");
            if (Directory.Exists(target))
                throw new ArgumentException("Copied asset cannot overwrite a directory.");
        }
        var references = platformAssemblies.Split(Path.PathSeparator).Concat(externalPaths).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputPath), [syntax], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                optimizationLevel: optimizationLevel, checkOverflow: false,
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
            if (comparer.Equals(CanonicalPath(path), CanonicalPath(target))) continue;
            Publish(target, temporary => File.Copy(path, temporary));
        }
        Publish(generatedPath, temporary => File.WriteAllText(temporary, generatedCode, Encoding.UTF8));
        Publish(outputPath, temporary => File.WriteAllBytes(temporary, assembly.ToArray()));
        Publish(Path.ChangeExtension(outputPath, ".pdb"), temporary => File.WriteAllBytes(temporary, symbols.ToArray()));
        Publish(Path.ChangeExtension(outputPath, ".runtimeconfig.json"), temporary => File.WriteAllText(temporary,
            JsonSerializer.Serialize(new
            {
                runtimeOptions = new
                {
                    tfm = "net10.0",
                    framework = new { name = "Microsoft.NETCore.App", version = "10.0.0" }
                }
            }, new JsonSerializerOptions { WriteIndented = true })));
    }

    private static void Publish(string target, Action<string> write)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".blitzcs-{Guid.NewGuid():N}.tmp");
        try
        {
            write(temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var component in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return current;
    }
}
