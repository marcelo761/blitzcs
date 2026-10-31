using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class ObjectConstantSubmissionTests
{
    [Fact]
    public void RecordSizesMatchTheHlslConstantBuffers()
    {
        Assert.Equal(DrawRecordPlanner.TransformBytes, Marshal.SizeOf<D3D11Renderer.TransformConstants>());
        Assert.Equal(DrawRecordPlanner.MaterialBytes, Marshal.SizeOf<D3D11Renderer.MaterialConstants>());
        Assert.Equal(272, DrawRecordPlanner.InstanceRecordBytes);
        Assert.Equal(240, DrawRecordPlanner.MaxInstances);
        Assert.True(DrawRecordPlanner.MaxInstances * DrawRecordPlanner.InstanceRecordBytes <= ObjectConstantRing.MaxWindowBytes);
    }

    [Fact]
    public void AlignmentAndWindowArgumentsFollowTheD3D11_1Rules()
    {
        Assert.Equal(256, ObjectConstantRing.Align(1));
        Assert.Equal(256, ObjectConstantRing.Align(256));
        Assert.Equal(768, ObjectConstantRing.Align(DrawRecordPlanner.MaterialBytes));
        Assert.Equal(16u, ObjectConstantRing.ConstantCount(DrawRecordPlanner.TransformBytes));
        Assert.Equal(48u, ObjectConstantRing.ConstantCount(DrawRecordPlanner.MaterialBytes));
        Assert.Equal(4080u, ObjectConstantRing.ConstantCount(DrawRecordPlanner.MaxInstances * DrawRecordPlanner.InstanceRecordBytes));
        Assert.Equal(64u, ObjectConstantRing.FirstConstant(1024));
        foreach (var bytes in new[] { 1, 255, 257, 528, 272 * 3, 65280 })
        {
            Assert.Equal(0u, ObjectConstantRing.ConstantCount(bytes) % 16);
            Assert.True(ObjectConstantRing.ConstantCount(bytes) * 16 >= bytes);
        }
    }

    [Fact]
    public void RingAppendsWithNoOverwriteAndWrapsWithDiscard()
    {
        var ring = new ObjectConstantRing(4096, noOverwriteSupported: true);
        Assert.Equal(new RingReservation(0, true, 0), ring.Reserve(1000));      // first use always discards
        Assert.Equal(new RingReservation(1024, false, 0), ring.Reserve(300));   // 256-aligned append
        Assert.Equal(new RingReservation(1536, false, 0), ring.Reserve(2560));  // exactly fills the buffer
        Assert.Equal(4096, ring.Cursor);
        Assert.Equal(new RingReservation(0, true, 0), ring.Reserve(1));         // wrap: rename, start over
        Assert.Equal(new RingReservation(256, false, 0), ring.Reserve(1));
        ring.Reset();
        Assert.Equal(new RingReservation(0, true, 0), ring.Reserve(1));
    }

    [Fact]
    public void RingGrowsToAPowerOfTwoAndDiscardsWhenNoOverwriteIsUnsupported()
    {
        var ring = new ObjectConstantRing(4096, noOverwriteSupported: true);
        ring.Reserve(256);
        Assert.Equal(new RingReservation(0, true, 16384), ring.Reserve(9000));
        Assert.Equal(16384, ring.Capacity);
        Assert.Equal(new RingReservation(9216, false, 0), ring.Reserve(256));

        var discardOnly = new ObjectConstantRing(4096, noOverwriteSupported: false);
        for (var i = 0; i < 3; i++) Assert.Equal(new RingReservation(0, true, 0), discardOnly.Reserve(512));
    }

    [Fact]
    public void PlanPacksRecordsAt256ByteOffsetsWithoutOverlap()
    {
        var source = new FakeSource(5);
        var plan = new List<PlannedDraw>();
        var bytes = DrawRecordPlanner.Plan(ref source, instancing: false, plan);
        Assert.Equal(5, plan.Count);
        Assert.Equal(5 * (768 + 256), bytes);
        var ranges = new List<(int Start, int End)>();
        foreach (var draw in plan)
        {
            Assert.True(draw.WriteMaterial && draw.WriteTransform && !draw.Instanced);
            Assert.Equal(0, draw.MaterialOffset % 256);
            Assert.Equal(0, draw.TransformOffset % 256);
            ranges.Add((draw.MaterialOffset, draw.MaterialOffset + 768));
            ranges.Add((draw.TransformOffset, draw.TransformOffset + 256));
        }
        ranges.Sort();
        for (var i = 1; i < ranges.Count; i++) Assert.True(ranges[i - 1].End <= ranges[i].Start);
        Assert.True(ranges[^1].End <= bytes);
    }

    [Fact]
    public void PlanReusesRecordsEqualToThePreviouslyWrittenOne()
    {
        // Multi-surface mesh: one transform shared by three surfaces, then a material shared by the last three draws.
        var source = new FakeSource(4) { Transforms = [7, 7, 7, 8], Materials = [1, 2, 2, 2] };
        var plan = new List<PlannedDraw>();
        var bytes = DrawRecordPlanner.Plan(ref source, instancing: false, plan);
        Assert.Equal([true, false, false, true], plan.Select(d => d.WriteTransform));
        Assert.Equal([true, true, false, false], plan.Select(d => d.WriteMaterial));
        Assert.Equal(plan[0].TransformOffset, plan[2].TransformOffset);
        Assert.Equal(plan[1].MaterialOffset, plan[3].MaterialOffset);
        Assert.Equal(2 * 768 + 2 * 256, bytes);
    }

    [Fact]
    public void InstancingMergesOnlyConsecutiveCompatibleDrawsInOrder()
    {
        // Keys: equal key = same mesh/state/material-except-color. A A A B A A C C (blended, back-to-front order).
        var source = new FakeSource(8) { Keys = [1, 1, 1, 2, 1, 1, 3, 3] };
        var plan = new List<PlannedDraw>();
        DrawRecordPlanner.Plan(ref source, instancing: true, plan);
        Assert.Equal([(0, 3), (3, 1), (4, 2), (6, 2)], plan.Select(d => (d.First, d.Count)));
        AssertCoversInOrder(plan, 8);
        foreach (var draw in plan.Where(d => d.Instanced))
        {
            Assert.Equal(draw.Count * 272, draw.InstanceBytes);
            Assert.Equal(0, draw.InstanceOffset % 256);
            Assert.False(draw.WriteTransform);
        }

        DrawRecordPlanner.Plan(ref source, instancing: false, plan);
        Assert.Equal(8, plan.Count);
        AssertCoversInOrder(plan, 8);
    }

    [Fact]
    public void InstancedRunsAreSplitAtTheConstantWindowLimit()
    {
        var source = new FakeSource(500) { Keys = Enumerable.Repeat(1, 500).ToArray() };
        var plan = new List<PlannedDraw>();
        DrawRecordPlanner.Plan(ref source, instancing: true, plan);
        Assert.Equal([240, 240, 20], plan.Select(d => d.Count));
        AssertCoversInOrder(plan, 500);
        Assert.All(plan, d => Assert.True(ObjectConstantRing.ConstantCount(d.InstanceBytes) <= 4096));
    }

    [Fact]
    public void InstancingRequiresIdenticalStateAndAllowsOnlyMaterialColorToDiffer()
    {
        var mesh = FakeMesh();
        var otherMesh = FakeMesh();
        var state = new D3D11LightingDrawState(Effects: 1 | 8, Shininess: 0, Blend: 3, DepthMode: 1);
        var draw = new D3D11Renderer.QueuedMeshDraw(mesh, 6, 1, state, false);
        D3D11TextureLayer[] layers = [new D3D11TextureLayer(null, Blend: 2)];
        var red = Material(layers, new Vector4(1, 0, 0, 0.5f), state);
        var green = Material(layers, new Vector4(0, 1, 0, 0.25f), state);
        Assert.True(D3D11Renderer.CanShareInstancedDraw(draw, draw, layers, layers, red, green));

        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw with { Mesh = otherMesh }, layers, layers, red, red));
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw with { IndexCount = 3 }, layers, layers, red, red));
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw with { AlphaBlend = true }, layers, layers, red, red));
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw with { State = state with { Blend = 1 } }, layers, layers, red, red));
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw with { State = state with { DepthMode = 2 } }, layers, layers, red, red));
        D3D11TextureLayer[] clamped = [new D3D11TextureLayer(null, Blend: 2, ClampU: true)];
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw, layers, clamped, red, red));
        var fogged = Material(layers, new Vector4(1, 0, 0, 0.5f), state, new D3D11Fog(Vector3.One, 1, 10));
        Assert.False(D3D11Renderer.CanShareInstancedDraw(draw, draw, layers, layers, red, fogged));

        // Masked texture: the alpha-test threshold (RegionFlags.w) depends on the color alpha, so it must not merge.
        D3D11TextureLayer[] masked = [new D3D11TextureLayer(null, Blend: 2, Masked: true)];
        var maskedLit = new D3D11LightingDrawState(Effects: 0, Shininess: 0);
        var a = MaterialWithMaskedTexture(new Vector4(1, 1, 1, 1f), maskedLit);
        var b = MaterialWithMaskedTexture(new Vector4(1, 1, 1, 0.5f), maskedLit);
        Assert.NotEqual(a.RegionFlags.W, b.RegionFlags.W);
        var maskedDraw = draw with { State = maskedLit };
        Assert.False(D3D11Renderer.CanShareInstancedDraw(maskedDraw, maskedDraw, masked, masked, a, b));
    }

    /// <summary>
    /// Batched vs unbatched: what VSInstanced reads for each sprite (InstanceData) must be byte-for-byte what VSMain
    /// reads from cbuffer Transform plus MaterialColor from cbuffer Material for the same sprite drawn alone, across
    /// world matrices built like every SpriteViewMode with rotation, scale, handle and per-sprite color.
    /// </summary>
    [Fact]
    public void InstanceRecordsEqualTheUnbatchedPerDrawConstants()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(3, 2, -6), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 4f / 3f, 0.1f, 100f);
        var cameraWorld = Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(3, 2, -6);
        var state = new D3D11LightingDrawState(Effects: 1, Shininess: 0, Blend: 3, DepthMode: 1);
        D3D11TextureLayer[] layers = [new D3D11TextureLayer(null, Blend: 2)];
        var record = new byte[DrawRecordPlanner.InstanceRecordBytes];
        var random = new Random(12345);
        for (var viewMode = 1; viewMode <= 4; viewMode++)
        {
            for (var i = 0; i < 25; i++)
            {
                var orientation = viewMode switch
                {
                    1 => WithoutTranslation(cameraWorld),
                    2 => Matrix4x4.CreateFromYawPitchRoll(random.NextSingle() * 6, random.NextSingle(), random.NextSingle()),
                    3 => Matrix4x4.CreateRotationY(random.NextSingle() * 6),
                    _ => Matrix4x4.CreateRotationX(random.NextSingle()) * Matrix4x4.CreateRotationY(0.4f),
                };
                var world = Matrix4x4.CreateTranslation(-random.NextSingle(), -random.NextSingle(), 0) *
                    Matrix4x4.CreateScale(0.1f + random.NextSingle(), 0.1f + random.NextSingle(), 1) *
                    Matrix4x4.CreateRotationZ(random.NextSingle() * 360 * (MathF.PI / 180f)) *
                    orientation * Matrix4x4.CreateTranslation(random.NextSingle() * 10, random.NextSingle() * 3, random.NextSingle() * 10);
                var color = new Vector4(random.NextSingle(), random.NextSingle(), random.NextSingle(), random.NextSingle());

                D3D11Renderer.BuildTransformConstants(out var unbatchedTransform, world * view * projection, world * view, world, Matrix4x4.Identity);
                var unbatchedMaterial = Material(layers, color, state);
                D3D11Renderer.WriteInstanceRecord(record, unbatchedTransform, unbatchedMaterial.Color);

                Assert.True(record.AsSpan(0, 256).SequenceEqual(Bytes(unbatchedTransform)));
                Assert.True(record.AsSpan(256, 16).SequenceEqual(Bytes(unbatchedMaterial).Slice(0, 16)));
                Assert.Equal(color, unbatchedMaterial.Color);
            }
        }
    }

    [Fact]
    public void InstancedShaderIsVSMainWithOnlyTheConstantSourcesRenamed()
    {
        var baseSource = D3D11Renderer.BaseShaderSource;
        var instanced = D3D11Renderer.InstancedShaderSource;
        Assert.StartsWith(baseSource, instanced);
        var appended = instanced[baseSource.Length..];
        Assert.Contains("cbuffer InstanceBuffer : register(b3) { InstanceData Instances[240]; };", appended);
        Assert.Contains("PixelInput VSInstanced(VertexInput input, uint instanceId : SV_InstanceID)", appended);

        // Undo the renames: the derived functions must be the original text again.
        var lighting = Slice(appended, "void EvaluateInstanceLighting(", "PixelInput VSInstanced(")
            .Replace("void EvaluateInstanceLighting(VertexInput input, InstanceData instance,", "void EvaluateLighting(VertexInput input,")
            .Replace("instance.", "");
        Assert.Equal(Slice(baseSource, "void EvaluateLighting(", "PixelInput VSMain("), lighting);
        var vertex = appended[appended.IndexOf("PixelInput VSInstanced(", StringComparison.Ordinal)..]
            .Replace("PixelInput VSInstanced(VertexInput input, uint instanceId : SV_InstanceID)", "PixelInput VSMain(VertexInput input)")
            .Replace("InstanceData instance = Instances[instanceId];\n    ", "")
            .Replace("EvaluateInstanceLighting(input, instance,", "EvaluateLighting(input,")
            .Replace("instance.", "");
        Assert.Equal(Slice(baseSource, "PixelInput VSMain(", "PixelInput VSBlit("), vertex);
    }

    [Fact]
    public void InstancedShaderDerivationFailsLoudlyWhenTheShaderTextChanges()
    {
        var changed = D3D11Renderer.BaseShaderSource.Replace("* MaterialColor;", "* MaterialColor * 1.0;");
        Assert.Throws<InvalidOperationException>(() => D3D11Renderer.BuildInstancedShaderSource(changed));
    }

    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }

    private static void AssertCoversInOrder(List<PlannedDraw> plan, int count)
    {
        var next = 0;
        foreach (var draw in plan) { Assert.Equal(next, draw.First); next += draw.Count; }
        Assert.Equal(count, next);
    }

    private static D3D11Mesh FakeMesh() => new(null!, null!, null!, 6, 4);

    private static D3D11Renderer.MaterialConstants Material(ReadOnlySpan<D3D11TextureLayer> layers, Vector4 color,
        D3D11LightingDrawState state, D3D11Fog? fog = null)
    {
        var material = new D3D11Renderer.MaterialConstants();
        D3D11Renderer.BuildMaterialConstants(ref material, layers, color, false, fog, state.Effects, state.Shininess);
        return material;
    }

    private static D3D11Renderer.MaterialConstants MaterialWithMaskedTexture(Vector4 color, D3D11LightingDrawState state)
    {
        // BuildMaterialConstants only sets the alpha-test fields when stage 0 has a texture; emulate that record.
        var material = Material([new D3D11TextureLayer(null, Blend: 2, Masked: true)], color, state);
        material.RegionFlags.Z = 1;
        material.RegionFlags.W = MathF.Truncate(128 * color.W) / 255f;
        return material;
    }

    private static Matrix4x4 WithoutTranslation(Matrix4x4 m) { m.M41 = m.M42 = m.M43 = 0; return m; }

    private static ReadOnlySpan<byte> Bytes<T>(in T value) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1)).ToArray();

    /// <summary>Fake queue: integer keys stand for record bytes (equal key = byte-identical record).</summary>
    private struct FakeSource(int count) : IDrawRecordSource
    {
        public int[]? Transforms, Materials, Keys;
        public readonly int Count => count;
        public readonly bool SameTransform(int a, int b) => Transforms is { } t ? t[a] == t[b] : a == b;
        public readonly bool SameMaterial(int a, int b) => Materials is { } m ? m[a] == m[b] : a == b;
        public readonly bool CanInstance(int first, int candidate) => Keys is { } k && k[first] == k[candidate];
    }
}
