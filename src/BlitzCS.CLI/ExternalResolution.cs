using System.Text.Json;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal sealed record ExternalResolution(string Name, string ReturnType, IReadOnlyList<string> Parameters,
    int MinimumArguments, IReadOnlyList<string> CatalogSources, string? Provider, string? Library, string? Export,
    string? CallingConvention, string? BindingMethod, string? BindingAdaptation, JsonElement? BindingCandidate, JsonElement? NativeContract, bool UsableExecutableBinding, string Status, string? Reason, JsonElement? LegacyDeclaration)
{
    internal static ExternalResolution Create(JsonElement item, ExternalFunctionSignature signature, ExternalFunction? binding, string? statusOverride = null)
    {
        string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var legacy = item.TryGetProperty("legacyDeclaration", out var declaration) ? declaration : (JsonElement?)null;
        var binary = item.TryGetProperty("binary", out var spec) ? spec : (JsonElement?)null;
        var sources = item.TryGetProperty("catalogSources", out var paths) ? paths.EnumerateArray().Select(p => p.GetString()!).ToArray() : [];
        var minimum = signature.ParameterTypes.Count - signature.DefaultValues.Count;
        // Catalogs without explicit defaults retain the selected method's optional CLR metadata.
        if (!item.TryGetProperty("defaults", out _) && binding is not null)
            minimum = binding.Method.GetParameters().Count(p => !p.IsOptional);
        return new(signature.Name, signature.ReturnType.ToString(), signature.ParameterTypes.Select(p => p.ToString()).ToArray(),
            minimum, sources, binary is { } b ? Text(b,"library") : binding?.Method.DeclaringType?.FullName ?? signature.Contract?.Provider,
            legacy is { } l ? Text(l,"library") : binary is { } lib ? Text(lib,"library") : null,
            legacy is { } e ? Text(e,"export") : binary is { } be ? Text(be,"export") : signature.Contract?.Symbol,
            legacy is { } c ? Text(c,"callingConvention") : binary is { } bc ? Text(bc,"convention") : binding is not null ? "managed" : signature.Contract?.CallingConvention,
            binding?.Method.Name, (binding?.Method.ReturnType == typeof(int) && signature.ReturnType == ExternalValueType.Void ? "discard-integer-return" : Text(item,"bindingAdaptation")), item.TryGetProperty("bindingCandidate",out var candidate) ? candidate.Clone() : null, item.TryGetProperty("nativeContract",out var nativeContract) ? nativeContract.Clone() : null,
            binding is not null, statusOverride ?? Text(item,"resolutionStatus") ?? (binding is not null ? "bound" : "provider_missing"),
            signature.BindingFailure ?? (binding is null ? "Contract resolved, but no executable provider is configured." : null), legacy?.Clone());
    }
}

internal static class DependencyResolutionReport
{
    internal static (string Json, int Unresolved) Create(string source, string sourcePath, ExternalCatalogResult catalog, string? tracePath, string? assetRoot = null)
    {
        var analysis = BasicDependencyAnalysis.AnalyzeDetailed(source, sourcePath);
        if (tracePath is not null)
        {
            var output = DotNetEmitter.CanonicalPath(tracePath);
            var protectedPaths = analysis.SourcePaths.Concat(catalog.InputPaths ?? []).Concat(catalog.NativeLibraries).Concat(catalog.Functions.ReferencePaths);
            if (protectedPaths.Any(p => DotNetEmitter.CanonicalPath(p).Equals(output,StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Resolution trace cannot overwrite a compiler input.");
            if (assetRoot is not null)
            {
                var assets = DotNetEmitter.CanonicalPath(assetRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (output.Equals(assets,StringComparison.OrdinalIgnoreCase) || output.StartsWith(assets + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Resolution trace cannot overwrite an asset input.");
            }
            if (Directory.Exists(output)) throw new ArgumentException("Resolution trace output is a directory.");
        }
        var lookup = (catalog.Resolutions ?? []).ToDictionary(r => r.Name,StringComparer.OrdinalIgnoreCase);
        var unresolved = 0;
        var dependencies = analysis.CallSites.GroupBy(c => c.Name,StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            lookup.TryGetValue(group.Key, out var found);
            var arityErrors = found is null ? [] : group.Where(c => c.ArgumentCount < found.MinimumArguments || c.ArgumentCount > found.Parameters.Count
                || !c.UsedAsStatement && found.ReturnType == "Void").ToArray();
            var status = arityErrors.Length > 0 ? "signature_mismatch" : found?.Status ?? "declaration_missing";
            if (status != "bound") unresolved++;
            return new
            {
                name = group.Key, status, reason = arityErrors.Length > 0 ? "Call arity or value usage disagrees with the resolved signature." : found?.Reason ?? (found is null ? "No declaration was found in the configured catalog closure." : null),
                catalogSources = found?.CatalogSources, provider = found?.Provider, library = found?.Library,
                export = found?.Export, callingConvention = found?.CallingConvention, bindingMethod = found?.BindingMethod,
                bindingAdaptation = found?.BindingAdaptation, bindingCandidate = found?.BindingCandidate, nativeContract = found?.NativeContract,
                selectedProvider = found?.UsableExecutableBinding == true ? found.Provider : null, usableExecutableBinding = found?.UsableExecutableBinding ?? false,
                legacyDeclaration = found?.LegacyDeclaration, returnType = found?.ReturnType, parameters = found?.Parameters,
                calls = group.ToArray(), signatureErrors = arityErrors
            };
        }).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            stage = "dependency_audit", semanticValidation = false, emissionAttempted = false,
            sourcePaths = analysis.SourcePaths, catalogInputs = catalog.InputPaths ?? [],
            declaredSymbols = lookup.Count, requestedSymbols = dependencies.Length, unresolvedSymbols = unresolved,
            groups = dependencies.GroupBy(d => (d.library ?? d.provider ?? "unconfigured",d.status)).Select(g => new { libraryOrProvider = g.Key.Item1, status = g.Key.status, symbolCount = g.Count(), symbols = g.Select(d=>d.name).ToArray() }).ToArray(),
            dependencies
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (tracePath is not null)
        {
            var path = Path.GetFullPath(tracePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary,json + Environment.NewLine);
                File.Move(temporary,path,overwrite:true); // Replace directory entry; never truncate a hard-linked compiler input.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return (json,unresolved);
    }
}
