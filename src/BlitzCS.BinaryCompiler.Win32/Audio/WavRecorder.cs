using System.Buffers.Binary;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// Records <see cref="WaveOutWrite"/>s into a RIFF/WAVE file. The file is valid after every append (sizes are rewritten each time), so a crashed process still leaves a usable file.
/// A RIFF file has one format: when a write arrives in another format a new file named <c>name.1.wav</c>, <c>name.2.wav</c> ... is started and <paramref name="diagnostic"/> says so.
/// </summary>
public sealed class WavRecorder : IDisposable
{
    private readonly string basePath;
    private readonly Action<string>? diagnostic;
    private readonly object gate = new();
    private FileStream? file;
    private WaveOutFormat? format;
    private long dataBytes;
    private int fileIndex;
    private int headerLength;

    public WavRecorder(string path, Action<string>? diagnostic = null)
    {
        basePath = path;
        this.diagnostic = diagnostic;
    }

    /// <summary>Paths of the files written so far (the first one is the configured path).</summary>
    public List<string> Files { get; } = [];

    public void Append(WaveOutWrite write)
    {
        lock (gate)
        {
            if (file is null || !SameFormat(format!, write.Format))
            {
                var next = Start(write.Format);
                if (format is not null) diagnostic?.Invoke($"WaveOut recorder: format changed, recording continues in {next}");
            }
            var pos = headerLength + dataBytes;
            file!.Position = pos;
            file.Write(write.Data);
            dataBytes += write.Data.Length;
            if ((dataBytes & 1) != 0) file.WriteByte(0);
            WriteSizes();
            file.Flush();
        }
    }

    private static bool SameFormat(WaveOutFormat a, WaveOutFormat b) => a.Raw.AsSpan().SequenceEqual(b.Raw);

    private string Start(WaveOutFormat f)
    {
        file?.Dispose();
        var path = fileIndex == 0 ? basePath : Path.Combine(Path.GetDirectoryName(basePath) ?? "", Path.GetFileNameWithoutExtension(basePath) + "." + fileIndex + Path.GetExtension(basePath));
        fileIndex++;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Files.Add(path);
        format = f;
        dataBytes = 0;
        var fmt = f.Tag == WaveOutConst.FormatPcm ? f.Raw.AsSpan(0, 16).ToArray() : f.Raw;
        var head = new byte[12 + 8 + fmt.Length + (fmt.Length & 1) + 8];
        "RIFF"u8.CopyTo(head); "WAVE"u8.CopyTo(head.AsSpan(8)); "fmt "u8.CopyTo(head.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), (uint)fmt.Length);
        fmt.CopyTo(head, 20);
        "data"u8.CopyTo(head.AsSpan(head.Length - 8));
        headerLength = head.Length;
        file.Write(head);
        WriteSizes();
        return path;
    }

    private void WriteSizes()
    {
        var pad = (int)(dataBytes & 1);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(headerLength - 8 + dataBytes + pad));
        file!.Position = 4; file.Write(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)dataBytes);
        file.Position = headerLength - 4; file.Write(b);
    }

    public void Dispose()
    {
        lock (gate) { file?.Dispose(); file = null; }
    }
}
