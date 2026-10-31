using System.Numerics;
using Blitz3DCS.Graphics.D3D11;
using Blitz3DCS.Windowing;
using Xunit;
using Xunit.Sdk;

namespace Blitz3DCS.Graphics.D3D11.Tests;

public sealed class RendererWindowsSmokeTests
{
    [WindowsD3D11SmokeFact]
    public void Sdl_window_creates_d3d11_device_clears_draws_resizes_presents_and_reads_back()
    {
        RunSmoke();
    }

    [WindowsD3D11SmokeFact]
    public void Antialias_resolves_reads_backbuffer_writes_copies_and_survives_resize()
    {
        RunAntialiasSmoke();
    }

    [WindowsD3D11SmokeFact]
    public void Mutable_and_immutable_mipmaps_generate_and_regenerate_after_writes()
    {
        RunMipmapsSmoke();
    }

    [WindowsD3D11SmokeFact]
    public void Texture_blend_alpha_and_bump_environment_modes_match_legacy_contract()
    {
        RunTextureBlendSmoke();
    }

    internal static void RunTextureBlendSmoke()
    {
        using var renderer = new D3D11Renderer(64, 64, "Blitz3DCS D3D11 texture blend smoke");
        var fullscreen = new[]
        {
            new D3D11Vertex(new Vector3(-1, -1, .5f), Vector4.One, Vector3.UnitZ, new Vector2(.5f, .5f), new Vector2(.1875f, .5f)),
            new D3D11Vertex(new Vector3(-1, 3, .5f), Vector4.One, Vector3.UnitZ, new Vector2(.5f, .5f), new Vector2(.1875f, .5f)),
            new D3D11Vertex(new Vector3(3, -1, .5f), Vector4.One, Vector3.UnitZ, new Vector2(.5f, .5f), new Vector2(.1875f, .5f)),
        };
        using var mesh = renderer.CreateMesh(fullscreen, [0, 1, 2]);

        using var alphaTexture = renderer.CreateTexture(1, 1, [0, 0, 255, 128]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(mesh, Matrix4x4.Identity,
            [new D3D11TextureLayer(alphaTexture, Blend: 1, UseAlpha: false)],
            materialColor: new Vector4(1, 0, 0, 1));
        var alphaBlend = renderer.ReadPixelForTesting(32, 32);
        Assert.InRange(alphaBlend.R, (byte)127, (byte)128);
        Assert.Equal((byte)0, alphaBlend.G);
        Assert.InRange(alphaBlend.B, (byte)127, (byte)128);
        Assert.Equal((byte)255, alphaBlend.A);

        var environmentPixels = new byte[8 * 4];
        for (var x = 0; x < 8; x++)
        {
            environmentPixels[x * 4] = (byte)(x * 24);
            environmentPixels[x * 4 + 1] = (byte)(255 - x * 24);
            environmentPixels[x * 4 + 2] = (byte)(x * 17);
            environmentPixels[x * 4 + 3] = 255;
        }
        using var bump = renderer.CreateTexture(1, 1, [255, 0, 0, 255]);
        using var environment = renderer.CreateTexture(8, 1, environmentPixels);
        renderer.Clear(0, 0, 0);
        D3D11TextureLayer[] bumpLayers =
        [
            new(bump, Blend: 6, UseAlpha: false, BumpMatrix: new Vector4(.5f, .25f, .125f, .25f), BumpScale: 9, BumpOffset: -4),
            new(environment, Blend: 2, UseAlpha: false, CoordinateSet: 1)
        ];
        renderer.DrawMesh(mesh, Matrix4x4.Identity, bumpLayers);
        var bumped = renderer.ReadPixelForTesting(32, 32);
        Assert.Equal((byte)(4 * 24), bumped.R);
        Assert.Equal((byte)(255 - 4 * 24), bumped.G);
        Assert.Equal((byte)(4 * 17), bumped.B);
        Assert.Equal((byte)255, bumped.A);
    }

    internal static void RunAntialiasSmoke()
    {
        using var renderer = new D3D11Renderer(96, 80, "Blitz3DCS D3D11 MSAA smoke");
        renderer.Clear(.13f, .27f, .41f);
        renderer.SetViewport(20, 12, 48, 36);
        renderer.UpdateTexturePixels(null, 5, 7, 2, 1, [23, 71, 149, 255, 181, 43, 97, 255]);
        var originalFrame = renderer.ReadTexturePixels();

        renderer.SetAntialias(true);
        Assert.Contains(renderer.AntialiasSampleCount, new[] { 2, 4 });
        var sampleCount = renderer.AntialiasSampleCount;
        Assert.Equal(originalFrame, renderer.ReadTexturePixels());

        renderer.SetAntialias(false);
        Assert.Equal(1, renderer.AntialiasSampleCount);
        Assert.Equal(originalFrame, renderer.ReadTexturePixels());
        renderer.SetViewport(0, 0, 96, 80);
        renderer.SetAntialias(true);

        renderer.Clear(0, 0, 0);
        var triangle = new[]
        {
            new D3D11Vertex(new Vector3(-.83f, -.71f, .5f), Vector4.One),
            new D3D11Vertex(new Vector3(.19f, .89f, .5f), Vector4.One),
            new D3D11Vertex(new Vector3(.81f, -.37f, .5f), Vector4.One),
        };
        renderer.DrawTriangles(triangle, [0, 1, 2], Matrix4x4.Identity);
        var antialiasedPixels = renderer.ReadTexturePixels();
        var antialiasedEdgePixels = CountPartialCoveragePixels(antialiasedPixels);
        Assert.True(antialiasedEdgePixels > 0, "MSAA resolve should retain partial coverage at an edge pixel.");

        renderer.SetAntialias(false);
        renderer.Clear(0, 0, 0);
        renderer.DrawTriangles(triangle, [0, 1, 2], Matrix4x4.Identity);
        var singleSamplePixels = renderer.ReadTexturePixels();
        Assert.Equal(0, CountPartialCoveragePixels(singleSamplePixels));

        renderer.SetAntialias(true);
        renderer.Clear(0, 0, 0);
        renderer.DrawTriangles(triangle, [0, 1, 2], Matrix4x4.Identity);
        var resolvedPixels = renderer.ReadTexturePixels();
        Assert.True(CountPartialCoveragePixels(resolvedPixels) > 0);

        using var copied = renderer.CreateMutableTexture(96, 80);
        renderer.CopyTextureRegion(null, copied, 0, 0, 0, 0, 96, 80);
        Assert.Equal(resolvedPixels, renderer.ReadTexturePixels(copied));
        renderer.CopyTextureRegion(copied, null, 0, 0, 0, 0, 96, 80);
        Assert.Equal(resolvedPixels, renderer.ReadTexturePixels());

        renderer.UpdateTexturePixels(null, 8, 9, 1, 1, [23, 71, 149, 255]);
        Assert.Equal(new Rgba32(23, 71, 149, 255), renderer.ReadPixelForTesting(8, 9));
        renderer.Present(vsync: false);

        renderer.Resize(112, 88);
        Assert.Equal(sampleCount, renderer.AntialiasSampleCount);
        renderer.Clear(.25f, .5f, .75f);
        Assert.Equal(new Rgba32(64, 128, 191, 255), renderer.ReadPixelForTesting(3, 3));
        renderer.Present(vsync: false);

        renderer.SetAntialias(false);
        Assert.Equal(1, renderer.AntialiasSampleCount);
        renderer.Clear(0, 0, 0);
    }

    internal static void RunMipmapsSmoke()
    {
        using var renderer = new D3D11Renderer(64, 64, "Blitz3DCS D3D11 mipmap smoke");
        var checker = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var offset = (y * 8 + x) * 4;
            var value = (byte)(((x + y) & 1) == 0 ? 0 : 255);
            checker[offset] = checker[offset + 1] = checker[offset + 2] = value;
            checker[offset + 3] = 255;
        }

        using var singleLevel = renderer.CreateTexture(8, 8, checker);
        using var mipTexture = renderer.CreateTexture(8, 8, checker, mipmaps: true);
        using var mutableSingleLevel = renderer.CreateMutableTexture(8, 8);
        using var mutableMipTexture = renderer.CreateMutableTexture(8, 8, mipmaps: true);
        Assert.Equal(1, singleLevel.MipLevels);
        Assert.Equal(1, mutableSingleLevel.MipLevels);
        Assert.Equal(4, mipTexture.MipLevels);
        Assert.Equal(4, mutableMipTexture.MipLevels);
        AssertAverageCheckerMip(renderer.ReadTextureMipPixelsForTesting(mipTexture, 3));

        renderer.UpdateTexturePixels(mutableMipTexture, 0, 0, 8, 8, checker);
        renderer.DrawTextureRegion(mutableMipTexture, 0, 0, 8, 8, 0, 0, 1, 1, alphaBlend: false);
        AssertAverageCheckerMip(renderer.ReadTextureMipPixelsForTesting(mutableMipTexture, 3));

        renderer.ClearTexture(mutableMipTexture, 1, 0, 0, 1);
        renderer.DrawTextureRegion(mutableMipTexture, 0, 0, 8, 8, 1, 0, 1, 1, alphaBlend: false);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, renderer.ReadTextureMipPixelsForTesting(mutableMipTexture, 3));

