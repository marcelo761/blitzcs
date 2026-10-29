using System.Globalization;
using BlitzCS.LegacyRuntime;
using Blitz3DCS.Engine.Windows;
using Blitz3DCS.Runtime;
using Blitz3DCS.Windowing;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsSystemWindowsSmokeTests
{
    [Fact]
    public void Legacy_display_modes_and_bank_rect_use_the_persistent_native_window()
        => RunSmoke();

    internal static void RunSmoke()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1") return;

        LegacyGraphics.ResetForTests();
        try
        {
            var modeCount = LegacyGraphics.CountGfxModes3D();
            Assert.True(modeCount > 0);
            Assert.All(Enumerable.Range(1, modeCount), mode =>
            {
                Assert.True(LegacyGraphics.GfxModeWidth(mode) > 0);
                Assert.True(LegacyGraphics.GfxModeHeight(mode) > 0);
            });
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.GfxModeWidth(0));

            LegacyGraphics.AppTitle("Blitz3DCS native system smoke", "Blitz3DCS native system error");
            var windowHandleText = LegacyWindowBindings.SystemProperty("AppHWND");
            Assert.True(long.TryParse(windowHandleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowHandleValue));
            Assert.NotEqual(0L, windowHandleValue);
            var windowToken = unchecked((int)windowHandleValue);
            var desktop = LegacyWindowBindings.api_GetDesktopWindow();

            var bank = LegacyBanks.CreateBank(16);
            try
            {
                Assert.Equal(1, LegacyWindowBindings.api_GetClientRect(desktop, bank));
                Assert.True(LegacyBanks.PeekInt(bank, 8) > 0);
                Assert.True(LegacyBanks.PeekInt(bank, 12) > 0);
                Assert.Equal(1, LegacyWindowBindings.api_GetClientRect(windowToken, bank));
                Assert.Equal(1, LegacyBanks.PeekInt(bank, 8));
                Assert.Equal(1, LegacyBanks.PeekInt(bank, 12));

            LegacyGraphics.Graphics3D(320, 240);
            Assert.Equal(1, LegacyGraphics.CountGfxDrivers());
            Assert.Equal("Direct3D 11", LegacyGraphics.GfxDriverName(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.GfxDriverName(2));
            LegacyGraphics.HidePointer();
            LegacyGraphics.ShowPointer();
            var afterGraphicsHandle = LegacyWindowBindings.SystemProperty("AppHWND");
                Assert.Equal(windowHandleText, afterGraphicsHandle);
                Assert.Equal(1, LegacyWindowBindings.api_GetClientRect(windowToken, bank));
                Assert.Equal(320, LegacyBanks.PeekInt(bank, 8));
                Assert.Equal(240, LegacyBanks.PeekInt(bank, 12));
            }
            finally { LegacyBanks.FreeBank(bank); }
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyInput.CloseWindow();
            LegacyGraphics.ResetForTests();
        }
    }
}
