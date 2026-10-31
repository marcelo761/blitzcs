using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static bool _wireframe;

    /// <summary>Selects wireframe rasterization for all subsequently rendered 3D meshes; 2D drawing is unaffected.</summary>
    public static void WireFrame(int enable)
    {
        CheckThreadIfStarted();
        _wireframe = enable != 0;
    }

    /// <summary>
    /// Selects full-scene multisampling using a sample count supported by color and depth targets.
    /// </summary>
    public static void AntiAlias(int enable)
    {
        CheckThreadIfStarted();
        if (enable == 0 && _renderer is null) return;
        RequireRenderer().SetAntialias(enable != 0);
    }

    /// <summary>Sets the global mip LOD bias used by every texture stage; it resets with each new display.</summary>
    public static void TextureLodBias(float bias)
    {
        CheckThreadIfStarted();
        RequireRenderer().TextureLodBias = bias;
    }

    /// <summary>Returns the number of live texture objects, matching the legacy active texture counter.</summary>
    public static int ActiveTextures()
    {
        CheckThreadIfStarted();
        return Textures.Count;
    }

    /// <summary>Returns the adapter's total dedicated video memory in bytes, saturated to a BASIC integer.</summary>
    public static int TotalVidMem()
    {
        CheckThreadIfStarted();
        return D3D11VideoMemory.ToBasicInteger(RequireRenderer().QueryVideoMemory().Total);
    }

    /// <summary>Returns the video memory still available to this process in bytes, saturated to a BASIC integer.</summary>
    public static int AvailVidMem()
    {
        CheckThreadIfStarted();
        return D3D11VideoMemory.ToBasicInteger(RequireRenderer().QueryVideoMemory().Available);
    }

    internal static bool WireframeForRendering => _wireframe;
    internal static void ResetRenderStateForTesting() => _wireframe = false;
}
