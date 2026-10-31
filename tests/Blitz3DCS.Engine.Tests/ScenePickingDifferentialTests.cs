using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>
/// Drives two identical scenes through randomized, interleaved edits (creation/free/reparent/copy, transforms, pick
/// modes, shapes, mesh edits, hide/show, collision rules with moving sources and UpdateWorld) and after every step runs
/// a battery of LinePick/EntityPick/EntityVisible queries. One scene uses the original full-scan picking, the other the
/// pick-target index; every result and every Picked* output must match bit for bit.
/// </summary>
public sealed class ScenePickingDifferentialTests
{
    private sealed class Pair
    {
        public readonly SceneGraph Legacy = new() { UseLegacyPickPathForTesting = true };
        public readonly SceneGraph Fast = new();
        public readonly List<int> Live = [];
        public int Queries;
        public void Each(Action<SceneGraph> action) { action(Legacy); action(Fast); }
        public int Create(int parent)
        {
            var a = Legacy.CreatePivot(parent);
            var b = Fast.CreatePivot(parent);
            Assert.Equal(a, b);
            Live.Add(a);
            return a;
        }
    }

    [ThreadStatic] private static int LastOp;
    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    private static SceneCollisionTriangle[] RandomMesh(Random rng)
    {
        var list = new List<SceneCollisionTriangle>();
        var kind = rng.Next(3);
        if (kind == 0)
        {
            // Floor grid (item ground probe target).
            var n = 2 + rng.Next(6);
            var size = 1 + (float)rng.NextDouble() * 6;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    float x0 = -size + 2 * size * i / n, x1 = -size + 2 * size * (i + 1) / n;
                    float z0 = -size + 2 * size * j / n, z1 = -size + 2 * size * (j + 1) / n;
                    list.Add(new(new(x0, 0, z0), new(x0, 0, z1), new(x1, 0, z1), list.Count, 1 + rng.Next(3)));
                    list.Add(new(new(x0, 0, z0), new(x1, 0, z1), new(x1, 0, z0), list.Count, 1 + rng.Next(3)));
                }
        }
        else if (kind == 1)
        {
            // Closed-ish room box with inward faces.
            float h = 1 + (float)rng.NextDouble() * 5, y = 1 + (float)rng.NextDouble() * 3;
            void Quad(SceneVector3 a, SceneVector3 b, SceneVector3 c, SceneVector3 d)
            {
                list.Add(new(a, b, c, list.Count));
                list.Add(new(a, c, d, list.Count));
            }
            Quad(new(-h, 0, -h), new(-h, 0, h), new(h, 0, h), new(h, 0, -h));
            Quad(new(-h, 0, h), new(-h, y, h), new(h, y, h), new(h, 0, h));
            Quad(new(h, 0, -h), new(h, y, -h), new(-h, y, -h), new(-h, 0, -h));
            Quad(new(-h, 0, -h), new(-h, y, -h), new(-h, y, h), new(-h, 0, h));
            Quad(new(h, 0, h), new(h, y, h), new(h, y, -h), new(h, 0, -h));
            Quad(new(-h, y, -h), new(h, y, -h), new(h, y, h), new(-h, y, h));
        }
        else
        {
            var count = rng.Next(0, 12);
            for (var i = 0; i < count; i++)
                list.Add(new(RandomVector(rng, 3), RandomVector(rng, 3), RandomVector(rng, 3), rng.Next(2) == 0 ? -1 : i, rng.Next(4)));
        }
        return list.ToArray();
    }

    private static SceneVector3 RandomVector(Random rng, float scale) =>
        new((float)(rng.NextDouble() * 2 - 1) * scale, (float)(rng.NextDouble() * 2 - 1) * scale, (float)(rng.NextDouble() * 2 - 1) * scale);

    private static void ConfigureTarget(Pair p, Random rng, int id)
    {
        var mode = rng.Next(5) switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => rng.Next(1, 4) };
        var obscurer = rng.Next(4) != 0;
        switch (mode)
        {
            case 1:
            {
                var r = (float)rng.NextDouble() * 1.5f + (rng.Next(10) == 0 ? -0.5f : 0.01f);
                p.Each(g => g.EntityRadius(id, r));
                break;
            }
            case 2:
                if (rng.Next(12) == 0) p.Each(g => g.SetInfinitePlane(id));
                else
                {
                    var mesh = RandomMesh(rng);
                    p.Each(g => g.SetCollisionTriangles(id, mesh));
                }
                break;
            case 3:
            {
                var a = RandomVector(rng, 1); var s = RandomVector(rng, 1.5f);
                p.Each(g => g.EntityBox(id, a.X, a.Y, a.Z, s.X, s.Y, s.Z));
                break;
            }
        }
        p.Each(g => g.EntityPickMode(id, mode, obscurer));
    }

    private static void RandomStep(Pair p, Random rng, List<int> movers)
    {
        var live = p.Live;
        int Pick() => live[rng.Next(live.Count)];
        var op = live.Count < 8 ? 0 : rng.Next(20);
        LastOp = op;
        switch (op)
        {
            case 0:
            case 1:
            {
                var parent = live.Count > 0 && rng.Next(3) == 0 ? Pick() : 0;
                var id = p.Create(parent);
                var pos = RandomVector(rng, 12);
                p.Each(g => g.PositionEntity(id, pos.X, pos.Y, pos.Z, true));
                if (rng.Next(3) != 0) ConfigureTarget(p, rng, id);
                break;
            }
            case 2:
            {
                var id = Pick();
                var removed = p.Legacy.GetSubtreeEntityIds(id);
                p.Each(g => g.FreeEntity(id));
                foreach (var r in removed) { live.Remove(r); movers.Remove(r); }
                break;
            }
            case 3:
            {
                var id = Pick();
                var parent = rng.Next(3) == 0 ? 0 : Pick();
                if (p.Legacy.GetSubtreeEntityIds(id).Contains(parent)) break;
                var global = rng.Next(2) == 0;
                p.Each(g => g.EntityParent(id, parent, global));
                break;
            }
            case 4:
            case 5:
            {
                var id = Pick();
                var v = RandomVector(rng, 3);
                var global = rng.Next(2) == 0;
                p.Each(g => g.TranslateEntity(id, v.X, v.Y, v.Z, global));
                break;
            }
            case 6:
            {
                var id = Pick();
                var v = RandomVector(rng, 180);
                var global = rng.Next(2) == 0;
                p.Each(g => g.RotateEntity(id, v.X, v.Y, v.Z, global));
                break;
            }
            case 7:
            {
                var id = Pick();
                var v = RandomVector(rng, 2);
                var uniform = rng.Next(2) == 0;
                var s = uniform ? new SceneVector3(v.X, v.X, v.X) : v;
                if (rng.Next(20) == 0) s = new(0, 1, 1);
                p.Each(g => g.ScaleEntity(id, s.X, s.Y, s.Z));
                break;
            }
            case 8:
            case 9:
                ConfigureTarget(p, rng, Pick());
                break;
            case 10:
            {
                // Mesh vertex edit of an existing (possibly pickable) entity.
                var id = Pick();
                var mesh = RandomMesh(rng);
                p.Each(g => g.SetCollisionTriangles(id, mesh));
                break;
            }
            case 11:
            {
                var id = Pick();
                if (rng.Next(2) == 0) p.Each(g => g.HideEntity(id)); else p.Each(g => g.ShowEntity(id));
                break;
            }
            case 12:
            {
                var id = Pick();
                var parent = rng.Next(2) == 0 ? 0 : Pick();
                if (p.Legacy.GetSubtreeEntityIds(id).Contains(parent)) break;
                var a = p.Legacy.CopyEntity(id, parent);
                var b = p.Fast.CopyEntity(id, parent);
                Assert.Equal(a, b);
                live.AddRange(p.Legacy.GetSubtreeEntityIds(a));
                break;
            }
            case 13:
            {
                // Collision source that moves every UpdateWorld.
                var id = Pick();
                p.Each(g => { g.EntityType(id, 1); g.EntityRadius(id, 0.3f); });
                if (!movers.Contains(id)) movers.Add(id);
                break;
            }
            case 14:
            {
                var id = Pick();
                var type = 2 + rng.Next(2);
                p.Each(g => g.EntityType(id, type));
                break;
            }
            case 15:
            {
                var method = 1 + rng.Next(3);
                var response = rng.Next(4);
                var dest = 2 + rng.Next(2);
                p.Each(g => g.Collisions(1, dest, method, response));
                break;
            }
            default:
            {
                foreach (var m in movers)
                {
                    var v = RandomVector(rng, 1.5f);
                    p.Each(g => g.TranslateEntity(m, v.X, v.Y, v.Z, true));
                }
                p.Each(g => g.UpdateWorld());
                break;
            }
        }
    }

    private static void AssertPicked(Pair p, int a, int b, string what)
    {
        Assert.True(a == b, $"{what}: result {a} vs {b}");
        Assert.True(p.Legacy.PickedEntity == p.Fast.PickedEntity, $"{what}: PickedEntity {p.Legacy.PickedEntity} vs {p.Fast.PickedEntity}");
        Assert.True(Bits(p.Legacy.PickedX) == Bits(p.Fast.PickedX), $"{what}: PickedX {p.Legacy.PickedX} vs {p.Fast.PickedX}");
        Assert.True(Bits(p.Legacy.PickedY) == Bits(p.Fast.PickedY), $"{what}: PickedY");
        Assert.True(Bits(p.Legacy.PickedZ) == Bits(p.Fast.PickedZ), $"{what}: PickedZ");
        Assert.True(Bits(p.Legacy.PickedNX) == Bits(p.Fast.PickedNX), $"{what}: PickedNX");
        Assert.True(Bits(p.Legacy.PickedNY) == Bits(p.Fast.PickedNY), $"{what}: PickedNY");
        Assert.True(Bits(p.Legacy.PickedNZ) == Bits(p.Fast.PickedNZ), $"{what}: PickedNZ");
        p.Queries++;
    }

    private static int RunQueries(Pair p, Random rng, string where)
    {
        var hits = 0;
        void Line(SceneVector3 s, SceneVector3 d, float r, string kind)
        {
            var a = p.Legacy.LinePick(s.X, s.Y, s.Z, d.X, d.Y, d.Z, r);
            var b = p.Fast.LinePick(s.X, s.Y, s.Z, d.X, d.Y, d.Z, r);
            if (a != 0) hits++;
            AssertPicked(p, a, b, $"{where} LinePick[{kind}] ({s.X},{s.Y},{s.Z}) d=({d.X},{d.Y},{d.Z}) r={r}");
        }
        float Radius() => rng.Next(3) switch { 0 => 0, 1 => (float)rng.NextDouble() * 0.6f, _ => rng.Next(2) == 0 ? 0.3f : 0 };

        for (var i = 0; i < 6; i++) Line(RandomVector(rng, 15), RandomVector(rng, 25), Radius(), "random");
        Line(RandomVector(rng, 15), default, Radius(), "zero");
        Line(RandomVector(rng, 15), RandomVector(rng, 5000), Radius(), "huge");
        if (p.Live.Count > 0)
        {
            for (var i = 0; i < 4; i++)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                var t = p.Legacy.GetWorldTransform(id);
                // Inside a target, then downward 10-unit item probe from just above it.
                Line(t.Position, RandomVector(rng, 6), Radius(), "inside");
                Line(new(t.Position.X, t.Position.Y + 0.5f, t.Position.Z), new(0, -10, 0), 0, "probe");
                // Grazing rays along the edges of the target's unit/box neighbourhood.
                var e = (float)rng.NextDouble() * 2;
                var off = rng.Next(4) switch { 0 => e, 1 => -e, 2 => e + 0.0101f, _ => e - 0.0099f };
                var axis = rng.Next(3);
                var start = axis switch
                {
                    0 => new SceneVector3(t.Position.X - 20, t.Position.Y + off, t.Position.Z + off),
                    1 => new SceneVector3(t.Position.X + off, t.Position.Y - 20, t.Position.Z + off),
                    _ => new SceneVector3(t.Position.X + off, t.Position.Y + off, t.Position.Z - 20)
                };
                var dir = axis switch { 0 => new SceneVector3(40, 0, 0), 1 => new SceneVector3(0, 40, 0), _ => new SceneVector3(0, 0, 40) };
                Line(start, dir, Radius(), "graze");
            }
            for (var i = 0; i < 3; i++)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                var range = rng.Next(4) == 0 ? 0.3f : (float)rng.NextDouble() * 30;
                var a = p.Legacy.EntityPick(id, range);
                var b = p.Fast.EntityPick(id, range);
                if (a != 0) hits++;
                AssertPicked(p, a, b, $"{where} EntityPick {id} {range}");
            }
            for (var i = 0; i < 5; i++)
            {
                var s = p.Live[rng.Next(p.Live.Count)];
                var d = p.Live[rng.Next(p.Live.Count)];
                var a = p.Legacy.EntityVisible(s, d);
                var b = p.Fast.EntityVisible(s, d);
                Assert.True(a == b, $"{where} EntityVisible {s}->{d}: {a} vs {b}");
                p.Queries++;
            }
        }
        return hits;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    [InlineData(13)] [InlineData(14)]
    public void Randomized_interleaved_edits_pick_identically(int seed)
    {
        var rng = new Random(seed * 7919);
        var p = new Pair();
        var movers = new List<int>();
        p.Each(g => g.Collisions(1, 2, 2, 2));
        p.Each(g => g.Collisions(1, 3, 3, 1));
        var hits = 0;
        for (var step = 0; step < 160; step++)
        {
            RandomStep(p, rng, movers);
            // Sparse divergence check: computing every world transform would also clean every node each step and
            // hide stale-index bugs that depend on nodes staying dirty between queries.
            if (step % 7 == 6)
                foreach (var id in p.Live)
                    Assert.True(p.Legacy.GetWorldTransform(id) == p.Fast.GetWorldTransform(id), $"scenes diverged: seed {seed} step {step} op {LastOp} id {id}");
            hits += RunQueries(p, rng, $"seed {seed} step {step}");
        }
        Assert.True(hits > 20, $"too few hits ({hits}) to be meaningful");
        Assert.True(p.Queries > 160 * 20);
        Assert.True(p.Fast.PickNarrowTestsForTesting > 0);
    }

    [Fact]
    public void Door_button_box_is_pickable_after_update_world_with_far_moving_collider()
    {
        var p = new Pair();
        p.Each(g => g.Collisions(1, 2, 2, 2));
        var floor = p.Create(0);
        var room = new SceneCollisionTriangle[]
        {
            new(new(-50, 0, -50), new(-50, 0, 50), new(50, 0, 50), 0),
            new(new(-50, 0, -50), new(50, 0, 50), new(50, 0, -50), 1)
        };
        p.Each(g => { g.SetCollisionTriangles(floor, room); g.EntityType(floor, 2); g.EntityPickMode(floor, 2); });
        var door = p.Create(0);
        p.Each(g => g.PositionEntity(door, 3, 1.5f, 0));
        var button = p.Create(door);
        p.Each(g =>
        {
            g.PositionEntity(button, 0.2f, 0, 0);
            g.EntityBox(button, -0.05f, -0.05f, -0.05f, 0.1f, 0.1f, 0.1f);
            g.EntityPickMode(button, 3);
        });
        var player = p.Create(0);
        p.Each(g => { g.PositionEntity(player, 0, 1.5f, 0); g.PointEntity(player, button); });
        var npc = p.Create(0);
        p.Each(g => { g.PositionEntity(npc, 40, 3, 40); g.EntityType(npc, 1); g.EntityRadius(npc, 0.3f); });
        for (var frame = 0; frame < 10; frame++)
        {
            p.Each(g => { g.TranslateEntity(npc, -0.5f, -0.2f, 0.1f); g.UpdateWorld(); });
            p.Each(g => g.PointEntity(player, button));
            var a = p.Legacy.EntityPick(player, 4);
            var b = p.Fast.EntityPick(player, 4);
            AssertPicked(p, a, b, $"button frame {frame}");
            Assert.Equal(button, b);
            // The door slides; the button must follow without a stale index entry.
            p.Each(g => g.TranslateEntity(door, 0, 0, 0.01f));
        }
    }

    [Fact]
    public void Item_ground_probe_hits_polygon_floor_like_the_full_scan()
    {
        var p = new Pair();
        var room = p.Create(0);
        p.Each(g => g.PositionEntity(room, 8, 0, -4));
        var floorMesh = p.Create(room);
        var tris = new List<SceneCollisionTriangle>();
        for (var i = 0; i < 16; i++)
            for (var j = 0; j < 16; j++)
            {
                float x0 = -4 + i * .5f, z0 = -4 + j * .5f, x1 = x0 + .5f, z1 = z0 + .5f;
                tris.Add(new(new(x0, 0, z0), new(x0, 0, z1), new(x1, 0, z1), tris.Count, 3));
                tris.Add(new(new(x0, 0, z0), new(x1, 0, z1), new(x1, 0, z0), tris.Count, 3));
            }
        var mesh = tris.ToArray();
        p.Each(g => { g.SetCollisionTriangles(floorMesh, mesh); g.EntityPickMode(floorMesh, 2); });
        var items = new List<int>();
        var rng = new Random(5);
        for (var i = 0; i < 40; i++)
        {
            var item = p.Create(0);
            var x = 4 + (float)rng.NextDouble() * 8; var z = -8 + (float)rng.NextDouble() * 8;
            p.Each(g => { g.PositionEntity(item, x, 0.5f + (float)i / 40, z); g.EntityRadius(item, 0.1f); g.EntityPickMode(item, 1, false); });
            items.Add(item);
        }
        var floorHits = 0;
        for (var frame = 0; frame < 5; frame++)
        {
            foreach (var item in items)
            {
                var x = p.Legacy.EntityX(item, true); var y = p.Legacy.EntityY(item, true); var z = p.Legacy.EntityZ(item, true);
                var a = p.Legacy.LinePick(x, y, z, 0, -10, 0);
                var b = p.Fast.LinePick(x, y, z, 0, -10, 0);
                AssertPicked(p, a, b, $"probe item {item} frame {frame}");
                if (b == floorMesh) floorHits++;
                p.Each(g => g.TranslateEntity(item, 0, -0.05f, 0));
            }
            // Room mesh vertex edit (raise the floor) must be seen by the next probes.
            if (frame == 2)
            {
                var raised = mesh.Select(t => new SceneCollisionTriangle(
                    t.A with { Y = 0.2f }, t.B with { Y = 0.2f }, t.C with { Y = 0.2f }, t.Index, t.Surface)).ToArray();
                p.Each(g => g.SetCollisionTriangles(floorMesh, raised));
            }
        }
        Assert.True(floorHits > 100, $"floor hits {floorHits}");
    }

    [Fact]
    public void Static_targets_are_not_narrow_tested_by_a_short_probe_far_away()
    {
        var g = new SceneGraph();
        for (var i = 0; i < 500; i++)
        {
            var e = g.CreatePivot();
            g.PositionEntity(e, i * 3, 0, 0);
            g.EntityRadius(e, 0.5f);
            g.EntityPickMode(e, 1);
        }
        g.LinePick(-100, 10, 0, 0, -10, 0);
        var before = g.PickNarrowTestsForTesting;
        Assert.Equal(0, g.LinePick(-100, 10, 0, 0, -10, 0));
        Assert.Equal(before, g.PickNarrowTestsForTesting);
        Assert.NotEqual(0, g.LinePick(30, 10, 0, 0, -20, 0));
        Assert.True(g.PickNarrowTestsForTesting - before <= 4);
    }
}
