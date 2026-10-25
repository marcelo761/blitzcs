using BlitzCS.CLI;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class PackageSafetyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-package-safety-" + Guid.NewGuid());
    private string Source => Path.Combine(directory, "Main.bb");

    public PackageSafetyTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Source, "Print 42");
    }

    [Fact]
    public void UnownedDirectoryCannotBeReplaced()
    {
        var destination = Path.Combine(directory, "output");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "keep.txt");
        File.WriteAllText(sentinel, "preserve");
        var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", Source, "-o", destination], TextWriter.Null, errors));
        Assert.Contains("not a BlitzCS package", errors.ToString());
        Assert.Equal("preserve", File.ReadAllText(sentinel));
    }

    [Fact]
    public void AssetsCannotBeInsideReplacedPackage()
    {
        var destination = Path.Combine(directory, "output");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, ".blitzcs-package"), "BlitzCS application package v1\n");
        var assets = Directory.CreateDirectory(Path.Combine(destination, "data")).FullName;
        var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", Source, "-o", destination, "--assets", assets], TextWriter.Null, errors));
        Assert.Contains("must not overlap", errors.ToString());
        Assert.True(Directory.Exists(assets));
    }

    [Fact]
    public void SymlinkedPackageCannotHideSourceInputs()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(directory, ".blitzcs-package"), "BlitzCS application package v1\n");
        var alias = directory + "-alias";
        Directory.CreateSymbolicLink(alias, directory);
        try
        {
            var errors = new StringWriter();
            Assert.Equal(1, CliApplication.Run(["build", Source, "-o", alias], TextWriter.Null, errors));
            Assert.Contains("contains a compiler input", errors.ToString());
            Assert.True(File.Exists(Source));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void WindowsPackageRejectsElfNativeDependency()
    {
        var library = Path.Combine(directory, "library.so");
        File.WriteAllBytes(library, [0x7f, 69, 76, 70, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 62, 0]);
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(catalog, "{\"nativeLibraries\":[\"library.so\"]}");
        var destination = Path.Combine(directory, "output");
        var errors = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["build", Source, "-o", destination, "--externals", catalog], TextWriter.Null, errors));
        Assert.NotEmpty(errors.ToString());
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
