using System.Numerics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Linear camera-space-Z fog applied to mesh draws. Equal endpoints use a step at that depth.</summary>
public readonly record struct D3D11Fog(Vector3 Color, float Start, float End);
