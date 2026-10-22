using System.Reflection;
using System.Text.Json;
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
        string Read(JsonElement item, string key) => item.GetProperty(key).GetString()
            ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
        var boundItems = items.Where(item => item.TryGetProperty("method", out var method) && method.ValueKind != JsonValueKind.Null).ToArray();
        var assemblyPaths = boundItems.Select(item => Path.GetFullPath(Read(item, "assembly"), Path.GetDirectoryName(catalogPath)!)).ToArray();
        // Each compilation can bind a different assembly with the same simple name.
        // Unload is requested after metadata inspection; MethodInfo keeps it alive
        // only while the returned registry is observable.
        var context = new ExternalAssemblyLoadContext();
        try
        {
        context.Discover(assemblyPaths);
        var byItem = boundItems.Zip(assemblyPaths).ToDictionary(pair => pair.First, pair => pair.Second);
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
                var type = assembly.GetType(Read(item, "type"), throwOnError: true)!;
                var methodName = Read(item, "method");
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
        return new(ExternalFunctionRegistry.FromDeclarations(signatures, functions), nativeLibraries);
        }
        finally { context.Dispose(); }
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
    IReadOnlyList<string> NativeLibraries);
