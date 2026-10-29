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
    /// Records the legacy full-scene antialias request. The Direct3D 11 backend has no multisampled
    /// render path yet, so enabling it fails explicitly rather than pretending to antialias.
    /// </summary>
    public static void AntiAlias(int enable)
    {
        CheckThreadIfStarted();
        if (enable != 0)
            throw new NotSupportedException("AntiAlias 1 is not implemented: the Direct3D 11 backend renders without multisampling. AntiAlias 0 is accepted.");
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
