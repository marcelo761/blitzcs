using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlitzCS.CLI;

/// <summary>Composes catalogs before method inspection; every relative path keeps its declaring catalog's base.</summary>
internal static class ExternalCatalogDocument
{
    internal sealed record Result(JsonElement Root, IReadOnlyList<string> InputPaths);

    internal static Result Read(string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
        var active = new HashSet<string>(comparer);
        var inputs = new List<string>();
        var functions = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var native = new JsonArray();
        var binary = new JsonArray();
        var result = new JsonObject();
        Visit(Path.GetFullPath(path));
        result["functions"] = new JsonArray(functions.Values.Select(f => (JsonNode)f).ToArray());
        result["nativeLibraries"] = native;
        result["binaryLibraries"] = binary;
        return new(JsonSerializer.SerializeToElement(result), inputs.AsReadOnly());

        void Visit(string file)
        {
            if (active.Contains(file)) throw new ArgumentException($"External catalog profile cycle involving '{file}'.");
            if (!visited.Add(file)) return; // diamond imports refer to one declaration, not duplicate functions
            if (inputs.Count >= 256) throw new ArgumentException("External catalog profiles exceed the limit of 256 files.");
            active.Add(file);
            inputs.Add(file);
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new ArgumentException($"External catalog '{file}' must be an object.");
            var directory = Path.GetDirectoryName(file)!;
            if (root["profiles"] is { } profiles)
                foreach (var profile in Array(profiles, "profiles"))
                    Visit(FullPath(profile, "profiles", directory));
            foreach (var key in new[] { "binaryProfile", "binaryTarget" })
            {
                if (root[key] is not { } value) continue;
                if (result[key] is { } prior && !JsonNode.DeepEquals(prior, value))
                    throw new ArgumentException($"External catalogs disagree about '{key}'.");
                result[key] = value.DeepClone();
            }
            if (root["nativeLibraries"] is { } natives)
                foreach (var library in Array(natives, "nativeLibraries")) native.Add(FullPath(library, "nativeLibraries", directory));
            if (root["binaryLibraries"] is { } binaries)
                foreach (var entry in Array(binaries, "binaryLibraries"))
                {
                    if (entry is JsonValue) binary.Add(FullPath(entry, "binaryLibraries", directory));
                    else if (entry is JsonObject spec)
                    {
                        var copy = (JsonObject)spec.DeepClone();
                        // BinaryLibrarySpec uses 'path' for explicit library objects.
                        if (copy["path"] is { } library) copy["path"] = FullPath(library, "binaryLibraries.path", directory);
                        binary.Add(copy);
                    }
                    else throw new ArgumentException("External catalog 'binaryLibraries' entries must be paths or objects.");
                }
            if (root["functions"] is { } items)
                foreach (var node in Array(items, "functions"))
                {
                    var item = node as JsonObject ?? throw new ArgumentException("External catalog functions must be objects.");
                    var copy = (JsonObject)item.DeepClone();
                    if (copy["assembly"] is { } assembly) copy["assembly"] = FullPath(assembly, "assembly", directory);
                    if (copy["binary"] is JsonObject binding && binding["library"] is { } library)
                        binding["library"] = FullPath(library, "binary.library", directory);
                    var name = copy["name"]?.GetValue<string>() ?? throw new ArgumentException("External function 'name' is required.");
                    if (functions.TryGetValue(name, out var declaration))
                    {
                        if (!JsonNode.DeepEquals(declaration["parameters"], copy["parameters"]) ||
                            copy["returnType"] is { } returned && !JsonNode.DeepEquals(declaration["returnType"], returned))
                            throw new ArgumentException($"External function '{name}' conflicts with its profile signature.");
                        if (Bound(declaration) || !Bound(copy))
                            throw new ArgumentException($"Duplicate external BASIC name '{name}'; a profile declaration may only be completed by one implementation.");
                        foreach (var key in new[] { "defaults", "contract" })
                            if (copy.ContainsKey(key) && declaration[key] is { } original && !JsonNode.DeepEquals(original, copy[key]))
                                throw new ArgumentException($"External function '{name}' cannot change profile {key}.");
                        foreach (var pair in copy)
                            declaration[pair.Key] = pair.Value?.DeepClone();
                    }
                    else functions.Add(name, copy);
                }
            active.Remove(file);
        }
    }

    private static bool Bound(JsonObject item) => item["method"] is not null || item["binary"] is not null;
    private static JsonArray Array(JsonNode node, string field) => node as JsonArray
        ?? throw new ArgumentException($"External catalog '{field}' must be an array.");
    private static string FullPath(JsonNode? node, string field, string directory)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"External catalog '{field}' entries must be nonempty paths.");
        return Path.GetFullPath(path, directory);
    }
}
