using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Support;

/// <summary>Failure classes used in every differential assertion message.</summary>
public static class FailureClass
{
    public const string Translation = "TRANSLATION";   // translated guest diverges from the x86 oracle
    public const string Environment = "ENVIRONMENT";   // oracle could not be built or run (toolchain/sandbox)
    public const string Unsupported = "UNSUPPORTED";   // translator reported an Error diagnostic
}

public sealed class OracleEnvironmentException(string message) : Exception(FailureClass.Environment + ": " + message);

public sealed record OracleFunction(string Name, GuestCallingConvention Convention, int ArgCount);

public sealed record OracleVector(int Index, string Function, uint[] Args, uint Result, uint DataHash, string[] Trace);

/// <summary>Deterministic transcript printed by a natively executed x86 oracle driver.</summary>
public sealed class OracleTranscript
{
    public required int DataLength { get; init; }
    public required IReadOnlyList<OracleFunction> Functions { get; init; }
    public required uint InitialDataHash { get; init; }
    public required IReadOnlyList<OracleVector> Vectors { get; init; }
    public required byte[] FinalData { get; init; }
}

public sealed record OracleArtifacts(string Name, byte[] Code, byte[] Data, IReadOnlyList<(string Name, uint Offset)> Exports, IReadOnlyList<uint> Relocs, string? OraclePath);

