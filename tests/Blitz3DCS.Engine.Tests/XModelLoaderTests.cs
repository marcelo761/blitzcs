using System.IO.Compression;
using System.Numerics;
using System.Text;
using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class XModelLoaderTests
{
    [Fact]
    public void Every_mesh_in_a_frame_contributes_geometry_and_equal_materials_share_a_group()
    {
        // 3D World Studio exports (e.g. door frames) place many brush meshes directly inside one frame.
        var text = """
            xof 0302txt 0064
            Material metal { 1.0;1.0;1.0;1.0;; 1.0; 1.0;1.0;1.0;; 0.0;0.0;0.0;; TextureFilename { "metal.jpg"; } }
            Frame World {
              FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1;; }
              Mesh Brush1 { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; MeshMaterialList { 1; 1; 0;; { metal } } }
              Mesh Brush2 { 3; 10;0;0;, 11;0;0;, 10;1;0;; 1; 3;0,1,2;; MeshMaterialList { 1; 1; 0;; { metal } } }
            }
            """;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        var model = XModelLoader.Load(stream);

        var mesh = Assert.IsType<B3dMesh>(Assert.Single(model.Root.Children).Mesh);
        Assert.Equal(6, mesh.Vertices.Count);
        Assert.Equal(new B3dVector3(10, 0, 0), mesh.Vertices[3].Position);
        var group = Assert.Single(mesh.TriangleGroups);
        Assert.Equal(new[] { new B3dTriangle(0, 1, 2), new B3dTriangle(3, 4, 5) }, group.Triangles);
        Assert.Single(model.Brushes);
        Assert.Single(model.Textures);
    }

    [Fact]
    public void Rotated_frame_matrix_round_trips_through_the_source_quaternion_convention()
    {
        // Row-vector X matrix for a quarter turn that maps +X to +Y.
        var text = """
            xof 0303txt 0032
            Frame Turned {
              FrameTransformMatrix { 0,1,0,0, -1,0,0,0, 0,0,1,0, 0,0,0,1;; }
              Mesh { 3; 1;0;0;, 0;1;0;, 0;0;1;; 1; 3;0,1,2;; }
            }
            """;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));
        var model = XModelLoader.Load(stream);
        var pose = new B3dPose(model);
        B3dAnimationEvaluator.EvaluateInto(model, 0, pose);

        var expected = new Vector3(0, 1, 0);
        var poseResult = Vector3.Transform(Vector3.UnitX, pose.Nodes[1].ModelMatrix);
        Assert.Equal(expected.X, poseResult.X, 4);
        Assert.Equal(expected.Y, poseResult.Y, 4);
        Assert.Equal(expected.Z, poseResult.Z, 4);

        // The scene graph must agree with the pose matrices for the same stored quaternion.
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        var node = model.Root.Children[0];
        scene.SetLocalPose(entity, new SceneVector3(0, 0, 0), new SceneVector3(1, 1, 1),
            node.Rotation.W, node.Rotation.X, node.Rotation.Y, node.Rotation.Z);
        var sceneResult = scene.GetWorldTransform(entity).TransformPoint(new SceneVector3(1, 0, 0));
        Assert.Equal(expected.X, sceneResult.X, 4);
        Assert.Equal(expected.Y, sceneResult.Y, 4);
        Assert.Equal(expected.Z, sceneResult.Z, 4);
    }

    [Fact]
    public void Loads_text_frames_mesh_materials_normals_uvs_and_polygon_fan()
    {
        var text = """
            xof 0303txt 0032
            Frame Root {
              FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 4,5,6,1;; }
              Frame Part {
                Mesh Panel {
                  4; 0;0;0;, 1;0;0;, 1;1;0;, 0;1;0;;
                  1; 4;0,1,2,3;;
                  MeshNormals { 1; 0;0;1;; 1; 4;0,0,0,0;; }
                  MeshTextureCoords { 4; 0;0;, 1;0;, 1;1;, 0;1;; }
                  MeshMaterialList { 1; 1; 0;; Material paint { 0.25;0.5;0.75;0;; 8; 0.2;0.3;0.4;; 0;0;; TextureFilename { "panel.png"; } } }
                }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        var model = XModelLoader.Load(stream);

        var frame = Assert.Single(model.Root.Children);
        Assert.Equal("Root", frame.Name);
        Assert.Equal(new B3dVector3(4, 5, 6), frame.Position);
        var part = Assert.Single(frame.Children);
        Assert.Equal("Part", part.Name);
        Assert.Equal(new B3dVector3(1, 1, 1), part.Scale);
        Assert.Equal(new B3dQuaternion(1, 0, 0, 0), part.Rotation);
        var mesh = Assert.IsType<B3dMesh>(part.Mesh);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(new B3dVector3(0, 0, 1), mesh.Vertices[2].Normal);
        Assert.Equal(new[] { 1f, 1f }, mesh.Vertices[2].TextureCoordinates[0]);
        Assert.Equal(new[] { new B3dTriangle(0, 1, 2), new B3dTriangle(0, 2, 3) }, mesh.TriangleGroups[0].Triangles);
        Assert.Equal(0, mesh.TriangleGroups[0].BrushId);
        Assert.Equal(new B3dColor(1, 1, 1, 1), model.Brushes[0].Color); // texture-bearing X materials are white-tinted upstream
        Assert.Equal(0, model.Brushes[0].Blend);
        Assert.Equal(0, model.Brushes[0].Shininess);
        Assert.Equal(0, model.Textures[0].Flags);
        Assert.Equal(2, model.Textures[0].Blend);
        Assert.Equal("panel.png", model.Textures[0].Name);

        var pose = new B3dPose(model);
        B3dAnimationEvaluator.EvaluateInto(model, 0, pose);
        var nodes = pose.Nodes.ToArray();
        Assert.Equal(Matrix4x4.Identity, nodes[0].ModelMatrix);
        var translated = Matrix4x4.CreateTranslation(4, 5, 6);
        Assert.Equal(translated, nodes[1].ModelMatrix);
        Assert.Equal(translated, nodes[2].ModelMatrix);
    }

    [Fact]
    public void Loads_ms_zip_compressed_binary_mesh()
    {
        var binary = BinaryMesh();
        var compressed = CompressMsZip(binary);
        var file = new byte[16 + 4 + compressed.Length];
        Encoding.ASCII.GetBytes("xof 0303bzip0032").CopyTo(file, 0);
        BitConverter.GetBytes(16 + binary.Length).CopyTo(file, 16);
        compressed.CopyTo(file, 20);
        using var stream = new MemoryStream(file);

        var model = XModelLoader.Load(stream);

        var mesh = Assert.IsType<B3dMesh>(model.Root.Mesh);
        Assert.Equal(3, mesh.Vertices.Count);
        Assert.Equal(new B3dVector3(0, 1, 0), mesh.Vertices[2].Position);
        Assert.Equal(new B3dTriangle(0, 1, 2), Assert.Single(mesh.TriangleGroups[0].Triangles));
        Assert.Equal("panel.png", Assert.Single(model.Textures).Name);
    }

    [Fact]
    public void Rejects_truncated_and_invalid_index_data()
    {
        using var truncated = new MemoryStream(Encoding.ASCII.GetBytes("xof 0303txt 0032 Mesh { 3; 0;0;0; }"));
        Assert.Throws<InvalidDataException>(() => XModelLoader.Load(truncated));

        var text = "xof 0303txt 0032 Mesh { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,3;; }";
        using var invalidIndex = new MemoryStream(Encoding.ASCII.GetBytes(text));
        Assert.Throws<InvalidDataException>(() => XModelLoader.Load(invalidIndex));
    }

    [Fact]
    public void Rejects_normal_face_data_that_does_not_match_mesh_faces()
    {
        var text = "xof 0303txt 0032 Mesh { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; MeshNormals { 1; 0;0;1;; 1; 2;0,0;; } }";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        Assert.Throws<InvalidDataException>(() => XModelLoader.Load(stream));
    }

    [Fact]
    public void Splits_mesh_vertices_when_face_corner_normals_have_hard_edges()
    {
        var text = "xof 0303txt 0032 Mesh { 4; 0;0;0;, 1;0;0;, 1;1;0;, 0;1;0;; 2; 3;0,1,2;, 3;0,2,3;; MeshNormals { 2; 0;0;1;, 1;0;0;; 2; 3;0,0,0;, 3;1,1,1;; } }";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        var mesh = Assert.IsType<B3dMesh>(XModelLoader.Load(stream).Root.Mesh);

        Assert.Equal(6, mesh.Vertices.Count);
        Assert.Equal(new B3dVector3(0, 0, 1), mesh.Vertices[0].Normal);
        Assert.Equal(new B3dVector3(1, 0, 0), mesh.Vertices[4].Normal);
        Assert.Equal(new B3dVector3(1, 0, 0), mesh.Vertices[5].Normal);
        Assert.Equal(new[] { new B3dTriangle(0, 1, 2), new B3dTriangle(4, 5, 3) }, mesh.TriangleGroups[0].Triangles);
    }

    [Theory]
    [InlineData("AnimationSet Set { Animation { { Root } AnimationKey { 2; 1; 0; 3; 1,2,3;; } } }")]
    [InlineData("Mesh { 0; 0;; SkinWeights { \"Root\"; 1; 0; 1.0; 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1;; } }")]
    public void Rejects_animated_or_skinned_data_when_preservation_is_requested(string payload)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("xof 0303txt 0032 " + payload));

        Assert.Throws<NotSupportedException>(() => XModelLoader.Load(stream, preserveAnimation: true));
    }

    [Fact]
    public void Loads_static_geometry_before_trailing_animation_set_and_single_exporter_closure()
    {
        var text = "xof 0303txt 0032 Frame Root { Mesh { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; } } } AnimationSet AnimationSet0 { Animation { } }";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        var model = XModelLoader.Load(stream);

        Assert.Equal("Root", Assert.Single(model.Root.Children).Name);
        Assert.Single(Assert.Single(model.Root.Children).Mesh!.TriangleGroups[0].Triangles);
    }

    [Fact]
    public void Resolves_named_material_reference_after_final_face_index()
    {
        var text = "xof 0303txt 0032 Material Material01 { 0.8;0.8;0.8;1;; 50; 0;0;0;; 0;0;0;; TextureFilename { \"lever.png\"; } } Mesh Mesh01 { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; MeshMaterialList { 1; 1; 0; { Material01 } } }";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        var model = XModelLoader.Load(stream);

        Assert.Equal("lever.png", Assert.Single(model.Textures).Name);
        Assert.Equal(new B3dColor(1, 1, 1, 1), Assert.Single(model.Brushes).Color);
        Assert.Equal(2, model.Textures[0].Blend);
        Assert.Equal(new B3dTriangle(0, 1, 2), Assert.Single(Assert.Single(model.Root.Mesh!.TriangleGroups).Triangles));
    }

    private static byte[] BinaryMesh()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        Token(writer, 31); Name(writer, "SyntheticTemplate"); Token(writer, 10);
        Token(writer, 5); writer.Write(new byte[16]);
        Token(writer, 52); Token(writer, 41); Name(writer, "values"); Token(writer, 14); Name(writer, "count"); Token(writer, 15); Token(writer, 20);
        Token(writer, 11);
        Name(writer, "Mesh"); Token(writer, 10);
        Ints(writer, 3);
        Floats(writer, 0, 0, 0, 1, 0, 0, 0, 1, 0);
        Ints(writer, 1, 3, 0, 1, 2);
        Name(writer, "MeshMaterialList"); Token(writer, 10);
        Ints(writer, 1, 1, 0);
        Name(writer, "Material"); Name(writer, "paint"); Token(writer, 10);
        Floats(writer, 1, 1, 1, 1, 0, 0, 0, 0, 0);
        Name(writer, "TextureFilename"); Token(writer, 10);
        Token(writer, 2); var texture = Encoding.ASCII.GetBytes("panel.png"); writer.Write(texture.Length); writer.Write(texture); Token(writer, 19);
        Token(writer, 11); Token(writer, 11); Token(writer, 11);
        Token(writer, 11);
        return stream.ToArray();
    }

    private static byte[] CompressMsZip(byte[] data)
    {
        using var deflated = new MemoryStream();
        using (var compressor = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(data);
        var deflate = deflated.ToArray();
        var result = new byte[4 + 2 + deflate.Length];
        BitConverter.GetBytes((ushort)data.Length).CopyTo(result, 0);
        BitConverter.GetBytes((ushort)(deflate.Length + 2)).CopyTo(result, 2);
        result[4] = (byte)'C'; result[5] = (byte)'K';
        deflate.CopyTo(result, 6);
        return result;
    }

    private static void Ints(BinaryWriter writer, params int[] values)
    {
        Token(writer, 6); writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static void Floats(BinaryWriter writer, params float[] values)
    {
        Token(writer, 7); writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static void Name(BinaryWriter writer, string value)
    {
        Token(writer, 1); var bytes = Encoding.ASCII.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
    }

    private static void Token(BinaryWriter writer, ushort token) => writer.Write(token);
}
