using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsModelTests
{
    [Fact]
    public void X_files_use_the_same_static_and_hierarchy_bridges_with_identity_synthetic_root()
    {
        const string source = """
            xof 0303txt 0032
            Frame part {
              FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 2,3,4,1;; }
              Mesh triangle { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; }
            }
            """;
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-x-model-{Guid.NewGuid():N}.x");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllText(file, source);
            var collapsed = LegacyGraphics.LoadMesh(file);
            var surface = LegacyGraphics.GetSurface(collapsed, 1);
            Assert.Equal(new Vector3(2, 3, 4), LegacyGraphics.GetVertexForTesting(surface, 0).Position);
            var hierarchy = LegacyGraphics.LoadAnimMesh(file);
            var part = LegacyScene.FindChild(hierarchy, "part");
            Assert.NotEqual(0, part);
            Assert.Equal(2, LegacyScene.EntityX(part));
            Assert.Equal(3, LegacyScene.EntityY(part));
            Assert.Equal(4, LegacyScene.EntityZ(part));
            Assert.Equal(Vector3.Zero, LegacyGraphics.GetVertexForTesting(LegacyGraphics.GetSurface(part, 1), 0).Position);
            Assert.Equal(-1, LegacyGraphics.AnimLength(hierarchy));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Static_model_bakes_hierarchy_preserves_both_uv_sets_and_frees_loaded_surfaces()
    {
        var bytes = ModelBytes(normals: true);
        var parsed = B3dLoader.Parse(bytes);
        var surface = Assert.Single(LegacyGraphics.BuildStaticB3dSurfaces(parsed));
        Assert.Equal(new Vector3(14, 0, 0), surface.Vertices[0].Position);
        Assert.Equal(new Vector3(0, 0, 6), surface.Vertices[0].Normal);
        Assert.Equal(new Vector2(.1f, .2f), surface.Vertices[0].TexCoord);
        Assert.Equal(new Vector2(.75f, .25f), surface.Vertices[0].TexCoord2);
        Assert.Equal(new uint[] { 0, 1, 2 }, surface.Indices);

        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-model-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, bytes);
            var parent = LegacyScene.CreatePivot();
            var mesh = LegacyGraphics.LoadMesh(file, parent);
            Assert.Equal(parent, LegacyScene.GetParent(mesh));
            Assert.Equal(0, LegacyScene.CountChildren(mesh));
            var handle = LegacyGraphics.GetSurface(mesh, 1);
            Assert.Equal(3, LegacyGraphics.CountVertices(handle));
            Assert.Equal(new Vector3(14, 0, 0), LegacyGraphics.GetVertexForTesting(handle, 0).Position);
            LegacyGraphics.FreeEntity(mesh);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountVertices(handle));
            Assert.Equal(0, LegacyGraphics.LoadMesh(file + ".b3d"));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_normals_are_generated_then_transformed_by_the_cofactor()
    {
        var parsed = B3dLoader.Parse(ModelBytes(normals: false));
        var surface = Assert.Single(LegacyGraphics.BuildStaticB3dSurfaces(parsed));
        Assert.All(surface.Vertices, vertex => Assert.Equal(new Vector3(0, 0, 6), vertex.Normal));
    }

    private static byte[] ModelBytes(bool normals)
    {
        var vertices = Chunk("VRTS", Write(w =>
        {
            w.Write(normals ? 1 : 0); w.Write(2); w.Write(2);
            foreach (var p in new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, 0) })
            {
                Floats(w, p.X, p.Y, p.Z);
                if (normals) Floats(w, 0, 0, 1);
                Floats(w, .1f, .2f, .75f, .25f);
            }
        }));
        var triangles = Chunk("TRIS", Write(w => { w.Write(-1); w.Write(0); w.Write(1); w.Write(2); }));
        var mesh = Chunk("MESH", Write(w => { w.Write(-1); w.Write(vertices); w.Write(triangles); }));
        var child = Chunk("NODE", Write(w => { Header(w, "child", 1, 1, 1, 1); w.Write(mesh); }));
        var root = Chunk("NODE", Write(w => { Header(w, "root", 10, 2, 3, 4); w.Write(child); }));
        return Chunk("BB3D", Write(w => { w.Write(1); w.Write(root); }));
    }

    private static void Header(BinaryWriter w, string name, float x, float sx, float sy, float sz)
    {
        w.Write(Encoding.ASCII.GetBytes(name)); w.Write((byte)0);
        Floats(w, x, 0, 0, sx, sy, sz, 1, 0, 0, 0);
    }
    private static void Floats(BinaryWriter w, params float[] values) { foreach (var value in values) w.Write(value); }
    private static byte[] Chunk(string name, byte[] body) => Write(w => { w.Write(Encoding.ASCII.GetBytes(name)); w.Write(body.Length); w.Write(body); });
    private static byte[] Write(Action<BinaryWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) action(writer);
        return stream.ToArray();
    }
}
