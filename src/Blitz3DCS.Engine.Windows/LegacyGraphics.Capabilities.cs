using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    /// <summary>Returns 1 when a windowed hardware D3D11 device is available, otherwise 0.</summary>
    public static int Windowed3D() => D3D11Capabilities.SupportsWindowed3D ? 1 : 0;
}
