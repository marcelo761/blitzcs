using System.Reflection;
using System.Runtime.Loader;
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
        // Each compilation can bind a different assembly with the same simple name.
        // Unload is requested after metadata inspection; MethodInfo keeps it alive
        // only while the returned registry is observable.
        var context = new AssemblyLoadContext("BlitzCatalog-" + Guid.NewGuid(), isCollectible: true);
        try
        {
        foreach (var item in document.RootElement.GetProperty("functions").EnumerateArray())
        {
            string Read(string key) => item.GetProperty(key).GetString()
                ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
            var assemblyPath = Path.GetFullPath(Read("assembly"), Path.GetDirectoryName(catalogPath)!);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(Read("type"), throwOnError: true)!;
            var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => p.GetString() switch
            {
                "Integer" => typeof(int), "Float" => typeof(float), "String" => typeof(string),
                _ => throw new ArgumentException("External parameter types must be Integer, Float or String.")
            }).ToArray();
            var method = type.GetMethod(Read("method"), BindingFlags.Public | BindingFlags.Static, parameters)
                ?? throw new ArgumentException($"External method '{type.FullName}.{Read("method")}' with the declared signature does not exist.");
            functions.Add(new ExternalFunction(Read("name"), method));
        }
        return new(functions);
        }
        finally { context.Unload(); }
    }
}
