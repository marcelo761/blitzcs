using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Blitz3DCS.Graphics.D3D11;

public sealed unsafe partial class D3D11Renderer
{
    private ID3D11Texture2D? _antialiasColorTexture;
    private int _antialiasSampleCount = 1;
    private int _antialiasQuality;

    internal int AntialiasSampleCount => _antialiasSampleCount;

    internal static int SelectAntialiasSampleCount(Func<Format, uint, uint> qualityLevels)
    {
        ArgumentNullException.ThrowIfNull(qualityLevels);
        foreach (var sampleCount in new uint[] { 4, 2 })
        {
            if (qualityLevels(Format.R8G8B8A8_UNorm, sampleCount) > 0 &&
                qualityLevels(Format.D32_Float, sampleCount) > 0)
                return (int)sampleCount;
        }

        return 0;
    }

    internal static int RequireAntialiasSampleCount(Func<Format, uint, uint> qualityLevels)
    {
        var sampleCount = SelectAntialiasSampleCount(qualityLevels);
        return sampleCount > 0
            ? sampleCount
            : throw new PlatformNotSupportedException("The D3D11 device does not support 2x or 4x multisampling for both color and depth targets.");
    }

    /// <summary>Enables or disables multisampled rendering on the window backbuffer.</summary>
    public void SetAntialias(bool enabled)
    {
        CheckAccess();
        var requestedCount = 1;
        var requestedQuality = 0;
        if (enabled)
        {
            requestedCount = RequireAntialiasSampleCount((format, samples) =>
                _device!.CheckMultisampleQualityLevels(format, samples));
        }

        if (_antialiasSampleCount == requestedCount && _antialiasQuality == requestedQuality)
            return;

        var previousCount = _antialiasSampleCount;
        var previousQuality = _antialiasQuality;
        var previousViewport = _viewport;
        SnapshotBackBuffer();
        ReleaseWindowTargets();
        _antialiasSampleCount = requestedCount;
        _antialiasQuality = requestedQuality;
        try
        {
            CreateWindowResources(_width, _height);
            _viewport = previousViewport;
            RestoreSnapshotToAntialiasTarget();
            BindRenderTargets();
        }
        catch
        {
            ReleaseWindowTargets();
            _antialiasSampleCount = previousCount;
            _antialiasQuality = previousQuality;
            CreateWindowResources(_width, _height);
            _viewport = previousViewport;
            RestoreSnapshotToAntialiasTarget();
            BindRenderTargets();
            throw;
        }
    }

    private void SnapshotBackBuffer()
    {
        ResolveBackBuffer();
        ClearPipelineState();
        EnsureBlitScratch(_width, _height);
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _context!.CopyResource(_blitScratch!, backBuffer);
    }

    private void RestoreSnapshotToAntialiasTarget()
    {
        if (_antialiasSampleCount == 1)
            return;

        var snapshot = new D3D11Texture(this, _blitScratch!, _blitScratchView!, _width, _height, mutable: false);
        Span<D3D11TextureRegion> region = stackalloc D3D11TextureRegion[1]
        {
            new(0, 0, _width, _height, 0, 0, _width, _height)
        };
        DrawTextureRegions(snapshot, region, alphaBlend: false);
    }

    private void ResolveBackBuffer()
    {
        if (_antialiasSampleCount == 1)
            return;

        ClearPipelineState();
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _context!.ResolveSubresource(backBuffer, 0, _antialiasColorTexture!, 0, Format.R8G8B8A8_UNorm);
    }

    private void ReleaseWindowTargets()
    {
        ClearPipelineState();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _depthView?.Dispose();
        _depthView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
        _antialiasColorTexture?.Dispose();
        _antialiasColorTexture = null;
    }
}
