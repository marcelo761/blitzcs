using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Blitz3DCS.Engine.Windows;

/// <summary>Rasterizes the legacy GDI font once; drawing thereafter samples a cached GPU atlas.</summary>
internal sealed record GdiFontAtlas(int Width, int Height, int LineHeight, int MaximumCharacterWidth,
    byte[] Pixels, GdiFontAtlas.Glyph[] Glyphs)
{
    internal readonly record struct Glyph(int X, int Y, int Width);
    internal static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    internal static GdiFontAtlas Load(string name, int height, bool bold, bool italic, bool underline, int canvasWidth)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GDI fonts require Windows.");
        ArgumentNullException.ThrowIfNull(name);
        nint dc = 0, font = 0, bitmap = 0, oldFont = 0, oldBitmap = 0;
        string? registeredFile = null;
        try
        {
            var family = name;
            if (Path.HasExtension(name))
            {
                var path = Path.GetFullPath(name.Replace('\\', Path.DirectorySeparatorChar));
                if (Native.AddFontResourceExW(path, 0x10, 0) > 0) registeredFile = path;
                family = Path.GetFileNameWithoutExtension(path);
            }
            // Per-font quality replaces the original global font-smoothing toggle.
            font = Native.CreateFontW(height, 0, 0, 0, bold ? 700 : 400,
                italic ? 1u : 0u, underline ? 1u : 0u, 0, 0, 0, 0, 3, 0, family);
            if (font == 0) throw new Win32Exception();
            dc = Native.CreateCompatibleDC(0);
            if (dc == 0) throw new Win32Exception();
            oldFont = Native.SelectObject(dc, font);
            if (!Native.GetTextMetricsA(dc, out var metrics) || metrics.Height <= 0)
                throw new Win32Exception();

            var widths = new int[256];
            var padding = new int[256];
            var characters = new string[256];
            var first = (int)metrics.FirstCharacter;
            var last = (int)metrics.LastCharacter;
            var maximumCell = 1;
            for (var code = first; code <= last; code++)
            {
                var text = Ansi.GetString([(byte)code]);
                characters[code] = text;
                if (!Native.GetTextExtentPoint32W(dc, text, 1, out var size)) throw new Win32Exception();
                var width = size.Width;
                if (Native.GetCharABCWidthsW(dc, text[0], text[0], out var abc))
                {
                    if (abc.A < 0) { padding[code] = -abc.A; width += -abc.A; }
                    if (abc.C < 0) width += -abc.C;
                }
                widths[code] = Math.Max(0, width);
                maximumCell = Math.Max(maximumCell, widths[code]);
            }

            var widthPixels = Math.Max(Math.Max(1, canvasWidth), maximumCell);
            var glyphs = new Glyph[256];
            var x = 0;
            var y = 0;
            for (var code = first; code <= last; code++)
            {
                if (x != 0 && (long)x + widths[code] > widthPixels) { x = 0; y = checked(y + metrics.Height); }
                glyphs[code] = new(x, y, widths[code]);
                x = checked(x + widths[code]);
            }
            var heightPixels = checked(y + metrics.Height);
            var pixels = new byte[checked(widthPixels * heightPixels * 4)];
            var info = new BitmapInfoHeader
            {
                Size = 40, Width = widthPixels, Height = -heightPixels, Planes = 1,
                BitCount = 32, SizeImage = checked((uint)pixels.Length)
            };
            bitmap = Native.CreateDIBSection(dc, in info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception();
            oldBitmap = Native.SelectObject(dc, bitmap);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Native.SetBkColor(dc, 0);
            Native.SetTextColor(dc, 0x00ffffff);
            for (var code = first; code <= last; code++)
            {
                var glyph = glyphs[code];
                var rect = new Rect(glyph.X, glyph.Y, glyph.X + glyph.Width, glyph.Y + metrics.Height);
                if (!Native.ExtTextOutW(dc, glyph.X + padding[code], glyph.Y, 6, in rect, characters[code], 1, 0))
                    throw new Win32Exception();
            }
            if (!Native.GdiFlush()) throw new Win32Exception();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                var coverage = pixels[offset + 2];
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                pixels[offset + 3] = coverage;
            }
            var fallback = glyphs[metrics.DefaultCharacter];
            for (var code = 0; code < first; code++) glyphs[code] = fallback;
            for (var code = last + 1; code < 256; code++) glyphs[code] = fallback;
            return new(widthPixels, heightPixels, metrics.Height, metrics.MaximumCharacterWidth, pixels, glyphs);
        }
        finally
        {
            if (dc != 0 && oldBitmap != 0) Native.SelectObject(dc, oldBitmap);
            if (dc != 0 && oldFont != 0) Native.SelectObject(dc, oldFont);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            if (font != 0) Native.DeleteObject(font);
            if (dc != 0) Native.DeleteDC(dc);
            if (registeredFile is not null) Native.RemoveFontResourceExW(registeredFile, 0x10, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetrics
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AverageCharacterWidth,
            MaximumCharacterWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public byte FirstCharacter, LastCharacter, DefaultCharacter, BreakCharacter,
            Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Abc { public int A; public uint B; public int C; }

    private static class Native
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint characterSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string name);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll", ExactSpelling = true)] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTextMetricsA(nint dc, out TextMetrics metrics);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTextExtentPoint32W(nint dc, string text, int length, out Size size);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCharABCWidthsW(nint dc, uint first, uint last, out Abc abc);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, in BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll", ExactSpelling = true)] internal static extern uint SetBkColor(nint dc, uint color);
        [DllImport("gdi32.dll", ExactSpelling = true)] internal static extern uint SetTextColor(nint dc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ExtTextOutW(nint dc, int x, int y, uint options, in Rect rect, string text, uint length, nint spacing);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int AddFontResourceExW(string name, uint flags, nint reserved);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveFontResourceExW(string name, uint flags, nint reserved);
    }
}