        renderer.CopyTextureRegion(mipTexture, mutableMipTexture, 0, 0, 0, 0, 8, 8);
        renderer.DrawTextureRegion(mutableMipTexture, 0, 0, 8, 8, 2, 0, 1, 1, alphaBlend: false);
        AssertAverageCheckerMip(renderer.ReadTextureMipPixelsForTesting(mutableMipTexture, 3));

        renderer.DrawTextureRegion(mutableMipTexture, 0, 0, 8, 8, 0, 0, 8, 8,
            alphaBlend: false, destination: mutableMipTexture);
        AssertAverageCheckerMip(renderer.ReadTextureMipPixelsForTesting(mutableMipTexture, 3));

        renderer.ClearTexture(mutableMipTexture, 0, 0, 0, 0);
        renderer.DrawTextureRegion(mipTexture, 0, 0, 8, 8, 0, 0, 8, 8,
            alphaBlend: false, destination: mutableMipTexture);
        AssertAverageCheckerMip(renderer.ReadTextureMipPixelsForTesting(mutableMipTexture, 3));
    }

    private static int CountPartialCoveragePixels(byte[] rgbaPixels)
    {
        var count = 0;
        for (var pixel = 0; pixel < rgbaPixels.Length / 4; pixel++)
        {
            var red = rgbaPixels[pixel * 4];
            if (red is > 0 and < 255) count++;
        }
        return count;
    }

    private static void AssertAverageCheckerMip(byte[] pixel)
    {
        Assert.Equal(4, pixel.Length);
        Assert.InRange(pixel[0], (byte)126, (byte)129);
        Assert.InRange(pixel[1], (byte)126, (byte)129);
        Assert.InRange(pixel[2], (byte)126, (byte)129);
        Assert.Equal((byte)255, pixel[3]);
    }

    internal static void RunSmoke()
    {
        using var renderer = new D3D11Renderer(320, 240, "Blitz3DCS D3D11 smoke");
        renderer.Clear(0.12f, 0.34f, 0.56f);

        var vertices = new[]
        {
            new D3D11Vertex(new Vector3(-0.6f, -0.6f, 0.5f), new Vector4(1, 0, 0, 1)),
            new D3D11Vertex(new Vector3(0, 0.6f, 0.5f), new Vector4(0, 1, 0, 1)),
            new D3D11Vertex(new Vector3(0.6f, -0.6f, 0.5f), new Vector4(0, 0, 1, 1)),
        };
        var whiteTriangle = new[]
        {
            new D3D11Vertex(new Vector3(-0.5f, -0.5f, 0.5f), Vector4.One),
            new D3D11Vertex(new Vector3(0, 0.5f, 0.5f), Vector4.One),
            new D3D11Vertex(new Vector3(0.5f, -0.5f, 0.5f), Vector4.One),
        };
        renderer.DrawTriangles(vertices, [0, 1, 2], Matrix4x4.Identity);

        var clearPixel = renderer.ReadPixelForTesting(4, 4);
        Assert.Equal((byte)31, clearPixel.R);
        Assert.Equal((byte)87, clearPixel.G);
        Assert.Equal((byte)143, clearPixel.B);
        Assert.Equal((byte)255, clearPixel.A);
        Assert.NotEqual(clearPixel, renderer.ReadPixelForTesting(160, 120));

        using var mesh = renderer.CreateMesh(vertices, [0, 1, 2]);
        using var fogMesh = renderer.CreateMesh(whiteTriangle, [0, 1, 2]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity);
        renderer.Clear(1, 0, 0, clearColor: true, clearDepth: false);
        renderer.DrawMesh(fogMesh, Matrix4x4.CreateTranslation(0, 0, .1f));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 0, 0);
        renderer.Clear(0, 1, 0, clearColor: false, clearDepth: true);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.CreateTranslation(0, 0, .1f));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);
        Array.Fill(vertices, new D3D11Vertex(new Vector3(2, 2, 2), new Vector4(1, 1, 1, 1)));
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(mesh, Matrix4x4.Identity);
        Assert.NotEqual(new Rgba32(0, 0, 0, 255), renderer.ReadPixelForTesting(160, 120));

        var redFog = new D3D11Fog(Vector3.UnitX, 0, 1);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], fog: redFog, worldView: Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 128, 128);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], fog: redFog, worldView: Matrix4x4.CreateTranslation(0, 0, 2));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 0, 0);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], fog: new D3D11Fog(Vector3.UnitX, 0.5f, 0.5f), worldView: Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 0, 0);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);
        renderer.DrawTriangles(whiteTriangle, [0, 1, 2], Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);

        var unlit = new D3D11LightingDrawState(Effects: 0, Shininess: 0);
        renderer.SetSceneLighting(new Vector3(.25f), Vector3.Zero, []);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], lightingDrawState: unlit);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 64, 64, 64);
        renderer.SetSceneLighting(Vector3.Zero, Vector3.Zero, []);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [],
            lightingDrawState: new D3D11LightingDrawState(Effects: 1, Shininess: 0));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);

        renderer.SetSceneLighting(Vector3.Zero, Vector3.Zero,
            [new D3D11Light(1, Vector3.Zero, new Vector3(0, 0, -1), new Vector3(.5f, 0, 0), 1, 1, 0, 0)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], lightingDrawState: unlit);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 128, 0, 0);

        renderer.SetSceneLighting(Vector3.One, Vector3.Zero,
            [new D3D11Light(1, Vector3.Zero, new Vector3(0, 0, -1), Vector3.One, 1, 1, 0, 0)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], materialColor: new Vector4(.25f, .25f, .25f, 1),
            lightingDrawState: unlit);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 128, 128, 128);

        renderer.SetSceneLighting(Vector3.Zero, Vector3.Zero,
            [new D3D11Light(2, new Vector3(0, 0, 1.5f), Vector3.Zero, new Vector3(0, .5f, 0), .5f, 2, 0, 0)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], lightingDrawState: unlit);
        var nearPointPixel = renderer.ReadPixelForTesting(160, 120);
        Assert.InRange(nearPointPixel.G, (byte)1, (byte)255);
        Assert.Equal((byte)0, nearPointPixel.R);
        Assert.Equal((byte)0, nearPointPixel.B);
        renderer.SetSceneLighting(Vector3.Zero, Vector3.Zero,
            [new D3D11Light(2, new Vector3(0, 0, 2.5f), Vector3.Zero, new Vector3(0, .5f, 0), .5f, 2, 0, 0)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], lightingDrawState: unlit);
        var farPointPixel = renderer.ReadPixelForTesting(160, 120);
        Assert.InRange(farPointPixel.G, (byte)1, (byte)254);
        Assert.Equal((byte)0, farPointPixel.R);
        Assert.Equal((byte)0, farPointPixel.B);
        Assert.True(nearPointPixel.G > farPointPixel.G, $"Point light should attenuate with distance: near={nearPointPixel.G}, far={farPointPixel.G}.");

        renderer.SetSceneLighting(Vector3.Zero, Vector3.Zero,
            [new D3D11Light(3, new Vector3(0, 0, 1.5f), new Vector3(0, 0, -1), new Vector3(0, 0, .5f), .5f, 2, 0, MathF.PI)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], lightingDrawState: unlit);
        var spotPixel = renderer.ReadPixelForTesting(160, 120);
        Assert.InRange(spotPixel.B, (byte)1, (byte)255);
        Assert.Equal((byte)0, spotPixel.R);
        Assert.Equal((byte)0, spotPixel.G);

        renderer.SetSceneLighting(Vector3.Zero, new Vector3(0, 0, 2),
            [new D3D11Light(1, Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitX, 1, 1, 0, 0)]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [],
            lightingDrawState: new D3D11LightingDrawState(Effects: 0, Shininess: .5f));
        var specularPixel = renderer.ReadPixelForTesting(160, 120);
        Assert.InRange(specularPixel.R, (byte)253, (byte)255);
        Assert.InRange(specularPixel.G, (byte)1, (byte)255);
        Assert.InRange(specularPixel.B, (byte)1, (byte)255);
        Assert.InRange(Math.Abs(specularPixel.G - specularPixel.B), 0, 2);

        renderer.SetSceneLighting(Vector3.One, Vector3.Zero, []);
        var flatBackfaceVertices = new[]
        {
            new D3D11Vertex(new Vector3(-.5f, -.5f, .5f), Vector4.UnitX),
            new D3D11Vertex(new Vector3(0, .5f, .5f), Vector4.UnitY),
            new D3D11Vertex(new Vector3(.5f, -.5f, .5f), Vector4.UnitZ),
        };
        using var flatBackface = renderer.CreateMesh(flatBackfaceVertices, [0, 2, 1]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(flatBackface, Matrix4x4.Identity, [],
            lightingDrawState: new D3D11LightingDrawState(Effects: 2 | 4 | 16, Shininess: 0));
        var flatPixel = renderer.ReadPixelForTesting(160, 120);
        Assert.True(flatPixel == new Rgba32(255, 0, 0, 255) || flatPixel == new Rgba32(0, 255, 0, 255) || flatPixel == new Rgba32(0, 0, 255, 255),
            $"Flat, double-sided FX should keep one provoking-vertex color; got {flatPixel}.");
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [],
            lightingDrawState: new D3D11LightingDrawState(Effects: 16, Shininess: 0));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);
        renderer.Clear(.4f, .4f, .4f);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], alphaBlend: true,
            materialColor: new Vector4(.5f, .25f, 1, .5f),
            lightingDrawState: new D3D11LightingDrawState(Effects: 1, Shininess: 0, Blend: 2));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 51, 26, 102);
        renderer.Clear(.4f, .4f, .4f);
        renderer.DrawMesh(fogMesh, Matrix4x4.Identity, [], alphaBlend: true,
            materialColor: new Vector4(.5f, .25f, 1, .5f),
            lightingDrawState: new D3D11LightingDrawState(Effects: 1, Shininess: 0, Blend: 3));
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 166, 134, 230);
        renderer.SetSceneLighting(Vector3.One, Vector3.Zero, []);

        var halfAlphaTriangle = whiteTriangle.Select(vertex =>
            new D3D11Vertex(vertex.Position, new Vector4(1, 1, 1, .5f))).ToArray();
        renderer.Clear(0, 0, 0);
        renderer.DrawTriangles(halfAlphaTriangle, [0, 1, 2], Matrix4x4.Identity);
        Assert.Equal((byte)128, renderer.ReadPixelForTesting(160, 120).A);

        renderer.DrawMesh(mesh, Matrix4x4.Identity);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) renderer.DrawMesh(mesh, Matrix4x4.Identity);
        var steadyDrawAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Assert.InRange(steadyDrawAllocations, 0, 2048);

        var dynamicVertices = new[]
        {
            new D3D11Vertex(new Vector3(-.6f, -.6f, .5f), Vector4.One),
            new D3D11Vertex(new Vector3(0, .6f, .5f), Vector4.One),
            new D3D11Vertex(new Vector3(.6f, -.6f, .5f), Vector4.One)
        };
        using var dynamicMesh = renderer.CreateDynamicMesh(dynamicVertices, [0, 1, 2]);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(dynamicMesh, Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 255, 255, 255);
        for (var i = 0; i < dynamicVertices.Length; i++)
            dynamicVertices[i] = new D3D11Vertex(dynamicVertices[i].Position + new Vector3(2, 0, 0), Vector4.One);
        renderer.UpdateMeshVertices(dynamicMesh, dynamicVertices);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(dynamicMesh, Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(160, 120), 0, 0, 0);
        Assert.Throws<ArgumentException>(() => renderer.UpdateMeshVertices(mesh, dynamicVertices));
        Assert.Throws<ArgumentException>(() => renderer.UpdateMeshVertices(dynamicMesh, dynamicVertices.AsSpan(1)));
        var vertexUpdateAllocations = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++) renderer.UpdateMeshVertices(dynamicMesh, dynamicVertices);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - vertexUpdateAllocations, 0, 2048);

        var quadVertices = new[]
        {
            new D3D11Vertex(new Vector3(-0.8f, -0.8f, 0.4f), Vector4.One, Vector3.UnitZ, new Vector2(0, 0)),
            new D3D11Vertex(new Vector3(-0.8f, 0.8f, 0.4f), Vector4.One, Vector3.UnitZ, new Vector2(0, 2)),
            new D3D11Vertex(new Vector3(0.8f, 0.8f, 0.4f), Vector4.One, Vector3.UnitZ, new Vector2(2, 2)),
            new D3D11Vertex(new Vector3(0.8f, -0.8f, 0.4f), Vector4.One, Vector3.UnitZ, new Vector2(2, 0)),
        };
        uint[] quadIndices = [0, 1, 2, 0, 2, 3];
        using var quad = renderer.CreateMesh(quadVertices, quadIndices);
        var rgba = new byte[] { 255, 0, 0, 128, 0, 255, 0, 128 };
        using var texture = renderer.CreateTexture(2, 1, rgba);
        Array.Fill(rgba, (byte)0);

        var farWhiteVertices = quadVertices.Select(v => new D3D11Vertex(
            new Vector3(v.Position.X, v.Position.Y, 0.7f), Vector4.One, v.Normal, v.TexCoord)).ToArray();
        using var farWhite = renderer.CreateMesh(farWhiteVertices, quadIndices);
        renderer.Clear(0, 0, 1);
        renderer.DrawMesh(quad, Matrix4x4.Identity, texture, alphaBlend: true);
        renderer.DrawMesh(farWhite, Matrix4x4.Identity);

        AssertBlendedPixel(renderer.ReadPixelForTesting(64, 120), 128, 0, 127);
        AssertBlendedPixel(renderer.ReadPixelForTesting(128, 120), 0, 128, 127);
        AssertBlendedPixel(renderer.ReadPixelForTesting(192, 120), 128, 0, 127);
        AssertBlendedPixel(renderer.ReadPixelForTesting(256, 120), 0, 128, 127);

        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(quad, Matrix4x4.Identity,
            [new D3D11TextureLayer(texture, Blend: 2)], materialColor: new Vector4(0.25f, 0.5f, 0.75f, 1));
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 64, 0, 0);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(quad, Matrix4x4.Identity,
            [new D3D11TextureLayer(texture, Blend: 3, UseAlpha: true)], materialColor: new Vector4(0.25f, 0.5f, 0.75f, 1));
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 255, 128, 191);

        using var maskTexture = renderer.CreateTexture(2, 1, [255, 0, 0, 0, 0, 255, 0, 255]);
        renderer.Clear(0, 0, 1);
        renderer.DrawMesh(quad, Matrix4x4.Identity, [new D3D11TextureLayer(maskTexture, Masked: true)],
            lightingDrawState: new D3D11LightingDrawState(3, 0));
        renderer.DrawMesh(farWhite, Matrix4x4.Identity);
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 255, 255, 255);
        AssertRgb(renderer.ReadPixelForTesting(128, 120), 0, 255, 0);
        using var thresholdTexture = renderer.CreateTexture(1, 1, [255, 0, 0, 128]);
        renderer.Clear(0, 0, 1);
        renderer.DrawMesh(quad, Matrix4x4.Identity, [new D3D11TextureLayer(thresholdTexture, Masked: true)],
            lightingDrawState: new D3D11LightingDrawState(3, 0));
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 0, 0, 255);
        renderer.DrawMesh(quad, Matrix4x4.Identity, [new D3D11TextureLayer(thresholdTexture, Masked: true)],
            lightingDrawState: new D3D11LightingDrawState(1 | 32, 0));
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 255, 0, 0);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(quad, Matrix4x4.Identity,
            [new D3D11TextureLayer(texture, Blend: 5)], materialColor: new Vector4(0.25f, 0.5f, 0.75f, 1));
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 128, 0, 0);

        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(quad, Matrix4x4.Identity,
            [new D3D11TextureLayer(texture, UseAlpha: false, UvTransform: Matrix3x2.CreateTranslation(0.5f, 0))]);
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 0, 255, 0);
        using var secondaryUvQuad = renderer.CreateMesh(quadVertices.Select(v =>
            new D3D11Vertex(v.Position, v.Color, v.Normal, v.TexCoord, new Vector2(0.75f, 0))).ToArray(), quadIndices);
        renderer.Clear(0, 0, 0);
        renderer.DrawMesh(secondaryUvQuad, Matrix4x4.Identity,
            [new D3D11TextureLayer(texture, UseAlpha: false, CoordinateSet: 1)]);
        AssertRgb(renderer.ReadPixelForTesting(64, 120), 0, 255, 0);

        renderer.Clear(0, 0, 0);
        renderer.DrawTextureRegion(texture, 0, 0, 1, 1, 40, 40, 10, 10);
        AssertRgb(renderer.ReadPixelForTesting(40, 45), 128, 0, 0);
        AssertRgb(renderer.ReadPixelForTesting(45, 45), 128, 0, 0);
        using var checkerTexture = renderer.CreateTexture(2, 2,
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 0, 255,
        ]);
        renderer.Clear(0, 0, 0);
        renderer.DrawTextureRegion(checkerTexture, 0, 0, 2, 2, 90, 80, 2, 2, alphaBlend: false);
        Assert.Equal(new Rgba32(255, 0, 0, 255), renderer.ReadPixelForTesting(90, 80));
        Assert.Equal(new Rgba32(0, 255, 0, 255), renderer.ReadPixelForTesting(91, 80));
        Assert.Equal(new Rgba32(0, 0, 255, 255), renderer.ReadPixelForTesting(90, 81));
        Assert.Equal(new Rgba32(255, 255, 0, 255), renderer.ReadPixelForTesting(91, 81));
        renderer.Clear(0, 0, 0);
        D3D11TextureRegion[] checkerRegions =
        [
            new(0, 0, 1, 1, 100, 80, 1, 1),
            new(1, 0, 1, 1, 101, 80, 1, 1),
            new(0, 1, 1, 1, 100, 81, 1, 1),
            new(1, 1, 1, 1, 101, 81, 1, 1),
        ];
        renderer.DrawTextureRegions(checkerTexture, checkerRegions, alphaBlend: false);
        Assert.Equal(new Rgba32(255, 0, 0, 255), renderer.ReadPixelForTesting(100, 80));
        Assert.Equal(new Rgba32(0, 255, 0, 255), renderer.ReadPixelForTesting(101, 80));
        Assert.Equal(new Rgba32(0, 0, 255, 255), renderer.ReadPixelForTesting(100, 81));
        Assert.Equal(new Rgba32(255, 255, 0, 255), renderer.ReadPixelForTesting(101, 81));
        var manyRegions = new D3D11TextureRegion[129];
        Array.Fill(manyRegions, new D3D11TextureRegion(0, 0, 1, 1, 110, 90, 1, 1));
        renderer.DrawTextureRegions(checkerTexture, manyRegions, alphaBlend: false);
        Assert.Equal(new Rgba32(255, 0, 0, 255), renderer.ReadPixelForTesting(110, 90));
        var batchAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++) renderer.DrawTextureRegions(checkerTexture, checkerRegions, alphaBlend: false);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - batchAllocationStart, 0, 2048);
        using var renderTargetTexture = renderer.CreateMutableTexture(4, 2);
        renderer.ClearTexture(renderTargetTexture, 0, 0, 0, 1);
        renderer.DrawTextureRegion(texture, 0, 0, 1, 1, 1, 0, 2, 2,
            alphaBlend: false, destination: renderTargetTexture);
        AssertRgb(PixelAt(renderer.ReadTexturePixels(renderTargetTexture), 4, 1, 0), 255, 0, 0);

        using var selfBlit = renderer.CreateMutableTexture(4, 2);
        byte[] selfBlitPixels =
        [
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255,
            20, 30, 40, 255, 50, 60, 70, 255, 80, 90, 100, 255, 110, 120, 130, 255,
        ];
        renderer.UpdateTexturePixels(selfBlit, 0, 0, 4, 2, selfBlitPixels);
        renderer.DrawTextureRegion(selfBlit, 0, 0, 2, 1, 2, 0, 2, 1,
            alphaBlend: false, destination: selfBlit);
        var selfBlitResult = renderer.ReadTexturePixels(selfBlit);
        Assert.Equal(selfBlitPixels[..8], selfBlitResult[8..16]);
        var blitAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
            renderer.DrawTextureRegion(texture, 0, 0, 1, 1, 40, 40, 10, 10, alphaBlend: false);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - blitAllocationStart, 0, 2048);

        using var procedural = renderer.CreateMutableTexture(4, 3);
        Assert.Equal(4, procedural.Width);
        Assert.Equal(3, procedural.Height);
        Assert.All(renderer.ReadTexturePixels(procedural), value => Assert.Equal((byte)0, value));

        byte[] upload =
        [
            255, 0, 0, 255,   0, 255, 0, 255,   0, 0, 255, 255, 255, 255, 255, 255,
            10, 20, 30, 40,   50, 60, 70, 80,   90, 100, 110, 120, 130, 140, 150, 160,
            1, 2, 3, 4,        5, 6, 7, 8,       9, 10, 11, 12,     13, 14, 15, 16,
        ];
        renderer.UpdateTexturePixels(procedural, 0, 0, 4, 3, upload);
        Assert.Equal(upload, renderer.ReadTexturePixels(procedural));

        renderer.UpdateTexturePixels(procedural, 1, 1, 1, 1, [201, 202, 203, 204]);
        var afterPartialUpload = renderer.ReadTexturePixels(procedural);
        Assert.Equal(new byte[] { 201, 202, 203, 204 }, afterPartialUpload[20..24]);
        Assert.Equal(upload.AsSpan(0, 20).ToArray(), afterPartialUpload.AsSpan(0, 20).ToArray());

        using var copied = renderer.CreateMutableTexture(4, 3);
        renderer.ClearTexture(copied, 0, 0, 0, 0);
        renderer.CopyTextureRegion(procedural, copied, -1, 0, 0, 0, 4, 3);
        var clippedCopy = renderer.ReadTexturePixels(copied);
        byte[] expectedClippedCopy = new byte[48];
        for (var y = 0; y < 3; y++)
            Array.Copy(afterPartialUpload, y * 16, expectedClippedCopy, y * 16 + 4, 12);
        Assert.Equal(expectedClippedCopy, clippedCopy);

        using var overlap = renderer.CreateMutableTexture(4, 1);
        byte[] overlapSource = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        renderer.UpdateTexturePixels(overlap, 0, 0, 4, 1, overlapSource);
        renderer.CopyTextureRegion(overlap, overlap, 0, 0, 1, 0, 3, 1);
        Assert.Equal(new byte[]
        {
            255, 0, 0, 255, 255, 0, 0, 255,
            0, 255, 0, 255, 0, 0, 255, 255,
        }, renderer.ReadTexturePixels(overlap));

        renderer.CopyTextureRegion(procedural, null, 0, 0, 12, 10, 4, 3);
        var copiedToBackBuffer = renderer.ReadTexturePixels();
        var backBufferOffset = (10 * 320 + 12) * 4;
        Assert.Equal(afterPartialUpload[..16], copiedToBackBuffer[backBufferOffset..(backBufferOffset + 16)]);

        renderer.ClearTexture(procedural, 0, 0.5f, 1, 0.25f);
        var cleared = renderer.ReadTexturePixels(procedural);
        Assert.Equal(new byte[] { 0, 128, 255, 64 }, cleared[..4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.CopyTextureRegion(procedural, copied, 0, 0, 0, 0, -1, 1));
        Assert.Throws<ArgumentException>(() => renderer.UpdateTexturePixels(procedural, 0, 0, 1, 1, [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => renderer.UpdateTexturePixels(texture, 0, 0, 1, 1, [0, 0, 0, 0]));

        mesh.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.DrawMesh(mesh, Matrix4x4.Identity));

        renderer.Present(vsync: false);
        renderer.Resize(321, 241);
        renderer.Clear(0, 0, 0);
        renderer.Present(vsync: false);
        var meshDisposedWithRenderer = renderer.CreateMesh(vertices, [0, 1, 2]);
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.DrawMesh(meshDisposedWithRenderer, Matrix4x4.Identity));
        meshDisposedWithRenderer.Dispose();

        using var sharedWindow = new WindowHost(1, 1, "shared application window");
        var sharedHandle = sharedWindow.GetWin32WindowHandle();
        var sharedRenderer = new D3D11Renderer(320, 240, "Blitz3DCS shared smoke", sharedWindow);
        Assert.Equal(sharedHandle, sharedWindow.GetWin32WindowHandle());
        sharedRenderer.Dispose();
        Assert.Equal(sharedHandle, sharedWindow.GetWin32WindowHandle());
    }

    private static void AssertBlendedPixel(Rgba32 actual, byte r, byte g, byte b)
    {
        Assert.InRange(actual.R, (byte)Math.Max(0, r - 2), (byte)Math.Min(255, r + 2));
        Assert.InRange(actual.G, (byte)Math.Max(0, g - 2), (byte)Math.Min(255, g + 2));
        Assert.InRange(actual.B, (byte)Math.Max(0, b - 2), (byte)Math.Min(255, b + 2));
        Assert.InRange(actual.A, (byte)189, (byte)193);
    }

    private static void AssertRgb(Rgba32 actual, byte r, byte g, byte b)
    {
        Assert.InRange(actual.R, (byte)Math.Max(0, r - 2), (byte)Math.Min(255, r + 2));
        Assert.InRange(actual.G, (byte)Math.Max(0, g - 2), (byte)Math.Min(255, g + 2));
        Assert.InRange(actual.B, (byte)Math.Max(0, b - 2), (byte)Math.Min(255, b + 2));
    }

    private static Rgba32 PixelAt(byte[] pixels, int width, int x, int y)
    {
        var offset = checked((y * width + x) * 4);
        return new(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private sealed class WindowsD3D11SmokeFactAttribute : FactAttribute
    {
        public WindowsD3D11SmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real device/window smoke test.";
        }
    }
}
