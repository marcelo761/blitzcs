namespace Blitz3DCS.Runtime;

/// <summary>Host system queries; no graphics or compiler dependency.</summary>
public static class LegacySystem
{
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
