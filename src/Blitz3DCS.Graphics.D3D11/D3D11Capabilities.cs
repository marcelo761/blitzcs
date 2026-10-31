using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Queries D3D11 hardware support without creating a window or swap chain.</summary>
public static class D3D11Capabilities
{
    private static readonly Lazy<bool> Windowed3DSupport = new(ProbeHardwareDevice);

    /// <summary>Whether a hardware D3D11 device can be created for the windowed renderer.</summary>
    public static bool SupportsWindowed3D => OperatingSystem.IsWindows() && Windowed3DSupport.Value;

    private static bool ProbeHardwareDevice()
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                out device, out context).CheckError();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            context?.Dispose();
            device?.Dispose();
        }
    }
}
