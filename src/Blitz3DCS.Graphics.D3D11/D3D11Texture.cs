using Vortice.Direct3D11;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Renderer-owned RGBA8 texture and shader-resource view.</summary>
public sealed class D3D11Texture : IDisposable
{
    private bool _disposed;

    internal D3D11Texture(
        D3D11Renderer owner,
        ID3D11Texture2D resource,
        ID3D11ShaderResourceView view,
        int width,
        int height,
        bool mutable,
        ID3D11RenderTargetView? renderTargetView = null)
    {
        Owner = owner;
        Resource = resource;
        ShaderResourceView = view;
        Width = width;
        Height = height;
        IsMutable = mutable;
        RenderTargetView = renderTargetView;
    }

    internal D3D11Renderer Owner { get; }
    internal ID3D11Texture2D Resource { get; }
    internal ID3D11ShaderResourceView ShaderResourceView { get; }
    internal ID3D11RenderTargetView? RenderTargetView { get; }
    internal bool IsMutable { get; }

    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        if (_disposed) return;
        Owner.DisposeTexture(this);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void DisposeFromRenderer()
    {
        if (_disposed) return;
        _disposed = true;
        RenderTargetView?.Dispose();
        ShaderResourceView.Dispose();
        Resource.Dispose();
    }
}
