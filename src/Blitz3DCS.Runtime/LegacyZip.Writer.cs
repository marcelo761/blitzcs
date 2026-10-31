using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BlitzCS.LegacyRuntime;

namespace Blitz3DCS.Runtime;

public static partial class LegacyZip
{
    private static readonly Dictionary<int, WriterState> Writers = [];

    private sealed class WriterEntry(string name, byte[] data, int method, int level, int strategy,
        DateTimeOffset modified, string comment, byte[] localExtra, byte[] centralExtra, uint externalAttributes)
    {
        public string Name { get; } = name;
        public byte[] Data { get; } = data;
        public int Method { get; } = method;
        public int Level { get; } = level;
        public int Strategy { get; } = strategy;
        public DateTimeOffset Modified { get; } = modified;
        public string Comment { get; } = comment;
        public byte[] LocalExtra { get; } = localExtra;
        public byte[] CentralExtra { get; } = centralExtra;
        public uint ExternalAttributes { get; } = externalAttributes;
    }

    private sealed class PendingWriterEntry(
        string name, int method, int level, int strategy, DateTimeOffset modified,
        string comment, byte[] localExtra, byte[] centralExtra, uint attributes)
    {
        public string Name { get; } = name;
        public int Method { get; } = method;
        public int Level { get; } = level;
        public int Strategy { get; } = strategy;
        public DateTimeOffset Modified { get; } = modified;
        public string Comment { get; } = comment;
        public byte[] LocalExtra { get; } = localExtra;
        public byte[] CentralExtra { get; } = centralExtra;
        public uint Attributes { get; } = attributes;
        public MemoryStream Content { get; } = new();
    }

    private sealed class WriterState(string path, byte[] prefix, List<WriterEntry> entries)
    {
        public string Path { get; } = path;
        public byte[] Prefix { get; } = prefix;
        public List<WriterEntry> Entries { get; } = entries;
        public PendingWriterEntry? Current { get; set; }
    }

    /// <summary>Opens a ZIP writer: 0 creates, 1 appends a new archive after existing bytes, 2 adds entries to an existing archive.</summary>
    public static int ZlibWapi_ZipOpen(string fileName, int appendMode)
    {
        if (appendMode is < 0 or > 2) return 0;
        try
        {
            var path = ResolveOutputPath(fileName);
            if (path is null) return 0;
            var prefix = Array.Empty<byte>();
            var entries = new List<WriterEntry>();
            if (appendMode == 1 && File.Exists(path))
                prefix = File.ReadAllBytes(path);
            else if (appendMode == 2 && File.Exists(path) && new FileInfo(path).Length != 0)
                entries = ReadEntriesForAppend(path);
            else if (appendMode != 1)
                File.WriteAllBytes(path, []);

            lock (Gate)
            {
                var handle = NextZipHandle();
                Writers.Add(handle, new WriterState(path, prefix, entries));
                return handle;
            }
        }
        catch (Exception e) when (IsZipFailure(e) || e is OverflowException) { return 0; }
    }

    public static int ZlibWapi_ZipOpenNewFileInZip(int zipHandle, string fileName, int fileInfoBank,
        int extraFieldLocalBank, int extraFieldLocalSize, int extraFieldGlobalBank, int extraFieldGlobalSize,
        string comment, int method, int level) =>
        OpenWriterEntry(zipHandle, fileName, fileInfoBank, extraFieldLocalBank, extraFieldLocalSize,
            extraFieldGlobalBank, extraFieldGlobalSize, comment, method, level, strategy: 0);

