using System.Numerics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>
/// Direct-mapped cache of the inverse-transpose of world matrices, keyed by the exact matrix. Static entities
/// repeat the same world every frame, so the per-draw inversion is replaced by a 16-float compare.
/// </summary>
internal sealed class NormalMatrixCache
{
    private const int Size = 512;
    private readonly Matrix4x4[] _worlds = new Matrix4x4[Size];
    private readonly Matrix4x4[] _normals = new Matrix4x4[Size];
    private readonly bool[] _used = new bool[Size];

    internal int Inversions { get; private set; }

    internal Matrix4x4 Get(in Matrix4x4 world)
    {
        var slot = (int)(Hash(world) & (Size - 1));
        if (_used[slot] && _worlds[slot] == world) return _normals[slot];
        var normal = Matrix4x4.Identity;
        Inversions++;
        if (Matrix4x4.Invert(world, out var inverse)) normal = Matrix4x4.Transpose(inverse);
        _worlds[slot] = world;
        _normals[slot] = normal;
        _used[slot] = true;
        return normal;
    }

    private static uint Hash(in Matrix4x4 m)
    {
        var h = new HashCode();
        h.Add(m.M11); h.Add(m.M22); h.Add(m.M33); h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M12); h.Add(m.M21);
        return (uint)h.ToHashCode();
    }
}