/// <summary>
/// Builds controlled C libraries lazily with tools/x86_oracle/build.sh and runs the natively executing ELF32 oracle.
/// The oracle is real IA-32 execution (kernel compat mode); the translator output is never used as an oracle.
/// </summary>
public static class OracleRunner
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, OracleArtifacts> Built = [];
    private static readonly Dictionary<string, OracleTranscript> Runs = [];
    private static string? unavailable;
    private static bool probed;

    public static string RepoRoot { get; } = FindRoot();
    private static string ToolDir => Path.Combine(RepoRoot, "tools", "x86_oracle");
    private static string CacheDir => Path.Combine(AppContext.BaseDirectory, "x86_oracle-cache");

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "x86_oracle", "build.sh"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Null when the oracle works, else a human-readable reason (used as the Skip reason).</summary>
    public static string? UnavailableReason
    {
        get { lock (Gate) { if (!probed) { probed = true; unavailable = Probe(); } return unavailable; } }
    }

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "x86 oracle requires Linux x86-64";
        if (!File.Exists(Path.Combine(ToolDir, "build.sh"))) return "tools/x86_oracle/build.sh not found";
        foreach (var tool in new[] { "gcc", "ld", "objcopy", "nm", "readelf", "python3", "bash" })
            if (Exec(tool, ["--version"], 20_000) is not { ExitCode: 0 }) return $"required tool '{tool}' is not available";
        try
        {
            var a = BuildCore("arith", "arith", Path.Combine(CacheDir, "probe"));
            var r = Exec(a.OraclePath!, [], 20_000);
            if (r.ExitCode != 0 || !r.Stdout.Contains("E done")) return $"IA-32 oracle did not run (exit {r.ExitCode}; missing gcc -m32 support or IA32 emulation): {Trim(r.Stderr)}";
        }
        catch (Exception e) { return "gcc -m32/ld toolchain cannot build the oracle: " + Trim(e.Message); }
        return null;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public static OracleArtifacts Build(string lib, string? driver = null)
    {
        lock (Gate)
        {
            if (Built.TryGetValue(lib, out var a)) return a;
            try { a = BuildCore(lib, driver ?? lib, CacheDir); }
            catch (Exception e) when (e is not OracleEnvironmentException) { throw new OracleEnvironmentException($"building '{lib}' failed: {e.Message}"); }
            return Built[lib] = a;
        }
    }

    private static OracleArtifacts BuildCore(string lib, string? driver, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var src = Path.Combine(ToolDir, "lib", lib + ".c");
        var drv = driver is null ? null : Path.Combine(ToolDir, "drivers", driver + ".c");
        var oracle = Path.Combine(outDir, lib + ".oracle");
        var json = Path.Combine(outDir, lib + ".exports.json");
        var inputs = new List<string> { src, Path.Combine(ToolDir, "build.sh"), Path.Combine(ToolDir, "layout.ld"), Path.Combine(ToolDir, "oracle.ld") };
        inputs.AddRange(Directory.GetFiles(Path.Combine(ToolDir, "lib"), "*.h"));
        if (drv is not null && File.Exists(drv)) { inputs.Add(drv); inputs.Add(Path.Combine(ToolDir, "drivers", "common.h")); }
        var newest = inputs.Where(File.Exists).Max(File.GetLastWriteTimeUtc);
        var stale = !File.Exists(json) || File.GetLastWriteTimeUtc(json) < newest || (drv is not null && (!File.Exists(oracle) || File.GetLastWriteTimeUtc(oracle) < newest));
        if (stale)
        {
            var r = Exec("bash", [Path.Combine(ToolDir, "build.sh"), src, drv is not null && File.Exists(drv) ? drv : "-", outDir], 120_000);
            if (r.ExitCode != 0) throw new OracleEnvironmentException($"build.sh failed for '{lib}' (exit {r.ExitCode}): {r.Stderr}{r.Stdout}");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var exports = doc.RootElement.GetProperty("exports").EnumerateArray().Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("offset").GetUInt32())).ToList();
        var relocs = doc.RootElement.GetProperty("relocs").EnumerateArray().Select(e => e.GetUInt32()).ToList();
        return new OracleArtifacts(lib, File.ReadAllBytes(Path.Combine(outDir, lib + ".code.bin")), File.ReadAllBytes(Path.Combine(outDir, lib + ".data.bin")),
            exports, relocs, drv is not null && File.Exists(drv) ? oracle : null);
    }

    /// <summary>Runs the oracle once per library (cached) and parses its transcript.</summary>
    public static OracleTranscript Run(string lib)
    {
        lock (Gate)
        {
            if (Runs.TryGetValue(lib, out var t)) return t;
            var a = Build(lib);
            if (a.OraclePath is null) throw new OracleEnvironmentException($"library '{lib}' has no oracle driver");
            var r = Exec(a.OraclePath, [], 120_000);
            if (r.ExitCode != 0) throw new OracleEnvironmentException($"oracle '{lib}' exited with {r.ExitCode}: {Trim(r.Stderr)}");
            return Runs[lib] = Parse(r.Stdout);
        }
    }

    public static OracleTranscript Parse(string text)
    {
        var funcs = new List<OracleFunction>(); var vecs = new List<OracleVector>(); var trace = new List<string>();
        var dataLen = -1; uint init = 0; byte[]? final = null; var done = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split(' ');
            switch (p[0])
            {
                case "H": dataLen = (int)H(p[2]); break;
                case "F": funcs.Add(new OracleFunction(p[1], p[2] == "S" ? GuestCallingConvention.Stdcall : GuestCallingConvention.Cdecl, (int)H(p[3]))); break;
                case "I": init = H(p[1]); break;
                case "T": trace.Add(string.Join(' ', p.Skip(1))); break;
                case "V":
                    var f = funcs.First(x => x.Name == p[2]);
                    vecs.Add(new OracleVector((int)H(p[1]), p[2], p.Skip(3).Take(f.ArgCount).Select(H).ToArray(), H(p[3 + f.ArgCount]), H(p[4 + f.ArgCount]), trace.ToArray()));
                    trace.Clear(); break;
                case "D": final = p.Length > 1 ? Convert.FromHexString(p[1]) : []; break;
                case "E": done = true; break;
            }
        }
        if (!done || final is null || dataLen < 0) throw new OracleEnvironmentException("oracle transcript is truncated or malformed");
        return new OracleTranscript { DataLength = dataLen, Functions = funcs, InitialDataHash = init, Vectors = vecs, FinalData = final };
    }

    private static uint H(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public sealed record ExecResult(int ExitCode, string Stdout, string Stderr);

    public static ExecResult Exec(string file, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new OracleEnvironmentException($"'{Path.GetFileName(file)}' timed out after {timeoutMs} ms"); }
            p.WaitForExit();
            return new ExecResult(p.ExitCode, so.Result, se.Result);
        }
        catch (System.ComponentModel.Win32Exception e) { return new ExecResult(127, "", e.Message); }
    }
}

/// <summary>[Fact] that is skipped (with a clear reason) when the x86 oracle toolchain/kernel support is missing.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OracleFactAttribute : FactAttribute
{
    public OracleFactAttribute() { Skip = OracleRunner.UnavailableReason is { } r ? "x86 oracle unavailable: " + r : null; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class OracleTheoryAttribute : TheoryAttribute
{
    public OracleTheoryAttribute() { Skip = OracleRunner.UnavailableReason is { } r ? "x86 oracle unavailable: " + r : null; }
}
