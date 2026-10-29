using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class B3dSkinningTests
{
    [Fact]
    public void Deform_into_converts_source_world_skinning_to_mesh_local_with_bind_and_current_mesh_transforms()
    {
        var mesh = MeshChunk(
            (1, 0, 0, 1, 0, 0),
            (2, 0, 0, 1, 0, 0));
        var bone = Node("bone", (2, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(1, [0, 2, 0, 0], [10, 5, 0, 0]), Bone(0, 1));
        var root = Node("mesh", (10, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), Keys(1, [0, 10, 0, 0], [10, 15, 0, 0]), mesh, bone);
        var model = B3dLoader.Parse(Chunk("BB3D", Join(Int(1), root)));
        Assert.True(model.Root.Children[0].IsBone);

        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 5, pose);
        var positions = new B3dVector3[2];
        skin.DeformInto(pose, positions);

        // The bone moves 4 units from bind while the mesh moves 2.5; applying the scene-graph
        // mesh transform to output recovers upstream's world-space result.
        var meshWorld = pose.Nodes[0].ModelMatrix;
        AssertPoint((5, 0, 0), Vector3.Transform(ToVector3(positions[0]), meshWorld));
        // An unweighted vertex uses upstream's implicit bone zero (the mesh itself).
        AssertPoint((4.5f, 0, 0), Vector3.Transform(ToVector3(positions[1]), meshWorld));
    }

    [Fact]
    public void Deform_into_keeps_the_first_four_equal_weights_and_normalizes_the_blended_position()
    {
        var bones = new byte[5][];
        for (var i = 0; i < bones.Length; i++)
        {
            var bindX = i + 1;
            var delta = i == 4 ? 100 : bindX;
            bones[i] = Node($"bone-{i}", (bindX, 0, 0), (1, 1, 1), (1, 0, 0, 0),
                Keys(1, [0, bindX, 0, 0], [10, bindX + delta, 0, 0]), Bone(0, 1));
        }
        var root = Node("mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), MeshChunk((0, 0, 0, 0, 0, 1)), Join(bones));
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 10, pose);
        var positions = new B3dVector3[1];

        skin.DeformInto(pose, positions);

        // Equal weights preserve the first four source entries; the fifth, large motion is dropped.
        AssertPoint((2.5f, 0, 0), ToVector3(positions[0]));
    }

    [Fact]
    public void Deform_into_uses_source_cofactor_normal_transform_for_nonuniform_scale_and_rotation()
    {
        var bone = Node("bone", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(2 | 4,
                [0, 1, 1, 1, 1, 0, 0, 0],
                [10, 2, 3, 1, .70710677f, 0, 0, .70710677f]),
            Bone(0, 1));
        var root = Node("mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), MeshChunk((1, 0, 0, 0, 1, 0)), bone);
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 10, pose);
        var positions = new B3dVector3[1];
        var normals = new B3dVector3[1];

        skin.DeformInto(pose, positions, normals);

        AssertPoint((0, 2, 0), ToVector3(positions[0]));
        // A single influence keeps upstream's unnormalized cofactor normal: cross(z, x) * ny = -2x.
        AssertPoint((-2, 0, 0), ToVector3(normals[0]));
    }

    [Fact]
    public void Deform_normals_into_skins_surface_generated_normals_when_the_file_has_no_normals()
    {
        var bone = Node("bone", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(2 | 4,
                [0, 1, 1, 1, 1, 0, 0, 0],
                [10, 2, 3, 1, .70710677f, 0, 0, .70710677f]),
            Bone(0, 1));
        var root = Node("mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), MeshChunkWithoutNormals((1, 0, 0)), bone);
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 10, pose);
        var positions = new B3dVector3[1];
        var normals = new B3dVector3[1];

        Assert.Throws<InvalidOperationException>(() => skin.DeformNormalsInto([new B3dVector3(0, 1, 0)], normals));
        skin.DeformInto(pose, positions);
        skin.DeformNormalsInto([new B3dVector3(0, 1, 0)], normals);

        // Cofactor transform for the quarter-turn and nonuniform scale matches the source path.
        AssertPoint((-2, 0, 0), ToVector3(normals[0]));
    }

    [Fact]
    public void Deform_into_rejects_a_pose_that_has_not_been_evaluated()
    {
        var root = Node("mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0), MeshChunk((0, 0, 0, 0, 0, 1)));
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);

        Assert.Throws<InvalidOperationException>(() => skin.DeformInto(pose, new B3dVector3[1]));
    }

    [Fact]
    public void Deform_into_accepts_instance_world_matrices_with_an_independent_entity_root_transform()
    {
        var mesh = MeshChunk((1, 0, 0, 0, 0, 1));
        var bone = Node("bone", (2, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(1, [0, 2, 0, 0], [10, 6, 0, 0]), Bone(0, 1));
        var root = Node("mesh", (10, 0, 0), (1, 1, 1), (1, 0, 0, 0), Animation(20), mesh, bone);
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 5, pose);
        var worldOffset = Matrix4x4.CreateTranslation(100, 20, -5);
        var instanceMatrices = new Matrix4x4[pose.Count];
        for (var i = 0; i < pose.Count; i++) instanceMatrices[i] = pose.Nodes[i].ModelMatrix * worldOffset;
        var positions = new B3dVector3[1];

        skin.DeformInto(pose, instanceMatrices, positions);

        var expectedWorld = Vector3.Transform(ToVector3(positions[0]), instanceMatrices[0]);
        AssertPoint((103, 20, -5), expectedWorld);
    }

    [Fact]
    public void Deform_into_uses_nearest_mesh_context_for_nested_meshes()
    {
        var outerBone = Node("outer-bone", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(1, [0, 0, 0, 0], [10, 1, 0, 0]), Bone(0, 1));
        var innerBone = Node("inner-bone", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Keys(1, [0, 0, 0, 0], [10, 9, 0, 0]), Bone(0, 1));
        var innerMesh = Node("inner-mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            MeshChunk((0, 0, 0, 0, 0, 1)), innerBone);
        var root = Node("outer-mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), MeshChunk((0, 0, 0, 0, 0, 1)), outerBone, innerMesh);
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        B3dAnimationEvaluator.EvaluateInto(model, 10, pose);
        var positions = new B3dVector3[1];

        skin.DeformInto(pose, positions);

        AssertPoint((1, 0, 0), ToVector3(positions[0]));
    }

    [Fact]
    public void Deform_into_allocates_no_per_frame_buffers()
    {
        var bone = Node("bone", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0), Bone(0, 1));
        var root = Node("mesh", (0, 0, 0), (1, 1, 1), (1, 0, 0, 0),
            Animation(20), MeshChunk((1, 0, 0, 0, 0, 1)), bone);
        var model = Parse(root);
        var pose = new B3dPose(model);
        var skin = new B3dSkinningBuffer(pose, model.Root);
        var positions = new B3dVector3[1];
        var normals = new B3dVector3[1];
        for (var i = 0; i < 32; i++)
        {
            B3dAnimationEvaluator.EvaluateInto(model, i, pose);
            skin.DeformInto(pose, positions, normals);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            B3dAnimationEvaluator.EvaluateInto(model, i, pose);
            skin.DeformInto(pose, positions, normals);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void Optional_installed_asset_skinning_produces_finite_frame_zero_and_midpoint_data()
    {
        var path = Environment.GetEnvironmentVariable("BLITZ3D_B3D_VALIDATION_ASSET");
        if (string.IsNullOrWhiteSpace(path)) return;

        using var stream = File.OpenRead(path);
        var model = B3dLoader.Load(stream);
        var pose = new B3dPose(model);
        var frameCount = (model.Animation ?? model.Root.Animation ?? model.FileAnimation)?.FrameCount ?? 0;
        Assert.True(frameCount > 0, "The selected validation model must contain animation frames.");
        foreach (var meshNode in EnumerateMeshes(model.Root))
        {
            var skin = new B3dSkinningBuffer(pose, meshNode);
            var positions = new B3dVector3[meshNode.Mesh!.Vertices.Count];
            var normals = new B3dVector3[positions.Length];
            foreach (var frame in new[] { 0f, frameCount / 2f })
            {
                B3dAnimationEvaluator.EvaluateInto(model, frame, pose);
                skin.DeformInto(pose, positions, normals);
                Assert.All(positions, IsFinite);
                Assert.All(normals, IsFinite);
            }
        }
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
