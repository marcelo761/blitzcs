using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlitzCS.CLI;

/// <summary>Composes catalogs before method inspection; every relative path keeps its declaring catalog's base.</summary>
internal static class ExternalCatalogDocument
{
    internal sealed record Result(JsonElement Root, IReadOnlyList<string> InputPaths);

    internal static Result Read(string path) => ReadMany([path]);

    internal static Result ReadMany(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
        var active = new HashSet<string>(comparer);
        var inputs = new List<string>();
        var functions = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var legacy = new List<JsonObject>();
        var native = new JsonArray();
        var binary = new JsonArray();
        var result = new JsonObject();
        foreach (var path in paths) Visit(Path.GetFullPath(path));
        foreach (var declaration in legacy) LegacyCatalogComposition.Merge(functions, declaration);
        foreach (var item in functions.Values)
            if (item["bindingCandidate"] is JsonObject candidate && candidate["assembly"] is JsonValue assembly)
                inputs.Add(assembly.GetValue<string>());
        result["functions"] = new JsonArray(functions.Values.Select(f => (JsonNode)f).ToArray());
        result["nativeLibraries"] = native;
        result["binaryLibraries"] = binary;
        return new(JsonSerializer.SerializeToElement(result), inputs.AsReadOnly());

        void Visit(string file, bool fallback = false)
        {
            if (active.Contains(file)) throw new ArgumentException($"External catalog profile cycle involving '{file}'.");
            if (!visited.Add(file)) return; // diamond imports refer to one declaration, not duplicate functions
            if (inputs.Count >= 256) throw new ArgumentException("External catalog profiles exceed the limit of 256 files.");
            active.Add(file);
            inputs.Add(file);
            if (Path.GetExtension(file).Equals(".decls", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var declaration in LegacyDeclarationLoader.Read(file))
                {
                    declaration["catalogSources"] = new JsonArray(JsonValue.Create(file));
                    legacy.Add(declaration);
                }
                active.Remove(file); return;
            }
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new ArgumentException($"External catalog '{file}' must be an object.");
            var directory = Path.GetDirectoryName(file)!;
            if (root["profiles"] is { } profiles)
                foreach (var profile in Array(profiles, "profiles"))
                    Visit(FullPath(profile, "profiles", directory), fallback);
            foreach (var key in new[] { "binaryProfile", "binaryTarget", "blitz3dRuntime" })
            {
                if (root[key] is not { } value) continue;
                // The runtime image path is relative to the catalog that declares it.
                var effective = key == "blitz3dRuntime" && value is JsonValue runtime && runtime.TryGetValue<string>(out var runtimePath)
                    && !string.IsNullOrWhiteSpace(runtimePath)
                    ? JsonValue.Create(Path.GetFullPath(runtimePath, directory))
                    : value.DeepClone();
                if (result[key] is { } prior && !JsonNode.DeepEquals(prior, effective))
                    throw new ArgumentException($"External catalogs disagree about '{key}'.");
                result[key] = effective;
            }
            if (root["userlibs"] is JsonArray userlibs)
            {
                // Each .decls file and its DLL fallback directory stay relative to the declaring catalog.
                var target = result["userlibs"] as JsonArray ?? (JsonArray)(result["userlibs"] = new JsonArray());
                foreach (var entry in userlibs)
                {
                    if (entry is not JsonValue value || !value.TryGetValue<string>(out var declsPath) || string.IsNullOrWhiteSpace(declsPath))
                        throw new ArgumentException("External catalog 'userlibs' entries must be non-empty .decls file path strings.");
                    target.Add(new JsonObject { ["path"] = Path.GetFullPath(declsPath, directory), ["catalogDirectory"] = directory });
                }
            }
            else if (root["userlibs"] is not null)
                throw new ArgumentException("External catalog 'userlibs' must be an array of .decls file paths.");
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
            if (root["declarations"] is { } declarationPaths)
                foreach (var entry in Array(declarationPaths, "declarations"))
                {
                    var path = FullPath(entry, "declarations", directory);
                    if (Directory.Exists(path))
                    {
                        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                            .Where(p => Path.GetExtension(p).Equals(".decls", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal).ToArray();
                        if (files.Length == 0) throw new ArgumentException($"Declaration directory '{path}' contains no .decls files.");
                        foreach (var declarationFile in files) Visit(declarationFile);
                    }
                    else if (Path.GetExtension(path).Equals(".decls", StringComparison.OrdinalIgnoreCase)) Visit(path);
                    else throw new ArgumentException($"Declaration input '{path}' must be a .decls file or directory.");
                }
            if (root["functions"] is { } items)
                foreach (var node in Array(items, "functions"))
                {
                    var item = node as JsonObject ?? throw new ArgumentException("External catalog functions must be objects.");
                    var copy = (JsonObject)item.DeepClone();
                    copy["catalogSources"] = new JsonArray(JsonValue.Create(file));
                    if (fallback) copy["selectionRole"] = "fallback";
                    if (copy["assembly"] is { } assembly) copy["assembly"] = FullPath(assembly, "assembly", directory);
                    if (copy["binary"] is JsonObject binding && binding["library"] is { } library)
                        binding["library"] = FullPath(library, "binary.library", directory);
                    var name = copy["name"]?.GetValue<string>() ?? throw new ArgumentException("External function 'name' is required.");
                    if (functions.TryGetValue(name, out var declaration))
                    {
                        if (fallback && (Bound(declaration) || !Bound(copy)))
                        {
                            if (!Bound(declaration) && (!JsonNode.DeepEquals(declaration["parameters"],copy["parameters"]) ||
                                copy["returnType"] is { } fallbackReturn && !JsonNode.DeepEquals(declaration["returnType"],fallbackReturn)))
                                throw new ArgumentException($"Fallback declaration '{name}' in '{file}' conflicts with the selected contract.");
                            var candidateSources = ((JsonArray)declaration["catalogSources"]!).Select(n => n!.GetValue<string>()).Append(file).Distinct().ToArray();
                            declaration["catalogSources"] = new JsonArray(candidateSources.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                            var candidates = declaration["fallbackCandidates"] as JsonArray ?? new JsonArray();
                            if (declaration["fallbackCandidates"] is null) declaration["fallbackCandidates"] = candidates;
                            candidates.Add(copy.DeepClone());
                            if (copy["assembly"] is JsonValue candidateAssembly) inputs.Add(candidateAssembly.GetValue<string>());
                            continue;
                        }
                        if (fallback && copy["returnType"]?.GetValue<string>() == "Integer" && declaration["returnType"]?.GetValue<string>() == "Void"
                            && JsonNode.DeepEquals(declaration["parameters"],copy["parameters"]))
                        { copy["returnType"] = "Void"; copy["bindingAdaptation"] = "discard-integer-return"; }
                        if (fallback && (!JsonNode.DeepEquals(declaration["parameters"],copy["parameters"]) ||
                            copy["returnType"] is { } fallbackResult && !JsonNode.DeepEquals(declaration["returnType"],fallbackResult)))
                        {
                            declaration["bindingCandidate"] = copy.DeepClone();
                            declaration["resolutionStatus"] = "abi_incompatible";
                            declaration["bindingFailure"] = $"Fallback provider in '{file}' conflicts with the selected signature of '{name}'; an explicit matching adapter is required.";
                            if (copy["assembly"] is JsonValue rejectedAssembly) inputs.Add(rejectedAssembly.GetValue<string>());
                            continue;
                        }
                        if (!JsonNode.DeepEquals(declaration["parameters"], copy["parameters"]) ||
                            copy["returnType"] is { } returned && !JsonNode.DeepEquals(declaration["returnType"], returned))
                            throw new ArgumentException($"External function '{name}' conflicts with its profile signature.");
                        if (Bound(declaration) || !Bound(copy))
                            throw new ArgumentException($"Duplicate external BASIC name '{name}'; a profile declaration may only be completed by one implementation.");
                        if (fallback) { declaration.Remove("bindingFailure"); declaration.Remove("resolutionStatus"); }
                        foreach (var key in new[] { "defaults", "contract" })
                            if (copy.ContainsKey(key) && declaration[key] is { } original && !JsonNode.DeepEquals(original, copy[key]))
                                throw new ArgumentException($"External function '{name}' cannot change profile {key}.");
                        var origins = ((JsonArray)declaration["catalogSources"]!).Select(n => n!.GetValue<string>()).Concat(((JsonArray)copy["catalogSources"]!).Select(n => n!.GetValue<string>())).Distinct().ToArray();
                        foreach (var pair in copy)
                            declaration[pair.Key] = pair.Value?.DeepClone();
                        declaration["catalogSources"] = new JsonArray(origins.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                    }
                    else functions.Add(name, copy);
                }
            if (root["fallbackProfiles"] is { } fallbackProfiles)
                foreach (var profile in Array(fallbackProfiles,"fallbackProfiles")) Visit(FullPath(profile,"fallbackProfiles",directory),true);
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
        return ResolvePath(path, directory);
    }
    internal static string ResolvePath(string path, string directory)
    {
        var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory);
        if (File.Exists(full) || Directory.Exists(full) || OperatingSystem.IsWindows()) return full;
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = Path.Combine(current, part);
            if (File.Exists(exact) || Directory.Exists(exact)) { current = exact; continue; }
            var matches = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current)
                .Where(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase)).ToArray() : [];
            if (matches.Length > 1) throw new ArgumentException($"Ambiguous Windows-style dependency path '{full}'.");
            current = matches.Length == 1 ? matches[0] : exact;
        }
        return current;
    }
}
