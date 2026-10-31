using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>Proves the fused SkinInto pass is bit-identical to DeformInto + DeformNormalsInto.</summary>
public sealed class B3dSkinningFusedPassTests
{
    private struct Collector : ISkinSink
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        public int[] Vertices;
        public void Write(int slot, int meshVertex, Vector3 position, Vector3 normal)
        {
            Positions[slot] = position; Normals[slot] = normal; Vertices[slot] = meshVertex;
        }
    }

    [Theory]
    [InlineData(1, 12, 300, 3)]   // synthetic
    [InlineData(2, 30, 500, 3)]   // real-NPC-like: 30 bones, 1-3 influences, rest rotations
    [InlineData(3, 73, 900, 3)]
    [InlineData(4, 60, 200, 1)]   // single influence only
    public void Fused_pass_is_bit_identical_to_the_separate_passes(int seed, int bones, int vertexCount, int maxInfluences)
    {
        var model = BuildRig(seed, vertexCount, bones, maxInfluences, weightedEvery: seed == 4 ? 1 : 7);
        var pose = new B3dPose(model);
        var fused = new B3dSkinningBuffer(pose, model.Root);
        var reference = new B3dSkinningBuffer(pose, model.Root) { ForceLegacy = true };
        var random = new Random(seed * 31);
        var sourceNormals = new B3dVector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            sourceNormals[i] = new B3dVector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f);
        // A surface that uses a subset of the mesh, as a compacted triangle group does.
        var subset = Enumerable.Range(0, vertexCount).Where(i => i % 3 != 1).ToArray();
        var maps = new[] { Enumerable.Range(0, vertexCount).ToArray(), subset };

        foreach (var frame in new[] { 0f, 1f, 4.5f, 7.25f, 10f, 13f })
        {
            B3dAnimationEvaluator.EvaluateInto(model, frame, pose);
            var world = new Matrix4x4[pose.Count]; for (var n = 0; n < world.Length; n++) world[n] = pose.Nodes[n].ModelMatrix;
            var positions = new B3dVector3[vertexCount];
            var normals = new B3dVector3[vertexCount];
            reference.DeformInto(pose, world, positions);
            reference.DeformNormalsInto(sourceNormals, normals);
            fused.PrepareFrame(world);
            foreach (var map in maps)
            {
                var slotNormals = map.Select(v => new Vector3(sourceNormals[v].X, sourceNormals[v].Y, sourceNormals[v].Z)).ToArray();
                var sink = new Collector { Positions = new Vector3[map.Length], Normals = new Vector3[map.Length], Vertices = new int[map.Length] };
                fused.SkinInto(map, slotNormals, ref sink);
                for (var slot = 0; slot < map.Length; slot++)
                {
                    var v = map[slot];
                    Assert.Equal(v, sink.Vertices[slot]);
                    AssertBits(positions[v], sink.Positions[slot]);
                    AssertBits(normals[v], sink.Normals[slot]);
                }
            }
        }
    }

    private static void AssertBits(B3dVector3 expected, Vector3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    // Chain of bones, each with a non-identity rest rotation and keyed motion; 1..maxInfluences weights per vertex.
    internal static B3dModel BuildRig(int seed, int vertexCount, int boneCount, int maxInfluences, int weightedEvery)
    {
        var random = new Random(seed);
        var mesh = Chunk("MESH", Join(Int(-1), Chunk("VRTS", Build(w =>
        {
            w.Write(1); w.Write(0); w.Write(2);
            for (var i = 0; i < vertexCount; i++)
            {
                w.Write((float)random.NextDouble() * 10); w.Write((float)random.NextDouble() * 10); w.Write((float)random.NextDouble() * 10);
                w.Write((float)random.NextDouble() - .5f); w.Write((float)random.NextDouble() - .5f); w.Write(1f);
            }
        }))));
        var pairs = new List<(int Vertex, float Weight)>[boneCount];
        for (var b = 0; b < boneCount; b++) pairs[b] = [];
        for (var v = 0; v < vertexCount; v++)
        {
            if (v % weightedEvery != 0) continue;
            var influences = 1 + random.Next(maxInfluences);
            var used = new HashSet<int>();
            for (var k = 0; k < influences; k++)
            {
                var bone = random.Next(boneCount);
                if (used.Add(bone)) pairs[bone].Add((v, .1f + (float)random.NextDouble()));
            }
        }
        byte[]? chain = null;
        for (var b = boneCount - 1; b >= 0; b--)
        {
            var angle = .2f + .05f * b;
            var rest = (W: MathF.Cos(angle / 2), X: MathF.Sin(angle / 2) * .6f, Y: MathF.Sin(angle / 2) * .8f, Z: 0f);
            var chunks = new List<byte[]>();
            foreach (var pair in pairs[b]) chunks.Add(Chunk("BONE", Build(w => { w.Write(pair.Vertex); w.Write(pair.Weight); })));
            var boneIndex = b;
            chunks.Add(Chunk("KEYS", Build(w =>
            {
                w.Write(5);
                foreach (var (frame, dx, qa) in new[] { (0, 0f, 0f), (10, 1.5f, .3f * MathF.Sin(boneIndex)) })
                {
                    w.Write(frame); w.Write(.5f + dx); w.Write(boneIndex * .01f); w.Write(0f);
                    var q = new Quaternion(qa * .5f, 0, 0, MathF.Sqrt(1 - qa * qa * .25f));
                    w.Write(q.W); w.Write(q.X); w.Write(q.Y); w.Write(q.Z);
                }
            })));
            if (chain is not null) chunks.Add(chain);
            chain = Node($"bone-{b}", (.5f, b * .01f, 0), (1, 1, 1), rest, chunks.ToArray());
        }
        var root = Node("mesh", (1, 2, 3), (1, 1, 1), (1, 0, 0, 0),
            Chunk("ANIM", Build(w => { w.Write(0); w.Write(20); w.Write(24f); })), mesh, chain!);
        return B3dLoader.Parse(Chunk("BB3D", Join(Int(1), root)));
    }

    private static byte[] Node(string name, (float X, float Y, float Z) position, (float X, float Y, float Z) scale,
        (float W, float X, float Y, float Z) rotation, params byte[][] chunks) =>
        Chunk("NODE", Join(Build(w =>
        {
            foreach (var c in name) w.Write((byte)c);
            w.Write((byte)0);
            foreach (var f in new[] { position.X, position.Y, position.Z, scale.X, scale.Y, scale.Z, rotation.W, rotation.X, rotation.Y, rotation.Z })
                w.Write(f);
        }), Join(chunks)));

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
    private static byte[] Int(int value) => Build(w => w.Write(value));
    private static byte[] Chunk(string tag, byte[] payload) => Build(w => { w.Write(Encoding.ASCII.GetBytes(tag)); w.Write(payload.Length); w.Write(payload); });
    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
