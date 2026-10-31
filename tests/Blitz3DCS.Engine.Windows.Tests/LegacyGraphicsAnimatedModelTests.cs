using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsAnimatedModelTests
{
    [Fact]
    public void Load_anim_mesh_preserves_hierarchy_bind_vertices_uvs_and_brush_scopes()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-animated-model-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, AnimatedModelBytes());
            var parent = LegacyScene.CreatePivot();
            LegacyScene.PositionEntity(parent, 100, 0, 0);

            var root = LegacyGraphics.LoadAnimMesh(file, parent);

            Assert.NotEqual(0, root);
            Assert.Equal(parent, LegacyScene.GetParent(root));
            Assert.Equal("root", LegacyScene.EntityName(root));
            Assert.Equal(20f, LegacyScene.EntityX(root)); // frame-zero keyed root value
            var mesh = LegacyScene.GetChild(root, 1);
            Assert.Equal("mesh", LegacyScene.EntityName(mesh));
            Assert.Equal(1f, LegacyScene.EntityX(mesh));
            Assert.Equal(121f, LegacyScene.EntityX(mesh, global: 1));
            Assert.Equal(new Vector4(.1f, .2f, .3f, 1), LegacyGraphics.GetEntityMaterialColorForTesting(mesh));

            var animated = LegacyGraphics.AnimatedModels[root];
            Assert.Equal(new[] { root, mesh, LegacyScene.GetChild(mesh, 1) }, animated.Entities);
            Assert.True(animated.Pose.Nodes[0].HasPositionKeys);
            var animatedMesh = Assert.Single(animated.Meshes);
            Assert.Equal(mesh, animatedMesh.Entity);
            Assert.NotNull(animatedMesh.Skin);

            var surface = Assert.Single(animatedMesh.Surfaces);
            Assert.Equal(new Vector3(1, 0, 0), surface.BaseSurfaceVertices[0].Position);
            Assert.Equal(new Vector2(.1f, .2f), surface.BaseSurfaceVertices[0].TexCoord);
            Assert.Equal(new Vector2(.75f, .25f), surface.BaseSurfaceVertices[0].TexCoord2);
            Assert.Equal(new Vector3(0, 0, 1), surface.BaseSurfaceVertices[0].Normal);
            Assert.Equal(new B3dVector3(0, 0, 1), surface.SourceNormals[0]);
            Assert.Equal(3, surface.DeformedNormals.Length);
            Assert.Equal(new uint[] { 0, 1, 2 }, surface.Indices);
            Assert.Equal(new Vector4(.7f, .4f, .2f, 1), LegacyGraphics.GetSurfaceMaterialColorForTesting(surface.Handle));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Set_anim_time_prepares_skinned_vertices_and_copies_keep_independent_animation_state()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-animated-copy-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, AnimatedModelBytes());
            var root = LegacyGraphics.LoadAnimMesh(file);
            var original = LegacyGraphics.AnimatedModels[root];
            var originalMesh = Assert.Single(original.Meshes);
            var originalSurface = Assert.Single(originalMesh.Surfaces);

            LegacyGraphics.SetAnimTime(root, 2);
            LegacyGraphics.PrepareAnimatedGeometry();

            Assert.Equal(40f, LegacyScene.EntityX(root));
            Assert.Equal(new Vector3(1, 0, 0), originalSurface.BaseSurfaceVertices[0].Position);
            var sourceFrameTwoVertex = originalSurface.Vertices[0].Position;
            Assert.NotEqual(originalSurface.BaseSurfaceVertices[0].Position, sourceFrameTwoVertex);

            var copyRoot = LegacyGraphics.CopyEntity(root);
            var copy = LegacyGraphics.AnimatedModels[copyRoot];
            var copyMesh = Assert.Single(copy.Meshes);
            var copySurface = Assert.Single(copyMesh.Surfaces);
            Assert.NotSame(original, copy);
            Assert.NotSame(original.Pose, copy.Pose);
            Assert.NotSame(originalMesh.Skin, copyMesh.Skin);

            LegacyGraphics.SetAnimTime(copyRoot, 0);
            LegacyGraphics.PrepareAnimatedGeometry();

            Assert.Equal(40f, LegacyScene.EntityX(root));
            Assert.Equal(20f, LegacyScene.EntityX(copyRoot));
            Assert.Equal(sourceFrameTwoVertex, originalSurface.Vertices[0].Position);
            Assert.NotEqual(sourceFrameTwoVertex, copySurface.Vertices[0].Position);
            Assert.Equal(new Vector3(1, 0, 0), originalSurface.BaseSurfaceVertices[0].Position);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Off_screen_skinned_mesh_skips_deformation_then_skins_correctly_when_visible_and_bounds_are_conservative()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-skin-cull-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, AnimatedModelBytes(keyedRoot: false));
            var root = LegacyGraphics.LoadAnimMesh(file);
            var mesh = Assert.Single(LegacyGraphics.AnimatedModels[root].Meshes);
            var surface = Assert.Single(mesh.Surfaces);
            var viewProjection = LegacyGraphics.CreateLeftHandedPerspective(1.2f, 1.33f, .1f, 1000f);

            LegacyScene.PositionEntity(root, 0, 0, -500); // behind the camera
            LegacyGraphics.SetAnimTime(root, 2);
            LegacyGraphics.PrepareAnimatedGeometry(viewProjection);
            Assert.Equal(!EngineOptimizations.Disabled, mesh.SkinCulled);
            if (!EngineOptimizations.Disabled)
            {
                Assert.True(mesh.Dirty);
                Assert.Equal(surface.BaseSurfaceVertices[0].Position, surface.Vertices[0].Position);
            }

            LegacyScene.PositionEntity(root, 0, 0, 10);
            LegacyGraphics.PrepareAnimatedGeometry(viewProjection);
            Assert.False(mesh.SkinCulled);
            Assert.False(mesh.Dirty);
            var culledThenShown = surface.Vertices.ToArray();

            // Reference: the same pose skinned without any culling.
            LegacyGraphics.SetAnimTime(root, 0);
            LegacyGraphics.PrepareAnimatedGeometry();
            LegacyGraphics.SetAnimTime(root, 2);
            LegacyGraphics.PrepareAnimatedGeometry();
            Assert.Equal(surface.Vertices, culledThenShown);

            // The bound contains every skinned vertex for a sweep of animation times.
            var model = LegacyGraphics.AnimatedModels[root];
            for (var t = 0f; t <= 2f; t += .125f)
            {
                LegacyGraphics.SetAnimTime(root, t);
                LegacyGraphics.PrepareAnimatedGeometry();
                mesh.Skin!.PrepareFrame(model.CurrentWorld);
                Assert.True(mesh.Skin.TryGetSkinnedBounds(out var min, out var max));
                foreach (var vertex in surface.Vertices)
                {
                    Assert.True(vertex.Position.X >= min.X && vertex.Position.X <= max.X);
                    Assert.True(vertex.Position.Y >= min.Y && vertex.Position.Y <= max.Y);
                    Assert.True(vertex.Position.Z >= min.Z && vertex.Position.Z <= max.Z);
                }
            }
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Set_anim_time_preserves_an_unkeyed_game_root_position()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-unkeyed-animated-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, AnimatedModelBytes(keyedRoot: false));
            var root = LegacyGraphics.LoadAnimMesh(file);
            LegacyScene.PositionEntity(root, 77, 0, 0);

            LegacyGraphics.SetAnimTime(root, 2);
            LegacyGraphics.PrepareAnimatedGeometry();

            Assert.Equal(77f, LegacyScene.EntityX(root));
            var mesh = Assert.Single(LegacyGraphics.AnimatedModels[root].Meshes);
            Assert.NotEqual(new Vector3(1, 0, 0), Assert.Single(mesh.Surfaces).Vertices[0].Position);
            Assert.Equal(new Vector3(1, 0, 0), mesh.Surfaces[0].BaseSurfaceVertices[0].Position);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Animation_transitions_interpolate_current_pose_and_extracted_sequences_reach_their_endpoint()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-animated-transition-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, AnimatedModelBytes());
            var root = LegacyGraphics.LoadAnimMesh(file);
            LegacyScene.PositionEntity(root, 60, 0, 0);

            LegacyGraphics.Animate(root, mode: 1, speed: 0, transition: 2);
            LegacyGraphics.UpdateWorld(1);
            Assert.Equal(40f, LegacyScene.EntityX(root));

            var sequence = LegacyGraphics.ExtractAnimSeq(root, 1, 3);
            LegacyGraphics.Animate(root, mode: (int)B3dAnimationMode.OneShot, speed: 1, sequence: sequence);
            Assert.Equal(2, LegacyGraphics.AnimLength(root));
            LegacyGraphics.UpdateWorld(2);

            Assert.Equal(60f, LegacyScene.EntityX(root));
            Assert.Equal(0, LegacyGraphics.Animating(root));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    [Fact]
    public void Skinned_rest_pose_with_rotated_bones_reproduces_bind_vertices_for_loads_and_scaled_copies()
    {
        // Bones with non-identity rest rotations (as in every biped rig) must skin to their bind shape:
        // the bind matrices and the scene graph have to share the source quaternion convention.
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-rotated-bones-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, RotatedBoneModelBytes());
            var root = LegacyGraphics.LoadAnimMesh(file);
            var copy = LegacyGraphics.CopyEntity(root);
            LegacyScene.ScaleEntity(copy, 2, 2, 2);
            LegacyGraphics.PrepareAnimatedGeometry();

            foreach (var entity in new[] { root, copy })
            {
                var mesh = Assert.Single(LegacyGraphics.AnimatedModels[entity].Meshes);
                Assert.NotNull(mesh.Skin);
                for (var i = 0; i < RotatedBoneVertices.Length; i++)
                {
                    Assert.Equal(RotatedBoneVertices[i].X, mesh.Positions[i].X, 4);
                    Assert.Equal(RotatedBoneVertices[i].Y, mesh.Positions[i].Y, 4);
                    Assert.Equal(RotatedBoneVertices[i].Z, mesh.Positions[i].Z, 4);
                }
            }
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    private static readonly Vector3[] RotatedBoneVertices = [new(1, 0, 0), new(0, 3, 0), new(1, 2, 1)];

    private static byte[] RotatedBoneModelBytes()
    {
        var vertices = Chunk("VRTS", Write(w =>
        {
            w.Write(0); // no normals or colors
            w.Write(1); // one UV set
            w.Write(2); // two components
            foreach (var p in RotatedBoneVertices) Floats(w, p.X, p.Y, p.Z, 0, 0);
        }));
        var triangles = Chunk("TRIS", Write(w => { w.Write(-1); w.Write(0); w.Write(1); w.Write(2); }));
        var meshChunk = Chunk("MESH", Write(w => { w.Write(-1); w.Write(vertices); w.Write(triangles); }));
        var half = MathF.Sqrt(.5f);
        var tipBone = Chunk("BONE", Write(w => { w.Write(1); w.Write(1f); w.Write(2); w.Write(.5f); }));
        var tip = Chunk("NODE", Write(w =>
        {
            String(w, "tip");
            Floats(w, 1, 0, 0, 1, 1, 1, half, half, 0, 0); // quarter turn about X
            w.Write(tipBone);
        }));
        var armBone = Chunk("BONE", Write(w => { w.Write(0); w.Write(1f); w.Write(2); w.Write(.5f); }));
        var arm = Chunk("NODE", Write(w =>
        {
            String(w, "arm");
            Floats(w, 0, 2, 0, 1, 1, 1, half, 0, 0, half); // quarter turn about Z
            w.Write(armBone);
            w.Write(tip);
        }));
        var root = Chunk("NODE", Write(w => { Header(w, "mesh", 0, 1, 1, 1); w.Write(meshChunk); w.Write(arm); }));
        return Chunk("BB3D", Write(w => { w.Write(1); w.Write(root); }));
    }

    internal static byte[] AnimatedModelBytes(bool keyedRoot = true)
    {
        var vertices = Chunk("VRTS", Write(w =>
        {
            w.Write(0); // normals are generated per triangle group
            w.Write(2); // two UV sets
            w.Write(2); // two components each
            foreach (var p in new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, 0) })
            {
                Floats(w, p.X, p.Y, p.Z, .1f, .2f, .75f, .25f);
            }
        }));
        var triangles = Chunk("TRIS", Write(w => { w.Write(1); w.Write(0); w.Write(1); w.Write(2); }));
        var meshChunk = Chunk("MESH", Write(w => { w.Write(0); w.Write(vertices); w.Write(triangles); }));
        var bone = Chunk("BONE", Write(w => { w.Write(0); w.Write(1f); }));
        var boneKeys = Chunk("KEYS", Write(w =>
        {
            w.Write(1);
            Key(w, 0, 0);
            Key(w, 2, 2);
            Key(w, 3, 3);
        }));
        var boneNode = Chunk("NODE", Write(w => { Header(w, "bone", 0, 1, 1, 1); w.Write(bone); w.Write(boneKeys); }));
        var meshNode = Chunk("NODE", Write(w => { Header(w, "mesh", 1, 1, 1, 1); w.Write(meshChunk); w.Write(boneNode); }));
        var rootKeys = Chunk("KEYS", Write(w =>
        {
            w.Write(1);
            Key(w, 0, 20);
            Key(w, 2, 40);
            Key(w, 3, 60);
        }));
        var root = Chunk("NODE", Write(w =>
        {
            Header(w, "root", 10, 1, 1, 1);
            if (keyedRoot) w.Write(rootKeys);
            w.Write(meshNode);
        }));
        var brushes = Chunk("BRUS", Write(w =>
        {
            w.Write(0);
            Brush(w, "mesh", .1f, .2f, .3f);
            Brush(w, "surface", .7f, .4f, .2f);
        }));
        var animation = Chunk("ANIM", Write(w => { w.Write(0); w.Write(4); w.Write(30f); }));
        return Chunk("BB3D", Write(w => { w.Write(1); w.Write(animation); w.Write(brushes); w.Write(root); }));
    }

    private static void Key(BinaryWriter writer, int frame, float x)
    {
        writer.Write(frame);
        Floats(writer, x, 0, 0);
    }

    private static void Brush(BinaryWriter writer, string name, float red, float green, float blue)
    {
        String(writer, name);
        Floats(writer, red, green, blue, 1, 0);
        writer.Write(0); // blend
        writer.Write(0); // effects
    }

    private static void Header(BinaryWriter writer, string name, float x, float sx, float sy, float sz)
    {
        String(writer, name);
        Floats(writer, x, 0, 0, sx, sy, sz, 1, 0, 0, 0);
    }

    private static void String(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void Floats(BinaryWriter writer, params float[] values)
    {
        foreach (var value in values) writer.Write(value);
    }

    private static byte[] Chunk(string name, byte[] body) => Write(writer =>
    {
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write(body.Length);
        writer.Write(body);
    });

    private static byte[] Write(Action<BinaryWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) action(writer);
        return stream.ToArray();
    }
}
