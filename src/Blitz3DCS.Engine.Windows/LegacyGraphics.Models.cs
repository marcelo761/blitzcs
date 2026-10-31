using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    /// <summary>Loads and collapses a B3D hierarchy into a static mesh, baking its node transforms.</summary>
    private static int LoadMeshCore(string file, int parent)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        CheckThreadIfStarted();
        var path = Path.GetFullPath(file.Replace('\\', Path.DirectorySeparatorChar));
        ValidateModelFormat(path);
        B3dModel model;
        try
        {
            using var stream = File.OpenRead(path);
            model = ReadModelData(stream, path, preserveAnimation: false);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        var surfaces = BuildStaticB3dSurfaces(model);
        var textures = new int[model.Textures.Count];
        var mesh = 0;
        try
        {
            LoadModelTextures(model, path, textures);
            mesh = CreateMesh(parent);
            foreach (var source in surfaces)
            {
                var surface = GetSurface(CreateSurface(mesh));
                surface.Vertices.AddRange(source.Vertices);
                surface.Indices.AddRange(source.Indices);
                if (source.BrushId >= 0)
                    SetSurfaceMaterialForLoading(surface.Handle, BuildMaterial(model.Brushes[source.BrushId], textures));
            }
            return mesh;
        }
        catch
        {
            if (mesh != 0) FreeEntity(mesh);
            throw;
        }
        finally
        {
            foreach (var texture in textures)
                if (texture != 0) FreeTexture(texture);
        }
    }

    private static void ValidateModelFormat(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".b3d", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".x", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This model loader supports B3D and DirectX X files.");
    }

    private static B3dModel ReadModelData(Stream stream, string path, bool preserveAnimation) =>
        Path.GetExtension(path).Equals(".x", StringComparison.OrdinalIgnoreCase)
            ? XModelLoader.Load(stream, preserveAnimation)
            : B3dLoader.Load(stream);

    private static void LoadModelTextures(B3dModel model, string path, int[] textures)
    {
        for (var index = 0; index < textures.Length; index++)
        {
            var source = model.Textures[index];
            var storedPath = source.Name.Replace('\\', Path.DirectorySeparatorChar);
            // CachedTexture first searches next to the model using only the filename,
            // including when the author embedded an absolute path from another machine.
            var texturePath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(storedPath));
            var texture = LoadTexture(texturePath, source.Flags & 0xffff);
            if (texture == 0 && !string.Equals(texturePath, storedPath, StringComparison.OrdinalIgnoreCase))
                texture = LoadTexture(storedPath, source.Flags & 0xffff);
            textures[index] = texture;
            if (texture == 0) continue;
            TextureBlend(texture, source.Blend);
            TextureCoords(texture, (source.Flags & 0x10000) != 0 ? 1 : 0);
            var rep = GetTexture(texture);
            rep.Scale = new Vector2(source.Scale.X, source.Scale.Y);
            rep.Position = new Vector2(source.Position.X, source.Position.Y);
            rep.Rotation = source.Rotation;
            rep.UpdateTransform();
        }
    }

    internal sealed record StaticB3dSurface(int BrushId, D3D11Vertex[] Vertices, uint[] Indices);

    internal static List<StaticB3dSurface> BuildStaticB3dSurfaces(B3dModel model)
    {
        var result = new List<StaticB3dSurface>();
        Visit(model.Root, Matrix4x4.Identity);
        return result;

        void Visit(B3dNode node, Matrix4x4 parent)
        {
            var world = B3dAnimationEvaluator.ComposeLocalMatrix(node.Position, node.Scale, node.Rotation) * parent;
            // The original collapse walks children before consuming their parent mesh.
            foreach (var child in node.Children) Visit(child, world);
            if (node.Mesh is not { } mesh) return;
            if (!Matrix4x4.Invert(world, out var inverse))
                throw new InvalidDataException("A B3D mesh node has a singular transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse) * world.GetDeterminant();
            var vertices = new D3D11Vertex[mesh.Vertices.Count];
            for (var i = 0; i < vertices.Length; i++)
            {
                var vertex = mesh.Vertices[i];
                var p = vertex.Position;
                var n = vertex.Normal;
                var c = vertex.Color;
                vertices[i] = new D3D11Vertex(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), world),
                    c is { } color ? new Vector4(color.R, color.G, color.B, color.A) : Vector4.One,
                    n is { } normal ? Vector3.TransformNormal(new Vector3(normal.X, normal.Y, normal.Z), normalMatrix) : Vector3.Zero,
                    Uv(vertex, 0), Uv(vertex, 1));
            }
            foreach (var group in mesh.TriangleGroups)
            {
                var indices = new uint[checked(group.Triangles.Count * 3)];
                for (var i = 0; i < group.Triangles.Count; i++)
                {
                    var t = group.Triangles[i];
                    indices[i * 3] = (uint)t.A;
                    indices[i * 3 + 1] = (uint)t.B;
                    indices[i * 3 + 2] = (uint)t.C;
                }
                var surfaceVertices = vertices;
                if ((mesh.VertexFlags & 1) == 0)
                {
                    surfaceVertices = (D3D11Vertex[])vertices.Clone();
                    GenerateB3dNormals(mesh, group, surfaceVertices, normalMatrix);
                }
                result.Add(new StaticB3dSurface(group.BrushId, surfaceVertices, indices));
            }
        }
    }

    private static Vector2 Uv(B3dVertex vertex, int set) => vertex.TextureCoordinates.Count > set
        ? new Vector2(vertex.TextureCoordinates[set][0], vertex.TextureCoordinates[set].Length > 1 ? vertex.TextureCoordinates[set][1] : 0)
        : Vector2.Zero;

    private static Vector3 NormalizeOrZero(Vector3 vector) => vector.LengthSquared() > 0 ? Vector3.Normalize(vector) : Vector3.Zero;

    private static void GenerateB3dNormals(B3dMesh mesh, B3dTriangleGroup group, D3D11Vertex[] output, Matrix4x4 normalMatrix)
    {
        var normals = new Dictionary<B3dVector3, Vector3>();
        foreach (var triangle in group.Triangles)
        {
            var a = mesh.Vertices[triangle.A].Position;
            var b = mesh.Vertices[triangle.B].Position;
            var c = mesh.Vertices[triangle.C].Position;
            var cross = Vector3.Cross(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z),
                new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
            if (cross.Length() <= 0.000001f) continue;
            var normal = Vector3.Normalize(cross);
            normals[a] = normals.GetValueOrDefault(a) + normal;
            normals[b] = normals.GetValueOrDefault(b) + normal;
            normals[c] = normals.GetValueOrDefault(c) + normal;
        }
        for (var i = 0; i < output.Length; i++)
        {
            var vertex = output[i];
            var normal = NormalizeOrZero(normals.GetValueOrDefault(mesh.Vertices[i].Position));
            output[i] = new D3D11Vertex(vertex.Position, vertex.Color,
                Vector3.TransformNormal(normal, normalMatrix), vertex.TexCoord, vertex.TexCoord2);
        }
    }
}
