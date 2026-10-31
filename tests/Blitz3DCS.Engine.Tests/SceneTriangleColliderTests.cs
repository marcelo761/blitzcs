using Xunit;

namespace Blitz3DCS.Engine.Tests;

public sealed class SceneTriangleColliderTests
{
    [Fact]
    public void Bvh_candidates_match_linear_collision_results_for_random_scaled_targets()
    {
        for (var seed = 0; seed < 24; seed++)
        {
            var accelerated = RunCollision(seed, linear: false);
            var linear = RunCollision(seed, linear: true);

            Assert.Equal(linear.SourcePosition, accelerated.SourcePosition);
            Assert.Equal(linear.Collisions.Count, accelerated.Collisions.Count);
            for (var index = 0; index < linear.Collisions.Count; index++)
            {
                var expected = linear.Collisions[index];
                var actual = accelerated.Collisions[index];
                Assert.Equal(expected.Entity, actual.Entity);
                Assert.Equal(expected.Triangle, actual.Triangle);
                Assert.Equal(expected.Surface, actual.Surface);
                Assert.Equal(expected.Time, actual.Time, 5);
                Assert.Equal(expected.Position.X, actual.Position.X, 4);
                Assert.Equal(expected.Position.Y, actual.Position.Y, 4);
                Assert.Equal(expected.Position.Z, actual.Position.Z, 4);
                Assert.Equal(expected.Normal.X, actual.Normal.X, 4);
                Assert.Equal(expected.Normal.Y, actual.Normal.Y, 4);
                Assert.Equal(expected.Normal.Z, actual.Normal.Z, 4);
            }
        }
    }

    [Fact]
    public void Bvh_reduces_candidates_and_keeps_original_triangle_order()
    {
        var triangles = new SceneCollisionTriangle[513];
        for (var index = 0; index < triangles.Length; index++)
        {
            var z = index == 257 ? 0 : 100 + index;
            triangles[index] = new(new(-.2f, -.2f, z), new(.2f, -.2f, z), new(0, .2f, z), Index: index);
        }
        var collider = new SceneTriangleCollider(triangles);
        var candidates = new List<int>();

        collider.Query(Identity, new(0, 0, 2), new(0, 0, -4), .5f, candidates);

        Assert.Equal(new[] { 257 }, candidates);
        Assert.True(candidates.Count < triangles.Length / 100);
    }

    [Fact]
    public void Warmed_bvh_queries_allocate_no_managed_memory()
    {
        var triangles = Enumerable.Range(0, 256)
            .Select(index => new SceneCollisionTriangle(new(-1, -1, 100 + index), new(1, -1, 100 + index), new(0, 1, 100 + index)))
            .Append(new SceneCollisionTriangle(new(-.2f, -.2f, 0), new(.2f, -.2f, 0), new(0, .2f, 0)))
            .ToArray();
        var collider = new SceneTriangleCollider(triangles);
        var candidates = new List<int>();
        for (var index = 0; index < 16; index++) collider.Query(Identity, new(0, 0, 2), new(0, 0, -4), .5f, candidates);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 256; index++) collider.Query(Identity, new(0, 0, 2), new(0, 0, -4), .5f, candidates);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static CollisionSnapshot RunCollision(int seed, bool linear)
    {
        var random = new Random(seed * 7919 + 23);
        var scene = new SceneGraph { ForceLinearPolygonSearchForTesting = linear };
        var source = scene.CreatePivot();
        var target = scene.CreatePivot();
        scene.PositionEntity(target, Next(-8, 8), Next(-8, 8), Next(-8, 8));
        scene.ScaleEntity(target, Next(.4f, 2.5f), Next(.4f, 2.5f), Next(.4f, 2.5f));
        scene.RotateEntity(target, Next(-160, 160), Next(-160, 160), Next(-160, 160));
        var radiusX = Next(.2f, 1.1f);
        var radiusY = Next(.15f, .9f);

        var triangles = new List<SceneCollisionTriangle>
        {
            new(new(-12, -12, 0), new(12, -12, 0), new(0, 12, 0), Index: 900, Surface: 4)
        };
        for (var index = 0; index < 192; index++)
        {
            var centre = new SceneVector3(Next(-80, 80), Next(-80, 80), Next(-80, 80));
            var extent = Next(.05f, 4);
            triangles.Add(new(
                new(centre.X - extent, centre.Y - extent, centre.Z),
                new(centre.X + extent, centre.Y - extent, centre.Z),
                new(centre.X, centre.Y + extent, centre.Z),
                Index: index,
                Surface: index % 5));
        }
        scene.EntityType(target, 2);
        scene.SetCollisionTriangles(target, triangles);
        var targetTransform = ScaleTransformY(scene.GetWorldTransform(target), radiusX / radiusY);
        var startScaled = Add(targetTransform.Position, Multiply(targetTransform.BasisZ, 5));
        var endScaled = Subtract(targetTransform.Position, Multiply(targetTransform.BasisZ, 5));
        scene.PositionEntity(source, startScaled.X, startScaled.Y / (radiusX / radiusY), startScaled.Z);
        scene.EntityRadius(source, radiusX, radiusY);
        scene.EntityType(source, 1);
        scene.Collisions(1, 2, method: 2, response: 1);
        scene.PositionEntity(source, endScaled.X, endScaled.Y / (radiusX / radiusY), endScaled.Z);
        scene.UpdateWorld();

        var collisions = new List<CollisionResult>();
        for (var index = 1; index <= scene.CountCollisions(source); index++)
        {
            collisions.Add(new(scene.CollisionEntity(source, index), scene.CollisionTriangle(source, index),
                scene.CollisionSurface(source, index), scene.CollisionTime(source, index),
                new(scene.CollisionX(source, index), scene.CollisionY(source, index), scene.CollisionZ(source, index)),
                new(scene.CollisionNX(source, index), scene.CollisionNY(source, index), scene.CollisionNZ(source, index))));
        }
        return new(new(scene.EntityX(source, true), scene.EntityY(source, true), scene.EntityZ(source, true)), collisions);

        float Next(float minimum, float maximum) => minimum + (float)random.NextDouble() * (maximum - minimum);
    }

    private static SceneTransform ScaleTransformY(SceneTransform transform, float scale) => new(
        ScaleY(transform.BasisX, scale), ScaleY(transform.BasisY, scale), ScaleY(transform.BasisZ, scale), ScaleY(transform.Position, scale));

    private static SceneVector3 ScaleY(SceneVector3 value, float scale) => new(value.X, value.Y * scale, value.Z);
    private static SceneVector3 Add(SceneVector3 left, SceneVector3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    private static SceneVector3 Subtract(SceneVector3 left, SceneVector3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    private static SceneVector3 Multiply(SceneVector3 value, float scale) => new(value.X * scale, value.Y * scale, value.Z * scale);

    private static SceneTransform Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), default);

    private sealed record CollisionSnapshot(SceneVector3 SourcePosition, List<CollisionResult> Collisions);
    private sealed record CollisionResult(int Entity, int Triangle, int Surface, float Time, SceneVector3 Position, SceneVector3 Normal);
}
