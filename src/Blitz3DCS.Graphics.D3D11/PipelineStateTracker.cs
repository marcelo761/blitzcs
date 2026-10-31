namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Pipeline binding points whose last-bound object is shadowed to skip redundant device calls.</summary>
public enum PipelineSlot
{
    InputLayout, VertexBuffer, IndexBuffer, Topology, VertexShader, PixelShader,
    VsConstantBuffer0, VsConstantBuffer1, VsConstantBuffer2, PsConstantBuffer1, PsConstantBuffer2,
    Blend, DepthStencil, Rasterizer, RenderTarget, DepthView, VsConstantBuffer3, Count,
}

/// <summary>
/// Pure shadow of the device binding state. Each query returns true when the device call must be issued and records
/// the new value; <see cref="Invalidate"/> must follow anything that resets the device (ClearState, resource disposal).
////// </summary>
public sealed class PipelineStateTracker
{
    public const int ResourceSlots = 8;
    private static readonly object Unset = new();
    private readonly object?[] _slots = NewArray((int)PipelineSlot.Count);
    private readonly object?[] _shaderResources = NewArray(ResourceSlots);
    private readonly object?[] _samplers = NewArray(ResourceSlots);
    private (float, float, float, float, float, float)? _viewport;

    /// <summary>Number of device calls reported as redundant since construction.</summary>
    public long Skipped { get; private set; }

    private static object?[] NewArray(int length) { var a = new object?[length]; Array.Fill(a, Unset); return a; }

    public void Invalidate()
    {
        Array.Fill(_slots, Unset);
        Array.Fill(_shaderResources, Unset);
        Array.Fill(_samplers, Unset);
        _viewport = null;
    }

    /// <summary>True when <paramref name="value"/> differs from the shadow (or tracking is off); null is a valid value.</summary>
    public bool Changed(PipelineSlot slot, object? value)
    {
        ref var current = ref _slots[(int)slot];
        if (ReferenceEquals(current, value)) { Skipped++; return false; }
        current = value;
        return true;
    }

    public bool ViewportChanged(float x, float y, float width, float height, float minDepth, float maxDepth)
    {
        var next = (x, y, width, height, minDepth, maxDepth);
        if (_viewport == next) { Skipped++; return false; }
        _viewport = next;
        return true;
    }

    /// <summary>Smallest [start, start+length) range of pixel-shader SRV slots that differs from the shadow.</summary>
    public bool DirtyShaderResources(ReadOnlySpan<object?> desired, out int start, out int length) =>
        Dirty(_shaderResources, desired, out start, out length);

    /// <summary>Smallest [start, start+length) range of pixel-shader sampler slots that differs from the shadow.</summary>
    public bool DirtySamplers(ReadOnlySpan<object?> desired, out int start, out int length) =>
        Dirty(_samplers, desired, out start, out length);

    private bool Dirty(object?[] shadow, ReadOnlySpan<object?> desired, out int start, out int length)
    {
        if (desired.Length != ResourceSlots) throw new ArgumentException("Exactly eight slots are required.", nameof(desired));
        int first = -1, last = -1;
        for (var i = 0; i < ResourceSlots; i++)
            if (!ReferenceEquals(shadow[i], desired[i])) { if (first < 0) first = i; last = i; }
        if (first < 0) { start = length = 0; Skipped++; return false; }
        start = first; length = last - first + 1;
        for (var i = first; i <= last; i++) shadow[i] = desired[i];
        return true;
    }
}
