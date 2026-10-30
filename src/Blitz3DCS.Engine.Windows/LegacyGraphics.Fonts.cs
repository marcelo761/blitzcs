using System.Numerics;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly Dictionary<int, FontState> Fonts = [];
    private static int _nextFontHandle = 1;
    private static int _currentFont;
    private static int _defaultFont;
    private static Vector4 _drawingColor = Vector4.One;

    public static int LoadFont(string name, int height = 12, int bold = 0, int italic = 0, int underline = 0)
    {
        CheckThreadIfStarted();
        var renderer = RequireRenderer();
        try
        {
            var atlas = GdiFontAtlas.Load(name, height, bold != 0, italic != 0, underline != 0, _width);
            var texture = renderer.CreateTexture(atlas.Width, atlas.Height, atlas.Pixels);
            if (_nextFontHandle == int.MaxValue) { texture.Dispose(); throw new InvalidOperationException("Font handle space is exhausted."); }
            var handle = _nextFontHandle++;
            Fonts.Add(handle, new(atlas, texture));
            return handle;
        }
        catch (System.ComponentModel.Win32Exception) { return 0; }
    }

    public static void FreeFont(int font)
    {
        CheckThreadIfStarted();
        if (!Fonts.TryGetValue(font, out var state)) throw new ArgumentException("Font does not exist.", nameof(font));
        if (font == _defaultFont) return;
        if (_currentFont == font) _currentFont = DefaultFont();
        Fonts.Remove(font);
        state.Texture.Dispose();
    }
    public static void SetFont(int font)
    {
        CheckThreadIfStarted();
        if (!Fonts.ContainsKey(font)) throw new ArgumentException("Font does not exist.", nameof(font));
        _currentFont = font;
    }
    public static int GetFont() { _ = CurrentFont(); return _currentFont; }
    public static int FontWidth() => CurrentFont().Atlas.MaximumCharacterWidth;
    public static int FontHeight() => CurrentFont().Atlas.LineHeight;
    public static int StringHeight(string text) { ArgumentNullException.ThrowIfNull(text); return FontHeight(); }

    public static int StringWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var font = CurrentFont();
        Span<byte> characters = text.Length <= 1024 ? stackalloc byte[text.Length] : new byte[text.Length];
        var length = GdiFontAtlas.Ansi.GetBytes(text.AsSpan(), characters);
        var width = 0;
        foreach (var code in characters[..length]) width = checked(width + font.Atlas.Glyphs[code].Width);
        return width;
    }

    public static void Color(int red, int green, int blue)
    {
        ValidateRgb(red, green, blue);
        _drawingColor = new Vector4(red / 255f, green / 255f, blue / 255f, 1);
    }
    public static int ColorRed() => (int)MathF.Round(_drawingColor.X * 255);
    public static int ColorGreen() => (int)MathF.Round(_drawingColor.Y * 255);
    public static int ColorBlue() => (int)MathF.Round(_drawingColor.Z * 255);

    /// <summary>Sets the current drawing color from a pixel in the selected buffer.</summary>
    public static void GetColor(int x, int y)
    {
        CheckThreadIfStarted();
        var target = GetBuffer(ResolveBufferHandle(0));
        var offset = PixelOffset(target, x, y);
        byte red, green, blue;
        if (target.LockedPixels is { } locked)
        {
            red = locked[offset];
            green = locked[offset + 1];
            blue = locked[offset + 2];
        }
        else if (target.IsBackBuffer)
        {
            var pixel = RequireRenderer().ReadPixelForTesting(x, y);
            red = pixel.R;
            green = pixel.G;
            blue = pixel.B;
        }
        else
        {
            var pixels = RequireRenderer().ReadTexturePixels(target.Frame!.Resource);
            red = pixels[offset];
            green = pixels[offset + 1];
            blue = pixels[offset + 2];
        }
        _drawingColor = new Vector4(red / 255f, green / 255f, blue / 255f, 1);
    }

    public static void Text(int x, int y, string text, int centerX = 0, int centerY = 0)
    {
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        try { TextImpl(x, y, text, centerX, centerY); }
        finally { if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.Draw2D, profileStart); }
    }

    private static void TextImpl(int x, int y, string text, int centerX, int centerY)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        ArgumentNullException.ThrowIfNull(text);
        var font = CurrentFont();
        if (centerX != 0) x -= StringWidth(text) / 2;
        if (centerY != 0) y -= font.Atlas.LineHeight / 2;
        var destination = GetBuffer(ResolveBufferHandle(0)).Frame?.Resource;
        Span<byte> characters = text.Length <= 1024 ? stackalloc byte[text.Length] : new byte[text.Length];
        var length = GdiFontAtlas.Ansi.GetBytes(text.AsSpan(), characters);
        var renderer = RequireRenderer();
        Span<D3D11TextureRegion> regions = stackalloc D3D11TextureRegion[128];
        var regionCount = 0;
        foreach (var code in characters[..length])
        {
            var glyph = font.Atlas.Glyphs[code];
            if (glyph.Width > 0)
            {
                regions[regionCount++] = new(glyph.X, glyph.Y, glyph.Width, font.Atlas.LineHeight,
                    x, y, glyph.Width, font.Atlas.LineHeight);
                if (regionCount == regions.Length)
                {
                    renderer.DrawTextureRegions(font.Texture, regions, _drawingColor, true, destination);
                    regionCount = 0;
                }
            }
            x = checked(x + glyph.Width);
        }
        if (regionCount != 0)
            renderer.DrawTextureRegions(font.Texture, regions[..regionCount], _drawingColor, true, destination);
    }

    private static int DefaultFont()
    {
        if (_defaultFont != 0) return _defaultFont;
        _defaultFont = LoadFont("courier", 12);
        if (_defaultFont == 0) throw new InvalidOperationException("The default GDI font could not be loaded.");
        return _defaultFont;
    }
    private static FontState CurrentFont()
    {
        if (_currentFont == 0) _currentFont = DefaultFont();
        return Fonts[_currentFont];
    }
    private static void CloseFonts()
    {
        foreach (var font in Fonts.Values) font.Texture.Dispose();
        Fonts.Clear();
        _currentFont = _defaultFont = 0;
        _drawingColor = Vector4.One;
    }
    private sealed record FontState(GdiFontAtlas Atlas, D3D11Texture Texture);
}
