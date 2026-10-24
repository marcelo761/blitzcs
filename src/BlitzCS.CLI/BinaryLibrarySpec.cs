using System.Reflection;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.CLI;

/// <summary>
/// One binary library of a catalog and its options. A <c>binaryLibraries</c> entry is either a path string (all defaults) or an object:
/// <c>path</c>, <c>unpack</c>, <c>win32</c> (true/false; absent = auto), <c>runEntryPoint</c> (default true), <c>defaultConvention</c> ("cdecl"),
/// <c>exportConventions</c> ({"name": "cdecl" | "stdcall:N"}) and <c>unboundImports</c> ("fault" default, or "error"). Unknown keys are rejected.
/// </summary>
internal sealed record BinaryLibrarySpec(string Path)
{
    internal const string UnpackerAssembly = "BlitzCS.BinaryCompiler.Unpack";

    public bool Unpack { get; init; }
    /// <summary>true: always provide the Win32 environment; false: never; null: only when the library imports from a DLL that is not a sibling.</summary>
    public bool? Win32 { get; init; }
    public bool RunEntryPoint { get; init; } = true;
    public bool DefaultCdecl { get; init; }
    public IReadOnlyDictionary<string, ExportSignature> ExportConventions { get; init; } = new Dictionary<string, ExportSignature>(StringComparer.Ordinal);
    public bool UnboundImportsAreErrors { get; init; }
    public bool Explicit { get; init; }
    public BinaryPreprocessResult? Preprocessed { get; init; }

    /// <summary>The PE that is analysed and translated: the derived image of a preprocessed library, else the library itself.</summary>
    public string EffectivePath => Preprocessed?.DerivedPath ?? Path;
    public string FileName => System.IO.Path.GetFileName(Path);

    private static readonly string[] Keys = ["path", "unpack", "win32", "runEntryPoint", "defaultConvention", "exportConventions", "unboundImports"];

    internal static BinaryLibrarySpec Parse(JsonElement entry, string catalogDirectory)
    {
        if (entry.ValueKind == JsonValueKind.String)
            return new BinaryLibrarySpec(System.IO.Path.GetFullPath(entry.GetString()!, catalogDirectory));
        if (entry.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("External catalog 'binaryLibraries' entries must be a path string or an object.");
        foreach (var property in entry.EnumerateObject())
            if (!Keys.Contains(property.Name))
                throw new ArgumentException($"External catalog 'binaryLibraries' entry has unknown key '{property.Name}'; expected {string.Join(", ", Keys)}.");
        if (!entry.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathElement.GetString()))
            throw new ArgumentException("External catalog 'binaryLibraries' object entries require a 'path' string.");
        var path = System.IO.Path.GetFullPath(pathElement.GetString()!, catalogDirectory);
        var label = System.IO.Path.GetFileName(path);

        bool? Bool(string key)
        {
            if (!entry.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
                : throw new ArgumentException($"Binary library '{label}': '{key}' must be true or false.");
        }

        var defaultCdecl = false;
        if (entry.TryGetProperty("defaultConvention", out var defaultElement) && defaultElement.ValueKind != JsonValueKind.Null)
        {
            if (defaultElement.ValueKind != JsonValueKind.String || !string.Equals(defaultElement.GetString(), "cdecl", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Binary library '{label}': defaultConvention accepts only \"cdecl\"; declare other exports individually with exportConventions or binary.convention.");
            defaultCdecl = true;
        }
        var unbound = false;
        if (entry.TryGetProperty("unboundImports", out var unboundElement) && unboundElement.ValueKind != JsonValueKind.Null)
            unbound = unboundElement.ValueKind == JsonValueKind.String ? unboundElement.GetString() switch
            {
                "fault" => false, "error" => true,
                _ => throw new ArgumentException($"Binary library '{label}': unboundImports must be \"fault\" or \"error\".")
            } : throw new ArgumentException($"Binary library '{label}': unboundImports must be \"fault\" or \"error\".");
        var conventions = new Dictionary<string, ExportSignature>(StringComparer.Ordinal);
        if (entry.TryGetProperty("exportConventions", out var map) && map.ValueKind != JsonValueKind.Null)
        {
            if (map.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Binary library '{label}': exportConventions must be an object.");
            foreach (var property in map.EnumerateObject())
            {
                if (conventions.ContainsKey(property.Name))
                    throw new ArgumentException($"Binary library '{label}': exportConventions declares '{property.Name}' twice.");
                conventions[property.Name] = ParseSignature(label, property.Name, property.Value);
            }
        }
        return new BinaryLibrarySpec(path)
        {
            Explicit = true, Unpack = Bool("unpack") ?? false, Win32 = Bool("win32"), RunEntryPoint = Bool("runEntryPoint") ?? true,
            DefaultCdecl = defaultCdecl, ExportConventions = conventions, UnboundImportsAreErrors = unbound,
        };
    }

    private static ExportSignature ParseSignature(string label, string export, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToLowerInvariant() : "";
        if (text == "cdecl") return ExportSignature.Cdecl;
        if (text.StartsWith("stdcall:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(8), out var count) && count is >= 0 and <= 256)
            return ExportSignature.Stdcall(count);
        throw new ArgumentException($"Binary library '{label}': exportConventions['{export}'] must be \"cdecl\" or \"stdcall:<argument count>\" (register conventions cannot be bridged between libraries).");
    }

    /// <summary>
    /// Runs the opt-in preprocessor for <c>unpack</c> libraries (loaded by name from the compiler directory, only on request). <paramref name="loadBase"/> is the guest base
    /// the build policy chose for the target BEFORE unpacking (null = preferred base); the derived image is valid for exactly that base.
    /// </summary>
    internal BinaryLibrarySpec Preprocess(string workDirectory, uint? loadBase = null)
    {
        if (!Unpack) return this;
        if (!File.Exists(Path)) throw new FileNotFoundException($"Binary library '{FileName}' does not exist.", Path);
        if (!BinaryBuild.IsPe32X86(Path)) throw new ArgumentException($"Binary library '{FileName}' is not a PE32 x86 image.");
        var preprocessor = LoadPreprocessor("unpack");
        try { return this with { Preprocessed = preprocessor.Preprocess(Path, workDirectory, loadBase) }; }
        catch (InvalidOperationException error) { throw new InvalidOperationException($"Binary library '{FileName}': {error.Message}", error); }
    }

    private static IBinaryPreprocessor LoadPreprocessor(string id)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(BinaryLibrarySpec).Assembly.Location) ?? AppContext.BaseDirectory, UnpackerAssembly + ".dll");
        if (!File.Exists(path))
            throw new InvalidOperationException($"'unpack' was requested but {UnpackerAssembly}.dll is not installed next to the compiler ('{path}').");
        var assembly = Assembly.LoadFrom(path);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IBinaryPreprocessor).IsAssignableFrom(type)) continue;
            if (Activator.CreateInstance(type, Enumerable.Repeat<object?>(null, type.GetConstructors().Max(c => c.GetParameters().Length)).ToArray()) is IBinaryPreprocessor candidate && candidate.Id == id)
                return candidate;
        }
        throw new InvalidOperationException($"{UnpackerAssembly}.dll contains no binary preprocessor with id '{id}'.");
    }
}
