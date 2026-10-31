using BlitzCS.LegacyRuntime;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyErrorsTests
{
    [Fact]
    public void RuntimeErrorRemainsFatal()
    {
        var error = Assert.Throws<InvalidOperationException>(() => LegacyErrors.RuntimeError("fatal runtime failure"));
        Assert.Equal("fatal runtime failure", error.Message);
    }

    [Fact]
    public void ErrorLogDrainsRecoverableErrorsInFifoOrder()
    {
        while (LegacyErrors.ErrorLog().Length > 0) { }

        LegacyErrors.RecordRecoverableError("first recoverable error");
        LegacyErrors.RecordRecoverableError("second recoverable error");

        Assert.Equal("first recoverable error", LegacyErrors.ErrorLog());
        Assert.Equal("second recoverable error", LegacyErrors.ErrorLog());
        Assert.Equal(string.Empty, LegacyErrors.ErrorLog());
    }

    [Fact]
    public void FileLoadFailuresDoNotEnterTheRecoverableErrorQueue()
    {
        while (LegacyErrors.ErrorLog().Length > 0) { }

        var directory = Path.Combine(Path.GetTempPath(), "blitzcs-missing-loads-" + Guid.NewGuid());
        Assert.Equal(0, Blitz3DCS.Engine.Windows.LegacyGraphics.LoadImage(Path.Combine(directory, "image.png")));
        Assert.Equal(0, Blitz3DCS.Engine.Windows.LegacyGraphics.LoadAnimImage(Path.Combine(directory, "anim-image.png"), 1, 1, 0, 1));
        Assert.Equal(0, Blitz3DCS.Engine.Windows.LegacyGraphics.LoadTexture(Path.Combine(directory, "texture.png")));
        Assert.Equal(0, Blitz3DCS.Engine.Windows.LegacyGraphics.LoadMesh(Path.Combine(directory, "mesh.b3d")));
        Assert.Equal(0, Blitz3DCS.Engine.Windows.LegacyGraphics.LoadAnimMesh(Path.Combine(directory, "anim-mesh.b3d")));
        Assert.Equal(string.Empty, LegacyErrors.ErrorLog());
    }
}
