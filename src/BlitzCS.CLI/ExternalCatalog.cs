using System.Reflection;
using System.Text.Json;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal static class ExternalCatalog
{
    internal static ExternalFunctionRegistry Load(string catalogPath)
    {
        catalogPath = Path.GetFullPath(catalogPath);
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var functions = new List<ExternalFunction>();
        var items = document.RootElement.GetProperty("functions").EnumerateArray().ToArray();
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
        return new(functions);
        }
        finally { context.Dispose(); }
    }
}
