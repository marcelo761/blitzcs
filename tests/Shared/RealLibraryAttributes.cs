using Xunit;

namespace BlitzCS.Testing;

/// <summary>
/// Locates the optional, locally available third-party binaries that the real-library tests run against. Nothing is copied into the repository.
/// <c>BLITZCS_REAL_PE32_DIR</c> names a directory with the PE32 DLLs (ogg.dll, vorbis.dll, vorbisFile.dll, eax.dll); <c>BLITZCS_PACKED_SAMPLE</c> names a packed DLL (UPX fmod.dll).
/// This file is shared between the test projects by <c>Compile Include ... Link</c> (no project reference between test assemblies).
/// </summary>
public static class RealLibraries
{
    public const string DirVariable = "BLITZCS_REAL_PE32_DIR";
    public const string PackedVariable = "BLITZCS_PACKED_SAMPLE";

    /// <summary>The configured DLL directory, or null when the variable is unset or empty.</summary>
    public static string? Dir => Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } d ? d : null;

    /// <summary>The configured packed sample path, or null when the variable is unset or empty.</summary>
    public static string? PackedSample => Environment.GetEnvironmentVariable(PackedVariable) is { Length: > 0 } p ? p : null;

    /// <summary>Full path of <paramref name="file"/> inside the DLL directory. Call only after <see cref="DirSkipReason"/> returned null.</summary>
    public static string PathOf(string file) => System.IO.Path.Combine(Dir ?? throw new InvalidOperationException(DirVariable + " is not set"), file);

    /// <summary>Null when the directory exists and holds every file in <paramref name="files"/>, else the reason the test cannot run (used as the xunit Skip text).</summary>
    public static string? DirSkipReason(params string[] files)
    {
        if (Dir is not { } dir) return $"{DirVariable} is not set (point it at a directory with the local PE32 DLLs; see docs/wine-testing.md)";
        if (!Directory.Exists(dir)) return $"{DirVariable}='{dir}' is not an existing directory";
        var missing = files.Where(f => !File.Exists(System.IO.Path.Combine(dir, f))).ToArray();
        return missing.Length == 0 ? null : $"{DirVariable}='{dir}' lacks {string.Join(", ", missing)}";
    }

    /// <summary>Null when the packed sample exists, else the reason the test cannot run.</summary>
    public static string? PackedSkipReason()
    {
        if (PackedSample is not { } path) return $"{PackedVariable} is not set (point it at a local UPX-packed fmod.dll; see docs/wine-testing.md)";
        return File.Exists(path) ? null : $"{PackedVariable}='{path}' does not exist";
    }

    /// <summary>Null when <paramref name="tool"/> is on PATH, else a skip reason.</summary>
    public static string? ToolSkipReason(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                try { if (File.Exists(System.IO.Path.Combine(dir, n))) return null; } catch (ArgumentException) { }
        return $"'{tool}' is not on PATH";
    }
}

/// <summary>
/// [Fact] for a test that needs files from <c>BLITZCS_REAL_PE32_DIR</c>. When they are absent the test is reported SKIPPED with the reason at discovery
/// instead of returning early and showing a meaningless PASS.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealLibraryFactAttribute : FactAttribute
{
    public RealLibraryFactAttribute(params string[] files) { Skip = RealLibraries.DirSkipReason(files); }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RealLibraryTheoryAttribute : TheoryAttribute
{
    public RealLibraryTheoryAttribute(params string[] files) { Skip = RealLibraries.DirSkipReason(files); }
}

/// <summary>
/// [Fact] for a test that needs the packed sample named by <c>BLITZCS_PACKED_SAMPLE</c>; skipped with the reason when it is not available.
/// <paramref name="skipOnWindows"/>: a reason to skip on Windows hosts (including Wine) even when the sample exists, for a test that only makes sense elsewhere.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PackedSampleFactAttribute : FactAttribute
{
    public PackedSampleFactAttribute(string? skipOnWindows = null) { Skip = RealLibraries.PackedSkipReason() ?? (OperatingSystem.IsWindows() ? skipOnWindows : null); }
}

/// <summary>[Fact] for a test that needs an external executable (for example a 32-bit C toolchain or ffmpeg) on PATH; skipped with the reason otherwise.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(params string[] tools) { Skip = tools.Select(RealLibraries.ToolSkipReason).FirstOrDefault(r => r is not null); }
}
