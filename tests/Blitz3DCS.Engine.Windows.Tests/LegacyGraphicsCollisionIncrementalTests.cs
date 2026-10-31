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
            if (EngineOptimizations.CollGeom) Assert.Equal(before, LegacyGraphics.CollisionPrepMeshTotalForTesting);
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
            if (EngineOptimizations.CollGeom) Assert.Equal(m0 + 1, LegacyGraphics.CollisionPrepMeshTotalForTesting);
            if (EngineOptimizations.CollGeom) Assert.Equal(r0 + 1, LegacyGraphics.CollisionPrepRebuiltTotalForTesting);

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
                switch (rng.Next(17))
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
                    case 11 when meshes.Count > 0:
                        { var m = meshes[rng.Next(meshes.Count)]; scene.EntityType(m, scene.GetEntityType(m)); break; }
                    case 12 when meshes.Count > 0:
                        { var m = meshes[rng.Next(meshes.Count)]; scene.EntityType(m, scene.GetEntityType(m)); scene.EntityPickMode(m, 0); break; }
                    case 13:
                        { var sprite = LegacyGraphics.CreateSprite(); if (rng.Next(2) == 0) LegacyGraphics.FreeEntity(sprite); else meshes.Add(sprite); break; }
                    case 14 when pivots.Count > 0 && meshes.Count > 0:
                        scene.EntityParent(meshes[rng.Next(meshes.Count)], pivots[rng.Next(pivots.Count)]); break;
                    case 15 when pivots.Count > 0:
                        {
                            var i = rng.Next(pivots.Count);
                            LegacyGraphics.FreeEntity(pivots[i]);
                            pivots.RemoveAt(i);
                            meshes.RemoveAll(m => !MeshAlive(scene, m));
                            break;
                        }
                    case 16 when meshes.Count > 0: scene.EntityPickMode(meshes[rng.Next(meshes.Count)], 2, false); break;
                }
                if (rng.Next(3) == 0)
                {
                    LegacyGraphics.UpdateWorld();
                    Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
                    Assert.Equal(0, LegacyGraphics.CollisionSelectionMismatchForTesting());
                }
            }
        });
    }

    private static bool MeshAlive(SceneGraph scene, int entity)
    {
        try { _ = scene.GetEntityType(entity); return true; }
        catch (Exception) { return false; }
    }

    [Fact]
    public void Idempotent_setters_and_particle_churn_do_not_rebuild_the_selection()
    {
        Run(scene =>
        {
            var parent = scene.CreatePivot();
            var statics = Enumerable.Range(0, 10).Select(_ => Triangle()).ToArray();
            foreach (var mesh in statics) { scene.EntityParent(mesh, parent); }
            scene.EntityType(parent, 2, true);
            scene.EntityPickMode(statics[0], 2);
            LegacyGraphics.UpdateWorld();
            var rebuilds = LegacyGraphics.CollisionSelectionRebuildTotalForTesting;
            var prepared = LegacyGraphics.CollisionPrepMeshTotalForTesting;
            for (var frame = 0; frame < 20; frame++)
            {
                scene.EntityType(parent, 2, true);
                scene.EntityType(statics[1], 2);
                scene.EntityPickMode(statics[0], 2);
                scene.Collisions(1, 2, 2, 1);
                for (var i = 0; i < 5; i++) LegacyGraphics.FreeEntity(LegacyGraphics.CreateSprite());
                var kept = LegacyGraphics.CreateSprite();
                LegacyGraphics.UpdateWorld();
                Assert.Equal(0, LegacyGraphics.CollisionSelectionMismatchForTesting());
                Assert.Equal(10, LegacyGraphics.PolygonCollisionMeshCountForTesting);
                LegacyGraphics.FreeEntity(kept);
            }
            if (EngineOptimizations.CollGeom && EngineOptimizations.IncrSelection)
            {
                Assert.Equal(rebuilds, LegacyGraphics.CollisionSelectionRebuildTotalForTesting);
                Assert.Equal(prepared, LegacyGraphics.CollisionPrepMeshTotalForTesting);
            }
        });
    }

    [Fact]
    public void Selection_follows_recursive_type_changes_and_copies_incrementally()
    {
        Run(scene =>
        {
            var parent = scene.CreatePivot();
            var children = Enumerable.Range(0, 4).Select(_ => Triangle()).ToArray();
            foreach (var child in children) scene.EntityParent(child, parent);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(0, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            scene.EntityType(parent, 2, true);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(4, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            var copy = LegacyGraphics.CopyEntity(children[0]);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(5, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            scene.EntityType(parent, 0, true);
            LegacyGraphics.UpdateWorld();
            Assert.Equal(1, LegacyGraphics.PolygonCollisionMeshCountForTesting);
            Assert.Equal(0, LegacyGraphics.CollisionSelectionMismatchForTesting());
            Assert.Equal(0, LegacyGraphics.CollisionStaleMeshCountForTesting());
            _ = copy;
        });
    }
}
