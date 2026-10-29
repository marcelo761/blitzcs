using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class NativeAudioImportResolverTests
{
    [Theory]
    [InlineData("libSDL3.so.0", "SDL3.dll")]
    [InlineData("libsndfile.so.1", "sndfile.dll")]
    public void Windows_imports_resolve_to_official_runtime_names(string importName, string expectedFileName)
    {
        Assert.Equal(expectedFileName, NativeAudioImportResolver.WindowsFileName(importName));
    }

    [Fact]
    public void Unknown_imports_are_left_to_the_platform_loader()
    {
        Assert.Null(NativeAudioImportResolver.WindowsFileName("unrelated-native.dll"));
    }
}
