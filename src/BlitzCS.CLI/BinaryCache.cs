namespace BlitzCS.CLI;

internal static class BinaryCache
{
    /// <summary>
    /// BLITZCS_BINARY_CACHE, else XDG_CACHE_HOME/blitzcs/binary; otherwise the OS default: ~/.cache/blitzcs/binary on Linux and
    /// %LOCALAPPDATA%\blitzcs\binary on Windows (a Windows profile has no ~/.cache convention).
    /// </summary>
    internal static string Directory() =>
        Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE")
        ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg : DefaultRoot(), "blitzcs", "binary");

    private static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows() && Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local) return local;
        // No profile folder (a service account, a stripped environment): a relative path would put the cache in the current directory.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? Path.Combine(profile, ".cache") : Path.GetTempPath();
    }
}