    public static int ZlibWapi_ZipOpenNewFileInZip3(int zipHandle, string fileName, int fileInfoBank,
        int extraFieldLocalBank, int extraFieldLocalSize, int extraFieldGlobalBank, int extraFieldGlobalSize,
        string comment, int method, int level, int raw, int windowBits, int memoryLevel, int strategy,
        string password, int crcForCrypting)
    {
        if (!string.IsNullOrEmpty(password)) return ParamError; // ZipArchive writer does not implement PKWARE encryption.
        if (raw != 0 || windowBits != -15 || memoryLevel != 8 || strategy is < 0 or > 4) return ParamError;
        return OpenWriterEntry(zipHandle, fileName, fileInfoBank, extraFieldLocalBank, extraFieldLocalSize,
            extraFieldGlobalBank, extraFieldGlobalSize, comment, method, level, strategy);
    }

    public static int ZlibWapi_ZipWriteFileInZip(int zipHandle, int bank, int fileLength)
    {
        lock (Gate)
        {
            if (!Writers.TryGetValue(zipHandle, out var writer) || writer.Current is null || fileLength < 0)
                return ParamError;
            try
            {
                var bytes = LegacyBankStreams.ReadRange(bank, 0, fileLength);
                if (bytes.Length != fileLength) return ParamError;
                writer.Current.Content.Write(bytes);
                return Ok;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
            { return ParamError; }
        }
    }

    public static int ZlibWapi_ZipCloseFileInZip(int zipHandle)
    {
        lock (Gate)
        {
            if (!Writers.TryGetValue(zipHandle, out var writer) || writer.Current is null) return ParamError;
            try
            {
                var entry = writer.Current;
                writer.Entries.Add(new WriterEntry(entry.Name, entry.Content.ToArray(), entry.Method, entry.Level,
                    entry.Strategy, entry.Modified, entry.Comment, entry.LocalExtra, entry.CentralExtra, entry.Attributes));
                entry.Content.Dispose();
                writer.Current = null;
                return Ok;
            }
            catch (OutOfMemoryException) { return DataError; }
        }
    }

    public static int ZlibWapi_ZipClose(int zipHandle, string globalComment)
    {
        lock (Gate)
        {
            if (!Writers.TryGetValue(zipHandle, out var writer) || writer.Current is not null) return ParamError;
            var comment = LegacyEncoding.GetBytes(globalComment ?? string.Empty);
            if (comment.Length > ushort.MaxValue) return ParamError;
            try
            {
                WriteZipArchive(writer, comment);
                Writers.Remove(zipHandle);
                return Ok;
            }
            catch (Exception e) when (IsZipFailure(e) || e is ArgumentException or OverflowException or OutOfMemoryException)
            {
                Writers.Remove(zipHandle);
                return DataError;
            }
        }
    }

    private static int OpenWriterEntry(int zipHandle, string fileName, int fileInfoBank,
        int localBank, int localSize, int globalBank, int globalSize, string comment, int method, int level, int strategy)
    {
        if (string.IsNullOrEmpty(fileName) || method is not (0 or 8) || level is < -1 or > 9 || strategy is < 0 or > 4)
            return ParamError;
        lock (Gate)
        {
            if (!Writers.TryGetValue(zipHandle, out var writer) || writer.Current is not null) return ParamError;
            try
            {
                var modified = ReadZipDate(fileInfoBank);
                var localExtra = ReadOptionalBank(localBank, localSize);
                var centralExtra = ReadOptionalBank(globalBank, globalSize);
                if (Encoding.UTF8.GetByteCount(fileName) > ushort.MaxValue || Encoding.UTF8.GetByteCount(comment ?? string.Empty) > ushort.MaxValue ||
                    localExtra.Length > ushort.MaxValue || centralExtra.Length > ushort.MaxValue) return ParamError;
                var attributes = fileName.EndsWith('/') ? 0x10u : 0x20u;
                writer.Current = new PendingWriterEntry(fileName.Replace('\\', '/'), method, level, strategy,
                    modified, comment ?? string.Empty, localExtra, centralExtra, attributes);
                return Ok;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
            { return ParamError; }
        }
    }

    private static DateTimeOffset ReadZipDate(int bank)
    {
        if (LegacyBanks.BankSize(bank) < 24) throw new ArgumentException("ZIP file info bank must contain six date integers.");
        var seconds = LegacyBanks.PeekInt(bank, 0);
        var minutes = LegacyBanks.PeekInt(bank, 4);
        var hours = LegacyBanks.PeekInt(bank, 8);
        var day = LegacyBanks.PeekInt(bank, 12);
        var month = LegacyBanks.PeekInt(bank, 16) + 1;
        var year = LegacyBanks.PeekInt(bank, 20);
        if (year < 1980) year += 1980;
        year = Math.Clamp(year, 1980, 2107);
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));
        return new DateTimeOffset(year, month, day, Math.Clamp(hours, 0, 23), Math.Clamp(minutes, 0, 59),
            Math.Clamp(seconds, 0, 59), TimeSpan.Zero);
    }

