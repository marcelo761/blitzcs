using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

/// <summary>
/// Native scene for comparing the instanced and queued D3D11 submission paths. Assertions are structural and
/// renderer-independent; the log carries a full back-buffer hash for byte-for-byte A/B comparison between runs
/// with and without BLITZCS_D3D11_NOINSTANCING / BLITZCS_D3D11_NOOPT.
/// </summary>
public sealed class LegacyGraphicsInstancingSmokeTests
{
    private const int Width = 192;
    private const int Height = 144;

    /// <summary>Report lines (hash=, pass.*, regions.*) produced by the last successful smoke run.</summary>
    internal static string LastReport { get; private set; } = "";

    [WindowsInstancingSmokeFact]
    public void Instanced_and_queued_submission_render_structurally_correct_pixels() => RunSmoke();

    internal readonly record struct SpriteSpec(float X, float Y, float Z, int ViewMode, float Scale, float Rotation,
        int R, int G, int B, float Alpha);

    /// <summary>Deterministic 50 sprite layout: four runs of six same-view-mode sprites, then 26 interleaved ones.</summary>
    internal static SpriteSpec[] BuildSpriteScene()
    {
        var specs = new SpriteSpec[50];
        uint state = 12345;
        float Next()
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return (state >> 8) / (float)(1 << 24);
        }
        for (var i = 0; i < specs.Length; i++)
        {
            var view = i < 24 ? i / 6 + 1 : i % 4 + 1;
            specs[i] = new SpriteSpec(-3f + 6f * Next(), -2f + 4f * Next(), i * .001f, view,
                .3f + .9f * Next(), 360f * Next(), 40 + (int)(Next() * 215), 40 + (int)(Next() * 215),
                40 + (int)(Next() * 215), .25f + .75f * Next());
        }
        return specs;
    }

    /// <summary>One additive layer over black: min(255, count * channel).</summary>
    internal static int AdditiveExpected(int channel, int count) => Math.Min(255, channel * count);

    /// <summary>Composites (r,g,b,alpha) layers given back-to-front over a base colour with 8-bit rounding per step.</summary>
    internal static (int R, int G, int B) CompositeOver((int R, int G, int B) background,
        IEnumerable<(int R, int G, int B, float A)> backToFront)
    {
        var (r, g, b) = background;
        foreach (var layer in backToFront)
        {
            r = (int)MathF.Round(layer.R * layer.A + r * (1 - layer.A));
            g = (int)MathF.Round(layer.G * layer.A + g * (1 - layer.A));
            b = (int)MathF.Round(layer.B * layer.A + b * (1 - layer.A));
        }
        return (r, g, b);
    }

    internal static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>FNV-1a 32-bit checksums of a gridX by gridY partition of an RGBA image, as a comma separated hex list.</summary>
    internal static string RegionChecksums(byte[] rgba, int width, int height, int gridX, int gridY)
    {
        var parts = new List<string>(gridX * gridY);
        for (var gy = 0; gy < gridY; gy++)
            for (var gx = 0; gx < gridX; gx++)
            {
                var hash = 2166136261u;
                for (var y = gy * height / gridY; y < (gy + 1) * height / gridY; y++)
                    for (var x = gx * width / gridX; x < (gx + 1) * width / gridX; x++)
                        for (var c = 0; c < 4; c++)
                            hash = unchecked((hash ^ rgba[(y * width + x) * 4 + c]) * 16777619u);
                parts.Add(hash.ToString("x8", CultureInfo.InvariantCulture));
            }
        return string.Join(',', parts);
    }

    /// <summary>Counts pixels whose RGB differs from black.</summary>
    internal static int CountLit(byte[] rgba)
    {
        var count = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) count++;
        return count;
    }

    /// <summary>Counts non-black pixels whose horizontal mirror differs by more than the tolerance in any RGB channel.</summary>
    internal static (int Lit, int Mismatched) MirrorMismatch(byte[] rgba, int width, int height, int tolerance)
    {
        int lit = 0, bad = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var a = (y * width + x) * 4;
                var b = (y * width + (width - 1 - x)) * 4;
                if (rgba[a] != 0 || rgba[a + 1] != 0 || rgba[a + 2] != 0) lit++;
                if (Math.Abs(rgba[a] - rgba[b]) > tolerance || Math.Abs(rgba[a + 1] - rgba[b + 1]) > tolerance ||
                    Math.Abs(rgba[a + 2] - rgba[b + 2]) > tolerance) bad++;
            }
        return (lit, bad);
    }

    [Fact]
    public void Sprite_scene_is_deterministic_covers_all_view_modes_with_runs_and_interleaving()
    {
        var first = BuildSpriteScene();
        Assert.Equal(first, BuildSpriteScene());
        Assert.Equal(50, first.Length);
        Assert.Equal([1, 2, 3, 4], first.Select(s => s.ViewMode).Distinct().Order());
        Assert.All(first[..6], s => Assert.Equal(1, s.ViewMode));
        Assert.All(first[6..12], s => Assert.Equal(2, s.ViewMode));
        Assert.Equal([1, 2, 3, 4, 1, 2], first[24..30].Select(s => s.ViewMode));
        Assert.All(first, s =>
        {
            Assert.InRange(s.Alpha, .25f, 1f);
            Assert.InRange(s.R, 40, 255);
            Assert.InRange(s.Scale, .3f, 1.2f);
        });
    }

    [Fact]
    public void Additive_expectation_saturates_per_channel()
    {
        Assert.Equal(60, AdditiveExpected(60, 1));
        Assert.Equal(180, AdditiveExpected(60, 3));
        Assert.Equal(255, AdditiveExpected(100, 3));
    }

    [Fact]
    public void Alpha_composite_depends_on_order_and_matches_hand_computation()
    {
        var near = (255, 0, 0, .5f);
        var far = (0, 255, 0, .5f);
        Assert.Equal((128, 64, 0), CompositeOver((0, 0, 0), [far, near]));
        Assert.Equal((64, 128, 0), CompositeOver((0, 0, 0), [near, far]));
        Assert.Equal((10, 20, 30), CompositeOver((10, 20, 30), []));
        Assert.Equal((200, 100, 50), CompositeOver((0, 0, 0), [(200, 100, 50, 1f)]));
    }

    [Fact]
    public void Checksums_are_stable_and_sensitive_to_single_byte_changes()
    {
        var image = new byte[8 * 4 * 4];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)(i * 7);
        Assert.Equal(64, Sha256Hex(image).Length);
        Assert.Equal(Sha256Hex(image), Sha256Hex((byte[])image.Clone()));
        var regions = RegionChecksums(image, 8, 4, 2, 2);
        Assert.Equal(4, regions.Split(',').Length);
        var changed = (byte[])image.Clone();
        changed[0] ^= 1;
        var changedRegions = RegionChecksums(changed, 8, 4, 2, 2).Split(',');
        var original = regions.Split(',');
        Assert.NotEqual(original[0], changedRegions[0]);
        Assert.Equal(original[1..], changedRegions[1..]);
        Assert.NotEqual(Sha256Hex(image), Sha256Hex(changed));
    }

    [Fact]
    public void Mirror_mismatch_and_lit_counts_detect_asymmetry()
    {
        var image = new byte[4 * 2 * 4];
        image[0] = 100;
        image[(3) * 4] = 100;
        Assert.Equal((2, 0), MirrorMismatch(image, 4, 2, 0));
        image[(3) * 4] = 90;
        Assert.Equal((2, 2), MirrorMismatch(image, 4, 2, 0));
        Assert.Equal((2, 0), MirrorMismatch(image, 4, 2, 10));
        Assert.Equal(2, CountLit(image));
    }

    private static readonly (int R, int G, int B)[] Palette =
        [(255, 60, 60), (60, 255, 60), (60, 60, 255), (255, 200, 40), (40, 220, 220)];

    internal static void RunSmoke()
    {
        var previous = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        var entities = new List<int>();
        var report = new StringBuilder();
        var allHashes = new StringBuilder();
        try
        {
            LegacyGraphics.Graphics3D(Width, Height);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.PositionEntity(camera, 0, 0, -10);
            LegacyGraphics.CameraRange(camera, .1f, 100);
            LegacyGraphics.CameraClsColor(camera, 0, 0, 0);
            var backBuffer = LegacyGraphics.BackBuffer();

            var white = MakeTexture(backBuffer);
            var altTexture = MakeTexture(backBuffer);

            Sprites(entities, white, report, allHashes);
            Clear(entities);
            AdditiveCounts(entities, white);
            Clear(entities);
            MirrorPairs(entities, white);
            Clear(entities);
            LitMeshes(entities, camera, altTexture, report, allHashes);
            Clear(entities);
            BlendedRun(entities, camera, report, allHashes);

            LastReport = $"\nhash={Sha256Hex(Encoding.ASCII.GetBytes(allHashes.ToString()))}\n{report}";
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previous;
        }
    }

    private static int MakeTexture(int backBuffer)
    {
        var texture = LegacyGraphics.CreateTexture(4, 4, flags: 1);
        LegacyGraphics.SetBuffer(LegacyGraphics.TextureBuffer(texture));
        LegacyGraphics.LockBuffer();
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                LegacyGraphics.WritePixelFast(x, y, unchecked((int)0xFFFFFFFF));
        LegacyGraphics.UnlockBuffer();
        LegacyGraphics.SetBuffer(backBuffer);
        return texture;
    }

    private static void Clear(List<int> entities)
    {
        foreach (var entity in entities) LegacyGraphics.FreeEntity(entity);
        entities.Clear();
    }

    private static int NewSprite(List<int> entities, int texture, float x, float y, float z, int view, float scale,
        float rotation, int r, int g, int b, float alpha)
    {
        var sprite = LegacyGraphics.CreateSprite();
        entities.Add(sprite);
        LegacyScene.PositionEntity(sprite, x, y, z);
        LegacyGraphics.SpriteViewMode(sprite, view);
        LegacyGraphics.ScaleSprite(sprite, scale, scale);
        LegacyGraphics.RotateSprite(sprite, rotation);
        LegacyGraphics.EntityTexture(sprite, texture);
        LegacyGraphics.EntityFX(sprite, 1);
        LegacyGraphics.EntityBlend(sprite, 3);
        LegacyGraphics.EntityColor(sprite, r, g, b);
        LegacyGraphics.EntityAlpha(sprite, alpha);
        return sprite;
    }

    private static byte[] Render(string name, StringBuilder? report, StringBuilder? hashes)
    {
        LegacyGraphics.RenderWorld();
        var pixels = LegacyGraphics.ReadBackbufferPixelsForTesting();
        Assert.Equal(Width * Height * 4, pixels.Length);
        if (report is not null && hashes is not null)
        {
            var sha = Sha256Hex(pixels);
            hashes.Append(name).Append('=').Append(sha).Append(';');
            report.Append("pass.").Append(name).Append('=').Append(sha).Append('\n');
            report.Append("regions.").Append(name).Append('=').Append(RegionChecksums(pixels, Width, Height, 4, 4)).Append('\n');
        }
        return pixels;
    }

    private static void AssertCornersBlack(byte[] pixels, string pass)
    {
        foreach (var (x, y) in new[] { (2, 2), (Width - 3, 2), (2, Height - 3), (Width - 3, Height - 3) })
        {
            var o = (y * Width + x) * 4;
            Assert.True(pixels[o] == 0 && pixels[o + 1] == 0 && pixels[o + 2] == 0, $"{pass}: corner ({x},{y}) must stay at the clear colour.");
        }
    }

    private static (int R, int G, int B) Pixel(byte[] pixels, int x, int y)
    {
        var o = (y * Width + x) * 4;
        return (pixels[o], pixels[o + 1], pixels[o + 2]);
    }

    private static void AssertNear((int R, int G, int B) actual, (int R, int G, int B) expected, int tolerance, string what)
    {
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance &&
                    Math.Abs(actual.B - expected.B) <= tolerance, $"{what}: expected {expected} +-{tolerance}, got {actual}.");
    }

    private static void Sprites(List<int> entities, int texture, StringBuilder report, StringBuilder hashes)
    {
        foreach (var s in BuildSpriteScene())
            NewSprite(entities, texture, s.X, s.Y, s.Z, s.ViewMode, s.Scale, s.Rotation, s.R, s.G, s.B, s.Alpha);
        var pixels = Render("sprites", report, hashes);
        AssertCornersBlack(pixels, "sprites");
        Assert.True(CountLit(pixels) > 400, "The 50 additive sprites must cover a visible area.");
    }

    private static void AdditiveCounts(List<int> entities, int texture)
    {
        var colour = (R: 60, G: 100, B: 40);
        for (var count = 1; count <= 3; count++)
        {
            NewSprite(entities, texture, 0, 0, 0, 1, 1, 0, colour.R, colour.G, colour.B, 1);
            var pixels = Render("additive" + count, null, null);
            AssertNear(Pixel(pixels, Width / 2, Height / 2),
                (AdditiveExpected(colour.R, count), AdditiveExpected(colour.G, count), AdditiveExpected(colour.B, count)),
                count + 2, $"additive overlap of {count} sprites");
        }
    }

    private static void MirrorPairs(List<int> entities, int texture)
    {
        foreach (var rotation in new[] { 0f, 23f })
        {
            Clear(entities);
            NewSprite(entities, texture, -2.07f, .31f, 0, 1, .8f, rotation, 200, 90, 40, 1);
            NewSprite(entities, texture, 2.07f, .31f, 0, 1, .8f, -rotation, 200, 90, 40, 1);
            var pixels = Render("mirror" + rotation, null, null);
            var (lit, bad) = MirrorMismatch(pixels, Width, Height, 2);
            Assert.True(lit > 200, "Mirrored sprite pair must cover pixels.");
            Assert.True(bad * 100 <= lit, $"Mirrored sprite pair (rotation {rotation}) must produce mirrored pixels; {bad} of {lit} differ.");
        }
    }

    private static void LitMeshes(List<int> entities, int camera, int altTexture, StringBuilder report, StringBuilder hashes)
    {
        LegacyGraphics.AmbientLight(100, 100, 100);
        var light = LegacyGraphics.CreateLight(1);
        entities.Add(light);
        LegacyGraphics.LightColor(light, 255, 255, 255);
        var template = LegacyGraphics.CreateCube();
        entities.Add(template);
        LegacyScene.PositionEntity(template, 0, 0, -500);
        var breaker = LegacyGraphics.CreateCube();
        entities.Add(breaker);
        LegacyScene.PositionEntity(breaker, 0, 0, -500);
        LegacyGraphics.EntityTexture(breaker, altTexture);
        var cubes = new List<(int Entity, int Index)>();
        for (var i = 0; i < 15; i++)
        {
            var isBreaker = i is 3 or 7 or 11;
            var cube = LegacyGraphics.CopyEntity(isBreaker ? breaker : template);
            entities.Add(cube);
            LegacyScene.PositionEntity(cube, (i % 5 - 2) * 1.8f, (1 - i / 5) * 1.6f, 0);
            LegacyScene.ScaleEntity(cube, .45f, .45f, .45f);
            LegacyScene.RotateEntity(cube, i * 2f, i * 3f, 0);
            var c = Palette[i % Palette.Length];
            LegacyGraphics.EntityColor(cube, c.R, c.G, c.B);
            LegacyGraphics.EntityAlpha(cube, 1);
            cubes.Add((cube, i));
        }
        var pixels = Render("lit", report, hashes);
        AssertCornersBlack(pixels, "lit");
        foreach (var (cube, i) in cubes)
        {
            LegacyGraphics.CameraProject(camera, LegacyScene.EntityX(cube, 1), LegacyScene.EntityY(cube, 1), LegacyScene.EntityZ(cube, 1));
            var pixel = Pixel(pixels, (int)LegacyGraphics.ProjectedX(), (int)LegacyGraphics.ProjectedY());
            var c = Palette[i % Palette.Length];
            Assert.True(pixel.R + pixel.G + pixel.B > 30, $"Lit cube {i} must be visible at its centre.");
            Assert.Equal(Dominant(c.R, c.G, c.B), Dominant(pixel.R, pixel.G, pixel.B));
        }
    }

    private static int Dominant(int r, int g, int b) => r >= g && r >= b ? 0 : g >= b ? 1 : 2;

    private static void BlendedRun(List<int> entities, int camera, StringBuilder report, StringBuilder hashes)
    {
        LegacyGraphics.AmbientLight(0, 0, 0);
        var template = LegacyGraphics.CreateCube();
        entities.Add(template);
        LegacyScene.PositionEntity(template, 0, 0, -500);
        var layers = new List<(float Z, (int R, int G, int B, float A) Layer)>();
        var order = new[] { 0f, 2f, -2f, 1f, -1f };
        var alphas = new[] { .5f, .4f, .6f, .3f, .5f };
        for (var i = 0; i < order.Length; i++)
        {
            var cube = LegacyGraphics.CopyEntity(template);
            entities.Add(cube);
            LegacyScene.PositionEntity(cube, 0, 0, order[i]);
            LegacyScene.ScaleEntity(cube, .5f, .5f, .5f);
            var c = Palette[i];
            LegacyGraphics.EntityFX(cube, 1);
            LegacyGraphics.EntityColor(cube, c.R, c.G, c.B);
            LegacyGraphics.EntityAlpha(cube, alphas[i]);
            layers.Add((order[i], (c.R, c.G, c.B, alphas[i])));
        }
        var pixels = Render("blended", report, hashes);
        AssertCornersBlack(pixels, "blended");
        var backToFront = layers.OrderByDescending(l => l.Z).Select(l => l.Layer).ToArray();
        var correct = CompositeOver((0, 0, 0), backToFront);
        var wrong = CompositeOver((0, 0, 0), backToFront.Reverse());
        Assert.True(Math.Abs(correct.R - wrong.R) + Math.Abs(correct.G - wrong.G) + Math.Abs(correct.B - wrong.B) > 24,
            "The scene must discriminate back-to-front from front-to-back composition.");
        var centre = Pixel(pixels, Width / 2, Height / 2);
        AssertNear(centre, correct, 5, "blended run centre (far to near composition)");
    }

    private sealed class WindowsInstancingSmokeFactAttribute : FactAttribute
    {
        public WindowsInstancingSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real D3D11 instancing smoke test.";
        }
    }
}
