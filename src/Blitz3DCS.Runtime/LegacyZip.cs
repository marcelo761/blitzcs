using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BlitzCS.LegacyRuntime;

namespace Blitz3DCS.Runtime;

/// <summary>Managed minizip-compatible read operations used by the legacy ZIP API.</summary>
/// <remarks>ZIP handles are opaque managed tokens and are independent of legacy bank handles.</remarks>
public static partial class LegacyZip
{
    private const int Ok = 0, DataError = -3, EndOfList = -100, ParamError = -102;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, ArchiveState> Archives = [];
    private static int _nextHandle = 0x40000000;
    private static readonly Encoding LegacyEncoding = CodePagesEncodingProvider.Instance.GetEncoding(1252,
        EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback)!;

    private sealed class ArchiveState(string path, FileStream file, ZipArchive archive)
    {
        public string Path { get; } = path;
        public FileStream File { get; } = file;
        public ZipArchive Archive { get; } = archive;
        public int Position { get; set; }
        public Stream? Current { get; set; }
        public Dictionary<ZipArchiveEntry, uint> Crcs { get; } = [];
        public int[]? Methods { get; set; }
    }

    /// <summary>Opens an existing ZIP archive; returns zero when it cannot be opened.</summary>
    public static int ZlibWapi_UnzOpen(string filePath)
    {
        try
        {
            var resolved = LegacyFiles.Resolve(filePath, false);
            if (resolved is null || !File.Exists(resolved)) return 0;
            var file = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            lock (Gate)
            {
                var handle = _nextHandle++;
                if (handle <= 0) { archive.Dispose(); file.Dispose(); return 0; }
                Archives.Add(handle, new ArchiveState(resolved, file, archive));
                return handle;
            }
        }
        catch (Exception e) when (IsZipFailure(e)) { return 0; }
    }

    public static int ZlibWapi_UnzClose(int zipHandle)
    {
        lock (Gate)
        {
            if (!Archives.Remove(zipHandle, out var state)) return ParamError;
            try { state.Current?.Dispose(); state.Archive.Dispose(); state.File.Dispose(); return Ok; }
            catch (Exception e) when (IsZipFailure(e)) { return DataError; }
        }
    }

