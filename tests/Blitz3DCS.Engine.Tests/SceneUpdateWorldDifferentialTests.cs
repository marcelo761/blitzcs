using Blitz3DCS.Engine;
using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>
/// Drives two identical scenes through randomized operations; one runs the original per-frame UpdateWorld path, the
/// other the persistent-list path. Every observable (transforms, collision results) must match bit for bit.
/// </summary>
public sealed class SceneUpdateWorldDifferentialTests
{
    private static readonly SceneCollisionTriangle[] Room = BuildRoom();

    private static SceneCollisionTriangle[] BuildRoom()
    {
        var list = new List<SceneCollisionTriangle>();
        void Quad(SceneVector3 a, SceneVector3 b, SceneVector3 c, SceneVector3 d)
        {
            list.Add(new(a, b, c, list.Count));
            list.Add(new(a, c, d, list.Count));
        }
        const float h = 12;
        Quad(new(-h, 0, -h), new(-h, 0, h), new(h, 0, h), new(h, 0, -h));
        Quad(new(-h, 0, h), new(-h, 6, h), new(h, 6, h), new(h, 0, h));
        Quad(new(h, 0, -h), new(h, 6, -h), new(-h, 6, -h), new(-h, 0, -h));
        Quad(new(-h, 0, -h), new(-h, 6, -h), new(-h, 6, h), new(-h, 0, h));
        Quad(new(h, 0, h), new(h, 6, h), new(h, 6, -h), new(h, 0, -h));
        return list.ToArray();
    }

