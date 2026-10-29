using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static long GeometryVersion(MeshGeometry geometry)
    {
        long version = geometry.SurfaceHandles.Count;
        foreach (var handle in geometry.SurfaceHandles) version += Surfaces[handle].Version;
        return version;
    }

    private static bool IsMeshVisible(MeshEntity mesh, in Matrix4x4 worldViewProjection)
    {
        if (mesh.Geometry.CullBox is { } box)
            return BoxIntersectsClipVolume(box.Minimum, box.Maximum, worldViewProjection);
        // A skinned vertex may leave its bind-pose bounds. Keep it visible until
        // per-instance animated bounds are available.
        if (mesh.Animated is { Skin: not null }) return true;
        var geometry = mesh.Geometry;
        EnsureGeometryBounds(geometry);
        return geometry.HasBounds && BoxIntersectsClipVolume(geometry.BoundsMinimum, geometry.BoundsMaximum, worldViewProjection);
    }

    private static void EnsureGeometryBounds(MeshGeometry geometry)
    {
        var version = GeometryVersion(geometry);
        if (geometry.BoundsVersion != version)
        {
            var minimum = new Vector3(float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity);
            var hasVertices = false;
            foreach (var handle in geometry.SurfaceHandles)
                foreach (var vertex in Surfaces[handle].Vertices)
                {
                    hasVertices = true;
                    minimum = Vector3.Min(minimum, vertex.Position);
                    maximum = Vector3.Max(maximum, vertex.Position);
                }
            geometry.BoundsMinimum = minimum;
            geometry.BoundsMaximum = maximum;
            geometry.HasBounds = hasVertices;
            geometry.BoundsVersion = version;
        }
    }

    internal static bool BoxIntersectsClipVolume(Vector3 minimum, Vector3 maximum, in Matrix4x4 matrix)
    {
        var center = (minimum + maximum) * .5f;
        var extent = (maximum - minimum) * .5f;
        return Inside(new Vector4(matrix.M11 + matrix.M14, matrix.M21 + matrix.M24, matrix.M31 + matrix.M34, matrix.M41 + matrix.M44))
            && Inside(new Vector4(matrix.M14 - matrix.M11, matrix.M24 - matrix.M21, matrix.M34 - matrix.M31, matrix.M44 - matrix.M41))
            && Inside(new Vector4(matrix.M12 + matrix.M14, matrix.M22 + matrix.M24, matrix.M32 + matrix.M34, matrix.M42 + matrix.M44))
            && Inside(new Vector4(matrix.M14 - matrix.M12, matrix.M24 - matrix.M22, matrix.M34 - matrix.M32, matrix.M44 - matrix.M42))
            && Inside(new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43))
            && Inside(new Vector4(matrix.M14 - matrix.M13, matrix.M24 - matrix.M23, matrix.M34 - matrix.M33, matrix.M44 - matrix.M43));

        bool Inside(Vector4 plane)
        {
            var distance = plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W;
            var radius = MathF.Abs(plane.X) * extent.X + MathF.Abs(plane.Y) * extent.Y + MathF.Abs(plane.Z) * extent.Z;
            // Invalid source geometry must not accidentally disappear through a NaN comparison.
            return !(distance + radius < 0);
        }
    }
}