    public static int ZlibWapi_UnzGetGlobalInfo(int zipHandle, int infoBank)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state)) return ParamError;
            try
            {
                if (LegacyBanks.BankSize(infoBank) < 8) return ParamError;
                WriteInt(infoBank, 0, state.Archive.Entries.Count);
                var comment = ReadArchiveComment(state);
                WriteInt(infoBank, 4, comment.Length);
                return Ok;
            }
            catch (Exception e) when (IsZipFailure(e) || e is KeyNotFoundException) { return ParamError; }
        }
    }

    public static int ZlibWapi_UnzGetGlobalComment(int zipHandle, int commentBank, int commentLength)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state) || commentLength < 0) return ParamError;
            try
            {
                var bytes = ReadArchiveComment(state);
                var count = Math.Min(commentLength, bytes.Length);
                WriteBank(commentBank, count, bytes.AsSpan(0, count), terminate: true);
                return count;
            }
            catch (Exception e) when (IsZipFailure(e) || e is KeyNotFoundException) { return ParamError; }
        }
    }

    public static int ZlibWapi_UnzGoToFirstFile(int zipHandle)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state)) return ParamError;
            if (state.Archive.Entries.Count == 0) return EndOfList;
            state.Position = 0; return Ok;
        }
    }

    public static int ZlibWapi_UnzGoToNextFile(int zipHandle)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state)) return ParamError;
            if (state.Position < 0 || state.Position + 1 >= state.Archive.Entries.Count) return EndOfList;
            state.Position++; return Ok;
        }
    }

    public static int ZlibWapi_UnzLocateFile(int zipHandle, string fileName, int caseSensitive)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state)) return ParamError;
            var comparison = caseSensitive != 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var target = fileName.Replace('\\', '/');
            for (var i = 0; i < state.Archive.Entries.Count; i++)
                if (string.Equals(state.Archive.Entries[i].FullName, target, comparison)) { state.Position = i; return Ok; }
            return EndOfList;
        }
    }

    public static int ZlibWapi_UnzGetCurrentFileInfo(int zipHandle, int fileInfoBank, int fileNameBank,
        int fileNameBufferSize, int extraFieldBank, int extraFieldBufferSize, int commentBank, int commentBufferSize)
    {
        lock (Gate)
        {
            if (!TryGetCurrent(zipHandle, out var state, out var entry)) return ParamError;
            if (fileNameBufferSize < 0 || extraFieldBufferSize < 0 || commentBufferSize < 0) return ParamError;
            try
            {
                if (LegacyBanks.BankSize(fileInfoBank) < 80) return ParamError;
                var name = LegacyEncoding.GetBytes(entry.FullName);
                var comment = LegacyEncoding.GetBytes(entry.Comment ?? string.Empty);
                var dt = entry.LastWriteTime;
                Span<byte> info = stackalloc byte[80];
                BinaryPrimitives.WriteInt32LittleEndian(info[8..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(info[12..], GetCompressionMethod(state)[state.Position]);
                BinaryPrimitives.WriteInt32LittleEndian(info[20..], unchecked((int)GetCrc(state, entry)));
                BinaryPrimitives.WriteInt32LittleEndian(info[24..], unchecked((int)entry.CompressedLength));
                BinaryPrimitives.WriteInt32LittleEndian(info[28..], unchecked((int)entry.Length));
                BinaryPrimitives.WriteInt32LittleEndian(info[32..], name.Length);
                BinaryPrimitives.WriteInt32LittleEndian(info[40..], comment.Length);
                BinaryPrimitives.WriteInt32LittleEndian(info[56..], dt.Second);
                BinaryPrimitives.WriteInt32LittleEndian(info[60..], dt.Minute);
                BinaryPrimitives.WriteInt32LittleEndian(info[64..], dt.Hour);
                BinaryPrimitives.WriteInt32LittleEndian(info[68..], dt.Day);
                BinaryPrimitives.WriteInt32LittleEndian(info[72..], dt.Month);
                BinaryPrimitives.WriteInt32LittleEndian(info[76..], dt.Year - 1980);
                var infoBytes = info.ToArray();
                LegacyBanks.WithBytes(fileInfoBank, 0, 80, bytes => { infoBytes.CopyTo(bytes); return 0; });
                if (fileNameBufferSize > 0) WriteBank(fileNameBank, fileNameBufferSize, name.AsSpan(0, Math.Min(name.Length, fileNameBufferSize)), true);
                if (commentBufferSize > 0) WriteBank(commentBank, commentBufferSize, comment.AsSpan(0, Math.Min(comment.Length, commentBufferSize)), true);
                if (extraFieldBufferSize > 0) WriteBank(extraFieldBank, extraFieldBufferSize, ReadOnlySpan<byte>.Empty, true);
                return Ok;
            }
            catch (Exception e) when (IsZipFailure(e) || e is KeyNotFoundException) { return ParamError; }
        }
    }

    public static int ZlibWapi_UnzOpenCurrentFile(int zipHandle)
    {
        lock (Gate)
        {
            if (!TryGetCurrent(zipHandle, out var state, out var entry) || state.Current is not null) return ParamError;
            try { state.Current = entry.Open(); return Ok; }
            catch (Exception e) when (IsZipFailure(e)) { return DataError; }
        }
    }

    public static int ZlibWapi_UnzOpenCurrentFilePassword(int zipHandle, string password) =>
        string.IsNullOrEmpty(password) ? ZlibWapi_UnzOpenCurrentFile(zipHandle) : ParamError;

    public static int ZlibWapi_UnzReadCurrentFile(int zipHandle, int bufferBank, int bufferLength)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state) || state.Current is null || bufferLength < 0) return ParamError;
            try
            {
                var count = Math.Min(bufferLength, LegacyBanks.BankSize(bufferBank));
                var bytes = new byte[count];
                var read = state.Current.Read(bytes, 0, count);
                if (read > 0) LegacyBanks.WithBytes(bufferBank, 0, read, target => { bytes.AsSpan(0, read).CopyTo(target); return 0; });
                return read;
            }
            catch (InvalidDataException) { return DataError; }
            catch (Exception e) when (IsZipFailure(e) || e is KeyNotFoundException) { return ParamError; }
        }
    }

    public static int ZlibWapi_UnzCloseCurrentFile(int zipHandle)
    {
        lock (Gate)
        {
            if (!Archives.TryGetValue(zipHandle, out var state) || state.Current is null) return ParamError;
            try { state.Current.Dispose(); state.Current = null; return Ok; }
            catch (InvalidDataException) { state.Current = null; return DataError; }
            catch (Exception e) when (IsZipFailure(e)) { state.Current = null; return DataError; }
        }
    }

    private static bool TryGetCurrent(int handle, out ArchiveState state, out ZipArchiveEntry entry)
    {
        if (Archives.TryGetValue(handle, out state!) && state.Position >= 0 && state.Position < state.Archive.Entries.Count)
        { entry = state.Archive.Entries[state.Position]; return true; }
        state = null!; entry = null!; return false;
    }

    private static byte[] ReadArchiveComment(ArchiveState state)
    {
        // ZipArchive does not expose the EOCD comment, so read it from the archive directory.
        var bytes = File.ReadAllBytes(state.Path);
        var start = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        for (var i = bytes.Length - 22; i >= start; i--)
        {
            if (i < 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) != 0x06054b50) continue;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 20, 2));
            return bytes.AsSpan(i + 22, Math.Min(length, bytes.Length - i - 22)).ToArray();
        }
        return [];
    }

    private static uint GetCrc(ArchiveState state, ZipArchiveEntry entry)
    {
        if (state.Crcs.TryGetValue(entry, out var crc)) return crc;
        using var stream = entry.Open();
        var buffer = new byte[8192];
        var value = 0xffffffffu;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++)
            {
                value ^= buffer[i];
                for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
            }
        crc = ~value; state.Crcs.Add(entry, crc); return crc;
    }

    private static int[] GetCompressionMethod(ArchiveState state)
    {
        if (state.Methods is not null) return state.Methods;
        var bytes = File.ReadAllBytes(state.Path);
        var start = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        var eocd = -1;
        for (var i = bytes.Length - 22; i >= start; i--)
            if (i >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP end record is missing.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10, 2));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16, 4));
        var methods = new int[count];
        var cursor = checked((int)offset);
        for (var i = 0; i < count; i++)
        {
            if (cursor < 0 || cursor + 46 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)) != 0x02014b50)
                throw new InvalidDataException("ZIP central directory is malformed.");
            methods[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 10, 2));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 32, 2));
            cursor = checked(cursor + 46 + nameLength + extraLength + commentLength);
        }
        state.Methods = methods;
        return methods;
    }

    private static void WriteInt(int bank, int offset, int value) => LegacyBanks.WithBytes(bank, offset, 4, bytes =>
    { BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return 0; });

    private static void WriteBank(int bank, int capacity, ReadOnlySpan<byte> value, bool terminate)
    {
        var data = value.ToArray();
        var count = Math.Min(capacity, data.Length);
        LegacyBanks.WithBytes(bank, 0, Math.Min(capacity, LegacyBanks.BankSize(bank)), bytes =>
        {
            var copied = Math.Min(count, bytes.Length);
            data.AsSpan(0, copied).CopyTo(bytes);
            if (terminate && copied < bytes.Length) bytes[copied] = 0;
            return 0;
        });
    }

    private static bool IsZipFailure(Exception e) => e is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or InvalidDataException or ObjectDisposedException;
}
