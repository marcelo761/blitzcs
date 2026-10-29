using System.Numerics;
using System.Runtime.CompilerServices;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly ConditionalWeakTable<CameraState, FogState> CameraFog = new();

    /// <summary>Sets a camera's linear fog color. The default is black.</summary>
    public static void CameraFogColor(int camera, int red, int green, int blue)
    {
        CheckThreadIfStarted();
        ValidateRgb(red, green, blue);
        CameraFog.GetValue(GetCamera(camera), static _ => new()).Color = new Vector3(red, green, blue) / 255f;
    }

    /// <summary>Sets the camera-space Z distances where linear fog begins and becomes complete.</summary>
    public static void CameraFogRange(int camera, float near, float far)
    {
        CheckThreadIfStarted();
        if (!float.IsFinite(near)) throw new ArgumentOutOfRangeException(nameof(near));
        if (!float.IsFinite(far)) throw new ArgumentOutOfRangeException(nameof(far));
        var state = CameraFog.GetValue(GetCamera(camera), static _ => new());
        state.Start = near;
        state.End = far;
    }

    /// <summary>Sets camera fog mode: zero disables it and one selects linear fog.</summary>
    public static void CameraFogMode(int camera, int mode)
    {
        CheckThreadIfStarted();
        CameraFog.GetValue(GetCamera(camera), static _ => new()).Mode = mode;
    }

    internal static D3D11Fog? GetCameraFogForRendering(int camera) =>
        CameraFog.TryGetValue(GetCamera(camera), out var state) && state.Mode == 1
            ? new D3D11Fog(state.Color, state.Start, state.End)
            : null;

    internal static (int Mode, Vector3 Color, float Start, float End) GetCameraFogForTesting(int camera)
    {
        var state = CameraFog.GetValue(GetCamera(camera), static _ => new());
        return (state.Mode, state.Color, state.Start, state.End);
    }

    private sealed class FogState
    {
        public int Mode;
        public Vector3 Color;
        public float Start = 1;
        public float End = 1000;
    }
}
