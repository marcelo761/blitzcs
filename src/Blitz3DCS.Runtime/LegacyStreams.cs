using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlitzCS.LegacyRuntime;

namespace Blitz3DCS.Runtime;

/// <summary>Integer-owned legacy file streams with little-endian values and CP1252 strings.</summary>
public static class LegacyStreams
{
    private static readonly object Gate = new();
    private sealed class StreamEntry(Stream stream, Socket? socket = null)
    {
        public Stream Stream { get; } = stream;
        public Socket? Socket { get; } = socket;
        public bool RemoteEof { get; set; }
        public bool ReadError { get; set; }
    }
    private static readonly Dictionary<int, StreamEntry> Files = [];
    private static readonly Dictionary<int, IEnumerator<string>> Directories = [];
    private static readonly Encoding Ansi;
    private static int _nextHandle = 16384;

    static LegacyStreams()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate)
            {
                foreach (var stream in Files.Values) stream.Stream.Dispose();
                foreach (var directory in Directories.Values) directory.Dispose();
                Files.Clear();
                Directories.Clear();
            }
        };
    }

    public static int ReadFile(string path) => Open(path, FileMode.Open, FileAccess.Read);
    public static int WriteFile(string path) => Open(path, FileMode.Create, FileAccess.Write);
    public static int OpenFile(string path) => Open(path, FileMode.Open, FileAccess.ReadWrite);

    public static int OpenTCPStream(string host, int port, int localPort = 0)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (localPort != 0) socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            socket.Connect(host, port);
            var stream = new NetworkStream(socket, ownsSocket: true) { ReadTimeout = 10_000 };
            lock (Gate)
            {
                try { var handle = NextHandle(); Files.Add(handle, new StreamEntry(stream, socket)); return handle; }
                catch { stream.Dispose(); throw; }
            }
        }
        catch (Exception error) when (error is SocketException or IOException or ArgumentException or InvalidOperationException)
        {
            socket?.Dispose();
            return 0;
        }
    }

    public static void CloseTCPStream(int stream)
    {
        lock (Gate)
        {
            var entry = GetEntry(stream);
            if (entry.Socket is null) throw new ArgumentException("TCP stream does not exist.", nameof(stream));
            Files.Remove(stream);
            entry.Stream.Dispose();
        }
    }

    private static int Open(string path, FileMode mode, FileAccess access)
    {
        try
        {
            var resolved = mode == FileMode.Create ? OutputPath(path) : LegacyFiles.Resolve(path, false);
            if (resolved is null) return 0;
            var stream = new FileStream(resolved, mode, access, FileShare.ReadWrite);
            lock (Gate)
            {
                try { var handle = NextHandle(); Files.Add(handle, new StreamEntry(stream)); return handle; }
                catch { stream.Dispose(); throw; }
            }
        }
        catch (Exception error) when (IsPathFailure(error)) { return 0; }
    }

    public static void CloseFile(int file)
    {
        lock (Gate)
        {
            var entry = GetEntry(file);
            if (entry.Socket is not null) throw new ArgumentException("File does not exist.", nameof(file));
            Files.Remove(file);
            entry.Stream.Dispose();
        }
    }

    public static int Eof(int file)
    {
        lock (Gate)
        {
            var entry = GetEntry(file);
            if (entry.Socket is { } socket)
            {
                if (entry.ReadError) return -1;
                if (entry.RemoteEof) return 1;
                try
                {
                    if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) { entry.RemoteEof = true; return 1; }
                    return 0;
                }
                catch (SocketException) { entry.ReadError = true; return -1; }
            }
            var stream = entry.Stream;
            return !stream.CanRead || stream.Position >= stream.Length ? 1 : 0;
        }
    }
    public static int ReadAvail(int file)
    {
        lock (Gate)
        {
            var entry = GetEntry(file);
            if (entry.Socket is { } socket)
            {
                try { return entry.ReadError || entry.RemoteEof ? 0 : socket.Available; }
                catch (SocketException) { entry.ReadError = true; return 0; }
            }
            var stream = entry.Stream;
            return stream.CanRead ? checked((int)Math.Max(0, stream.Length - stream.Position)) : 0;
        }
    }
    public static int FilePos(int file) { lock (Gate) { var stream = GetFile(file); if (!stream.CanSeek) throw new NotSupportedException("TCP streams cannot be positioned."); return checked((int)stream.Position); } }
    public static int SeekFile(int file, int position)
    {
        lock (Gate)
        {
            var stream = GetFile(file);
            if (!stream.CanSeek) throw new NotSupportedException("TCP streams cannot be positioned.");
            if (position < 0) return -1;
            return checked((int)stream.Seek(position, SeekOrigin.Begin));
        }
    }

    public static int ReadByte(int file) { lock (Gate) { Span<byte> data = stackalloc byte[1]; ReadValue(GetFile(file), data); return data[0]; } }
    public static int ReadShort(int file) { lock (Gate) { Span<byte> data = stackalloc byte[2]; ReadValue(GetFile(file), data); return BinaryPrimitives.ReadUInt16LittleEndian(data); } }
    public static int ReadInt(int file) { lock (Gate) return ReadIntCore(GetFile(file)); }
    public static float ReadFloat(int file) => BitConverter.Int32BitsToSingle(ReadInt(file));
    public static void WriteByte(int file, int value) { lock (Gate) GetFile(file).WriteByte(unchecked((byte)value)); }
    public static void WriteShort(int file, int value) { lock (Gate) { Span<byte> data = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(data, unchecked((ushort)value)); GetFile(file).Write(data); } }
    public static void WriteInt(int file, int value) { lock (Gate) WriteIntCore(GetFile(file), value); }
    public static void WriteFloat(int file, float value) => WriteInt(file, BitConverter.SingleToInt32Bits(value));

    public static string ReadString(int file)
    {
        lock (Gate)
        {
            var stream = GetFile(file);
            var length = ReadIntCore(stream);
            if (length < 0 || (stream.CanSeek && length > stream.Length - stream.Position))
                throw new InvalidDataException("The file contains an invalid length-prefixed string.");
            var bytes = new byte[length];
            if (ReadValue(stream, bytes) != bytes.Length)
                throw new InvalidDataException("The stream ended inside a length-prefixed string.");
            return Ansi.GetString(bytes);
        }
    }
    public static void WriteString(int file, string value)
    {
        var bytes = Ansi.GetBytes(value);
        lock (Gate) { var stream = GetFile(file); WriteIntCore(stream, bytes.Length); stream.Write(bytes); }
    }
    public static string ReadLine(int file)
    {
        lock (Gate)
        {
            var stream = GetFile(file);
            var bytes = new List<byte>();
            if (!stream.CanRead) return "";
            for (var value = stream.ReadByte(); value >= 0 && value != '\n'; value = stream.ReadByte())
                if (value != '\r') bytes.Add((byte)value);
            return Ansi.GetString(CollectionsMarshal.AsSpan(bytes));
        }
    }
    public static void WriteLine(int file, string value)
    {
        var bytes = Ansi.GetBytes(value);
        lock (Gate) { var stream = GetFile(file); stream.Write(bytes); stream.Write("\r\n"u8); }
    }

    /// <summary>Reads up to count bytes from a stream into a bounded bank range, returning bytes read.</summary>
    public static int ReadBytes(int bank, int file, int offset, int count)
    {
        lock (Gate)
        {
            var stream = GetFile(file);
            if (!stream.CanRead) return 0;
            var length = LegacyBankStreams.TransferLength(bank, offset, count);
            if (length == 0) return 0;
            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var chunk = stream.Read(bytes, read, bytes.Length - read);
                if (chunk == 0) break;
                read += chunk;
            }
            return LegacyBankStreams.WriteRange(bank, offset, bytes, read);
        }
    }

    /// <summary>Writes up to count bytes from a bounded bank range and returns bytes written.</summary>
    public static int WriteBytes(int bank, int file, int offset, int count)
    {
        lock (Gate)
        {
            var stream = GetFile(file);
            if (!stream.CanWrite) return 0;
            var bytes = LegacyBankStreams.ReadRange(bank, offset, count);
            if (bytes.Length == 0) return 0;
            stream.Write(bytes);
            return bytes.Length;
        }
    }

    public static int ReadDir(string path)
    {
        try
        {
            var resolved = LegacyFiles.Resolve(path, false);
            if (resolved is null || !Directory.Exists(resolved)) return 0;
            // FindFirstFile enumeration includes dot entries; retain them on all hosts.
            var names = new[] { ".", ".." }.Concat(Directory.EnumerateFileSystemEntries(resolved).Select(Path.GetFileName)).Select(name => name!);
            var enumerator = names.GetEnumerator();
            lock (Gate) { var handle = NextHandle(); Directories.Add(handle, enumerator); return handle; }
        }
        catch (Exception error) when (IsPathFailure(error)) { return 0; }
    }
    public static string NextFile(int directory)
    {
        lock (Gate)
        {
            if (!Directories.TryGetValue(directory, out var enumerator)) throw new ArgumentException("Directory does not exist.", nameof(directory));
            return enumerator.MoveNext() ? enumerator.Current : "";
        }
    }
    public static void CloseDir(int directory)
    {
        lock (Gate)
        {
            if (!Directories.Remove(directory, out var enumerator)) throw new ArgumentException("Directory does not exist.", nameof(directory));
            enumerator.Dispose();
        }
    }
    public static void CreateDir(string path)
    {
        try { var output = OutputPath(path); if (output is not null) Directory.CreateDirectory(output); }
        catch (Exception error) when (IsPathFailure(error)) { }
    }
    public static void DeleteFile(string path)
    {
        try { var resolved = LegacyFiles.Resolve(path, false); if (resolved is not null) File.Delete(resolved); }
        catch (Exception error) when (IsPathFailure(error)) { }
    }
    public static void DeleteDir(string path)
    {
        try { var resolved = LegacyFiles.Resolve(path, false); if (resolved is not null) Directory.Delete(resolved, false); }
        catch (Exception error) when (IsPathFailure(error)) { }
    }

    private static string? OutputPath(string path)
    {
        var zero = path.IndexOf('\0');
        if (zero >= 0) path = path[..zero];
        if (path.Length == 0) return null;
        var existing = LegacyFiles.Resolve(path, false);
        if (existing is not null) return existing;
        var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
        var parent = LegacyFiles.Resolve(Path.GetDirectoryName(full)!, false);
        return parent is null ? null : Path.Combine(parent, Path.GetFileName(full));
    }
    private static StreamEntry GetEntry(int handle) => Files.TryGetValue(handle, out var stream)
        ? stream : throw new ArgumentException("File does not exist.", nameof(handle));
    private static Stream GetFile(int handle) => GetEntry(handle).Stream;
    private static int NextHandle() => _nextHandle == int.MaxValue
        ? throw new InvalidOperationException("File handle space is exhausted.") : _nextHandle++;
    private static int ReadValue(Stream stream, Span<byte> destination)
    {
        destination.Clear();
        if (!stream.CanRead) return 0;
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0) break;
            read += count;
        }
        return read;
    }
    private static int ReadIntCore(Stream stream) { Span<byte> bytes = stackalloc byte[4]; ReadValue(stream, bytes); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
    private static void WriteIntCore(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static bool IsPathFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
