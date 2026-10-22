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
        var items = root.GetProperty("functions").EnumerateArray().ToArray();
        var nativeLibraries = root.TryGetProperty("nativeLibraries", out var libraries)
            && libraries.ValueKind != JsonValueKind.Null
            ? libraries.EnumerateArray().Select(item => Path.GetFullPath(
                item.GetString() ?? throw new ArgumentException("External catalog 'nativeLibraries' entries cannot be null."),
                Path.GetDirectoryName(catalogPath)!)).ToArray()
            : [];
        string Read(JsonElement item, string key) => item.GetProperty(key).GetString()
            ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
        var assemblyPaths = items.Select(item => Path.GetFullPath(Read(item, "assembly"), Path.GetDirectoryName(catalogPath)!)).ToArray();
        // Each compilation can bind a different assembly with the same simple name.
        // Unload is requested after metadata inspection; MethodInfo keeps it alive
        // only while the returned registry is observable.
        var context = new ExternalAssemblyLoadContext();
        try
        {
        context.Discover(assemblyPaths);
        foreach (var (item, assemblyPath) in items.Zip(assemblyPaths))
        {
            var assembly = context.LoadAssembly(assemblyPath);
            var type = assembly.GetType(Read(item, "type"), throwOnError: true)!;
            var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => p.GetString() switch
            {
                "Integer" => typeof(int), "Float" => typeof(float), "String" => typeof(string),
                _ => throw new ArgumentException("External parameter types must be Integer, Float or String.")
            }).ToArray();
            var methodName = Read(item, "method");
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, parameters)
                ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
            functions.Add(new ExternalFunction(Read(item, "name"), method));
        }
        return new(new ExternalFunctionRegistry(functions), nativeLibraries);
        }
        finally { context.Dispose(); }
    }
}

internal sealed record ExternalCatalogResult(
    ExternalFunctionRegistry Functions,
    IReadOnlyList<string> NativeLibraries);
