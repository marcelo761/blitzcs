using System.Reflection;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal static class ExternalCatalog
{
    internal static ExternalCatalogResult Load(string catalogPath)
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
        var binaryLibraries = root.TryGetProperty("binaryLibraries", out var binaries) && binaries.ValueKind == JsonValueKind.Array
            ? binaries.EnumerateArray().Select(item => Path.GetFullPath(
                item.GetString() ?? throw new ArgumentException("External catalog 'binaryLibraries' entries cannot be null."),
                Path.GetDirectoryName(catalogPath)!)).ToArray()
            : [];
        var binaryProfile = root.TryGetProperty("binaryProfile", out var profile) && profile.ValueKind == JsonValueKind.String
            ? profile.GetString() : null;
        string Read(JsonElement item, string key) => item.GetProperty(key).GetString()
            ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
        var catalogDirectory = Path.GetDirectoryName(catalogPath)!;
        var binaryItems = items.Where(item => item.TryGetProperty("binary", out var binary) && binary.ValueKind != JsonValueKind.Null).ToArray();
        var binaryBindings = binaryItems.Select(item => ReadBinding(item, catalogDirectory)).ToArray();
        DirectoryStaging? staging = null;
        string? bindingAssembly = null;
        if (binaryBindings.Length > 0)
        {
            staging = new DirectoryStaging();
            try { bindingAssembly = BinaryBindingGenerator.Generate(binaryBindings, staging.Path); }
            catch { staging.Dispose(); throw; }
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            binaryLibraries = binaryLibraries.Concat(binaryBindings.Select(b => b.LibraryPath)).Distinct(comparer).ToArray();
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
        return new(ExternalFunctionRegistry.FromDeclarations(signatures, functions), nativeLibraries.Concat(binaryLibraries).ToArray(), binaryLibraries, binaryProfile, binaryBindings, staging);
        }
        catch { staging?.Dispose(); throw; }
        finally { context.Dispose(); }
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
    private static BinaryBinding ReadBinding(JsonElement item, string catalogDirectory)
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
        var image = PeReader.Read(File.ReadAllBytes(library));
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
        return new BinaryBinding(name, library, candidates[0].Name!, convention, parameters, returnType);
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
    IDisposable? Staging = null) : IDisposable
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