    private sealed class Pair
    {
        public readonly SceneGraph Legacy = new() { UseLegacyCollisionPathForTesting = true };
        public readonly SceneGraph Fast = new();
        public readonly List<int> Live = [];
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

    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    private static int AssertSame(Pair p, int step)
    {
        var hits = 0;
        foreach (var id in p.Live)
        {
            foreach (var global in new[] { true, false })
            {
                Assert.True(Bits(p.Legacy.EntityX(id, global)) == Bits(p.Fast.EntityX(id, global)), $"x {id} step {step}");
                Assert.True(Bits(p.Legacy.EntityY(id, global)) == Bits(p.Fast.EntityY(id, global)), $"y {id} step {step}");
                Assert.True(Bits(p.Legacy.EntityZ(id, global)) == Bits(p.Fast.EntityZ(id, global)), $"z {id} step {step}");
            }
            var count = p.Legacy.CountCollisions(id);
            hits += count;
            Assert.Equal(count, p.Fast.CountCollisions(id));
            for (var i = 1; i <= count; i++)
            {
                Assert.Equal(p.Legacy.CollisionEntity(id, i), p.Fast.CollisionEntity(id, i));
                Assert.Equal(p.Legacy.CollisionTriangle(id, i), p.Fast.CollisionTriangle(id, i));
                Assert.Equal(p.Legacy.CollisionSurface(id, i), p.Fast.CollisionSurface(id, i));
                Assert.Equal(Bits(p.Legacy.CollisionX(id, i)), Bits(p.Fast.CollisionX(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionY(id, i)), Bits(p.Fast.CollisionY(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionZ(id, i)), Bits(p.Fast.CollisionZ(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionNX(id, i)), Bits(p.Fast.CollisionNX(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionNY(id, i)), Bits(p.Fast.CollisionNY(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionNZ(id, i)), Bits(p.Fast.CollisionNZ(id, i)));
                Assert.Equal(Bits(p.Legacy.CollisionTime(id, i)), Bits(p.Fast.CollisionTime(id, i)));
            }
        }
        Assert.Equal(p.Legacy.GetRootEntityIds(), p.Fast.GetRootEntityIds());
        return hits;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Randomized_scenes_match_the_original_path_bit_for_bit(int seed)
    {
        var rng = new Random(seed);
        var p = new Pair();
        float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

        void Configure(int id)
        {
            var kind = rng.Next(5);
            var radius = R(.3f, 1.2f);
            var ellipsoid = rng.Next(2) == 0;
            if (kind == 0)
                p.Each(s => { s.EntityType(id, 1); s.EntityRadius(id, radius, ellipsoid ? radius * 1.7f : radius); });
            else if (kind == 1)
                p.Each(s => { s.EntityType(id, 2); s.EntityRadius(id, radius); });
            else if (kind == 2)
                p.Each(s => { s.EntityType(id, 3); s.EntityBox(id, -1, -1, -1, 2, 2, 2); });
            else if (kind == 3)
                p.Each(s => s.EntityType(id, 4));
            else
                p.Each(s => { s.EntityType(id, 5); s.EntityRadius(id, radius); });
        }

        var room = p.Create(0);
        p.Each(s => { s.EntityType(room, 4); s.SetCollisionTriangles(room, Room); });
        for (var i = 0; i < 60; i++)
        {
            var parent = p.Live.Count > 3 && rng.Next(3) == 0 ? p.Live[rng.Next(p.Live.Count)] : 0;
            var id = p.Create(parent);
            var x = R(-9, 9); var y = R(.5f, 4); var z = R(-9, 9);
            p.Each(s => s.PositionEntity(id, x, y, z));
            Configure(id);
        }
        int[,] rules = { { 1, 2, 1, 1 }, { 1, 3, 3, 2 }, { 1, 4, 2, 2 }, { 1, 5, 1, 3 }, { 2, 4, 2, 1 }, { 2, 2, 1, 2 }, { 3, 4, 2, 3 }, { 5, 3, 3, 2 }, { 5, 5, 1, 2 }, { 1, 1, 1, 2 } };
        for (var r = 0; r < rules.GetLength(0); r++)
        {
            var a = rules[r, 0]; var b = rules[r, 1]; var m = rules[r, 2]; var resp = rules[r, 3];
            p.Each(s => s.Collisions(a, b, m, resp));
        }

        var totalHits = 0;
        for (var step = 0; step < 150; step++)
        {
            var moves = rng.Next(1, 12);
            for (var k = 0; k < moves && p.Live.Count > 0; k++)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                var dx = R(-1.5f, 1.5f); var dy = R(-1, 1); var dz = R(-1.5f, 1.5f);
                var global = rng.Next(2) == 0;
                p.Each(s => s.TranslateEntity(id, dx, dy, dz, global));
            }
            var op = rng.Next(40);
            if (op == 0 && p.Live.Count > 8)
            {
                var id = p.Live[rng.Next(1, p.Live.Count)];
                var subtree = p.Legacy.GetSubtreeEntityIds(id);
                p.Each(s => s.FreeEntity(id));
                p.Live.RemoveAll(e => Array.IndexOf(subtree, e) >= 0);
            }
            else if (op == 1)
            {
                var parent = p.Live.Count > 3 && rng.Next(2) == 0 ? p.Live[rng.Next(p.Live.Count)] : 0;
                var id = p.Create(parent);
                var x = R(-9, 9); var z = R(-9, 9);
                p.Each(s => s.PositionEntity(id, x, 2, z));
                Configure(id);
            }
            else if (op == 2)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                p.Each(s => s.HideEntity(id));
            }
            else if (op == 3)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                p.Each(s => s.ShowEntity(id));
            }
            else if (op == 4)
            {
                Configure(p.Live[rng.Next(p.Live.Count)]);
            }
            else if (op == 5 && p.Live.Count > 4)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                var parent = rng.Next(3) == 0 ? 0 : p.Live[rng.Next(p.Live.Count)];
                var global = rng.Next(2) == 0;
                try { p.Legacy.EntityParent(id, parent, global); }
                catch (ArgumentException) { continue; }
                p.Fast.EntityParent(id, parent, global);
            }
            else if (op == 6 && p.Live.Count > 4)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                var parent = rng.Next(2) == 0 ? 0 : p.Live[rng.Next(p.Live.Count)];
                int a;
                try { a = p.Legacy.CopyEntity(id, parent); } catch (ArgumentException) { continue; }
                var b = p.Fast.CopyEntity(id, parent);
                Assert.Equal(a, b);
                foreach (var copied in p.Legacy.GetSubtreeEntityIds(a)) p.Live.Add(copied);
            }
            else if (op == 7)
            {
                var id = p.Live[rng.Next(p.Live.Count)];
                p.Each(s => s.ResetEntity(id));
            }
            else if (op == 8)
            {
                p.Each(s => s.Collisions(2, 3, 3, 1));
            }
            p.Each(s => s.UpdateWorld());
            totalHits += AssertSame(p, step);
        }
        Assert.True(totalHits > 100, "scenario must actually produce collisions");
    }
}

/// <summary>Broadphase and no-op-setter differential: spread, scaled and rotated scenes against the legacy path.</summary>
public sealed class SceneBroadphaseDifferentialTests
{
    private static SceneCollisionTriangle[] Slab(float size)
    {
        var t = new List<SceneCollisionTriangle>();
        for (var i = 0; i < 8; i++)
        {
            var x = i * size / 4;
            t.Add(new(new(x, 0, 0), new(x, 0, size), new(x + size / 4, 0, size), t.Count));
            t.Add(new(new(x, 0, 0), new(x + size / 4, 0, size), new(x + size / 4, 0, 0), t.Count));
            t.Add(new(new(x, 0, 0), new(x, 3, 0), new(x + size / 4, 3, 0), t.Count));
        }
        return t.ToArray();
    }

    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)] [InlineData(21)] [InlineData(22)]
    public void Spread_scaled_rotated_scenes_match_legacy(int seed)
    {
        var rng = new Random(seed);
        float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
        var legacy = new SceneGraph { UseLegacyCollisionPathForTesting = true };
        var fast = new SceneGraph();
        var live = new List<int>();
        void Each(Action<SceneGraph> a) { a(legacy); a(fast); }
        int Create(int parent)
        {
            var a = legacy.CreatePivot(parent); var b = fast.CreatePivot(parent);
            Assert.Equal(a, b); live.Add(a); return a;
        }
        void Configure(int id)
        {
            var radius = R(.3f, 1.5f); var radiusY = rng.Next(2) == 0 ? radius : radius * 2; var tri = Slab(R(3, 14));
            switch (rng.Next(5))
            {
                case 0: Each(s => { s.EntityType(id, 1); s.EntityRadius(id, radius, radiusY); }); break;
                case 1: Each(s => { s.EntityType(id, 2); s.EntityRadius(id, radius); }); break;
                case 2: { var w = R(.5f, 4); Each(s => { s.EntityType(id, 3); s.EntityBox(id, -w, -1, -w, 2 * w, 2, 2 * w); }); break; }
                case 3: Each(s => { s.EntityType(id, 4); s.SetCollisionTriangles(id, tri); }); break;
                default: Each(s => { s.EntityType(id, 5); s.EntityRadius(id, radius); }); break;
            }
        }
        for (var i = 0; i < 70; i++)
        {
            var id = Create(live.Count > 3 && rng.Next(4) == 0 ? live[rng.Next(live.Count)] : 0);
            float x = R(-60, 60), y = R(0, 10), z = R(-60, 60), sc = R(.5f, 3), rx = R(0, 360), ry = R(0, 360); var sy = sc * (rng.Next(3) == 0 ? 2 : 1);
            Each(s => { s.PositionEntity(id, x, y, z); s.ScaleEntity(id, sc, sy, sc); s.RotateEntity(id, rx, ry, 0); });
            Configure(id);
        }
        int[,] rules = { { 1, 2, 1, 1 }, { 1, 3, 3, 2 }, { 1, 4, 2, 2 }, { 1, 5, 1, 3 }, { 2, 4, 2, 1 }, { 2, 3, 3, 2 }, { 5, 4, 2, 3 }, { 5, 3, 3, 2 }, { 5, 5, 1, 2 } };
        for (var r = 0; r < rules.GetLength(0); r++)
        {
            int a = rules[r, 0], b = rules[r, 1], m = rules[r, 2], resp = rules[r, 3];
            Each(s => s.Collisions(a, b, m, resp));
        }
        var hits = 0;
        for (var step = 0; step < 120; step++)
        {
            for (var k = rng.Next(1, 14); k > 0; k--)
            {
                var id = live[rng.Next(live.Count)];
                float dx = R(-9, 9), dy = R(-2, 2), dz = R(-9, 9);
                var global = rng.Next(2) == 0;
                Each(s => s.TranslateEntity(id, dx, dy, dz, global));
                if (rng.Next(4) == 0) { var same = legacy.EntityX(id, false); Each(s => s.PositionEntity(id, same, legacy.EntityY(id, false), legacy.EntityZ(id, false), false)); }
            }
            if (step % 15 == 7 && live.Count > 20)
            {
                var id = live[rng.Next(1, live.Count)];
                var sub = legacy.GetSubtreeEntityIds(id);
                Each(s => s.FreeEntity(id)); live.RemoveAll(e => Array.IndexOf(sub, e) >= 0);
            }
            if (step % 11 == 3) Configure(live[rng.Next(live.Count)]);
            Each(s => s.UpdateWorld());
            foreach (var id in live)
            {
                foreach (var g in new[] { true, false })
                {
                    Assert.Equal(Bits(legacy.EntityX(id, g)), Bits(fast.EntityX(id, g)));
                    Assert.Equal(Bits(legacy.EntityY(id, g)), Bits(fast.EntityY(id, g)));
                    Assert.Equal(Bits(legacy.EntityZ(id, g)), Bits(fast.EntityZ(id, g)));
                }
                var n = legacy.CountCollisions(id);
                Assert.Equal(n, fast.CountCollisions(id));
                hits += n;
                for (var i = 1; i <= n; i++)
                {
                    Assert.Equal(legacy.CollisionEntity(id, i), fast.CollisionEntity(id, i));
                    Assert.Equal(legacy.CollisionTriangle(id, i), fast.CollisionTriangle(id, i));
                    Assert.Equal(Bits(legacy.CollisionX(id, i)), Bits(fast.CollisionX(id, i)));
                    Assert.Equal(Bits(legacy.CollisionY(id, i)), Bits(fast.CollisionY(id, i)));
                    Assert.Equal(Bits(legacy.CollisionZ(id, i)), Bits(fast.CollisionZ(id, i)));
                    Assert.Equal(Bits(legacy.CollisionNX(id, i)), Bits(fast.CollisionNX(id, i)));
                    Assert.Equal(Bits(legacy.CollisionNY(id, i)), Bits(fast.CollisionNY(id, i)));
                    Assert.Equal(Bits(legacy.CollisionNZ(id, i)), Bits(fast.CollisionNZ(id, i)));
                    Assert.Equal(Bits(legacy.CollisionTime(id, i)), Bits(fast.CollisionTime(id, i)));
                }
            }
        }
        Assert.True(hits > 20, "scenario must produce collisions");
    }
}
