using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class B3dLoaderTests
{
    [Fact]
    public void Parses_scene_mesh_material_texture_animation_and_bone_data()
    {
        var vertices = Chunk("VRTS", Build(writer =>
        {
            writer.Write(3); // normals and colors
            writer.Write(1); // texture coordinate sets
            writer.Write(2); // two floats per set
            WriteVertex(writer, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, .25f, .75f);
            WriteVertex(writer, 1, 0, 0, 0, 0, 1, 0, 1, 0, 1, .5f, .25f);
            WriteVertex(writer, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, .1f, .9f);
        }));
        var triangles = Chunk("TRIS", Build(writer => { writer.Write(0); writer.Write(0); writer.Write(1); writer.Write(2); }));
        var mesh = Chunk("MESH", Join(Int(0), vertices, triangles));
        var keys = Chunk("KEYS", Build(writer =>
        {
            writer.Write(7); // position, scale, rotation
            writer.Write(12);
            WriteFloats(writer, 4, 5, 6, 2, 3, 4, 1, 0, 0, 0);
        }));
        var bone = Chunk("BONE", Build(writer => { writer.Write(2); writer.Write(.65f); }));
        var child = Chunk("NODE", NodeHeader("caf\u00e9", (0, 2, 0), (1, 1, 1), (1, 0, 0, 0)));
        var nodeAnimation = Chunk("ANIM", Build(writer => { writer.Write(1); writer.Write(24); writer.Write(30f); }));
        var rootNode = Chunk("NODE", Join(NodeHeader("root", (1, 2, 3), (2, 2, 2), (0, .25f, 0, .9682458f)), mesh, bone, keys, nodeAnimation, child));
        var texture = Chunk("TEXS", Build(writer =>
        {
            WriteString(writer, "wall.png"); writer.Write(0x10001); writer.Write(2);
            WriteFloats(writer, .25f, .5f, 2, 3, 45);
        }));
        var brush = Chunk("BRUS", Build(writer =>
        {
            writer.Write(1); WriteString(writer, "paint"); WriteFloats(writer, .1f, .2f, .3f, .8f);
            writer.Write(.5f); writer.Write(1); writer.Write(4); writer.Write(0);
        }));
        var file = Chunk("BB3D", Join(Int(1), texture, brush, rootNode));

        var model = B3dLoader.Parse(file);

        Assert.Equal(1, model.Version);
        Assert.Equal("root", model.Root.Name);
        Assert.Equal(new B3dVector3(1, 2, 3), model.Root.Position);
        Assert.Equal(new B3dVector3(2, 2, 2), model.Root.Scale);
        Assert.Equal(new B3dQuaternion(0, .25f, 0, .9682458f), model.Root.Rotation);
        Assert.Equal("café", Assert.Single(model.Root.Children).Name);
        Assert.Equal(new B3dVector3(0, 2, 0), model.Root.Children[0].Position);
        var loadedTexture = Assert.Single(model.Textures);
        Assert.Equal((0x10001, 2, new B3dVector2(.25f, .5f), new B3dVector2(2, 3), 45f),
            (loadedTexture.Flags, loadedTexture.Blend, loadedTexture.Position, loadedTexture.Scale, loadedTexture.Rotation));
        Assert.Equal(new B3dColor(.1f, .2f, .3f, .8f), model.Brushes[0].Color);
        Assert.Equal(0, model.Brushes[0].TextureIds[0]);
        var loadedMesh = Assert.IsType<B3dMesh>(model.Root.Mesh);
        Assert.Equal(3, loadedMesh.VertexFlags);
        Assert.Equal(new B3dVector3(0, 0, 1), loadedMesh.Vertices[0].Normal);
        Assert.Equal(new B3dColor(1, 0, 0, 1), loadedMesh.Vertices[0].Color);
        Assert.Equal(new[] { .25f, .75f }, loadedMesh.Vertices[0].TextureCoordinates[0]);
        Assert.Equal(new B3dTriangle(0, 1, 2), Assert.Single(loadedMesh.TriangleGroups[0].Triangles));
        Assert.Equal(new B3dBoneWeight(2, .65f), Assert.Single(model.Root.BoneWeights));
        Assert.Equal(new B3dAnimation(1, 24, 30), model.Animation);
        Assert.Equal(new B3dAnimationKey(7, 12, new(4, 5, 6), new(2, 3, 4), new(1, 0, 0, 0)), Assert.Single(model.Root.Keys));
    }

    [Fact]
    public void Skips_unknown_chunks_and_rejects_chunks_that_escape_their_parent()
    {
        var valid = Chunk("BB3D", Join(Int(1), Chunk("JUNK", [1, 2, 3]),
            Chunk("NODE", NodeHeader("", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0)))));
        Assert.Equal("", B3dLoader.Parse(valid).Root.Name);

        var malformed = Chunk("BB3D", Join(Int(1), Encoding.ASCII.GetBytes("JUNK"), Int(100)));
        Assert.Throws<InvalidDataException>(() => B3dLoader.Parse(malformed));
    }

    [Fact]
    public void Rejects_mesh_indices_outside_vertex_array()
    {
        var vertices = Chunk("VRTS", Build(writer => { writer.Write(0); writer.Write(0); writer.Write(2); WriteFloats(writer, 0, 0, 0, 1, 0, 0); }));
        var triangles = Chunk("TRIS", Build(writer => { writer.Write(-1); writer.Write(0); writer.Write(1); writer.Write(2); }));
        var mesh = Chunk("MESH", Join(Int(-1), vertices, triangles));
        var file = Chunk("BB3D", Join(Int(1), Chunk("NODE", Join(NodeHeader("", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0)), mesh))));

        Assert.Throws<InvalidDataException>(() => B3dLoader.Parse(file));
    }

    private static byte[] NodeHeader(string name, (float X, float Y, float Z) position, (float X, float Y, float Z) scale,
            (float W, float X, float Y, float Z) rotation) => Build(writer =>
    {
        WriteString(writer, name);
        WriteFloats(writer, position.X, position.Y, position.Z, scale.X, scale.Y, scale.Z,
            rotation.W, rotation.X, rotation.Y, rotation.Z);
    });

    private static void WriteVertex(BinaryWriter writer, float x, float y, float z, float nx, float ny, float nz,
        float r, float g, float b, float a, float u, float v) => WriteFloats(writer, x, y, z, nx, ny, nz, r, g, b, a, u, v);

    private static void WriteFloats(BinaryWriter writer, params float[] values)
    {
        foreach (var value in values) writer.Write(value);
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }

    private static byte[] Int(int value) => Build(writer => writer.Write(value));

    private static void WriteString(BinaryWriter writer, string value)
    {
        foreach (var character in value) writer.Write((byte)(character == 'é' ? 0xE9 : character));
        writer.Write((byte)0);
    }

    private static byte[] Chunk(string tag, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write(payload.Length);
        writer.Write(payload);
        return stream.ToArray();
    }

    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
