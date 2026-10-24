using System.Diagnostics;
using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.Linking.E2E.Tests;

[CollectionDefinition("LinkingE2E", DisableParallelization = true)]
public sealed class LinkingE2ECollection;

/// <summary>Builds a .bb with the CLI in-process and runs the generated program in a child dotnet process.</summary>
public abstract class E2EBase : IDisposable
{
    protected readonly string Root = Directory.CreateTempSubdirectory("blitzcs-e2e").FullName;
    private readonly string? previousCache = Environment.GetEnvironmentVariable("BLITZCS_BINARY_CACHE");

    protected E2EBase() => Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", Path.Combine(Root, "cache"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BLITZCS_BINARY_CACHE", previousCache);
        Directory.Delete(Root, recursive: true);
    }

    protected string Build(string source, string catalog, out int exit, out string stderr)
    {
        File.WriteAllText(Path.Combine(Root, "main.bb"), source);
        File.WriteAllText(Path.Combine(Root, "catalog.json"), catalog);
        var stdout = new StringWriter(); var error = new StringWriter();
        exit = CliApplication.Run(["build", Path.Combine(Root, "main.bb"), "-o", Path.Combine(Root, "out", "main.dll"), "--externals", Path.Combine(Root, "catalog.json")], stdout, error);
        stderr = error.ToString();
        return stdout.ToString();
    }

    protected string Run()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Root, "out", "main.dll"));
        using var process = Process.Start(info)!;
        var errorTask = process.StandardError.ReadToEndAsync();
        var text = process.StandardOutput.ReadToEnd() + errorTask.Result;
        process.WaitForExit();
        return text.Replace("\r\n", "\n");
    }

    protected static string? RealDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("BLITZCS_REAL_PE32_DIR");
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    protected static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BlitzCS.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("repository root not found"), relative);
    }

    protected static string Entry(string name, string[] parameters, string returnType, string library, string export, string? convention = null) =>
        $"{{\"name\":\"{name}\",\"parameters\":[{string.Join(",", parameters.Select(p => $"\"{p}\""))}],\"returnType\":\"{returnType}\","
        + $"\"binary\":{{\"library\":\"{library}\",\"export\":\"{export}\"" + (convention is null ? "" : $",\"convention\":\"{convention}\"") + "}}";
}