    private static byte[] ReadOptionalBank(int bank, int size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size == 0) return [];
        return LegacyBankStreams.ReadRange(bank, 0, size) is { Length: var length } bytes && length == size
            ? bytes : throw new ArgumentException("ZIP extra field bank is smaller than its declared size.");
    }

    private static List<WriterEntry> ReadEntriesForAppend(string path)
    {
        var result = new List<WriterEntry>();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var methods = GetCompressionMethodForArchive(path, archive.Entries.Count);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var source = archive.Entries[i];
            if (source.IsEncrypted || methods[i] is not (0 or 8)) throw new InvalidDataException("Cannot append an encrypted or unsupported ZIP entry.");
            using var entryStream = source.Open();
            using var data = new MemoryStream();
            entryStream.CopyTo(data);
            result.Add(new WriterEntry(source.FullName, data.ToArray(), methods[i], -1, 0,
                source.LastWriteTime, source.Comment ?? string.Empty, [], [], unchecked((uint)source.ExternalAttributes)));
        }
        return result;
    }

    private static void WriteZipArchive(WriterState writer, byte[] globalComment)
    {
        using var output = new FileStream(writer.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        output.Write(writer.Prefix);
        var central = new MemoryStream();
        foreach (var entry in writer.Entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var comment = Encoding.UTF8.GetBytes(entry.Comment);
            if (name.Length > ushort.MaxValue || comment.Length > ushort.MaxValue || entry.LocalExtra.Length > ushort.MaxValue || entry.CentralExtra.Length > ushort.MaxValue)
                throw new InvalidDataException("ZIP metadata exceeds the classic ZIP field size.");
            var compressed = CompressEntry(entry);
            if (entry.Data.LongLength > uint.MaxValue || compressed.LongLength > uint.MaxValue || output.Position > uint.MaxValue)
                throw new InvalidDataException("ZIP64 output is not supported by this legacy API.");
            var crc = ComputeCrc(entry.Data);
            var (dosTime, dosDate) = ToDosTime(entry.Modified);
            var localOffset = checked((uint)output.Position);
            WriteUInt32(output, 0x04034b50);
            WriteUInt16(output, 20); WriteUInt16(output, 0x0800); WriteUInt16(output, (ushort)entry.Method);
            WriteUInt16(output, dosTime); WriteUInt16(output, dosDate); WriteUInt32(output, crc);
            WriteUInt32(output, checked((uint)compressed.Length)); WriteUInt32(output, checked((uint)entry.Data.Length));
            WriteUInt16(output, checked((ushort)name.Length)); WriteUInt16(output, checked((ushort)entry.LocalExtra.Length));
            output.Write(name); output.Write(entry.LocalExtra); output.Write(compressed);

            WriteUInt32(central, 0x02014b50);
            WriteUInt16(central, 20); WriteUInt16(central, 20); WriteUInt16(central, 0x0800); WriteUInt16(central, (ushort)entry.Method);
            WriteUInt16(central, dosTime); WriteUInt16(central, dosDate); WriteUInt32(central, crc);
            WriteUInt32(central, checked((uint)compressed.Length)); WriteUInt32(central, checked((uint)entry.Data.Length));
            WriteUInt16(central, checked((ushort)name.Length)); WriteUInt16(central, checked((ushort)entry.CentralExtra.Length));
            WriteUInt16(central, checked((ushort)comment.Length)); WriteUInt16(central, 0); WriteUInt16(central, 0);
            WriteUInt32(central, entry.ExternalAttributes); WriteUInt32(central, localOffset);
            central.Write(name); central.Write(entry.CentralExtra); central.Write(comment);
        }
        if (writer.Entries.Count > ushort.MaxValue || central.Length > uint.MaxValue || output.Position > uint.MaxValue)
            throw new InvalidDataException("ZIP64 output is not supported by this legacy API.");
        var centralOffset = checked((uint)output.Position);
        central.Position = 0; central.CopyTo(output);
        WriteUInt32(output, 0x06054b50); WriteUInt16(output, 0); WriteUInt16(output, 0);
        WriteUInt16(output, checked((ushort)writer.Entries.Count)); WriteUInt16(output, checked((ushort)writer.Entries.Count));
        WriteUInt32(output, checked((uint)central.Length)); WriteUInt32(output, centralOffset);
        WriteUInt16(output, checked((ushort)globalComment.Length)); output.Write(globalComment);
        output.Flush(flushToDisk: true);
    }

    private static byte[] CompressEntry(WriterEntry entry)
    {
        if (entry.Method == 0) return entry.Data;
        using var output = new MemoryStream();
        var options = new ZLibCompressionOptions
        {
            CompressionLevel = entry.Level,
            CompressionStrategy = entry.Strategy switch
            {
                0 => ZLibCompressionStrategy.Default,
                1 => ZLibCompressionStrategy.Filtered,
                2 => ZLibCompressionStrategy.HuffmanOnly,
                3 => ZLibCompressionStrategy.RunLengthEncoding,
                4 => ZLibCompressionStrategy.Fixed,
                _ => throw new ArgumentOutOfRangeException(nameof(entry.Strategy))
            }
        };
        using (var compressor = new DeflateStream(output, options, leaveOpen: true)) compressor.Write(entry.Data);
        return output.ToArray();
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        var value = 0xffffffffu;
        foreach (var item in bytes)
        {
            value ^= item;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
        }
        return ~value;
    }

    private static (ushort Time, ushort Date) ToDosTime(DateTimeOffset value)
    {
        // DOS timestamps have no timezone marker; preserve their wall-clock fields.
        var date = value.DateTime;
        var year = Math.Clamp(date.Year, 1980, 2107);
        return ((ushort)((date.Hour << 11) | (date.Minute << 5) | (date.Second / 2)),
            (ushort)(((year - 1980) << 9) | (date.Month << 5) | date.Day));
    }

    private static int[] GetCompressionMethodForArchive(string path, int expectedCount)
    {
        var bytes = File.ReadAllBytes(path);
        var start = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        var eocd = -1;
        for (var i = bytes.Length - 22; i >= start; i--)
            if (i >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP end record is missing.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10, 2));
        var cursor = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16, 4)));
        if (count != expectedCount) throw new InvalidDataException("ZIP entry count disagrees with its central directory.");
        var methods = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (cursor + 46 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)) != 0x02014b50)
                throw new InvalidDataException("ZIP central directory is malformed.");
            methods[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 10, 2));
            cursor = checked(cursor + 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 28, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 30, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 32, 2)));
        }
        return methods;
    }

    private static string? ResolveOutputPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var existing = LegacyFiles.Resolve(path, false);
        if (existing is not null) return existing;
        var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
        var parent = LegacyFiles.Resolve(Path.GetDirectoryName(full)!, false);
        return parent is null ? null : Path.Combine(parent, Path.GetFileName(full));
    }

    private static int NextZipHandle()
    {
        if (_nextHandle <= 0 || _nextHandle == int.MaxValue) throw new InvalidOperationException("ZIP handle space is exhausted.");
        return _nextHandle++;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes);
    }
    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes);
    }
}
