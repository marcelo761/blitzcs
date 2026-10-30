using System.Numerics;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class PlaneGridTests
{
    [Fact]
    public void Segments_are_limited_to_the_supported_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaneGrid(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaneGrid(21));
        Assert.Equal(1, new PlaneGrid(1).Segments);
        Assert.Equal(20, new PlaneGrid(20).Segments);
    }

    [Fact]
    public void Eye_on_or_below_plane_produces_no_indices_and_clears_old_vertices()
    {
        var grid = new PlaneGrid(1);
        grid.Build(new Vector3(0, 2, 0), BelowLeft, BelowRight, BelowBottomRight, BelowBottomLeft);
        Assert.Equal(6, grid.IndexCount);

        grid.Build(new Vector3(0, 0, 0), BelowLeft, BelowRight, BelowBottomRight, BelowBottomLeft);

        Assert.Equal(0, grid.IndexCount);
        Assert.All(grid.Vertices, vertex => Assert.Equal(Vector3.Zero, vertex.Position));
    }

    [Fact]
    public void Projected_cell_has_upward_normals_white_color_and_local_planar_uvs()
    {
        var grid = new PlaneGrid(1);
        grid.Build(new Vector3(0, 2, 0), BelowLeft, BelowRight, BelowBottomRight, BelowBottomLeft);

        Assert.Equal(6, grid.IndexCount);
        for (var i = 0; i < grid.IndexCount; i++)
        {
            var vertex = grid.Vertices[i];
            Assert.Equal(Vector3.UnitY, vertex.Normal);
            Assert.Equal(Vector4.One, vertex.Color);
            Assert.Equal(new Vector2(vertex.Position.X, vertex.Position.Z), vertex.TexCoord);
            Assert.Equal(vertex.TexCoord, vertex.TexCoord2);
            Assert.Equal((uint)i, grid.Indices[i]);
        }
    }

    [Fact]
    public void Cell_with_three_below_corners_emits_five_vertices()
    {
        var grid = new PlaneGrid(1);
        grid.Build(
            new Vector3(0, 2, 0),
            AboveLeft,
            BelowRight,
            BelowBottomRight,
            BelowBottomLeft);

        Assert.Equal(9, grid.IndexCount);
        Assert.All(grid.Vertices.AsSpan(0, grid.IndexCount).ToArray(), vertex => Assert.Equal(0, vertex.Position.Y));
    }

    [Fact]
    public void Cell_with_one_below_corner_emits_three_vertices()
    {
        var grid = new PlaneGrid(1);
        grid.Build(
            new Vector3(0, 2, 0),
            BelowLeft,
            AboveRight,
            AboveBottomRight,
            AboveBottomLeft);

        Assert.Equal(3, grid.IndexCount);
    }

    [Fact]
    public void Warm_build_does_not_allocate_managed_memory()
    {
        var grid = new PlaneGrid(4);
        var eye = new Vector3(0, 2, 0);
        for (var i = 0; i < 5; i++)
            grid.Build(eye, BelowLeft, BelowRight, BelowBottomRight, BelowBottomLeft);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            grid.Build(eye, BelowLeft, BelowRight, BelowBottomRight, BelowBottomLeft);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static readonly Vector3 AboveLeft = new(-1, 1, -1);
    private static readonly Vector3 AboveRight = new(1, 1, -1);
    private static readonly Vector3 AboveBottomRight = new(1, 1, 1);
    private static readonly Vector3 AboveBottomLeft = new(-1, 1, 1);
    private static readonly Vector3 BelowLeft = new(-1, -1, -1);
    private static readonly Vector3 BelowRight = new(1, -1, -1);
    private static readonly Vector3 BelowBottomRight = new(1, -1, 1);
    private static readonly Vector3 BelowBottomLeft = new(-1, -1, 1);
}
