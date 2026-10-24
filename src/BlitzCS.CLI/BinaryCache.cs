namespace BlitzCS.CLI;

internal static class BinaryCache
{
    internal static string Directory() =>
        Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE")
        ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "blitzcs", "binary");
}
