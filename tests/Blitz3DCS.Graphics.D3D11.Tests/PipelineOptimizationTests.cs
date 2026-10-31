using System.Numerics;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class PipelineOptimizationTests
{
    [Fact]
    public void TrackerSkipsRepeatedBindingsAndRebindsAfterInvalidate()
    {
        var tracker = new PipelineStateTracker();
        var shader = new object();
        Assert.True(tracker.Changed(PipelineSlot.PixelShader, shader));
        Assert.False(tracker.Changed(PipelineSlot.PixelShader, shader));
        Assert.True(tracker.Changed(PipelineSlot.PixelShader, new object()));
        Assert.True(tracker.Changed(PipelineSlot.Blend, null));
        Assert.False(tracker.Changed(PipelineSlot.Blend, null));
        Assert.True(tracker.Changed(PipelineSlot.Blend, shader));
        tracker.Invalidate();
        Assert.True(tracker.Changed(PipelineSlot.Blend, shader));
        Assert.True(tracker.Changed(PipelineSlot.Blend, null));
        Assert.Equal(2, tracker.Skipped);
    }

    [Fact]
    public void ViewportOnlyReportsChanges()
    {
        var tracker = new PipelineStateTracker();
        Assert.True(tracker.ViewportChanged(0, 0, 640, 480, 0, 1));
        Assert.False(tracker.ViewportChanged(0, 0, 640, 480, 0, 1));
        Assert.True(tracker.ViewportChanged(0, 0, 320, 480, 0, 1));
        tracker.Invalidate();
        Assert.True(tracker.ViewportChanged(0, 0, 320, 480, 0, 1));
    }

    [Fact]
    public void ResourceRangesCoverExactlyTheChangedSlots()
    {
        var tracker = new PipelineStateTracker();
        var a = new object();
        var b = new object();
        var desired = new object?[8];
        Assert.True(tracker.DirtyShaderResources(desired, out var start, out var length));
        Assert.Equal((0, 8), (start, length));
        Assert.False(tracker.DirtyShaderResources(desired, out _, out _));
        desired[2] = a;
        Assert.True(tracker.DirtyShaderResources(desired, out start, out length));
        Assert.Equal((2, 1), (start, length));
        desired[2] = null;
        desired[5] = b;
        Assert.True(tracker.DirtyShaderResources(desired, out start, out length));
        Assert.Equal((2, 4), (start, length));
        Assert.False(tracker.DirtyShaderResources(desired, out _, out _));
        Assert.True(tracker.DirtySamplers(desired, out start, out length));
        Assert.Equal((0, 8), (start, length));
        tracker.Invalidate();
        Assert.True(tracker.DirtyShaderResources(desired, out start, out length));
        Assert.Equal((0, 8), (start, length));
    }

    [Fact]
    public void ShadowStateMatchesFullRebindOverRandomSequences()
    {
        // Replaying only the dirty ranges must leave the same "device" contents as binding all eight slots every draw.
        var random = new Random(1234);
        var pool = new object?[] { null, new object(), new object(), new object() };
        var tracker = new PipelineStateTracker();
        var device = new object?[8];
        var reference = new object?[8];
        for (var draw = 0; draw < 2000; draw++)
        {
            if (random.Next(25) == 0) { tracker.Invalidate(); Array.Clear(device); }
            var desired = new object?[8];
            for (var i = 0; i < 8; i++) desired[i] = pool[random.Next(pool.Length)];
            if (tracker.DirtyShaderResources(desired, out var start, out var length))
                for (var i = start; i < start + length; i++) device[i] = desired[i];
            Array.Copy(desired, reference, 8);
            Assert.Equal(reference, device);
        }
    }

    private static BlitBatchKey Key(object source, float tint = 1, bool alpha = true) =>
        new(source, new Vector4(tint), alpha, 640, 480);

    private static ClippedTextureRegion Quad(int n) => new(n, 0, 8, 8, n * 2, 1, 8, 8);

    [Fact]
    public void BatcherPreservesOrderAndStateAgainstUnbatchedReference()
    {
        var random = new Random(7);
        var textures = new[] { new object(), new object() };
        var batcher = new BlitBatcher();
        var drawn = new List<(BlitBatchKey Key, ClippedTextureRegion[] Quads)>();
        void Flush(in BlitBatchKey key, ReadOnlySpan<ClippedTextureRegion> quads) => drawn.Add((key, quads.ToArray()));
        var reference = new List<(BlitBatchKey Key, ClippedTextureRegion Quad)>();
        for (var i = 0; i < 1500; i++)
        {
            // Long same-state runs exercise the 128-quad cap; random switches exercise key changes.
            var key = i % 400 < 300 ? Key(textures[0]) : Key(textures[random.Next(2)], random.Next(2), random.Next(2) == 0);
            reference.Add((key, Quad(i)));
            batcher.Add(key, Quad(i), Flush);
        }
        batcher.Flush(Flush);

        Assert.Equal(0, batcher.Pending);
        Assert.Equal(reference.Select(r => r.Quad), drawn.SelectMany(d => d.Quads));
        Assert.Equal(reference.Select(r => r.Key), drawn.SelectMany(d => d.Quads.Select(_ => d.Key)));
        Assert.All(drawn, d => Assert.InRange(d.Quads.Length, 1, 128));
        for (var i = 1; i < drawn.Count; i++)
            Assert.True(drawn[i - 1].Quads.Length == 128 || !drawn[i - 1].Key.Equals(drawn[i].Key),
                "Adjacent draws with identical state must only be split by the 128-quad cap.");
        Assert.True(drawn.Count < reference.Count);
    }

    [Fact]
    public void BatcherFlushesAndDiscards()
    {
        var batcher = new BlitBatcher();
        var calls = 0;
        void Flush(in BlitBatchKey key, ReadOnlySpan<ClippedTextureRegion> quads) => calls++;
        batcher.Flush(Flush);
        Assert.Equal(0, calls);
        batcher.Add(Key(new object()), Quad(1), Flush);
        batcher.Discard();
        batcher.Flush(Flush);
        Assert.Equal(0, calls);
    }

    private static int _zeroAllocFlushes;
    private static void CountFlush(in BlitBatchKey key, ReadOnlySpan<ClippedTextureRegion> quads) => _zeroAllocFlushes += quads.Length;

    [Fact]
    public void BatcherAndTrackerAllocateNothingInSteadyState()
    {
        var batcher = new BlitBatcher();
        var tracker = new PipelineStateTracker();
        BlitBatcher.FlushHandler flush = CountFlush;
        var texture = new object();
        var other = new object();
        var desired = new object?[PipelineStateTracker.ResourceSlots];
        void Run()
        {
            for (var i = 0; i < 300; i++)
            {
                var key = Key(i % 50 == 0 ? other : texture);
                batcher.Add(key, Quad(i), flush);
                tracker.Changed(PipelineSlot.PixelShader, i % 2 == 0 ? texture : other);
                tracker.ViewportChanged(0, 0, 640 + i % 2, 480, 0, 1);
                desired[i % 8] = i % 3 == 0 ? texture : other;
                tracker.DirtyShaderResources(desired, out _, out _);
                tracker.DirtySamplers(desired, out _, out _);
            }
            batcher.Flush(flush);
        }
        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(_zeroAllocFlushes > 0);
    }
}
