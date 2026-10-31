using System.Numerics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>A texture stage and its sampler/blend settings for one mesh draw.</summary>
public readonly record struct D3D11TextureLayer(
    D3D11Texture? Texture,
    int Blend = 2,
    bool ClampU = false,
    bool ClampV = false,
    bool UseAlpha = true,
    Matrix3x2? UvTransform = null,
    int CoordinateSet = 0,
    bool Masked = false,
    Vector4? BumpMatrix = null,
    float BumpScale = 0,
    float BumpOffset = 0);
