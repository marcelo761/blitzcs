using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace BlitzCS.CLI;

/// <summary>Loads explicit external assemblies and their same-directory dependencies.</summary>
internal sealed class ExternalAssemblyLoadContext : AssemblyLoadContext, IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly HashSet<string> frameworkAssemblies = GetFrameworkAssemblyNames();
    private readonly Dictionary<string, string> assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> filenames = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);
    private readonly List<string> paths = [];

    internal ExternalAssemblyLoadContext() : base("BlitzExternal-" + Guid.NewGuid(), isCollectible: true) { }

    internal IReadOnlyList<string> Discover(IEnumerable<string> rootPaths)
    {
        var pending = new Queue<Assembly>();
        foreach (var path in rootPaths.Select(Path.GetFullPath).Distinct(PathComparer))
        {
            CheckFilename(path);
            var assembly = LoadFromAssemblyPath(path);
            Register(assembly, path);
            pending.Enqueue(assembly);
        }

        var visited = new HashSet<string>(PathComparer);
        while (pending.TryDequeue(out var assembly))
        {
            var assemblyPath = Path.GetFullPath(assembly.Location);
            if (!visited.Add(assemblyPath)) continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || frameworkAssemblies.Contains(reference.Name)) continue;
                if (assemblies.TryGetValue(reference.Name, out var resolved))
                {
                    if (!ReferenceMatches(reference, resolved))
                        throw MissingDependency(reference, assemblyPath);
                    var known = LoadFromAssemblyPath(resolved);
                    pending.Enqueue(known);
                    continue;
                }

                var candidate = Path.Combine(Path.GetDirectoryName(assemblyPath)!, reference.Name + ".dll");
                if (!File.Exists(candidate)) throw MissingDependency(reference, assemblyPath);
                if (!ReferenceMatches(reference, candidate)) throw IdentityMismatch(reference, candidate, assemblyPath);
                CheckFilename(candidate);
                var dependency = LoadFromAssemblyPath(candidate);
                Register(dependency, candidate);
                pending.Enqueue(dependency);
            }
        }

        return paths.AsReadOnly();
    }

    internal Assembly LoadAssembly(string path) => LoadFromAssemblyPath(Path.GetFullPath(path));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || frameworkAssemblies.Contains(assemblyName.Name)) return null;
        if (assemblies.TryGetValue(assemblyName.Name, out var path))
        {
            if (!ReferenceMatches(assemblyName, path)) throw MissingDependency(assemblyName, path);
            return LoadFromAssemblyPath(path);
        }
        throw MissingDependency(assemblyName, Name ?? "external assembly");
    }

    private void Register(Assembly assembly, string path)
    {
        path = Path.GetFullPath(path);
        var name = assembly.GetName().Name
            ?? throw new BadImageFormatException($"External assembly '{path}' has no assembly name.");
        if (assemblies.TryGetValue(name, out var previous) && !PathComparer.Equals(previous, path))
            throw new ArgumentException($"External assemblies have the same identity '{name}'.");
        assemblies[name] = path;
        if (!paths.Contains(path, PathComparer)) paths.Add(path);
    }

    private void CheckFilename(string path)
    {
        var filename = Path.GetFileName(path);
        if (filenames.TryGetValue(filename, out var previous) && !PathComparer.Equals(previous, path))
            throw new ArgumentException($"External assemblies have the same output filename '{filename}'.");
        filenames[filename] = path;
    }

    private static bool ReferenceMatches(AssemblyName reference, string path)
    {
        try
        {
            var actual = AssemblyName.GetAssemblyName(path);
            return string.Equals(reference.Name, actual.Name, StringComparison.OrdinalIgnoreCase)
                && Equals(reference.Version, actual.Version)
                && string.Equals(reference.CultureName ?? "", actual.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
                && (reference.GetPublicKeyToken() ?? []).SequenceEqual(actual.GetPublicKeyToken() ?? []);
        }
        catch (BadImageFormatException) { return false; }
    }

    private static FileNotFoundException MissingDependency(AssemblyName reference, string requester) =>
        new($"External dependency '{reference.Name}' referenced by '{Path.GetFileName(requester)}' was not found beside the referencing assembly.", reference.Name);

    private static FileLoadException IdentityMismatch(AssemblyName reference, string candidate, string requester) =>
        new($"External dependency '{Path.GetFileName(candidate)}' beside '{Path.GetFileName(requester)}' does not match the referenced assembly identity '{reference.FullName}'.", candidate);

    private static HashSet<string> GetFrameworkAssemblyNames()
    {
        var runtimeDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var names = new HashSet<string>(comparer);
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        foreach (var path in trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (comparer.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), runtimeDirectory))
                names.Add(Path.GetFileNameWithoutExtension(path));
        }
        return names;
    }

    public void Dispose() => Unload();
}
