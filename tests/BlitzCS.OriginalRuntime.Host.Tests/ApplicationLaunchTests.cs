using System.Text.Json;
using BlitzCS.OriginalRuntime;
using Xunit;

namespace BlitzCS.OriginalRuntime.Host.Tests;

public sealed class ApplicationLaunchTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("blitzcs-native-app-").FullName;
    private string Config(string? program = "program/Game.dll", string? runtime = "native/runtime.dll", string? working = "game")
    {
        var path = Path.Combine(root, "Game.app.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { program, runtime, workingDirectory = working, log = "logs/session.log" }));
        return path;
    }
    private void Inputs()
    {
        Directory.CreateDirectory(Path.Combine(root, "program"));
        Directory.CreateDirectory(Path.Combine(root, "native"));
        Directory.CreateDirectory(Path.Combine(root, "game"));
        File.WriteAllText(Path.Combine(root, "program", "Game.dll"), "");
        File.WriteAllText(Path.Combine(root, "native", "runtime.dll"), "");
    }
    [Fact]
    public void ResolvesPathsAgainstApplicationConfigurationRatherThanCallerWorkingDirectory()
    {
        Inputs();
        var launch = ApplicationLaunch.Load(Config());
        Assert.Equal(Path.Combine(root, "program", "Game.dll"), launch.Program);
        Assert.Equal(Path.Combine(root, "native", "runtime.dll"), launch.Runtime);
        Assert.Equal(Path.Combine(root, "native"), launch.NativeRoot);
        Assert.Equal(Path.Combine(root, "game"), launch.WorkingDirectory);
        Assert.Equal(Path.Combine(root, "logs", "session.log"), launch.Log);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullOrOmittedLogDisablesFileLogging(bool omit)
    {
        Inputs();
        var config = Config();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        if (omit) document.Remove("log"); else document["log"] = null;
        File.WriteAllText(config, document.ToJsonString());
        Assert.Null(ApplicationLaunch.Load(config).Log);
    }
    [Theory]
    [InlineData("program/Game.dll")]
    [InlineData("native/runtime.dll")]
    public void MissingProgramOrNativeRuntimeFailsBeforeLaunching(string missing)
    {
        Inputs(); File.Delete(Path.Combine(root, missing));
        var error = Assert.Throws<FileNotFoundException>(() => ApplicationLaunch.Load(Config()));
        Assert.Equal(Path.Combine(root, missing), error.FileName);
    }
    [Fact]
    public void MissingAssetsDirectoryFailsBeforeLaunching()
    {
        Inputs(); Directory.Delete(Path.Combine(root, "game"));
        Assert.Throws<DirectoryNotFoundException>(() => ApplicationLaunch.Load(Config()));
    }
    [Theory]
    [InlineData(null, "native/runtime.dll", "game")]
    [InlineData("", "native/runtime.dll", "game")]
    [InlineData("program/Game.dll", null, "game")]
    [InlineData("program/Game.dll", "native/runtime.dll", " ")]
    public void IncompleteConfigurationCannotCreateAnApparentlyRunnablePackage(string? program, string? runtime, string? working)
    {
        Inputs();
        Assert.Throws<ArgumentException>(() => ApplicationLaunch.Load(Config(program, runtime, working)));
    }
    public void Dispose() => Directory.Delete(root, true);
}
