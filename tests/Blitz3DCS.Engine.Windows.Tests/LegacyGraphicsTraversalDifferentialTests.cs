using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsTraversalDifferentialTests
{
    private static bool Exists(int id)
    {
        try { LegacyScene.Current.GetWorldTransform(id); return true; }
        catch (Exception) { return false; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Optimized_traversal_and_sort_match_the_legacy_draw_sequence_over_random_scenes(int seed)
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var random = new Random(seed);
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraViewport(camera, 0, 0, 800, 600);
            var entities = new List<int>();
            for (var i = 0; i < 160; i++)
            {
                var parent = entities.Count > 0 && random.Next(3) == 0 ? entities[random.Next(entities.Count)] : 0;
                var pivot = random.Next(5) == 0;
                var id = pivot ? LegacyScene.CreatePivot(parent) : LegacyGraphics.CreateCube(parent);
                LegacyScene.PositionEntity(id, random.Next(-60, 60), random.Next(-30, 30), random.Next(-20, 120));
                if (!pivot && random.Next(6) == 0) LegacyGraphics.EntityAlpha(id, .5f);
                if (!pivot && random.Next(8) == 0) LegacyGraphics.EntityOrder(id, random.Next(-2, 3));
                if (!pivot && random.Next(10) == 0) LegacyGraphics.EntityBlend(id, 1 + random.Next(3));
                if (!pivot && random.Next(12) == 0) LegacyGraphics.EntityAutoFade(id, 20, 90);
                if (random.Next(10) == 0) LegacyScene.HideEntity(id);
                entities.Add(id);
            }

            var total = 0;
            for (var frame = 0; frame < 12; frame++)
            {
                // Churn: moves, frees (slot reuse), re-creations, camera moves.
                foreach (var id in entities.Where(e => random.Next(4) == 0).ToList())
                    if (Exists(id)) LegacyScene.PositionEntity(id, random.Next(-60, 60), random.Next(-30, 30), random.Next(-20, 120));
                if (frame % 3 == 1 && entities.Count > 20)
                {
                    var victim = entities[random.Next(entities.Count)];
                    if (Exists(victim)) LegacyGraphics.FreeEntity(victim);
                    entities.RemoveAll(e => !Exists(e));
                    entities.Add(LegacyGraphics.CreateCube());
                }
                if (frame % 4 == 2)
                {
                    var child = entities[random.Next(entities.Count)];
                    var target = entities[random.Next(entities.Count)];
                    if (child != target && Exists(child) && Exists(target))
                        try { LegacyScene.EntityParent(child, random.Next(2) == 0 ? 0 : target); } catch (Exception) { }
                }
                LegacyScene.PositionEntity(camera, random.Next(-40, 40), random.Next(-10, 10), random.Next(-30, 30));
                LegacyScene.RotateEntity(camera, random.Next(-30, 30), random.Next(-180, 180), 0);

                var legacy = LegacyGraphics.SnapshotSceneDrawsForTesting(camera, 800, 600, optimized: false);
                var optimized = LegacyGraphics.SnapshotSceneDrawsForTesting(camera, 800, 600, optimized: true);
                total += legacy.Count;
                Assert.Equal(legacy, optimized);
            }
            Assert.True(total > 100, $"total={total}");
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }
}
