namespace Blitz3DCS.Runtime;

/// <summary>Host system queries; no graphics or compiler dependency.</summary>
public static class LegacySystem
{
    /// <summary>Queries real host properties. Native engine handles require a host integration.</summary>
    public static string SystemProperty(string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.ToLowerInvariant() switch
        {
            "tempdir" => DirectoryProperty(Path.GetTempPath()),
            "appdir" => DirectoryProperty(AppContext.BaseDirectory),
            "cpu" => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            "os" => System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            "windowsdir" when OperatingSystem.IsWindows() => DirectoryProperty(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            "systemdir" when OperatingSystem.IsWindows() => DirectoryProperty(Environment.SystemDirectory),
            "windowsdir" or "systemdir" => throw new PlatformNotSupportedException($"SystemProperty '{property}' requires Windows."),
            "apphwnd" or "apphinstance" or "direct3d7" or "direct3ddevice7" or "directdraw7" or "directinput7"
                => throw new PlatformNotSupportedException($"SystemProperty '{property}' requires a native Windows engine host; no handle is available in this runtime."),
            _ => ""
        };
    }

    private static string DirectoryProperty(string path) => Path.TrimEndingDirectorySeparator(path)
        .Replace('/', '\\').TrimEnd('\\') + "\\";

    /// <summary>Reads an environment variable using Windows-compatible case-insensitive names.</summary>
    public static string GetEnv(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var zero = name.IndexOf('\0');
        if (zero >= 0) name = name[..zero];
        if (name.Length == 0 || name.Contains('=')) return "";
        var exact = Environment.GetEnvironmentVariable(name);
        if (exact is not null) return exact;
        var matches = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => string.Equals(entry.Key as string, name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value as string ?? "" : "";
    }
}
