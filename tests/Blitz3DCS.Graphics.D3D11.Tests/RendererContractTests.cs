using System.Numerics;
using System.Runtime.InteropServices;
using Blitz3DCS.Graphics.D3D11;
using Xunit;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class RendererContractTests
{
    [Fact]
    public void Vertex_layout_is_position_color_normal_and_texture_coordinates()
    {
        Assert.Equal(56, Marshal.SizeOf<D3D11Vertex>());
        Assert.Equal(0, Marshal.OffsetOf<D3D11Vertex>(nameof(D3D11Vertex.Position)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<D3D11Vertex>(nameof(D3D11Vertex.Color)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<D3D11Vertex>(nameof(D3D11Vertex.Normal)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<D3D11Vertex>(nameof(D3D11Vertex.TexCoord)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<D3D11Vertex>(nameof(D3D11Vertex.TexCoord2)).ToInt32());
        var vertex = new D3D11Vertex(Vector3.Zero, Vector4.One);
        Assert.Equal(Vector3.UnitZ, vertex.Normal);
        Assert.Equal(Vector2.Zero, vertex.TexCoord);
    }

    [Fact]
    public void Triangle_input_requires_nonempty_triangles_and_valid_indices()
    {
        var vertices = new D3D11Vertex[3];
        Assert.Throws<ArgumentException>(() => D3D11Renderer.ValidateTriangles(vertices, []));
        Assert.Throws<ArgumentException>(() => D3D11Renderer.ValidateTriangles(vertices, [0, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateTriangles(vertices, [0, 1, 3]));
        D3D11Renderer.ValidateTriangles(vertices, [0, 1, 2]);
    }

    [Fact]
    public void Texture_dimensions_and_rgba_byte_count_must_match()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateTextureData(0, 1, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateTextureData(1, -1, new byte[4]));
        Assert.Throws<ArgumentException>(() => D3D11Renderer.ValidateTextureData(2, 1, new byte[7]));
        Assert.Throws<OverflowException>(() => D3D11Renderer.ValidateTextureData(int.MaxValue, int.MaxValue, []));
        D3D11Renderer.ValidateTextureData(2, 1, new byte[8]);
    }

    [Fact]
    public void Viewport_must_fit_inside_the_render_target()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateViewport(-1, 0, 10, 10, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateViewport(0, 0, 0, 10, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Renderer.ValidateViewport(90, 0, 11, 10, 100, 100));
        D3D11Renderer.ValidateViewport(10, 20, 90, 80, 100, 100);
    }

    [Fact]
    public void Invalid_dimensions_are_rejected_before_platform_initialization()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D11Renderer(0, 100, "invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D11Renderer(100, 0, "invalid"));
    }

    [Fact]
    public void Valid_renderer_creation_reports_platform_requirement_before_opening_a_window()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => new D3D11Renderer(100, 100, "unsupported"));
    }
}
