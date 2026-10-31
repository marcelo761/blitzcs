using System.Numerics;
using Vortice.Mathematics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Blitz3DCS.Graphics.D3D11;

public sealed unsafe partial class D3D11Renderer
{
    private readonly PipelineStateTracker _tracker = new();
    private readonly BlitBatcher _blits = new();
    private BlitBatcher.FlushHandler? _drawBlitBatchCache;
    // Cached once: a method-group conversion allocates a new delegate on every call.
    private BlitBatcher.FlushHandler _drawBlitBatch => _drawBlitBatchCache ??= DrawBlitBatch;
    private readonly object?[] _srvDesired = new object?[PipelineStateTracker.ResourceSlots];
    private readonly object?[] _samplerDesired = new object?[PipelineStateTracker.ResourceSlots];
    private static readonly object TriangleListTopology = new();

    private TransformConstants _lastTransform;
    private bool _hasLastTransform;
    private MaterialConstants _lastMaterial;
    private bool _hasLastMaterial;
    private Matrix4x4 _lastWorld, _lastNormal;
    private bool _hasLastNormal;
    private long _reportedSkips;

    private void ResetShadowState()
    {
        _tracker.Invalidate();
        _hasLastTransform = _hasLastMaterial = _hasLastNormal = false;
    }

    private void CountSkips()
    {
        if (!D3D11FrameCounters.Enabled) return;
        var skipped = _tracker.Skipped;
        D3D11FrameCounters.Add(D3D11FrameCounters.SkippedBinds, skipped - _reportedSkips);
        _reportedSkips = skipped;
    }

    private void BindTriangleList()
    {
        if (_tracker.Changed(PipelineSlot.Topology, TriangleListTopology))
            _context!.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    private void BindInputLayout(ID3D11InputLayout layout)
    {
        if (_tracker.Changed(PipelineSlot.InputLayout, layout)) _context!.IASetInputLayout(layout);
    }

    private void BindVertexBuffer(ID3D11Buffer buffer, uint stride)
    {
        if (_tracker.Changed(PipelineSlot.VertexBuffer, buffer)) _context!.IASetVertexBuffer(0, buffer, stride, 0);
    }

    private void BindIndexBuffer(ID3D11Buffer buffer)
    {
        if (_tracker.Changed(PipelineSlot.IndexBuffer, buffer)) _context!.IASetIndexBuffer(buffer, Format.R32_UInt, 0);
    }

    private void BindVertexShader(ID3D11VertexShader shader)
    {
        if (_tracker.Changed(PipelineSlot.VertexShader, shader)) _context!.VSSetShader(shader);
    }

    private void BindPixelShader(ID3D11PixelShader shader)
    {
        if (_tracker.Changed(PipelineSlot.PixelShader, shader)) _context!.PSSetShader(shader);
    }

    private void BindVsConstantBuffer(int slot, ID3D11Buffer buffer)
    {
        if (_tracker.Changed(PipelineSlot.VsConstantBuffer0 + slot, buffer)) _context!.VSSetConstantBuffer((uint)slot, buffer);
    }

    private void BindPsConstantBuffer(int slot, ID3D11Buffer buffer)
    {
        if (_tracker.Changed(slot == 1 ? PipelineSlot.PsConstantBuffer1 : PipelineSlot.PsConstantBuffer2, buffer))
            _context!.PSSetConstantBuffer((uint)slot, buffer);
    }

    private void BindBlendState(ID3D11BlendState? state)
    {
        if (_tracker.Changed(PipelineSlot.Blend, state)) _context!.OMSetBlendState(state, null, uint.MaxValue);
    }

    private void BindDepthStencilState(ID3D11DepthStencilState? state)
    {
        if (_tracker.Changed(PipelineSlot.DepthStencil, state)) _context!.OMSetDepthStencilState(state);
    }

    private void BindRasterizerState(ID3D11RasterizerState? state)
    {
        if (_tracker.Changed(PipelineSlot.Rasterizer, state)) _context!.RSSetState(state);
    }

    private void BindTargetAndViewport(ID3D11RenderTargetView target, ID3D11DepthStencilView? depth, in Viewport viewport)
    {
        var targetChanged = _tracker.Changed(PipelineSlot.RenderTarget, target);
        var depthChanged = _tracker.Changed(PipelineSlot.DepthView, depth);
        if (targetChanged || depthChanged) _context!.OMSetRenderTargets(target, depth);
        if (_tracker.ViewportChanged(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth))
            _context!.RSSetViewport(viewport);
    }

    /// <summary>
    /// Binds SRVs and samplers from <see cref="_pixelShaderResources"/>/<see cref="_pixelSamplers"/>, skipping unchanged
    /// slots: a single changed slot uses the single-slot call, several use the original full-range call.
    /// </summary>
    private void BindPixelResources()
    {
        for (var i = 0; i < PipelineStateTracker.ResourceSlots; i++)
        {
            _srvDesired[i] = _pixelShaderResources[i];
            _samplerDesired[i] = _pixelSamplers[i];
        }
        if (_tracker.DirtyShaderResources(_srvDesired, out var start, out var length))
        {
            if (length == 1) _context!.PSSetShaderResource((uint)start, _pixelShaderResources[start]);
            else _context!.PSSetShaderResources(0, 8, _pixelShaderResources);
        }
        if (_tracker.DirtySamplers(_samplerDesired, out start, out length))
        {
            if (length == 1) _context!.PSSetSampler((uint)start, _pixelSamplers[start]);
            else _context!.PSSetSamplers(0, 8, _pixelSamplers);
        }
    }

    private void FlushBlits()
    {
        if (_blits.Pending > 0) _blits.Flush(_drawBlitBatch);
    }

    private static bool SameBytes<T>(in T a, in T b) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in a, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in b, 1)));
}
