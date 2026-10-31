using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class NativeBlitzCommandLibraryTests
{
    private static NativeBlitzCommandLibrary CreateLibrary() =>
        NativeBlitzCommandLibrary.For(
            typeof(NativeBlitzCommandLibraryTests).Assembly,
            $"unused-{Guid.NewGuid():N}.dll",
            NativeBlitzCommandLibrary.SupportedRuntimeSha256);

    [Fact]
    public void ForRejectsAnUnverifiedRuntimeFingerprint()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            NativeBlitzCommandLibrary.For(
                typeof(NativeBlitzCommandLibraryTests).Assembly,
                "runtime.dll",
                new string('0', 64)));

        Assert.Contains("verified runtime image", error.Message);
    }

    [Fact]
    public void CommandValidatesItsMetadataWithoutLoadingTheNativeRuntime()
    {
        var library = CreateLibrary();

        Assert.Throws<ArgumentException>(() => library.Command(0x1234, " ", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => library.Command(0x1234, "Probe", 17));

        var command = library.Command(0x1234, "Probe", 1);
        Assert.Throws<ArgumentException>(() => command.Call());
    }

    [Fact]
    public void NativeCommandCallsRejectUnsupportedHostPlatformsBeforeLoading()
    {
        if (OperatingSystem.IsWindows() && IntPtr.Size == 4) return;

        var command = CreateLibrary().Command(0x1234, "Probe", 0);
        Assert.Throws<PlatformNotSupportedException>(() => command.Call());
    }
}
