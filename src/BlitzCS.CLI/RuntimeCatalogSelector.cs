using System.Text.Json;

namespace BlitzCS.CLI;

internal sealed record RuntimeCatalogSelection(string CatalogPath, IReadOnlyList<string> CatalogInputs);

internal static class RuntimeCatalogSelector
{
    private const int MaximumManifestDepth = 32;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static RuntimeCatalogSelection Resolve(string catalogPath, string? requestedTarget)
    {
        var target = requestedTarget is null
            ? CompilationTarget.Win64
            : TargetCapabilities.ParseTarget(requestedTarget);
        var targetName = target.CanonicalName();
        var currentPath = Path.GetFullPath(catalogPath);
        var visited = new HashSet<string>(PathComparer);
        var inputs = new List<string>();

        for (var depth = 0; ; depth++)
        {
            if (depth >= MaximumManifestDepth)
                throw new ArgumentException($"Runtime catalog manifest nesting exceeds the maximum depth of {MaximumManifestDepth}.");
            if (!visited.Add(currentPath))
                throw new ArgumentException($"Runtime catalog manifest cycle detected at '{currentPath}'.");
            inputs.Add(currentPath);

            using var document = JsonDocument.Parse(File.ReadAllText(currentPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"External catalog '{currentPath}' must contain a JSON object.");
            if (!root.TryGetProperty("runtimeTargets", out var targets))
                return new RuntimeCatalogSelection(currentPath, inputs.ToArray());

            var unexpectedName = root.EnumerateObject()
                .Where(property => property.Name != "runtimeTargets")
                .Select(property => property.Name)
                .FirstOrDefault();
            if (unexpectedName is not null)
                throw new ArgumentException($"Runtime catalog manifest '{currentPath}' cannot mix 'runtimeTargets' with catalog field '{unexpectedName}'.");
            if (targets.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Runtime catalog manifest '{currentPath}' field 'runtimeTargets' must be an object.");

            string? selectedPath = null;
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in targets.EnumerateObject())
            {
                if (!TargetCapabilities.TryParseTarget(property.Name, out var mappedTarget)
                    || !string.Equals(property.Name, mappedTarget.CanonicalName(), StringComparison.Ordinal))
                    throw new ArgumentException($"Runtime catalog manifest '{currentPath}' has invalid target key '{property.Name}'; use Win32, Linux32, Win64 or Linux64.");
                if (!seenTargets.Add(property.Name))
                    throw new ArgumentException($"Runtime catalog manifest '{currentPath}' contains duplicate target '{property.Name}'.");
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                    throw new ArgumentException($"Runtime catalog manifest '{currentPath}' target '{property.Name}' must map to a non-empty catalog path string.");
                if (string.Equals(property.Name, targetName, StringComparison.Ordinal))
                    selectedPath = property.Value.GetString();
            }

            if (selectedPath is null)
                throw new ArgumentException($"Runtime catalog manifest '{currentPath}' has no runtime catalog for target '{targetName}'.");
            currentPath = Path.GetFullPath(selectedPath, Path.GetDirectoryName(currentPath)!);
        }
    }
}
