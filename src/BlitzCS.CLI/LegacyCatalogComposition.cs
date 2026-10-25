using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlitzCS.CLI;

/// <summary>Connects historical BASIC contracts to explicit providers; never infers pointer forwarding.</summary>
internal static class LegacyCatalogComposition
{
    internal static void Merge(Dictionary<string, JsonObject> functions, JsonObject declaration)
    {
        var name = declaration["name"]!.GetValue<string>();
        var legacy = declaration["legacyDeclaration"]!.AsObject();
        var source = legacy["source"]!.GetValue<string>() + "(" + legacy["line"] + ")";
        if (!functions.TryGetValue(name, out var selected))
        {
            selected = (JsonObject)declaration.DeepClone(); functions.Add(name, selected);
        }
        else
        {
            if (selected["legacyDeclaration"] is { } prior)
                throw new ArgumentException($"Conflicting legacy declaration '{name}' at {source}; already declared at {prior["source"]}({prior["line"]}).");
            var sameSignature = JsonNode.DeepEquals(selected["parameters"], declaration["parameters"])
                && (selected["returnType"] is null || JsonNode.DeepEquals(selected["returnType"], declaration["returnType"]));
            selected["legacyDeclaration"] = legacy.DeepClone();
            selected["returnType"] ??= declaration["returnType"]!.DeepClone();
            var origins = ((JsonArray)selected["catalogSources"]!).Select(n => n!.GetValue<string>())
                .Concat(((JsonArray)declaration["catalogSources"]!).Select(n => n!.GetValue<string>())).Distinct().ToArray();
            selected["catalogSources"] = new JsonArray(origins.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            if (!sameSignature && JsonNode.DeepEquals(selected["parameters"],declaration["parameters"])
                && declaration["returnType"]!.GetValue<string>() == "Void" && selected["returnType"]?.GetValue<string>() == "Integer"
                && (selected["binary"] is not null || selected["method"] is not null))
            {
                // A historical command ignores the native EAX result. No value is fabricated and the native call remains intact.
                selected["bindingAdaptation"] = "discard-integer-return";
                selected["returnType"] = "Void";
                sameSignature = true;
            }
            if (!sameSignature)
            {
                Block(selected, "abi_incompatible", $"Historical declaration '{name}' at {source} ({declaration["returnType"]}, {declaration["parameters"]}) conflicts with the configured provider signature ({selected["returnType"]}, {selected["parameters"]}); an explicit ABI adapter with the historical signature is required.");
                foreach (var key in new[] { "parameters", "returnType", "defaults" }) selected[key] = declaration[key]!.DeepClone();
            }
        }
        selected["defaults"] = declaration["defaults"]!.DeepClone();
        var library = legacy["library"]!.GetValue<string>();
        var export = legacy["export"]!.GetValue<string>();
        selected["contract"] ??= new JsonObject
        {
            ["provider"] = string.IsNullOrWhiteSpace(library) ? "application-userlib" : library,
            ["resolution"] = "legacy-userlib", ["symbol"] = export, ["callingConvention"] = "stdcall", ["source"] = source
        };
        if (selected["method"] is not null && selected["selectionRole"]?.GetValue<string>() == "fallback" && !string.IsNullOrWhiteSpace(library)
            && functions.Values.Any(f => f["binary"] is JsonObject b && b["library"] is JsonValue path && Path.GetFileName(path.GetValue<string>()).Equals(Path.GetFileName(library.Replace('\\',Path.DirectorySeparatorChar)),StringComparison.OrdinalIgnoreCase)))
            Block(selected,"abi_incompatible",$"Historical library '{library}' already selects a binary provider; the managed fallback for '{name}' does not establish compatible resource ownership with that provider. Configure an explicit compatible provider instead of mixing native and managed handles.");
        var pointer = legacy["parameters"]!.AsArray().Any(p => p!["isPointer"]!.GetValue<bool>());
        var bound = selected["method"] is not null || selected["binary"] is not null;
        var managedAdapter = selected["method"] is not null && selected["legacyAdapter"]?.GetValue<bool>() == true;
        if (bound && pointer && !managedAdapter)
            Block(selected, "adapter_missing", $"Historical pointer parameter in '{name}' at {source} requires an explicit bank/object marshalling adapter (legacyAdapter); BASIC Integer handles are not native addresses.");
        if (selected["binary"] is JsonObject binary)
        {
            var selectedExport = binary["export"]?.GetValue<string>();
            if (selectedExport is not null && !selectedExport.Equals(export, StringComparison.Ordinal))
                Block(selected, "abi_incompatible", $"Export alias '{export}' at {source} disagrees with configured export '{selectedExport}'. Supply an explicit matching adapter.");
            else if (binary["convention"]?.GetValue<string>() is { } convention && convention != "stdcall")
                Block(selected, "abi_incompatible", $"Historical userlib '{name}' requires stdcall; provider declares {convention}.");
        }
        var decoration = Regex.Match(export, @"^_.+@(\d+)$", RegexOptions.CultureInvariant);
        if (decoration.Success && int.TryParse(decoration.Groups[1].Value, out var bytes)
            && bytes != 4 * declaration["parameters"]!.AsArray().Count && !managedAdapter)
            Block(selected, "abi_incompatible", $"Export '{export}' encodes {bytes} stdcall argument bytes, but {source} declares {4 * declaration["parameters"]!.AsArray().Count}; an explicit ABI adapter is required.");
        if (selected["bindingFailure"] is null && selected["method"] is null && selected["binary"] is null)
            selected["bindingFailure"] = $"Declaration at {source} resolved to {library}!{export}; no executable provider is configured." +
                (pointer ? " Pointer parameters require a bank/object marshalling adapter; no raw pointer conversion was generated." : "");
    }

    private static void Block(JsonObject selected, string status, string reason)
    {
        if (selected["bindingCandidate"] is null)
        {
            var candidate = new JsonObject();
            foreach (var key in new[] { "assembly", "type", "method", "binary", "parameters", "returnType" })
                if (selected[key] is { } value) candidate[key] = value.DeepClone();
            selected["bindingCandidate"] = candidate;
        }
        selected["resolutionStatus"] = status; selected["bindingFailure"] = reason;
        foreach (var key in new[] { "assembly", "type", "method", "binary" }) selected.Remove(key);
    }
}
