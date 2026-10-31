using Blitz3DCS.Graphics.D3D11;
using Blitz3DCS.Windowing;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class D3D11CapabilitiesTests
{
    [Fact]
    public void Windowed_3d_capability_is_false_off_windows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(D3D11Capabilities.SupportsWindowed3D);
    }

    [Fact]
    public void Hardware_capability_probe_does_not_create_a_window()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
            return;

        Assert.Throws<InvalidOperationException>(() => LegacyInput.CurrentWindow);
        Assert.True(D3D11Capabilities.SupportsWindowed3D);
        Assert.Throws<InvalidOperationException>(() => LegacyInput.CurrentWindow);
    }
}
