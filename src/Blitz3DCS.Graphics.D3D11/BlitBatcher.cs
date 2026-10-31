using System.Numerics;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Clipped source rectangle and destination rectangle (pixels) for one blit quad.</summary>
internal readonly record struct ClippedTextureRegion(
    int SourceX, int SourceY, int SourceWidth, int SourceHeight,
    float DestinationX, float DestinationY, float DestinationWidth, float DestinationHeight);

/// <summary>Render state shared by every quad of one batched draw.</summary>
internal readonly struct BlitBatchKey(object source, Vector4 tint, bool alphaBlend, int targetWidth, int targetHeight)
    : IEquatable<BlitBatchKey>
{
    public readonly object Source = source;
    public readonly Vector4 Tint = tint;
    public readonly bool AlphaBlend = alphaBlend;
    public readonly int TargetWidth = targetWidth, TargetHeight = targetHeight;

    public bool Equals(BlitBatchKey o) => ReferenceEquals(Source, o.Source) && Tint == o.Tint &&
        AlphaBlend == o.AlphaBlend && TargetWidth == o.TargetWidth && TargetHeight == o.TargetHeight;
    public override bool Equals(object? obj) => obj is BlitBatchKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(Source, Tint, AlphaBlend, TargetWidth, TargetHeight);
}

/// <summary>
/// Accumulates consecutive blit quads with identical state so they draw together. Order is preserved: a quad only joins
/// the pending draw when its key equals the pending key; any other key (or a full batch) flushes first.
/// </summary>
internal sealed class BlitBatcher(int maxQuads = 128)
{
    public delegate void FlushHandler(in BlitBatchKey key, ReadOnlySpan<ClippedTextureRegion> quads);

    private readonly List<ClippedTextureRegion> _quads = new(maxQuads);
    private BlitBatchKey _key;

    public int Pending => _quads.Count;

    public void Add(in BlitBatchKey key, in ClippedTextureRegion quad, FlushHandler flush)
    {
        if (_quads.Count > 0 && !_key.Equals(key)) Flush(flush);
        _key = key;
        _quads.Add(quad);
        if (_quads.Count >= maxQuads) Flush(flush);
    }

    public void Flush(FlushHandler flush)
    {
        if (_quads.Count == 0) return;
        try { flush(_key, CollectionsMarshal.AsSpan(_quads)); }
        finally { _quads.Clear(); }
    }

    public void Discard() => _quads.Clear();
}
