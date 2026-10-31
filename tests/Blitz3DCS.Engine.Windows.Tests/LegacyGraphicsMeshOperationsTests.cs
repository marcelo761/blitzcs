using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsMeshOperationsTests
{
    [Fact]
    public void Texture_coordinates_ignore_w_and_initialize_both_legacy_uv_sets()
    {
        WithCleanState(() =>
        {
            var surface = LegacyGraphics.CreateSurface(LegacyGraphics.CreateMesh());
            var vertex = LegacyGraphics.AddVertex(surface, 0, 0, 0, .2f, .3f, w: 0);
            Assert.Equal(.2f, LegacyGraphics.VertexU(surface, vertex, 1));
            Assert.Equal(.3f, LegacyGraphics.VertexV(surface, vertex, 1));
            LegacyGraphics.VertexTexCoords(surface, vertex, .4f, .5f, w: 0, coordSet: 0);
            LegacyGraphics.VertexTexCoords(surface, vertex, .6f, .7f, w: 9, coordSet: 1);
            Assert.Equal(.4f, LegacyGraphics.VertexU(surface, vertex));
            Assert.Equal(.5f, LegacyGraphics.VertexV(surface, vertex));
            Assert.Equal(.6f, LegacyGraphics.VertexU(surface, vertex, 1));
            Assert.Equal(.7f, LegacyGraphics.VertexV(surface, vertex, 1));
            Assert.Equal(1, LegacyGraphics.VertexW(surface, vertex, 1));
        });
    }

    [Fact]
    public void Vertex_queries_and_normal_updates_preserve_both_uv_sets()
    {
        WithCleanState(() =>
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            LegacyGraphics.AddVertex(surface, 0, 0, 0, .1f, .2f);
            LegacyGraphics.AddVertex(surface, 1, 0, 0, .3f, .4f);
            LegacyGraphics.AddVertex(surface, 0, 1, 0, .5f, .6f);
            LegacyGraphics.VertexTexCoords(surface, 0, .7f, .8f, coordSet: 1);
            LegacyGraphics.AddTriangle(surface, 0, 1, 2);
            LegacyGraphics.VertexNormal(surface, 0, 1, 2, 3);

            Assert.Equal(0, LegacyGraphics.VertexX(surface, 0));
            Assert.Equal(1, LegacyGraphics.VertexY(surface, 2));
            Assert.Equal(0, LegacyGraphics.VertexZ(surface, 1));
            Assert.Equal(1, LegacyGraphics.VertexNX(surface, 0));
            Assert.Equal(2, LegacyGraphics.VertexNY(surface, 0));
            Assert.Equal(3, LegacyGraphics.VertexNZ(surface, 0));
            Assert.Equal(.1f, LegacyGraphics.VertexU(surface, 0));
            Assert.Equal(.2f, LegacyGraphics.VertexV(surface, 0));
            Assert.Equal(.7f, LegacyGraphics.VertexU(surface, 0, 1));
            Assert.Equal(.8f, LegacyGraphics.VertexV(surface, 0, 1));
            Assert.Equal(1, LegacyGraphics.VertexW(surface, 0, 1));
            Assert.Equal(2, LegacyGraphics.TriangleVertex(surface, 0, 2));

            LegacyGraphics.UpdateNormals(mesh);

            Assert.Equal(Vector3.UnitZ, LegacyGraphics.GetVertexForTesting(surface, 0).Normal);
            Assert.Equal(new Vector2(.7f, .8f), LegacyGraphics.GetVertexForTesting(surface, 0).TexCoord2);
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.VertexU(surface, 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.TriangleVertex(surface, 0, 3));
        });
    }

    [Fact]
    public void Clear_surface_selectively_clears_vertices_and_triangles_without_releasing_surface_material()
    {
        WithCleanState(() =>
        {
            var brush = LegacyGraphics.CreateBrush(25, 50, 75);
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh, brush);
            AddTriangle(surface, 0);

            LegacyGraphics.ClearSurface(surface, clearVertices: 0, clearTriangles: 1);
            Assert.Equal(3, LegacyGraphics.CountVertices(surface));
            Assert.Equal(0, LegacyGraphics.CountTriangles(surface));
            Assert.Equal(0, LegacyGraphics.VertexX(surface, 2));
            Assert.Equal(new Vector4(25 / 255f, 50 / 255f, 75 / 255f, 1),
                LegacyGraphics.GetSurfaceMaterialColorForTesting(surface));

            LegacyGraphics.AddTriangle(surface, 0, 1, 2);
            LegacyGraphics.ClearSurface(surface, clearVertices: 1, clearTriangles: 0);
            Assert.Equal(0, LegacyGraphics.CountVertices(surface));
            Assert.Equal(1, LegacyGraphics.CountTriangles(surface));

            LegacyGraphics.ClearSurface(surface);
            Assert.Equal(0, LegacyGraphics.CountVertices(surface));
            Assert.Equal(0, LegacyGraphics.CountTriangles(surface));
            Assert.Equal(surface, LegacyGraphics.GetSurface(mesh, 1));
        });
    }

    [Fact]
    public void Mesh_transforms_use_source_cofactor_rotation_and_update_extents()
    {
        WithCleanState(() =>
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            LegacyGraphics.AddVertex(surface, 0, 0, 0, .1f, .2f);
            LegacyGraphics.AddVertex(surface, 1, 0, 0, .3f, .4f);
            LegacyGraphics.AddVertex(surface, 0, 1, 0, .5f, .6f);
            LegacyGraphics.AddTriangle(surface, 0, 1, 2);
            LegacyGraphics.VertexNormal(surface, 0, 1, 2, 3);
            LegacyGraphics.VertexTexCoords(surface, 0, .7f, .8f, coordSet: 1);

            LegacyGraphics.ScaleMesh(mesh, 2, 3, 4);
            Assert.Equal(new Vector3(12, 16, 18), LegacyGraphics.GetVertexForTesting(surface, 0).Normal);
            LegacyGraphics.RotateMesh(mesh, 0, 90, 0);
            LegacyGraphics.PositionMesh(mesh, 10, 20, 30);

            Assert.Equal(new Vector3(10, 20, 30), LegacyGraphics.GetVertexForTesting(surface, 0).Position);
            var rotatedNormal = LegacyGraphics.GetVertexForTesting(surface, 0).Normal;
            Assert.Equal(-18, rotatedNormal.X, 4);
            Assert.Equal(16, rotatedNormal.Y, 4);
            Assert.Equal(12, rotatedNormal.Z, 4);
            Assert.Equal(new Vector2(.7f, .8f), LegacyGraphics.GetVertexForTesting(surface, 0).TexCoord2);
            Assert.Equal(0, LegacyGraphics.MeshWidth(mesh), 4);
            Assert.Equal(3, LegacyGraphics.MeshHeight(mesh), 4);
            Assert.Equal(2, LegacyGraphics.MeshDepth(mesh), 4);

            LegacyGraphics.FlipMesh(mesh);
            var flippedNormal = LegacyGraphics.GetVertexForTesting(surface, 0).Normal;
            Assert.Equal(18, flippedNormal.X, 4);
            Assert.Equal(-16, flippedNormal.Y, 4);
            Assert.Equal(-12, flippedNormal.Z, 4);
            Assert.Equal(2, LegacyGraphics.TriangleVertex(surface, 0, 1));
            Assert.Equal(1, LegacyGraphics.TriangleVertex(surface, 0, 2));
        });
    }

    [Fact]
    public void AddMesh_merges_equal_brush_surfaces_and_copy_mesh_is_independent()
    {
        WithCleanState(() =>
        {
            var brush = LegacyGraphics.CreateBrush(20, 40, 60);
            var source = LegacyGraphics.CreateMesh();
            var sourceSurface = LegacyGraphics.CreateSurface(source, brush);
            AddTriangle(sourceSurface, 0);
            var destination = LegacyGraphics.CreateMesh();
            var destinationSurface = LegacyGraphics.CreateSurface(destination, brush);
            AddTriangle(destinationSurface, 10);

            Assert.Equal(destinationSurface, LegacyGraphics.FindSurface(destination, brush));
            var equivalentBrush = LegacyGraphics.GetSurfaceBrush(destinationSurface);
            Assert.Equal(destinationSurface, LegacyGraphics.FindSurface(destination, equivalentBrush));

            LegacyGraphics.AddMesh(source, destination);

            Assert.Equal(1, LegacyGraphics.CountSurfaces(destination));
            Assert.Equal(6, LegacyGraphics.CountVertices(destinationSurface));
            Assert.Equal(2, LegacyGraphics.CountTriangles(destinationSurface));
            Assert.Equal(3, LegacyGraphics.TriangleVertex(destinationSurface, 1, 0));

            var copy = LegacyGraphics.CopyMesh(destination);
            var copySurface = LegacyGraphics.GetSurface(copy, 1);
            Assert.NotEqual(destinationSurface, copySurface);
            Assert.Equal(6, LegacyGraphics.CountVertices(copySurface));
            LegacyGraphics.VertexCoords(copySurface, 0, 99, 98, 97);
            Assert.Equal(10, LegacyGraphics.VertexX(destinationSurface, 0));
            Assert.Equal(0, LegacyGraphics.FindSurface(destination, LegacyGraphics.CreateBrush(1, 2, 3)));
        });
    }

    private static void AddTriangle(int surface, float offset)
    {
        LegacyGraphics.AddVertex(surface, offset, 0, 0, .1f, .2f);
        LegacyGraphics.AddVertex(surface, offset + 1, 0, 0, .3f, .4f);
        LegacyGraphics.AddVertex(surface, offset, 1, 0, .5f, .6f);
        LegacyGraphics.AddTriangle(surface, 0, 1, 2);
    }

    private static void WithCleanState(Action action)
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try { action(); }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
