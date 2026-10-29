namespace Blitz3DCS.Runtime;

/// <summary>Opt-in filesystem queries implementing the documented legacy contract.</summary>
public static class LegacyFiles
{
    /// <summary>Returns the low signed 32 bits of file length, or zero for a directory/missing path.</summary>
    public static int FileSize(string file)
    {
        try
        {
            var path = Resolve(file, allowWildcard: true);
            return path is null || (File.GetAttributes(path) & FileAttributes.Directory) != 0
                ? 0 : unchecked((int)new FileInfo(path).Length);
        }
        catch (Exception error) when (IsPathFailure(error)) { return 0; }
    }

    /// <summary>Returns 1 for a file, 2 for a directory and 0 when unavailable.</summary>
    public static int FileType(string file)
    {
        try
        {
            var path = Resolve(file, allowWildcard: false);
            return path is null ? 0 : (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? 2 : 1;
        }
        catch (Exception error) when (IsPathFailure(error)) { return 0; }
    }

    /// <summary>Returns the host working directory with legacy separators and a trailing backslash.</summary>
    public static string CurrentDir() => Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory())
        .Replace('/', '\\').TrimEnd('\\') + "\\";

    private static bool IsPathFailure(Exception error) => error is IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    internal static string? Resolve(string file, bool allowWildcard)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var zero = file.IndexOf('\0');
        if (zero >= 0) file = file[..zero]; // Win32 receives the original BBStr through c_str().
        if (file.Length == 0) return null;
        var directoryRequired = file.EndsWith('\\') || file.EndsWith('/');
        var full = Path.GetFullPath(file.Replace('\\', Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.IndexOfAny(['*', '?']) >= 0)
            {
                if (!allowWildcard || index != parts.Length - 1) return null;
                // FindFirstFile historically observes filesystem enumeration order, not sorted order.
                var match = Directory.EnumerateFileSystemEntries(current, part, new EnumerationOptions {
                    MatchType = System.IO.MatchType.Win32, MatchCasing = MatchCasing.CaseInsensitive,
                    AttributesToSkip = 0, IgnoreInaccessible = false
                }).FirstOrDefault();
                return directoryRequired && !Directory.Exists(match) ? null : match;
            }
            var exact = Path.Combine(current, part);
            if (File.Exists(exact) || Directory.Exists(exact)) { current = exact; continue; }
            // A Windows legacy project may use different casing on a case-sensitive host.
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => string.Equals(Path.GetFileName(path), part, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length != 1) return null; // Ambiguous host spellings have no unique Windows equivalent.
            current = matches[0];
        }
        return directoryRequired && !Directory.Exists(current) ? null : current;
    }
}
