using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>Per-mesh change tracking: unrelated surface edits must not re-prepare collision meshes.</summary>
[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsCollisionIncrementalTests
{
    private static int Triangle(float z = 0)
    {
        var mesh = LegacyGraphics.CreateMesh();
        var surface = LegacyGraphics.CreateSurface(mesh);
        LegacyGraphics.AddVertex(surface, -10, -10, z);
        LegacyGraphics.AddVertex(surface, 0, 10, z);
        LegacyGraphics.AddVertex(surface, 10, -10, z);
        LegacyGraphics.AddTriangle(surface, 0, 1, 2);
        return mesh;
    }

    private static void Run(Action<SceneGraph> body)
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try { scene.Collisions(1, 2, 2, 1); body(scene); }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }

    [Fact]
    public void Particle_mesh_edited_every_frame_does_not_reprepare_static_collision_meshes()
    {
        Run(scene =>
        {
            var statics = Enumerable.Range(0, 20).Select(_ => Triangle()).ToArray();
            foreach (var mesh in statics) scene.EntityType(mesh, 2);
            var particle = Triangle();
            var surface = LegacyGraphics.GetSurface(particle, 1);
            LegacyGraphics.UpdateWorld();
            var before = LegacyGraphics.CollisionPrepMeshTotalForTesting;
            for (var frame = 0; frame < 10; frame++)
            {
                LegacyGraphics.ClearSurface(surface);
                LegacyGraphics.AddVertex(surface, 0, 0, frame);
                LegacyGraphics.AddVertex(surface, 1, 0, frame);
                LegacyGraphics.AddVertex(surface, 0, 1, frame);
                LegacyGraphics.AddTriangle(surface, 0, 1, 2);
                LegacyGraphics.UpdateWorld();
            }
            if (!EngineOptimizations.Disabled) Assert.Equal(before, LegacyGraphics.CollisionPrepMeshTotalForTesting);
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
        });
    }

    [Fact]
    public void Edited_static_mesh_is_reprepared_once_and_new_or_freed_meshes_are_tracked()
    {
        Run(scene =>
        {
            var a = Triangle();
            var b = Triangle();
            scene.EntityType(a, 2);
            scene.EntityType(b, 2);
            LegacyGraphics.UpdateWorld();
            var m0 = LegacyGraphics.CollisionPrepMeshTotalForTesting;
            var r0 = LegacyGraphics.CollisionPrepRebuiltTotalForTesting;

            LegacyGraphics.VertexCoords(LegacyGraphics.GetSurface(a, 1), 0, -5, -5, 3);
            LegacyGraphics.UpdateWorld();
            LegacyGraphics.UpdateWorld();
            if (!EngineOptimizations.Disabled) Assert.Equal(m0 + 1, LegacyGraphics.CollisionPrepMeshTotalForTesting);
            if (!EngineOptimizations.Disabled) Assert.Equal(r0 + 1, LegacyGraphics.CollisionPrepRebuiltTotalForTesting);

            var c = Triangle();
            scene.EntityType(c, 2);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(3, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
            LegacyGraphics.VertexCoords(LegacyGraphics.GetSurface(c, 1), 0, -5, -5, 3);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());

            LegacyGraphics.FreeEntity(b);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(2, LegacyGraphics.PolygonCollisionMeshCountForTesting);
        });
    }

    [Fact]
    public void Copy_of_collision_mesh_is_prepared_and_follows_shared_geometry_edits()
    {
        Run(scene =>
        {
            var a = Triangle();
            scene.EntityType(a, 2);
            LegacyGraphics.UpdateWorld();
            var copy = LegacyGraphics.CopyEntity(a);
            scene.EntityType(copy, 2);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(2, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
            LegacyGraphics.VertexCoords(LegacyGraphics.GetSurface(a, 1), 1, 0, 20, 0);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
        });
    }

    [Fact]
    public void Randomized_operations_keep_incremental_state_equal_to_a_full_scan()
    {
        var rng = new Random(12345);
        Run(scene =>
        {
            var meshes = new List<int>();
            var pivots = new List<int>();
            for (var step = 0; step < 1500; step++)
            {
                switch (rng.Next(11))
                {
                    case 0: meshes.Add(Triangle(rng.Next(5))); break;
                    case 1 when meshes.Count > 0:
                        {
                            var i = rng.Next(meshes.Count);
                            LegacyGraphics.FreeEntity(meshes[i]);
                            meshes.RemoveAt(i);
                            break;
                        }
                    case 2 when meshes.Count > 0: meshes.Add(LegacyGraphics.CopyEntity(meshes[rng.Next(meshes.Count)])); break;
                    case 3 when meshes.Count > 0: scene.EntityType(meshes[rng.Next(meshes.Count)], rng.Next(0, 4)); break;
                    case 4 when meshes.Count > 0: scene.EntityPickMode(meshes[rng.Next(meshes.Count)], rng.Next(0, 3)); break;
                    case 5 when meshes.Count > 0:
                        {
                            var m = meshes[rng.Next(meshes.Count)];
                            LegacyGraphics.VertexCoords(LegacyGraphics.GetSurface(m, 1), rng.Next(3), rng.Next(-9, 9), rng.Next(-9, 9), rng.Next(5));
                            break;
                        }
                    case 6 when meshes.Count > 0:
                        {
                            var s = LegacyGraphics.GetSurface(meshes[rng.Next(meshes.Count)], 1);
                            var v = LegacyGraphics.AddVertex(s, 1, 2, 3);
                            LegacyGraphics.AddVertex(s, 2, 2, 3);
                            LegacyGraphics.AddVertex(s, 3, 2, 3);
                            LegacyGraphics.AddTriangle(s, v, v + 1, v + 2);
                            break;
                        }
                    case 7 when meshes.Count > 0: LegacyGraphics.CreateSurface(meshes[rng.Next(meshes.Count)]); break;
                    case 8:
                        pivots.Add(scene.CreatePivot());
                        if (meshes.Count > 0 && rng.Next(2) == 0)
                            scene.EntityParent(meshes[rng.Next(meshes.Count)], pivots[^1]);
                        break;
                    case 9 when pivots.Count > 0: scene.EntityType(pivots[rng.Next(pivots.Count)], rng.Next(0, 4), true); break;
                    case 10: scene.Collisions(rng.Next(1, 4), 2, 2, 1); break;
                }
                if (rng.Next(3) == 0)
                {
                    LegacyGraphics.UpdateWorld();
                    Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
                }
            }
        });
    }
}
