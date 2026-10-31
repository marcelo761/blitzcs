using Vortice.Direct3D11;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Renderer-owned vertex and index buffers for repeated draws.</summary>
public sealed class D3D11Mesh : IDisposable
{
    private bool _disposed;

    internal D3D11Mesh(D3D11Renderer owner, ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, uint indexCount,
        int vertexCount, bool dynamic = false)
    {
        Owner = owner;
        VertexBuffer = vertexBuffer;
        IndexBuffer = indexBuffer;
        IndexCount = indexCount;
        VertexCount = vertexCount;
        IsDynamic = dynamic;
    }

    internal D3D11Renderer Owner { get; }
    internal ID3D11Buffer VertexBuffer { get; }
    internal ID3D11Buffer IndexBuffer { get; }
    internal uint IndexCount { get; set; }
    internal int VertexCount { get; set; }
    internal bool IsDynamic { get; }
    /// <summary>Resizable meshes own dynamic vertex and index buffers larger than their current contents.</summary>
    internal bool IsResizable { get; init; }
    internal int VertexCapacity { get; init; }
    internal uint IndexCapacity { get; init; }
    /// <summary>Renderer draw-queue epoch of the latest queued draw of this mesh (see D3D11Renderer.FlushDrawsReferencing).</summary>
    internal int QueueEpoch { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        Owner.DisposeMesh(this);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void DisposeFromRenderer()
    {
        if (_disposed) return;
        _disposed = true;
        IndexBuffer.Dispose();
        VertexBuffer.Dispose();
    }
}
