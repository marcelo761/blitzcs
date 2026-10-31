using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>
/// Adapter-level differential check: every CameraPick/LinePick/EntityPick/EntityVisible query is answered twice on the
/// same scene, once by the original full scan and once by the pick-target index, across mesh vertex edits,
/// PositionMesh, pick-mode changes, entity moves and UpdateWorld steps with a moving collider.
/// </summary>
public sealed class LegacyGraphicsPickingDifferentialTests
{
    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    private static void Same(SceneGraph scene, Func<int> query, string what)
    {
        scene.UseLegacyPickPathForTesting = true;
        var a = query();
        var legacy = (LegacyScene.PickedEntity(), Bits(LegacyScene.PickedX()), Bits(LegacyScene.PickedY()), Bits(LegacyScene.PickedZ()),
            Bits(LegacyScene.PickedNX()), Bits(LegacyScene.PickedNY()), Bits(LegacyScene.PickedNZ()));
        scene.UseLegacyPickPathForTesting = false;
        var b = query();
        var fast = (LegacyScene.PickedEntity(), Bits(LegacyScene.PickedX()), Bits(LegacyScene.PickedY()), Bits(LegacyScene.PickedZ()),
            Bits(LegacyScene.PickedNX()), Bits(LegacyScene.PickedNY()), Bits(LegacyScene.PickedNZ()));
        Assert.True(a == b, $"{what}: {a} vs {b}");
        Assert.True(legacy == fast, $"{what}: picked {legacy} vs {fast}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Adapter_queries_match_the_full_scan_across_mesh_edits_and_world_updates(int seed)
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        var scene = new SceneGraph();
        LegacyScene.Current = scene;
        try
        {
            var rng = new Random(seed);
            float R(float s) => (float)(rng.NextDouble() * 2 - 1) * s;
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraViewport(camera, 0, 0, 640, 480);
            var cubes = new List<int>();
            var pivots = new List<int>();
            for (var i = 0; i < 12; i++)
            {
                var cube = LegacyGraphics.CreateCube();
                LegacyScene.PositionEntity(cube, R(6), R(4), 4 + R(3) + 4);
                LegacyScene.EntityPickMode(cube, 2);
                LegacyScene.EntityType(cube, 2);
                cubes.Add(cube);
            }
            for (var i = 0; i < 20; i++)
            {
                var pivot = LegacyScene.CreatePivot();
                LegacyScene.PositionEntity(pivot, R(8), R(5), 3 + R(6) + 6);
                if (i % 2 == 0) { LegacyScene.EntityRadius(pivot, .3f); LegacyScene.EntityPickMode(pivot, 1); }
                else { LegacyScene.EntityBox(pivot, -.2f, -.2f, -.2f, .4f, .4f, .4f); LegacyScene.EntityPickMode(pivot, 3, 0); }
                pivots.Add(pivot);
            }
            var mover = LegacyScene.CreatePivot();
            LegacyScene.EntityType(mover, 1);
            LegacyScene.EntityRadius(mover, .25f);
            LegacyScene.Collisions(1, 2, 2, 2);
            var queries = 0;
            for (var step = 0; step < 120; step++)
            {
                switch (rng.Next(6))
                {
                    case 0:
                    {
                        var surface = LegacyGraphics.GetSurface(cubes[rng.Next(cubes.Count)], 1);
                        var v = rng.Next(LegacyGraphics.CountVertices(surface));
                        LegacyGraphics.VertexCoords(surface, v, R(1.5f), R(1.5f), R(1.5f));
                        break;
                    }
                    case 1:
                        LegacyGraphics.PositionMesh(cubes[rng.Next(cubes.Count)], R(.3f), R(.3f), R(.3f));
                        break;
                    case 2:
                        LegacyScene.PositionEntity(pivots[rng.Next(pivots.Count)], R(8), R(5), 3 + R(6) + 6);
                        break;
                    case 3:
                        LegacyScene.EntityPickMode(cubes[rng.Next(cubes.Count)], rng.Next(3) == 0 ? 0 : 2);
                        break;
                    default:
                        LegacyScene.PositionEntity(mover, R(6), R(4), 8 + R(4));
                        LegacyGraphics.UpdateWorld();
                        break;
                }
                var x = (float)rng.NextDouble() * 640; var y = (float)rng.NextDouble() * 480;
                Same(scene, () => LegacyGraphics.CameraPick(camera, x, y), $"seed {seed} step {step} CameraPick"); queries++;
                float sx = R(8), sy = R(5), sz = R(2), dx = R(10), dy = R(10), dz = 20 + R(5), r = rng.Next(2) == 0 ? 0 : .3f;
                Same(scene, () => LegacyGraphics.LinePick(sx, sy, sz, dx, dy, dz, r), $"seed {seed} step {step} LinePick"); queries++;
                var item = pivots[rng.Next(pivots.Count)];
                Same(scene, () => LegacyGraphics.LinePick(LegacyScene.EntityX(item, 1), LegacyScene.EntityY(item, 1), LegacyScene.EntityZ(item, 1), 0, -10, 0),
                    $"seed {seed} step {step} probe"); queries++;
                LegacyScene.PointEntity(camera, cubes[rng.Next(cubes.Count)]);
                Same(scene, () => LegacyGraphics.EntityPick(camera, 30), $"seed {seed} step {step} EntityPick"); queries++;
                var p = pivots[rng.Next(pivots.Count)]; var q = pivots[rng.Next(pivots.Count)];
                Same(scene, () => LegacyGraphics.EntityVisible(p, q), $"seed {seed} step {step} EntityVisible"); queries++;
                LegacyScene.PointEntity(camera, cubes[0]);
                LegacyScene.RotateEntity(camera, 0, 0, 0);
            }
            Assert.Equal(600, queries);
        }
        finally { LegacyGraphics.ResetForTests(); LegacyScene.Current = previous; }
    }
}
