using System.Text.Json;

namespace BlitzCS.OriginalRuntime;

internal sealed record ApplicationLaunch(string Program, string Runtime, string WorkingDirectory, string Log)
{
    public string NativeRoot => Path.GetDirectoryName(Runtime)!;

    public static ApplicationLaunch Load(string configuration)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(configuration))!;
        using var document = JsonDocument.Parse(File.ReadAllText(configuration));
        string Resolve(string name)
        {
            if (!document.RootElement.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException($"Original-runtime application configuration requires '{name}'.");
            return Path.GetFullPath(value.GetString()!, root);
        }
        var launch = new ApplicationLaunch(Resolve("program"), Resolve("runtime"), Resolve("workingDirectory"), Resolve("log"));
        foreach (var file in new[] { launch.Program, launch.Runtime })
            if (!File.Exists(file)) throw new FileNotFoundException("Original-runtime application dependency is missing: " + file, file);
        if (!Directory.Exists(launch.WorkingDirectory))
            throw new DirectoryNotFoundException("Original-runtime application assets directory is missing: " + launch.WorkingDirectory);
        return launch;
    }
}
