using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>Proves the flattened skinning path is bit-identical to the original implementation.</summary>
public sealed class B3dSkinningFastPathTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Fast_path_matches_legacy_bit_for_bit_on_a_multi_bone_rig(int seed, bool normals)
    {
        var model = BuildRig(seed, vertexCount: 257, boneCount: 12, normals);
        var pose = new B3dPose(model);
        var fast = new B3dSkinningBuffer(pose, model.Root);
        var legacy = new B3dSkinningBuffer(pose, model.Root) { ForceLegacy = true };
        var random = new Random(seed);
        var sourceNormals = new B3dVector3[257];
        for (var i = 0; i < sourceNormals.Length; i++)
            sourceNormals[i] = new B3dVector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f);

        foreach (var frame in new[] { 0f, 2.5f, 5f, 9.25f, 10f })
        {
            B3dAnimationEvaluator.EvaluateInto(model, frame, pose);
            var pa = new B3dVector3[257]; var pb = new B3dVector3[257];
            var na = normals ? new B3dVector3[257] : default; var nb = normals ? new B3dVector3[257] : default;
            fast.DeformInto(pose, pa, na);
            legacy.DeformInto(pose, pb, nb);
            Assert.Equal(pb, pa);
            if (normals) Assert.Equal(nb, na);
            var da = new B3dVector3[257]; var db = new B3dVector3[257];
            fast.DeformNormalsInto(sourceNormals, da);
            legacy.DeformNormalsInto(sourceNormals, db);
            Assert.Equal(db, da);
        }
    }

    [Fact]
    public void Fast_path_keeps_unweighted_vertices_on_the_mesh_transform_like_the_legacy_path()
    {
        var model = BuildRig(7, vertexCount: 40, boneCount: 3, normals: true, weightedEvery: 4);
        var pose = new B3dPose(model);
        var fast = new B3dSkinningBuffer(pose, model.Root);
        var legacy = new B3dSkinningBuffer(pose, model.Root) { ForceLegacy = true };
        B3dAnimationEvaluator.EvaluateInto(model, 7, pose);
        var pa = new B3dVector3[40]; var pb = new B3dVector3[40];
        var na = new B3dVector3[40]; var nb = new B3dVector3[40];
        fast.DeformInto(pose, pa, na);
        legacy.DeformInto(pose, pb, nb);
        Assert.Equal(pb, pa);
        Assert.Equal(nb, na);
    }

    private static B3dModel BuildRig(int seed, int vertexCount, int boneCount, bool normals, int weightedEvery = 1)
    {
        var random = new Random(seed);
        var vertices = new (float X, float Y, float Z, float Nx, float Ny, float Nz)[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            vertices[i] = ((float)random.NextDouble() * 10, (float)random.NextDouble() * 10, (float)random.NextDouble() * 10,
                (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, 1f);
        var mesh = normals ? MeshChunk(vertices) : MeshChunkWithoutNormals(vertices.Select(v => (v.X, v.Y, v.Z)).ToArray());
        var pairs = new List<(int Vertex, float Weight)>[boneCount];
        for (var b = 0; b < boneCount; b++) pairs[b] = [];
        for (var v = 0; v < vertexCount; v += 1)
        {
            if (v % weightedEvery != 0) continue;
            var influences = 1 + random.Next(4);
            for (var k = 0; k < influences; k++)
                pairs[random.Next(boneCount)].Add((v, .1f + (float)random.NextDouble()));
        }
        byte[]? chain = null;
        for (var b = boneCount - 1; b >= 0; b--)
        {
            var bind = (b + 1f, 0f, 0f);
            var qa = MathF.Sin(.3f * (b + 1)); var qw = MathF.Cos(.3f * (b + 1));
            var chunks = new List<byte[]>();
            foreach (var pair in pairs[b]) chunks.Add(Bone(pair.Vertex, pair.Weight));
            chunks.Add(Keys(5, [0, bind.Item1, 0, 0, 1, 0, 0, 0], [10, bind.Item1 + 2, b, 1, qw, 0, 0, qa]));
            if (chain is not null) chunks.Add(chain);
            chain = Node($"bone-{b}", bind, (1, 1, 1), (1, 0, 0, 0), chunks.ToArray());
        }
        var root = Node("mesh", (1, 2, 3), (1, 1, 1), (1, 0, 0, 0), Animation(20), mesh, chain!);
        return B3dLoader.Parse(Chunk("BB3D", Join(Int(1), root)));
    }

    private static byte[] MeshChunk(params (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices) =>
        Chunk("MESH", Join(Int(-1), Chunk("VRTS", Build(writer =>
        {
            writer.Write(1); // normals present
            writer.Write(0); // texture coordinate sets
            writer.Write(2); // texture coordinate width
            foreach (var vertex in vertices)
            {
                writer.Write(vertex.X); writer.Write(vertex.Y); writer.Write(vertex.Z);
                writer.Write(vertex.Nx); writer.Write(vertex.Ny); writer.Write(vertex.Nz);
            }
        }))));

    private static byte[] MeshChunkWithoutNormals(params (float X, float Y, float Z)[] vertices) =>
        Chunk("MESH", Join(Int(-1), Chunk("VRTS", Build(writer =>
        {
            writer.Write(0); // normals absent; the runtime generates normals per surface.
            writer.Write(0); // texture coordinate sets
            writer.Write(2); // texture coordinate width
            foreach (var vertex in vertices)
            {
                writer.Write(vertex.X); writer.Write(vertex.Y); writer.Write(vertex.Z);
            }
        }))));

    private static B3dModel Parse(byte[] node) => B3dLoader.Parse(Chunk("BB3D", Join(Int(1), node)));

    private static byte[] Node(string name, (float X, float Y, float Z) position, (float X, float Y, float Z) scale,
        (float W, float X, float Y, float Z) rotation, params byte[][] chunks) =>
        Chunk("NODE", Join(Build(writer =>
        {
            WriteString(writer, name);
            WriteFloats(writer, position.X, position.Y, position.Z, scale.X, scale.Y, scale.Z,
                rotation.W, rotation.X, rotation.Y, rotation.Z);
        }), Join(chunks)));

    private static byte[] Animation(int frames) =>
        Chunk("ANIM", Build(writer => { writer.Write(0); writer.Write(frames); writer.Write(24f); }));

    private static byte[] Keys(int flags, params float[][] keys) => Chunk("KEYS", Build(writer =>
    {
        writer.Write(flags);
        foreach (var key in keys)
        {
            writer.Write((int)key[0]);
            for (var i = 1; i < key.Length; i++) writer.Write(key[i]);
        }
    }));

    private static byte[] Bone(int vertex, float weight) =>
        Chunk("BONE", Build(writer => { writer.Write(vertex); writer.Write(weight); }));

    private static Vector3 ToVector3(B3dVector3 value) => new(value.X, value.Y, value.Z);

    private static IEnumerable<B3dNode> EnumerateMeshes(B3dNode node)
    {
        if (node.Mesh is not null) yield return node;
        foreach (var child in node.Children)
            foreach (var mesh in EnumerateMeshes(child)) yield return mesh;
    }

    private static void IsFinite(B3dVector3 value)
    {
        Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z), $"Nonfinite vector {value}.");
    }

    private static void AssertPoint((float X, float Y, float Z) expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        foreach (var character in value) writer.Write((byte)character);
        writer.Write((byte)0);
    }

    private static void WriteFloats(BinaryWriter writer, params float[] values)
    {
        foreach (var value in values) writer.Write(value);
    }

    private static byte[] Int(int value) => Build(writer => writer.Write(value));

    private static byte[] Chunk(string tag, byte[] payload) => Build(writer =>
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write(payload.Length);
        writer.Write(payload);
    });

    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
