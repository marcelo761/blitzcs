namespace Blitz3DCS.Graphics.D3D11;

/// <summary>A source rectangle and screen-space destination for one sprite draw.</summary>
public readonly record struct D3D11TextureRegion(
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    float DestinationX,
    float DestinationY,
    float DestinationWidth,
    float DestinationHeight);
