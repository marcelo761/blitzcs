namespace Blitz3DCS.Engine.Windows;

/// <summary>
/// Bounded LRU of decoded (and optionally colour-keyed) RGBA8 pixel buffers shared by repeated loads of one file.
/// Buffers are immutable once published; callers only upload them, so texture state stays per texture handle.
/// </summary>
internal static class TextureDecodeCache
{
    private const long MaxBytes = 96L * 1024 * 1024;
    private const int MaxEntryBytes = 16 * 1024 * 1024;

    private readonly record struct Key(string Path, long Ticks, long Length, bool Masked);
    private readonly record struct Entry(Key Key, int Width, int Height, byte[] Pixels);

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Map = [];
    private static readonly LinkedList<Entry> Lru = new();
    private static long _bytes;

    internal static bool TryGet(string fullPath, long ticks, long length, bool masked,
        out int width, out int height, out byte[] pixels)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(new Key(fullPath, ticks, length, masked), out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                width = node.Value.Width;
                height = node.Value.Height;
                pixels = node.Value.Pixels;
                return true;
            }
        }
        width = height = 0;
        pixels = [];
        return false;
    }

    internal static void Add(string fullPath, long ticks, long length, bool masked,
        int width, int height, byte[] pixels)
    {
        if (pixels.Length > MaxEntryBytes) return;
        var key = new Key(fullPath, ticks, length, masked);
        lock (Gate)
        {
            if (Map.ContainsKey(key)) return;
            Map[key] = Lru.AddFirst(new Entry(key, width, height, pixels));
            _bytes += pixels.Length;
            while (_bytes > MaxBytes && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                Map.Remove(last.Value.Key);
                _bytes -= last.Value.Pixels.Length;
            }
        }
    }
}
