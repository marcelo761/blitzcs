using System.Numerics;

using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

/// <summary>Builds the visible portion of a subdivided horizontal plane.</summary>
internal sealed class PlaneGrid
{
    private static readonly Vector4 White = Vector4.One;
    private static readonly Vector3 Up = Vector3.UnitY;

    public PlaneGrid(int segments)
    {
        if (segments is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(segments), "Segments must be between 1 and 20.");

        Segments = segments;
        var capacity = checked(segments * segments * 9);
        Vertices = new D3D11Vertex[capacity];
        Indices = new uint[capacity];
        for (var i = 0; i < capacity; i++)
            Indices[i] = (uint)i;
    }

    public int Segments { get; }
    public D3D11Vertex[] Vertices { get; }
    public uint[] Indices { get; }
    public int IndexCount { get; private set; }

    public void Build(
        Vector3 eye,
        Vector3 topLeftFar,
        Vector3 topRightFar,
        Vector3 bottomRightFar,
        Vector3 bottomLeftFar)
    {
        Array.Clear(Vertices);
        IndexCount = 0;
        if (eye.Y <= 0)
            return;

        Span<Vector3> corners = stackalloc Vector3[4];
        // The upstream routine uses five slots. Keep a sixth scratch slot so
        // malformed/non-frustum inputs cannot overrun, then apply its >5 skip.
        Span<Vector3> clipped = stackalloc Vector3[6];
        for (var x = 0; x < Segments; x++)
        {
            var x0 = (float)x / Segments;
            var x1 = (float)(x + 1) / Segments;
            var top0 = Vector3.Lerp(topLeftFar, topRightFar, x0);
            var top1 = Vector3.Lerp(topLeftFar, topRightFar, x1);
            var bottom0 = Vector3.Lerp(bottomLeftFar, bottomRightFar, x0);
            var bottom1 = Vector3.Lerp(bottomLeftFar, bottomRightFar, x1);

            for (var y = 0; y < Segments; y++)
            {
                var y0 = (float)y / Segments;
                var y1 = (float)(y + 1) / Segments;
                corners[0] = Vector3.Lerp(top0, bottom0, y0);
                corners[1] = Vector3.Lerp(top1, bottom1, y0);
                corners[2] = Vector3.Lerp(top1, bottom1, y1);
                corners[3] = Vector3.Lerp(top0, bottom0, y1);

                var count = ClipCell(eye, corners, clipped);
                if (count is < 3 or > 5)
                    continue;

                for (var triangle = 2; triangle < count; triangle++)
                {
                    WriteVertex(clipped[0]);
                    WriteVertex(clipped[triangle - 1]);
                    WriteVertex(clipped[triangle]);
                }
            }
        }
    }

    private static int ClipCell(Vector3 eye, ReadOnlySpan<Vector3> corners, Span<Vector3> output)
    {
        var count = 0;
        for (var i = 0; i < 4; i++)
        {
            var vertex = corners[i];
            var previous = corners[(i + 3) & 3];
            if (vertex.Y > 0)
            {
                if (previous.Y <= 0)
                    output[count++] = IntersectHorizon(previous, vertex);
            }
            else
            {
                if (previous.Y > 0)
                    output[count++] = IntersectHorizon(previous, vertex);
                output[count++] = ProjectToHorizon(eye, vertex);
            }
        }

        return count;
    }

    private static Vector3 IntersectHorizon(Vector3 from, Vector3 to)
    {
        var t = from.Y / (from.Y - to.Y);
        var point = Vector3.Lerp(from, to, t);
        point.Y = 0;
        return point;
    }

    private static Vector3 ProjectToHorizon(Vector3 eye, Vector3 point)
    {
        var t = eye.Y / (eye.Y - point.Y);
        var projected = Vector3.Lerp(eye, point, t);
        projected.Y = 0;
        return projected;
    }

    private void WriteVertex(Vector3 position)
    {
        var index = IndexCount;
        Vertices[index] = new D3D11Vertex(
            position,
            White,
            Up,
            new Vector2(position.X, position.Z),
            new Vector2(position.X, position.Z));
        IndexCount = index + 1;
    }
}
