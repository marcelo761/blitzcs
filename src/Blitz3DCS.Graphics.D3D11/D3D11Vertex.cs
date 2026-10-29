using System.Numerics;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Graphics.D3D11;

[StructLayout(LayoutKind.Sequential)]
public readonly struct D3D11Vertex
{
    public readonly Vector3 Position;
    public readonly Vector4 Color;
    public readonly Vector3 Normal;
    public readonly Vector2 TexCoord;
    public readonly Vector2 TexCoord2;

    public D3D11Vertex(Vector3 position, Vector4 color)
        : this(position, color, Vector3.UnitZ, Vector2.Zero)
    {
    }

    public D3D11Vertex(Vector3 position, Vector4 color, Vector3 normal, Vector2 texCoord, Vector2 texCoord2 = default)
    {
        Position = position;
        Color = color;
        Normal = normal;
        TexCoord = texCoord;
        TexCoord2 = texCoord2;
    }
}

internal readonly record struct Rgba32(byte R, byte G, byte B, byte A);
