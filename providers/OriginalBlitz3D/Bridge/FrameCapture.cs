namespace BlitzCS.OriginalRuntime;

public static partial class Commands
{
    /// <summary>Reads actual pixels from the original runtime's front buffer before shutdown.</summary>
    public static void CaptureBitmap(string path)
    {
        if (active is null) throw new InvalidOperationException("No active original runtime.");
        var width = GraphicsWidth(); var height = GraphicsHeight();
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidOperationException("No supported graphics surface to capture.");
        var buffer = FrontBuffer();
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        var bytes = checked(width * height * 4);
        writer.Write((ushort)0x4d42); writer.Write(54 + bytes); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(bytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        LockBuffer(buffer);
        try
        {
            for (var y = height - 1; y >= 0; y--)
                for (var x = 0; x < width; x++) writer.Write(ReadPixelFast(x, y, buffer));
        }
        finally { UnlockBuffer(buffer); }
    }
}
