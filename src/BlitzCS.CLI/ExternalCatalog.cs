using System.Reflection;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal static class ExternalCatalog
{
    internal static ExternalCatalogResult Load(string catalogPath, string? target = null)
    {
        catalogPath = Path.GetFullPath(catalogPath);
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var root = document.RootElement;
        var functions = new List<ExternalFunction>();
        var signatures = new List<ExternalFunctionSignature>();
        var items = root.GetProperty("functions").EnumerateArray().ToArray();
        var nativeLibraries = root.TryGetProperty("nativeLibraries", out var libraries)
            && libraries.ValueKind != JsonValueKind.Null
            ? libraries.EnumerateArray().Select(item => Path.GetFullPath(
                item.GetString() ?? throw new ArgumentException("External catalog 'nativeLibraries' entries cannot be null."),
                Path.GetDirectoryName(catalogPath)!)).ToArray()
            : [];
        var binaryProfile = root.TryGetProperty("binaryProfile", out var profile) && profile.ValueKind == JsonValueKind.String
            ? profile.GetString() : null;
        var binaryTarget = root.TryGetProperty("binaryTarget", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
            ? BinaryTargets.TryNormalize(targetElement.GetString()) ?? throw new ArgumentException($"Unknown binaryTarget '{targetElement.GetString()}'; expected linux-x64 or windows-x64.") : null;
        string Read(JsonElement item, string key) => item.GetProperty(key).GetString()
            ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
        var catalogDirectory = Path.GetDirectoryName(catalogPath)!;
        var binaryItems = items.Where(item => item.TryGetProperty("binary", out var binary) && binary.ValueKind != JsonValueKind.Null).ToArray();
        var specs = ReadLibrarySpecs(root, binaryItems, catalogDirectory, BinaryTranslationStep.ResolveTarget(binaryTarget, target));
        var binaryLibraries = specs.Select(spec => spec.Path).ToArray();
        var binaryBindings = binaryItems.Select(item => ReadBinding(item, catalogDirectory, specs)).ToArray();
        DirectoryStaging? staging = null;
        string? bindingAssembly = null;
        if (binaryBindings.Length > 0)
        {
            staging = new DirectoryStaging();
            try { bindingAssembly = BinaryBindingGenerator.Generate(binaryBindings, specs, staging.Path); }
            catch { staging.Dispose(); throw; }
        }
        var boundItems = items.Where(item => item.TryGetProperty("method", out var method) && method.ValueKind != JsonValueKind.Null
            || binaryItems.Contains(item)).ToArray();
        var assemblyPaths = boundItems.Select(item => binaryItems.Contains(item) ? bindingAssembly!
            : Path.GetFullPath(Read(item, "assembly"), catalogDirectory)).ToArray();
        // Each compilation can bind a different assembly with the same simple name.
        // Unload is requested after metadata inspection; MethodInfo keeps it alive
        // only while the returned registry is observable.
        var context = new ExternalAssemblyLoadContext();
        try
        {
        context.Discover(assemblyPaths);
        var byItem = boundItems.Zip(assemblyPaths).ToDictionary(pair => pair.First, pair => pair.Second);
        var libraryIndexes = BinaryBindingGenerator.Libraries(binaryBindings);
        foreach (var item in items)
        {
            var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => p.GetString() switch
            {
                "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
                _ => throw new ArgumentException("External parameter types must be Integer, Float or String.")
            }).ToArray();
            ExternalFunction? function = null;
            if (byItem.TryGetValue(item, out var assemblyPath))
            {
                var assembly = context.LoadAssembly(assemblyPath);
                var binaryIndex = Array.IndexOf(binaryItems, item);
                var typeName = binaryIndex >= 0 ? BinaryBindingGenerator.TypeName(IndexOf(libraryIndexes, binaryBindings[binaryIndex].LibraryPath)) : Read(item, "type");
                var type = assembly.GetType(typeName, throwOnError: true)!;
                var methodName = binaryIndex >= 0 ? BinaryBindingGenerator.MethodName(binaryBindings[binaryIndex].BasicName) : Read(item, "method");
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, parameters.Select(ToSystemType).ToArray())
                    ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
                function = new ExternalFunction(Read(item, "name"), method);
            }
            var returnType = item.TryGetProperty("returnType", out var returnTypeElement)
                ? returnTypeElement.GetString() switch
                {
                    "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
                    "Void" => ExternalValueType.Void,
                    _ => throw new ArgumentException("External return types must be Integer, Float, String or Void.")
                }
                : function?.Signature.ReturnType ?? throw new ArgumentException("External declarations without a method require a returnType.");
            object[]? defaults = null;
            if (item.TryGetProperty("defaults", out var defaultsElement))
            {
                if (defaultsElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("External catalog 'defaults' must be an array of trailing typed constants.");
                var values = defaultsElement.EnumerateArray().ToArray();
                if (values.Length > parameters.Length)
                    throw new ArgumentException("External catalog 'defaults' cannot exceed the parameter count.");
                defaults = values.Select((value, index) => ReadDefault(value,
                    parameters[parameters.Length - values.Length + index])).ToArray();
            }
            var signature = new ExternalFunctionSignature(Read(item, "name"), returnType, parameters, defaults);
            if (function is not null && (function.Signature.ReturnType != signature.ReturnType || !function.Signature.ParameterTypes.SequenceEqual(signature.ParameterTypes)))
                throw new ArgumentException($"External method '{function.Name}' does not match its declared signature.");
            signatures.Add(signature);
            if (function is not null) functions.Add(function);
        }
        return new(ExternalFunctionRegistry.FromDeclarations(signatures, functions), nativeLibraries.Concat(binaryLibraries).ToArray(), binaryLibraries, binaryProfile, binaryBindings, staging, specs, binaryTarget);
        }
        catch { staging?.Dispose(); throw; }
        finally { context.Dispose(); }
    }


    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Merges the <c>binaryLibraries</c> entries with the libraries named by function bindings (implicit entries with defaults), rejects duplicate files
    /// and duplicate output file names, and runs the opt-in preprocessors (<c>unpack</c>).
    /// </summary>
    private static List<BinaryLibrarySpec> ReadLibrarySpecs(JsonElement root, JsonElement[] binaryItems, string catalogDirectory, string targetOs)
    {
        var specs = new List<BinaryLibrarySpec>();
        if (root.TryGetProperty("binaryLibraries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                var spec = BinaryLibrarySpec.Parse(entry, catalogDirectory);
                var existing = specs.FindIndex(candidate => PathEquals(candidate.Path, spec.Path));
                if (existing >= 0)
                {
                    if (specs[existing].Explicit || spec.Explicit)
                        throw new ArgumentException($"External catalog lists binary library '{spec.FileName}' more than once; its options would be ambiguous.");
                    continue;
                }
                specs.Add(spec);
            }
        else if (root.TryGetProperty("binaryLibraries", out var other) && other.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("External catalog 'binaryLibraries' must be an array.");
        foreach (var item in binaryItems)
        {
            var binary = item.GetProperty("binary");
            if (binary.ValueKind != JsonValueKind.Object || !binary.TryGetProperty("library", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var path = Path.GetFullPath(name.GetString()!, catalogDirectory);
            if (!specs.Any(candidate => PathEquals(candidate.Path, path))) specs.Add(new BinaryLibrarySpec(path));
        }
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
            if (names.TryGetValue(spec.FileName, out var other) && !PathEquals(other, spec.Path))
                throw new ArgumentException($"Binary libraries '{other}' and '{spec.Path}' have the same file name; modules are published side by side and linked by name.");
            else names[spec.FileName] = spec.Path;
        var work = BinaryCache.Directory();
        var unpackBases = BinaryTranslationStep.PlanUnpackBases(specs, targetOs);
        for (var i = 0; i < specs.Count; i++) specs[i] = specs[i].Preprocess(work, unpackBases[i]);
        return specs;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        throw new InvalidOperationException("Binary library index not found.");
    }

    private static ExternalValueType ParseType(string? name, bool allowVoid) => name switch
    {
        "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
        "Void" when allowVoid => ExternalValueType.Void,
        _ => throw new ArgumentException("External parameter types must be Integer, Float or String.")
    };

    /// <summary>Validates one catalog 'binary' entry against the PE export table (no translation needed).</summary>
    private static BinaryBinding ReadBinding(JsonElement item, string catalogDirectory, IReadOnlyList<BinaryLibrarySpec> specs)
    {
        var name = item.GetProperty("name").GetString() ?? throw new ArgumentException("External catalog 'name' cannot be null.");
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"External BASIC name '{name}' requires a letter followed by letters, digits or underscores.");
        if (item.TryGetProperty("method", out var method) && method.ValueKind != JsonValueKind.Null
            || item.TryGetProperty("assembly", out var assembly) && assembly.ValueKind != JsonValueKind.Null)
            throw new ArgumentException($"External function '{name}' cannot combine 'binary' with a managed 'assembly'/'method' binding.");
        var binary = item.GetProperty("binary");
        if (binary.ValueKind != JsonValueKind.Object) throw new ArgumentException($"External function '{name}': 'binary' must be an object.");
        string Field(string key) => binary.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"External function '{name}': binary.{key} is required.");
        var libraryName = Field("library");
        var intrinsic = binary.TryGetProperty("intrinsic", out var intrinsicElement) && intrinsicElement.ValueKind == JsonValueKind.String ? intrinsicElement.GetString() : null;
        if (intrinsic is not null)
        {
            if (binary.TryGetProperty("export", out _)) throw new ArgumentException($"External function '{name}': binary.intrinsic cannot be combined with binary.export.");
            var shapes = new Dictionary<string, (int Parameters, bool Returns)> { ["alloc"] = (1, true), ["free"] = (1, false), ["peek8"] = (1, true), ["peek32"] = (1, true), ["poke8"] = (2, false), ["poke32"] = (2, false) };
            if (!shapes.TryGetValue(intrinsic, out var shape))
                throw new ArgumentException($"External function '{name}': unknown binary.intrinsic '{intrinsic}'; expected {string.Join(", ", shapes.Keys)}.");
            var declared = item.GetProperty("parameters").EnumerateArray().Select(p => ParseType(p.GetString(), false)).ToArray();
            var returned = item.TryGetProperty("returnType", out var r) ? ParseType(r.GetString(), true) : ExternalValueType.Void;
            if (declared.Length != shape.Parameters || declared.Any(t => t != ExternalValueType.Integer) || returned != (shape.Returns ? ExternalValueType.Integer : ExternalValueType.Void))
                throw new ArgumentException($"External function '{name}': intrinsic '{intrinsic}' takes {shape.Parameters} Integer parameter(s) and returns {(shape.Returns ? "Integer" : "Void")}.");
            var libraryPath = Path.GetFullPath(libraryName, catalogDirectory);
            if (!File.Exists(libraryPath)) throw new FileNotFoundException($"Binary library '{libraryName}' for '{name}' does not exist.", libraryPath);
            return new BinaryBinding(name, libraryPath, "", GuestCallingConvention.Unknown, declared, returned) { Intrinsic = intrinsic };
        }
        var requested = Field("export");
        var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => ParseType(p.GetString(), false)).ToArray();
        if (!item.TryGetProperty("returnType", out var returnElement))
            throw new ArgumentException($"External function '{name}': binary functions require a returnType.");
        var returnType = ParseType(returnElement.GetString(), true);
        if (returnType == ExternalValueType.String)
            throw new ArgumentException($"External function '{name}' ({libraryName}!{requested}): string return from binary library unsupported; the pointer ownership is not known.");
        var convention = GuestCallingConvention.Unknown;
        if (binary.TryGetProperty("convention", out var conventionElement) && conventionElement.ValueKind != JsonValueKind.Null)
            convention = conventionElement.GetString()?.ToLowerInvariant() switch
            {
                "cdecl" => GuestCallingConvention.Cdecl, "stdcall" => GuestCallingConvention.Stdcall,
                "thiscall" => GuestCallingConvention.Thiscall, "fastcall" => GuestCallingConvention.Fastcall,
                _ => throw new ArgumentException($"External function '{name}': binary.convention must be 'cdecl', 'stdcall', 'thiscall' or 'fastcall'.")
            };
        if (convention == GuestCallingConvention.Thiscall && (parameters.Length == 0 || parameters[0] != ExternalValueType.Integer))
            throw new ArgumentException($"External function '{name}': a thiscall binding needs an Integer first parameter, which is passed as 'this' in ECX.");
        var library = Path.GetFullPath(libraryName, catalogDirectory);
        if (!File.Exists(library)) throw new FileNotFoundException($"Binary library '{libraryName}' for '{name}' does not exist.", library);
        if (!BinaryBuild.IsPe32X86(library)) throw new ArgumentException($"Binary library '{libraryName}' is not a PE32 x86 image.");
        var spec = specs.First(candidate => PathEquals(candidate.Path, library));
        var image = PeReader.Read(File.ReadAllBytes(spec.EffectivePath));
        var exact = image.Exports.Where(e => e.Name == requested).ToArray();
        var candidates = exact.Length > 0 ? exact : image.Exports.Where(e => e.Name is not null
            && (e.Name == "_" + requested || e.Name.StartsWith("_" + requested + "@", StringComparison.Ordinal) || e.Name.StartsWith(requested + "@", StringComparison.Ordinal)
                || e.Name.StartsWith("@" + requested + "@", StringComparison.Ordinal))).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException($"Binary library '{libraryName}' has no export '{requested}' (declared by '{name}').");
        if (candidates.Length > 1)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is ambiguous ({string.Join(", ", candidates.Select(c => c.Name + "#" + c.Ordinal))}); name the exact export.");
        if (candidates[0].ForwarderTarget is { } forwarder)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is a forwarder to '{forwarder}', which is unsupported.");
        var exportName = candidates[0].Name!;
        if ((spec.ExportConventions.TryGetValue(exportName, out var mapped) || spec.ExportConventions.TryGetValue("#" + candidates[0].Ordinal, out mapped)))
        {
            if (convention != GuestCallingConvention.Unknown && convention != mapped.Convention)
                throw new ArgumentException($"External function '{name}' ({libraryName}!{exportName}): binary.convention says {convention} but the library's exportConventions says {mapped.Convention}; declare it once.");
            if (mapped.Convention == GuestCallingConvention.Stdcall && mapped.ArgCount != parameters.Length)
                throw new ArgumentException($"External function '{name}' ({libraryName}!{exportName}): exportConventions says stdcall:{mapped.ArgCount} but the declaration has {parameters.Length} parameter(s).");
            convention = mapped.Convention;
        }
        return new BinaryBinding(name, library, exportName, convention, parameters, returnType, spec.DefaultCdecl);
    }

    private static object ReadDefault(JsonElement value, ExternalValueType type) => type switch
    {
        ExternalValueType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        ExternalValueType.Float when value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var single) && float.IsFinite(single) => single,
        ExternalValueType.String when value.ValueKind == JsonValueKind.String => value.GetString()!,
        _ => throw new ArgumentException("External catalog 'defaults' must match trailing parameter types: Int32, finite Single or String.")
    };

    private static Type ToSystemType(ExternalValueType type) => type switch
    {
        ExternalValueType.Integer => typeof(int), ExternalValueType.Float => typeof(float),
        ExternalValueType.String => typeof(string), _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

internal sealed record ExternalCatalogResult(
    ExternalFunctionRegistry Functions,
    IReadOnlyList<string> NativeLibraries,
    IReadOnlyList<string>? BinaryLibraries = null,
    string? BinaryProfile = null,
    IReadOnlyList<BinaryBinding>? BinaryBindings = null,
    IDisposable? Staging = null,
    IReadOnlyList<BinaryLibrarySpec>? LibrarySpecs = null,
    string? BinaryTarget = null) : IDisposable
{
    public void Dispose() => Staging?.Dispose();
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class DirectoryStaging : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("blitzcs-binding-").FullName;
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
