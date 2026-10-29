using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class B3dAnimationTests
{
    [Fact]
    public void Evaluate_into_uses_preorder_indices_base_pose_fallback_and_model_space_matrices()
    {
        var childA = Node("bone-a", (0, 2, 0), (1, 1, 1), (1, 0, 0, 0), Bone(0, .75f));
        var childB = Node("bone-b", (0, 0, 3), (1, 1, 1), (1, 0, 0, 0), Bone(1, .25f));
        var root = Node("mesh", (10, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(10, 24), Keys(3, [0, 0, 0, 0, 1, 1, 1], [10, 10, 0, 0, 2, 2, 2]), childA, childB);
        var model = Parse(root);
        var pose = new B3dPose(model);

        B3dAnimationEvaluator.EvaluateInto(model, 5, pose);

        Assert.Equal(3, pose.Count);
        Assert.Equal("mesh", pose.Nodes[0].Source.Name);
        Assert.Equal("bone-a", pose.Nodes[1].Source.Name);
        Assert.Equal("bone-b", pose.Nodes[2].Source.Name);
        Assert.Equal(new B3dVector3(5, 0, 0), pose.Nodes[0].LocalPosition);
        Assert.Equal(new B3dVector3(0, 2, 0), pose.Nodes[1].LocalPosition); // no track: bind/base transform
        Assert.Equal(new B3dVector3(0, 0, 3), pose.Nodes[2].LocalPosition);
        AssertPoint((5, 3, 0), Vector3.Transform(Vector3.Zero, pose.Nodes[1].ModelMatrix));
        AssertPoint((5, 0, 4.5f), Vector3.Transform(Vector3.Zero, pose.Nodes[2].ModelMatrix));
        Assert.Equal(new B3dBoneWeight(0, .75f), Assert.Single(model.Root.Children[0].BoneWeights));
        Assert.Equal(new B3dBoneWeight(1, .25f), Assert.Single(model.Root.Children[1].BoneWeights));
    }

    [Fact]
    public void Evaluate_into_wraps_positive_and_negative_frames_and_slerps_the_shortest_quaternion_path()
    {
        var root = Node("root", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(10, 24), Keys(5 | 4,
                [0, 0, 0, 0, 1, 0, 0, 0],
                [10, 10, 0, 0, -.70710677f, 0, 0, -.70710677f]));
        var model = Parse(root);
        var pose = new B3dPose(model);
        B3dAnimationEvaluator.EvaluateInto(model, 5, pose);
        var halfway = pose.Nodes[0];
        var rootNode = halfway;
        Assert.Equal(5, halfway.LocalPosition.X, 4);
        var rotated = Vector3.TransformNormal(Vector3.UnitX, halfway.LocalMatrix);
        AssertPoint((.70710677f, .70710677f, 0), rotated);

        B3dAnimationEvaluator.EvaluateInto(model, 10, pose);
        Assert.Equal(0, pose.Nodes[0].LocalPosition.X);
        B3dAnimationEvaluator.EvaluateInto(model, -1, pose);
        Assert.Equal(9, pose.Nodes[0].LocalPosition.X, 4);
        B3dAnimationEvaluator.EvaluateInto(model, 1.5f, pose);
        Assert.Equal(1.5f, pose.Nodes[0].LocalPosition.X, 4);
        Assert.Equal(rootNode.Source, pose.Nodes[0].Source);
    }

    private static void AssertPoint((float X, float Y, float Z) expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static byte[] ParseNodeBytes(byte[] node) => Chunk("BB3D", Join(Int(1), node));
    private static B3dModel Parse(byte[] node) => B3dLoader.Parse(ParseNodeBytes(node));

    private static byte[] Node(string name, (float X, float Y, float Z) position, (float X, float Y, float Z) scale,
        (float W, float X, float Y, float Z) rotation, params byte[][] chunks) =>
        Chunk("NODE", Join(Build(writer =>
        {
            WriteString(writer, name);
            WriteFloats(writer, position.X, position.Y, position.Z, scale.X, scale.Y, scale.Z,
                rotation.W, rotation.X, rotation.Y, rotation.Z);
        }), Join(chunks)));

    private static byte[] Animation(int frames, float fps) =>
        Chunk("ANIM", Build(writer => { writer.Write(0); writer.Write(frames); writer.Write(fps); }));

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
