using Blitz3DCS.Graphics.D3D11;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Engine.Windows;

/// <summary>Small GPU-backed 2D drawing primitives used by the legacy graphics API.</summary>
public static partial class LegacyGraphics
{
    private static D3D11Renderer? _drawingRenderer;
    private static D3D11Texture? _drawingWhitePixel;
    private static readonly List<D3D11TextureRegion> DrawingRegions = [];

    /// <summary>Draws a filled or one-pixel outlined rectangle in the current drawing color.</summary>
    public static void Rect(int x, int y, int width, int height, int solid = 1)
    {
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        try { RectImpl(x, y, width, height, solid); }
        finally { if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.Draw2D, profileStart); }
    }

    private static void RectImpl(int x, int y, int width, int height, int solid)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (width <= 0 || height <= 0) return;

        var target = GetBuffer(ResolveBufferHandle(0));
        var destination = target.IsBackBuffer ? null : target.Frame!.Resource;
        var renderer = RequireRenderer();
        var pixel = DrawingWhitePixel(renderer);
        if (solid != 0)
        {
            renderer.DrawTextureRegion(pixel, 0, 0, 1, 1, x, y, width, height,
                _drawingColor, alphaBlend: false, destination: destination);
            return;
        }

        renderer.DrawTextureRegion(pixel, 0, 0, 1, 1, x, y, width, 1,
            _drawingColor, alphaBlend: false, destination: destination);
        renderer.DrawTextureRegion(pixel, 0, 0, 1, 1, x, y, 1, height,
            _drawingColor, alphaBlend: false, destination: destination);
        renderer.DrawTextureRegion(pixel, 0, 0, 1, 1, (float)x + width - 1, y, 1, height,
            _drawingColor, alphaBlend: false, destination: destination);
        renderer.DrawTextureRegion(pixel, 0, 0, 1, 1, x, (float)y + height - 1, width, 1,
            _drawingColor, alphaBlend: false, destination: destination);
    }

    /// <summary>Draws one pixel in the current drawing color.</summary>
    public static void Plot(int x, int y) => Rect(x, y, 1, 1);

    /// <summary>Draws a clipped, inclusive Bresenham line in the current drawing color.</summary>
    public static void Line(int x1, int y1, int x2, int y2)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        var target = GetBuffer(ResolveBufferHandle(0));
        if (!ClipLine(ref x1, ref y1, ref x2, ref y2, target.Width, target.Height)) return;

        var destination = target.IsBackBuffer ? null : target.Frame!.Resource;
        var renderer = RequireRenderer();
        var pixel = DrawingWhitePixel(renderer);
        var regions = DrawingRegions;
        regions.Clear();
        var dx = Math.Abs(x2 - x1);
        var dy = Math.Abs(y2 - y1);
        var sx = x1 < x2 ? 1 : -1;
        var sy = y1 < y2 ? 1 : -1;
        var runY = y1;
        var runLeft = x1;
        var runRight = x1;
        var previousX = x1;
        void Plot(int x, int y)
        {
            if (y != runY || Math.Abs(x - previousX) != 1)
            {
                AddHorizontalRun(regions, runLeft, runY, runRight);
                runY = y;
                runLeft = runRight = x;
            }
            else
            {
                runLeft = Math.Min(runLeft, x);
                runRight = Math.Max(runRight, x);
            }
            previousX = x;
        }

        var x = x1;
        var y = y1;
        if ((dx | dy) == 0)
        {
            Plot(x, y);
        }
        else if (dx >= dy)
        {
            var error = -dx;
            var sideAdjust = dx * 2;
            var primaryAdjust = dy * 2;
            for (var remaining = dx; remaining >= 0; remaining--)
            {
                Plot(x, y);
                x += sx;
                error += primaryAdjust;
                if (error >= 0) { y += sy; error -= sideAdjust; }
            }
        }
        else
        {
            var error = -dy;
            var sideAdjust = dy * 2;
            var primaryAdjust = dx * 2;
            for (var remaining = dy; remaining >= 0; remaining--)
            {
                Plot(x, y);
                y += sy;
                error += primaryAdjust;
                if (error >= 0) { x += sx; error -= sideAdjust; }
            }
        }
        AddHorizontalRun(regions, runLeft, runY, runRight);
        renderer.DrawTextureRegions(pixel, CollectionsMarshal.AsSpan(regions), _drawingColor,
            alphaBlend: false, destination: destination);
    }

    /// <summary>Draws a clipped filled or one-pixel outlined ellipse using legacy scanline coverage.</summary>
    public static void Oval(int x, int y, int width, int height, int solid = 1)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (width <= 0 || height <= 0) return;
        var target = GetBuffer(ResolveBufferHandle(0));
        var top = Math.Max(0L, y);
        var bottom = Math.Min((long)target.Height, (long)y + height);
        if (top >= bottom) return;

        var destination = target.IsBackBuffer ? null : target.Frame!.Resource;
        var renderer = RequireRenderer();
        var pixel = DrawingWhitePixel(renderer);
        var regions = DrawingRegions;
        regions.Clear();
        var xr = width * .5f;
        var yr = height * .5f;
        var aspect = (float)width / height;
        var centerX = x + xr + .5f;
        var centerY = y + yr - .5f;
        var radiusSquared = yr * yr;

        if (solid != 0)
        {
            for (var row = (int)top; row < bottom; row++)
            {
                var scanY = row - centerY;
                var halfWidth = MathF.Sqrt(Math.Max(0, radiusSquared - scanY * scanY)) * aspect;
                var left = (int)MathF.Floor(centerX - halfWidth);
                var right = (int)MathF.Floor(centerX + halfWidth);
                AddClippedRun(regions, left, right, row, target.Width);
            }
        }
        else
        {
            AddOvalOutlineHalf(regions, x, y, width, height, target.Width,
                (int)top, (int)bottom - 1, centerX, centerY, radiusSquared, aspect, topHalf: true);
            AddOvalOutlineHalf(regions, x, y, width, height, target.Width,
                (int)top, (int)bottom - 1, centerX, centerY, radiusSquared, aspect, topHalf: false);
        }

        renderer.DrawTextureRegions(pixel, CollectionsMarshal.AsSpan(regions), _drawingColor,
            alphaBlend: false, destination: destination);
    }

    private static bool ClipLine(ref int x0, ref int y0, ref int x1, ref int y1, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        var right = width - 1;
        var bottom = height - 1;
        while (true)
        {
            var code0 = OutCode(x0, y0, right, bottom);
            var code1 = OutCode(x1, y1, right, bottom);
            if ((code0 | code1) == 0) return true;
            if ((code0 & code1) != 0) return false;
            if ((code0 & 1) != 0) { x0 = ClipCoordinate(x0, x1, bottom, y0, y1); y0 = bottom; }
            else if ((code0 & 2) != 0) { x0 = ClipCoordinate(x0, x1, 0, y0, y1); y0 = 0; }
            else if ((code0 & 4) != 0) { y0 = ClipCoordinate(y0, y1, right, x0, x1); x0 = right; }
            else if ((code0 & 8) != 0) { y0 = ClipCoordinate(y0, y1, 0, x0, x1); x0 = 0; }
            else if ((code1 & 1) != 0) { x1 = ClipCoordinate(x0, x1, bottom, y0, y1); y1 = bottom; }
            else if ((code1 & 2) != 0) { x1 = ClipCoordinate(x0, x1, 0, y0, y1); y1 = 0; }
            else if ((code1 & 4) != 0) { y1 = ClipCoordinate(y0, y1, right, x0, x1); x1 = right; }
            else { y1 = ClipCoordinate(y0, y1, 0, x0, x1); x1 = 0; }
        }
    }

    private static int OutCode(int x, int y, int right, int bottom) =>
        (y > bottom ? 1 : y < 0 ? 2 : 0) | (x > right ? 4 : x < 0 ? 8 : 0);

    private static int ClipCoordinate(int a0, int a1, int boundary, int b0, int b1) =>
        checked((int)((decimal)a0 + ((decimal)(a1 - (long)a0) * (boundary - (long)b0)) / (b1 - (long)b0)));

    private static void AddOvalOutlineHalf(List<D3D11TextureRegion> regions, int x, int y, int width, int height,
        int targetWidth, int top, int bottom, float centerX, float centerY, float radiusSquared, float aspect, bool topHalf)
    {
        var middle = (int)MathF.Floor(centerY);
        var start = topHalf ? top : bottom;
        var finish = topHalf ? Math.Min(bottom, middle) : Math.Max(top, middle + 1);
        var step = topHalf ? 1 : -1;
        var scan = start - centerY;
        var previousLeft = (int)centerX;
        var previousRight = previousLeft;
        for (var row = start; topHalf ? row <= finish : row >= finish; row += step, scan += step)
        {
            var halfWidth = MathF.Sqrt(Math.Max(0, radiusSquared - scan * scan)) * aspect;
            var left = (int)MathF.Floor(centerX - halfWidth);
            var right = (int)MathF.Floor(centerX + halfWidth);
            AddClippedOutlineRun(regions, left, previousLeft, row, targetWidth, leftEdge: true);
            AddClippedOutlineRun(regions, previousRight, right, row, targetWidth, leftEdge: false);
            previousLeft = left;
            previousRight = right;
        }
    }

    private static void AddClippedRun(List<D3D11TextureRegion> regions, int left, int right, int row, int width)
    {
        left = Math.Max(0, left);
        right = Math.Min(width, right);
        if (right > left) AddHorizontalRun(regions, left, row, right - 1);
    }

    private static void AddClippedOutlineRun(List<D3D11TextureRegion> regions, int left, int right, int row, int width, bool leftEdge)
    {
        if (right <= left)
        {
            if (leftEdge) right = left + 1;
            else left = right - 1;
        }
        AddClippedRun(regions, left, right, row, width);
    }

    private static void AddHorizontalRun(List<D3D11TextureRegion> regions, int left, int row, int right) =>
        regions.Add(new(0, 0, 1, 1, left, row, right - left + 1, 1));

    /// <summary>Releases the cached white pixel before the renderer is disposed.</summary>
    internal static void CloseDrawing()
    {
        _drawingWhitePixel?.Dispose();
        _drawingWhitePixel = null;
        _drawingRenderer = null;
        DrawingRegions.Clear();
    }

    private static D3D11Texture DrawingWhitePixel(D3D11Renderer renderer)
    {
        if (ReferenceEquals(renderer, _drawingRenderer) && _drawingWhitePixel is not null)
            return _drawingWhitePixel;

        _drawingWhitePixel = null;
        _drawingRenderer = renderer;
        _drawingWhitePixel = renderer.CreateTexture(1, 1, [255, 255, 255, 255]);
        return _drawingWhitePixel;
    }
}
