using System.Numerics;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class NormalMatrixCacheTests
{
    private static Matrix4x4 Reference(Matrix4x4 world) =>
        Matrix4x4.Invert(world, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;

    [Fact]
    public void Cached_normal_matrices_equal_a_fresh_inverse_transpose_and_repeat_without_inverting()
    {
        var cache = new NormalMatrixCache();
        var random = new Random(5);
        var worlds = new List<Matrix4x4>();
        for (var i = 0; i < 300; i++)
            worlds.Add(Matrix4x4.CreateScale(1 + random.Next(1, 4)) * Matrix4x4.CreateFromYawPitchRoll(i, i * .3f, 0)
                * Matrix4x4.CreateTranslation(random.Next(-50, 50), i, random.Next(-50, 50)));
        worlds.Add(new Matrix4x4()); // singular -> identity, as before
        for (var pass = 0; pass < 3; pass++)
            foreach (var world in worlds) Assert.Equal(Reference(world), cache.Get(world));
        // Collisions may evict, but a second pass over a small working set must be served from the cache.
        var small = new NormalMatrixCache();
        for (var pass = 0; pass < 5; pass++)
            for (var i = 0; i < 20; i++) small.Get(worlds[i]);
        Assert.InRange(small.Inversions, 20, 40);
    }
}
