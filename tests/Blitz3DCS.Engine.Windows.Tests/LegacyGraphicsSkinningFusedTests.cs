using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsSkinningFusedTests
{
    [Fact]
    public void Multi_surface_skin_matches_the_separate_pass_reference_bit_for_bit_and_skips_unchanged_poses()
    {
        var previous = LegacyScene.Current;
        var file = Path.Combine(Path.GetTempPath(), $"blitz-fused-skin-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(file, TwoSurfaceRig());
            var root = LegacyGraphics.LoadAnimMesh(file);
            var copy = LegacyGraphics.CopyEntity(root);
            LegacyScene.ScaleEntity(copy, 2, 1, 1);

            foreach (var time in new[] { 0f, 3f, 7f, 10f })
            {
                LegacyGraphics.SetAnimTime(root, time);
                LegacyGraphics.SetAnimTime(copy, time);
                LegacyGraphics.PrepareAnimatedGeometry();
                foreach (var entity in new[] { root, copy })
                {
                    var model = LegacyGraphics.AnimatedModels[entity];
                    var mesh = Assert.Single(model.Meshes);
                    Assert.Equal(2, mesh.Surfaces.Count);
                    var reference = new B3dSkinningBuffer(model.Pose, mesh.Skin!.MeshNode) { ForceLegacy = true };
                    var positions = new B3dVector3[5];
                    reference.DeformInto(model.Pose, model.CurrentWorld, positions);
                    foreach (var surface in mesh.Surfaces)
                    {
                        if (!EngineOptimizations.Disabled) Assert.Equal(3, surface.Vertices.Length);
                        var full = new B3dVector3[5];
                        for (var slot = 0; slot < surface.VertexMap.Length; slot++) full[surface.VertexMap[slot]] = surface.SourceNormals[slot];
                        var normals = new B3dVector3[5];
                        reference.DeformNormalsInto(full, normals);
                        for (var slot = 0; slot < surface.VertexMap.Length; slot++)
                        {
                            var v = surface.VertexMap[slot];
                            var vertex = surface.Vertices[slot];
                            Assert.Equal(Bits(positions[v].X), Bits(vertex.Position.X));
                            Assert.Equal(Bits(positions[v].Y), Bits(vertex.Position.Y));
                            Assert.Equal(Bits(positions[v].Z), Bits(vertex.Position.Z));
                            Assert.Equal(Bits(normals[v].X), Bits(vertex.Normal.X));
                            Assert.Equal(Bits(normals[v].Y), Bits(vertex.Normal.Y));
                            Assert.Equal(Bits(normals[v].Z), Bits(vertex.Normal.Z));
                        }
                        foreach (var index in surface.Indices) Assert.True(index < surface.Vertices.Length || EngineOptimizations.Disabled);
                    }
                }
            }

            if (EngineOptimizations.Disabled) return;
            // Same animation time again: pose and world matrices are unchanged, so nothing is re-skinned or uploaded.
            var rootModel = LegacyGraphics.AnimatedModels[root];
            LegacyGraphics.SetAnimTime(root, 10f);
            LegacyGraphics.PrepareAnimatedGeometry();
            foreach (var surface in rootModel.Meshes[0].Surfaces) surface.UploadDirty = false;
            LegacyGraphics.SetAnimTime(root, 10f);
            LegacyGraphics.PrepareAnimatedGeometry();
            foreach (var surface in rootModel.Meshes[0].Surfaces) Assert.False(surface.UploadDirty);
            // A changed time must mark the surfaces dirty again.
            LegacyGraphics.SetAnimTime(root, 4f);
            LegacyGraphics.PrepareAnimatedGeometry();
            foreach (var surface in rootModel.Meshes[0].Surfaces) Assert.True(surface.UploadDirty);
            // Moving the entity changes world matrices and also re-skins.
            foreach (var surface in rootModel.Meshes[0].Surfaces) surface.UploadDirty = false;
            LegacyScene.PositionEntity(root, 5, 0, 0);
            LegacyGraphics.PrepareAnimatedGeometry();
            foreach (var surface in rootModel.Meshes[0].Surfaces) Assert.True(surface.UploadDirty);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
            File.Delete(file);
        }
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static byte[] TwoSurfaceRig()
    {
        var points = new[] { new Vector3(1, 0, 0), new Vector3(0, 3, 0), new Vector3(1, 2, 1), new Vector3(2, 2, 0), new Vector3(0, 1, 2) };
        var vertices = Chunk("VRTS", Write(w =>
        {
            w.Write(0); w.Write(1); w.Write(2);
            foreach (var p in points) Floats(w, p.X, p.Y, p.Z, 0, 0);
        }));
        var groupA = Chunk("TRIS", Write(w => { w.Write(-1); w.Write(0); w.Write(1); w.Write(2); }));
        var groupB = Chunk("TRIS", Write(w => { w.Write(-1); w.Write(2); w.Write(3); w.Write(4); }));
        var meshChunk = Chunk("MESH", Write(w => { w.Write(-1); w.Write(vertices); w.Write(groupA); w.Write(groupB); }));
        var half = MathF.Sqrt(.5f);
        var tipBone = Chunk("BONE", Write(w => { w.Write(2); w.Write(.5f); w.Write(3); w.Write(.7f); w.Write(4); w.Write(1f); }));
        var tipKeys = Chunk("KEYS", Write(w =>
        {
            w.Write(4);
            w.Write(0); Floats(w, 1, 0, 0, 0);
            w.Write(10); Floats(w, half, 0, half, 0);
        }));
        var tip = Chunk("NODE", Write(w =>
        {
            String(w, "tip");
            Floats(w, 1, 0, 0, 1, 1, 1, half, half, 0, 0);
            w.Write(tipBone); w.Write(tipKeys);
        }));
        var armBone = Chunk("BONE", Write(w =>
        {
            w.Write(0); w.Write(1f); w.Write(1); w.Write(.6f); w.Write(2); w.Write(.5f); w.Write(3); w.Write(.3f);
        }));
        var arm = Chunk("NODE", Write(w =>
        {
            String(w, "arm");
            Floats(w, 0, 2, 0, 1, 1, 1, half, 0, 0, half);
            w.Write(armBone); w.Write(tip);
        }));
        var anim = Chunk("ANIM", Write(w => { w.Write(0); w.Write(11); w.Write(30f); }));
        var root = Chunk("NODE", Write(w =>
        {
            String(w, "mesh");
            Floats(w, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0);
            w.Write(meshChunk); w.Write(arm);
        }));
        return Chunk("BB3D", Write(w => { w.Write(1); w.Write(anim); w.Write(root); }));
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
