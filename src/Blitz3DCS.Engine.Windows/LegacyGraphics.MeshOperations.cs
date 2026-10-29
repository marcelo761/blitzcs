using System.Numerics;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    /// <summary>Clears vertex and/or triangle buffers while preserving the surface handle and material.</summary>
    public static void ClearSurface(int surface, int clearVertices = 1, int clearTriangles = 1)
    {
        CheckThreadIfStarted();
        var state = GetSurface(surface);
        if (clearVertices != 0) state.Vertices.Clear();
        if (clearTriangles != 0) state.Indices.Clear();
        // Surface::clear increments its geometry generation even for (0, 0).
        state.Dirty = true;
    }

    public static float VertexX(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Position.X;
    public static float VertexY(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Position.Y;
    public static float VertexZ(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Position.Z;
    public static float VertexNX(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Normal.X;
    public static float VertexNY(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Normal.Y;
    public static float VertexNZ(int surface, int vertex) => GetVertex(GetSurface(surface), vertex).Normal.Z;

    public static float VertexU(int surface, int vertex, int coordSet = 0)
    {
        ValidateCoordSet(coordSet);
        var texCoord = GetVertex(GetSurface(surface), vertex);
        return (coordSet == 0 ? texCoord.TexCoord : texCoord.TexCoord2).X;
    }

    public static float VertexV(int surface, int vertex, int coordSet = 0)
    {
        ValidateCoordSet(coordSet);
        var texCoord = GetVertex(GetSurface(surface), vertex);
        return (coordSet == 0 ? texCoord.TexCoord : texCoord.TexCoord2).Y;
    }

    public static float VertexW(int surface, int vertex, int coordSet = 0)
    {
        ValidateCoordSet(coordSet);
        _ = GetVertex(GetSurface(surface), vertex);
        return 1;
    }

    public static void VertexNormal(int surface, int vertex, float nx, float ny, float nz)
    {
        CheckThreadIfStarted();
        var state = GetSurface(surface);
        var old = GetVertex(state, vertex);
        state.Vertices[vertex] = new D3D11Vertex(old.Position, old.Color, new Vector3(nx, ny, nz), old.TexCoord, old.TexCoord2);
        state.Dirty = true;
    }

    public static int TriangleVertex(int surface, int triangle, int corner)
    {
        var state = GetSurface(surface);
        if ((uint)triangle >= (uint)(state.Indices.Count / 3)) throw new ArgumentOutOfRangeException(nameof(triangle));
        if ((uint)corner >= 3) throw new ArgumentOutOfRangeException(nameof(corner));
        return checked((int)state.Indices[triangle * 3 + corner]);
    }

    public static void UpdateNormals(int mesh)
    {
        CheckThreadIfStarted();
        foreach (var handle in GetMesh(mesh).SurfaceHandles)
        {
            var surface = GetSurface(handle);
            var accumulated = new Dictionary<Vector3, Vector3>();
            for (var index = 0; index < surface.Indices.Count; index += 3)
            {
                var a = surface.Vertices[checked((int)surface.Indices[index])].Position;
                var b = surface.Vertices[checked((int)surface.Indices[index + 1])].Position;
                var c = surface.Vertices[checked((int)surface.Indices[index + 2])].Position;
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.Length() <= 0.000001f) continue;
                var normal = Vector3.Normalize(cross);
                accumulated[a] = accumulated.GetValueOrDefault(a) + normal;
                accumulated[b] = accumulated.GetValueOrDefault(b) + normal;
                accumulated[c] = accumulated.GetValueOrDefault(c) + normal;
            }

            for (var index = 0; index < surface.Vertices.Count; index++)
            {
                var vertex = surface.Vertices[index];
                var sum = accumulated.GetValueOrDefault(vertex.Position);
                var normal = sum.LengthSquared() > 0 ? Vector3.Normalize(sum) : Vector3.Zero;
                surface.Vertices[index] = new D3D11Vertex(vertex.Position, vertex.Color, normal, vertex.TexCoord, vertex.TexCoord2);
            }
            surface.Dirty = true;
        }
    }

    public static void ScaleMesh(int mesh, float x, float y, float z) => TransformMesh(mesh,
        position => new Vector3(position.X * x, position.Y * y, position.Z * z),
        normal => new Vector3(normal.X * y * z, normal.Y * x * z, normal.Z * x * y));

    public static void PositionMesh(int mesh, float x, float y, float z)
    {
        var offset = new Vector3(x, y, z);
        TransformMesh(mesh, position => position + offset, static normal => normal);
    }

    public static void RotateMesh(int mesh, float pitch, float yaw, float roll)
    {
        var p = DegreesToRadians(pitch);
        var y = DegreesToRadians(yaw);
        var r = DegreesToRadians(roll);
        TransformMesh(mesh,
            point => RotateBlitz(point, p, y, r),
            normal => RotateBlitz(normal, p, y, r));
    }

    public static float MeshWidth(int mesh) => MeshBounds(mesh, static (minimum, maximum) => maximum.X - minimum.X);
    public static float MeshHeight(int mesh) => MeshBounds(mesh, static (minimum, maximum) => maximum.Y - minimum.Y);
    public static float MeshDepth(int mesh) => MeshBounds(mesh, static (minimum, maximum) => maximum.Z - minimum.Z);

    public static void FlipMesh(int mesh)
    {
        CheckThreadIfStarted();
        foreach (var handle in GetMesh(mesh).SurfaceHandles)
        {
            var surface = GetSurface(handle);
            for (var index = 0; index < surface.Vertices.Count; index++)
            {
                var vertex = surface.Vertices[index];
                surface.Vertices[index] = new D3D11Vertex(vertex.Position, vertex.Color, -vertex.Normal, vertex.TexCoord, vertex.TexCoord2);
            }
            for (var index = 0; index < surface.Indices.Count; index += 3)
                (surface.Indices[index + 1], surface.Indices[index + 2]) = (surface.Indices[index + 2], surface.Indices[index + 1]);
            surface.Dirty = true;
        }
    }

    public static void AddMesh(int sourceMesh, int destinationMesh)
    {
        CheckThreadIfStarted();
        var source = GetMesh(sourceMesh);
        var destination = GetMesh(destinationMesh);
        if (ReferenceEquals(source.Geometry, destination.Geometry))
            throw new ArgumentException("A mesh cannot be added to itself or to a shared copy of itself.", nameof(sourceMesh));

        foreach (var sourceHandle in source.SurfaceHandles.ToArray())
        {
            var sourceSurface = GetSurface(sourceHandle);
            var destinationSurface = FindSurfaceState(destination, sourceSurface.Material);
            if (destinationSurface is null)
            {
                var handle = CreateSurface(destinationMesh);
                destinationSurface = GetSurface(handle);
                var oldMaterial = destinationSurface.Material;
                destinationSurface.Material = sourceSurface.Material.Copy();
                oldMaterial.Dispose();
            }

            var vertexOffset = destinationSurface.Vertices.Count;
            destinationSurface.Vertices.AddRange(sourceSurface.Vertices);
            foreach (var index in sourceSurface.Indices)
                destinationSurface.Indices.Add(checked(index + (uint)vertexOffset));
            destinationSurface.Dirty = true;
        }
    }

    public static int CopyMesh(int mesh, int parent = 0)
    {
        _ = GetMesh(mesh);
        var copy = CreateMesh(parent);
        try
        {
            AddMesh(mesh, copy);
            return copy;
        }
        catch
        {
            FreeEntity(copy);
            throw;
        }
    }

    public static int FindSurface(int mesh, int brush)
    {
        CheckThreadIfStarted();
        var target = GetBrush(brush);
        var found = FindSurfaceState(GetMesh(mesh), target);
        return found?.Handle ?? 0;
    }

    private static void TransformMesh(int mesh, Func<Vector3, Vector3> transformPosition, Func<Vector3, Vector3> transformNormal)
    {
        CheckThreadIfStarted();
        foreach (var handle in GetMesh(mesh).SurfaceHandles)
        {
            var surface = GetSurface(handle);
            for (var index = 0; index < surface.Vertices.Count; index++)
            {
                var vertex = surface.Vertices[index];
                surface.Vertices[index] = new D3D11Vertex(transformPosition(vertex.Position), vertex.Color,
                    transformNormal(vertex.Normal), vertex.TexCoord, vertex.TexCoord2);
            }
            surface.Dirty = true;
        }
    }

    private static float MeshBounds(int mesh, Func<Vector3, Vector3, float> extent)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var handle in GetMesh(mesh).SurfaceHandles)
        foreach (var vertex in GetSurface(handle).Vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        return extent(minimum, maximum);
    }

    private static SurfaceState? FindSurfaceState(MeshEntity mesh, EntityMaterial material)
    {
        foreach (var handle in mesh.SurfaceHandles)
        {
            var surface = GetSurface(handle);
            if (SameMaterial(surface.Material, material)) return surface;
        }
        return null;
    }

    private static bool SameMaterial(EntityMaterial left, EntityMaterial right)
    {
        if (left.Color != right.Color || left.Shininess != right.Shininess || left.Blend != right.Blend || left.Effects != right.Effects)
            return false;
        for (var index = 0; index < left.Layers.Length; index++)
            if (left.Layers[index] != right.Layers[index]) return false;
        return true;
    }

    private static Vector3 RotateBlitz(Vector3 value, float pitch, float yaw, float roll)
    {
        var sin = MathF.Sin(roll);
        var cos = MathF.Cos(roll);
        value = new Vector3(cos * value.X - sin * value.Y, sin * value.X + cos * value.Y, value.Z);
        sin = MathF.Sin(pitch);
        cos = MathF.Cos(pitch);
        value = new Vector3(value.X, cos * value.Y + sin * value.Z, -sin * value.Y + cos * value.Z);
        sin = MathF.Sin(yaw);
        cos = MathF.Cos(yaw);
        return new Vector3(cos * value.X - sin * value.Z, value.Y, sin * value.X + cos * value.Z);
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static void ValidateCoordSet(int coordSet)
    {
        if ((uint)coordSet > 1) throw new ArgumentOutOfRangeException(nameof(coordSet));
    }
}
