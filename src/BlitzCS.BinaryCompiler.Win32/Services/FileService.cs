using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

internal sealed class StdStream(int slot)
{
    public int Slot { get; } = slot; // 0 stdin, 1 stdout, 2 stderr
}

internal sealed class FileHandle(Stream stream, bool canRead, bool canWrite, string path) : IDisposable
{
    public Stream Stream { get; } = stream;
    public bool CanRead { get; } = canRead;
    public bool CanWrite { get; } = canWrite;
    public string Path { get; } = path;
    public void Dispose() => Stream.Dispose();
}

internal sealed class FindHandle(List<FileSystemInfo?> entries, List<string> names)
{
    public readonly List<FileSystemInfo?> Entries = entries; // null = "." / ".."
    public readonly List<string> Names = names;
    public int Next;
}

internal sealed class FileState : IDisposable
{
    private readonly Win32Context ctx;
    private readonly uint[] stdSlots = [3, 7, 11];
    private Stream?[] stdStreams = new Stream?[3];
    private readonly object gate = new();

    public FileState(Win32Context ctx)
    {
        this.ctx = ctx;
        for (var i = 0; i < 3; i++) ctx.SetHandle(stdSlots[i], new StdStream(i));
    }

    public uint StdHandle(uint nStd) => nStd switch { 0xFFFFFFF6 => stdSlots[0], 0xFFFFFFF5 => stdSlots[1], 0xFFFFFFF4 => stdSlots[2], _ => 0xFFFFFFFF };

    public bool SetStd(uint nStd, uint handle)
    {
        var slot = nStd switch { 0xFFFFFFF6 => 0, 0xFFFFFFF5 => 1, 0xFFFFFFF4 => 2, _ => -1 };
        if (slot < 0 || (ctx.GetHandle<FileHandle>(handle) is null && ctx.GetHandle<StdStream>(handle) is null)) return false;
        stdSlots[slot] = handle;
        return true;
    }

    public Stream HostStream(StdStream std)
    {
        lock (gate)
            return stdStreams[std.Slot] ??= std.Slot switch
            {
                0 => ctx.Options.Stdin ?? Stream.Null,
                1 => ctx.Options.Stdout ?? Console.OpenStandardOutput(),
                _ => ctx.Options.Stderr ?? Console.OpenStandardError(),
            };
    }

    public void Dispose() { }

    // ---- path translation ----

    /// <summary>Translates a Windows path into the sandbox. Returns the host path or a Win32 error.</summary>
    public (string? Host, uint Error) Translate(string windows)
    {
        var root = ctx.Options.SandboxRoot;
        if (root is null) return (null, Win32Error.AccessDenied);
        if (windows.Length == 0) return (null, Win32Error.PathNotFound);
        if (windows.StartsWith("\\\\") || windows.StartsWith("//")) return (null, Win32Error.BadPathname); // UNC and \\.\ device paths
        var path = windows;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path = path[2..];
        else if (path[0] is not ('\\' or '/')) path = ctx.Process.CurrentDirectory.TrimEnd('\\') + "\\" + path; // relative: resolved against the process's current directory
        var parts = new List<string>();
        foreach (var part in path.Split('\\', '/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; } // Windows clamps ".." at the drive root
            if (part.IndexOfAny(['<', '>', ':', '"', '|', '\0']) >= 0) return (null, 123); // ERROR_INVALID_NAME
            parts.Add(part);
        }
        var current = Path.GetFullPath(root);
        for (var i = 0; i < parts.Count; i++)
        {
            var next = Path.Combine(current, parts[i]);
            if (!Exists(next) && ctx.Options.CaseInsensitiveFileNames && Directory.Exists(current))
            {
                var match = Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).FirstOrDefault(n => string.Equals(n, parts[i], StringComparison.OrdinalIgnoreCase));
                if (match is not null) next = Path.Combine(current, match);
            }
            else if (!ctx.Options.CaseInsensitiveFileNames && HostIsCaseInsensitive && Exists(next) && Directory.Exists(current)
                     && !Directory.EnumerateFileSystemEntries(current).Any(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.Ordinal)))
            {
                // Exact-case lookups were requested but the host file system folds case (Windows, macOS, Wine): Exists() would accept "DIR" for "dir".
                // The name only matches in a different case, which a case-sensitive file system would report as missing.
                return (null, i == parts.Count - 1 ? Win32Error.FileNotFound : Win32Error.PathNotFound);
            }
            if (Exists(next) && (File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0) return (null, Win32Error.AccessDenied); // links are never followed
            current = next;
        }
        return (current, Win32Error.Success);
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>The default file systems of Windows and macOS (and therefore Wine) fold case; Linux file systems are case-sensitive.</summary>
    private static bool HostIsCaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
}

/// <summary>
/// KERNEL32 file I/O mapped to host files inside an explicit sandbox (<see cref="Win32Options.SandboxRoot"/>; null = every path API fails with
/// ERROR_ACCESS_DENIED). Path translation (Microsoft Learn, "Naming Files, Paths, and Namespaces"): drive letters are dropped, both separators accepted,
/// "." / ".." resolved with ".." clamped at the root, UNC and device paths rejected (ERROR_BAD_PATHNAME), names matched case-insensitively against existing
/// entries (option), symbolic links are never followed. Handles come from a table that never reuses a value, so CloseHandle twice is detected (ERROR_INVALID_HANDLE).
/// CreateFile dispositions follow the documented table (ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS semantics); share modes, security attributes, templates and
/// ordinary attribute flags are ignored (the host decides), FILE_FLAG_OVERLAPPED and FILE_FLAG_DELETE_ON_CLOSE fail with ERROR_NOT_SUPPORTED, overlapped
/// ReadFile/WriteFile fail with ERROR_NOT_SUPPORTED. Standard handles use the streams of <see cref="Win32Options"/> (stdout/stderr default to the host's).
/// GetFileSize's high part, SetFilePointer's high part and FindFirstFileA's WIN32_FIND_DATAA layout (320 bytes) follow the documentation.
/// </summary>
public sealed class FileService : Win32Service
{
    public override string Name => "Kernel32.File";
    private const uint InvalidHandle = 0xFFFFFFFF;

    private static uint Open(Win32Call c) => OpenNamed(c, c.Mem.ReadAnsiString(c[0], 1024));

    /// <summary>CreateFile with the file name already decoded (shared by CreateFileA and CreateFileW).</summary>
    internal static uint OpenNamed(Win32Call c, string name)
    {
        uint access = c[1], disposition = c[4], flags = c[5];
        if ((flags & (0x40000000 | 0x04000000)) != 0) return c.Ctx.Fail(Win32Error.NotSupported, InvalidHandle);
        if (disposition is < 1 or > 5) return c.Ctx.Fail(Win32Error.InvalidParameter, InvalidHandle);
        var (host, error) = c.Ctx.Files.Translate(name);
        if (host is null) return c.Ctx.Fail(error, InvalidHandle);
        var wantRead = (access & (0x80000000 | 1)) != 0;
        var wantWrite = (access & (0x40000000 | 2 | 4)) != 0;
        if (Directory.Exists(host)) return c.Ctx.Fail(Win32Error.AccessDenied, InvalidHandle);
        var existed = File.Exists(host);
        var parent = Path.GetDirectoryName(host);
        if (!existed && (parent is null || !Directory.Exists(parent))) return c.Ctx.Fail(Win32Error.PathNotFound, InvalidHandle);
        FileMode mode;
        switch (disposition)
        {
            case 1: if (existed) return c.Ctx.Fail(Win32Error.FileExists, InvalidHandle); mode = FileMode.CreateNew; break;
            case 2: mode = FileMode.Create; break;
            case 3: if (!existed) return c.Ctx.Fail(Win32Error.FileNotFound, InvalidHandle); mode = FileMode.Open; break;
            case 4: mode = FileMode.OpenOrCreate; break;
            default:
                if (!existed) return c.Ctx.Fail(Win32Error.FileNotFound, InvalidHandle);
                if (!wantWrite) return c.Ctx.Fail(Win32Error.InvalidParameter, InvalidHandle); // TRUNCATE_EXISTING requires GENERIC_WRITE
                mode = FileMode.Truncate; break;
        }
        var hostAccess = disposition is 1 or 2 or 4 || wantWrite ? FileAccess.ReadWrite : FileAccess.Read;
        try
        {
            var stream = new FileStream(host, mode, hostAccess, FileShare.ReadWrite | FileShare.Delete);
            var handle = c.Ctx.NewHandle(new FileHandle(stream, wantRead, wantWrite, host));
            c.Ctx.LastError = existed && disposition is 2 or 4 ? Win32Error.AlreadyExists : Win32Error.Success;
            return handle;
        }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied, InvalidHandle); }
        catch (FileNotFoundException) { return c.Ctx.Fail(Win32Error.FileNotFound, InvalidHandle); }
        catch (DirectoryNotFoundException) { return c.Ctx.Fail(Win32Error.PathNotFound, InvalidHandle); }
        catch (IOException) { return c.Ctx.Fail(32, InvalidHandle); } // ERROR_SHARING_VIOLATION
    }

    private static uint Read(Win32Call c)
    {
        uint handle = c[0], buffer = c[1], count = c[2], readPtr = c[3], overlapped = c[4];
        if (overlapped != 0) return c.Ctx.Fail(Win32Error.NotSupported);
        if (readPtr == 0 || (buffer == 0 && count != 0)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        Stream stream;
        if (c.Ctx.GetHandle<FileHandle>(handle) is { } f) { if (!f.CanRead) return c.Ctx.Fail(Win32Error.AccessDenied); stream = f.Stream; }
        else if (c.Ctx.GetHandle<StdStream>(handle) is { Slot: 0 } std) stream = c.Ctx.Files.HostStream(std);
        else return c.Ctx.Fail(c.Ctx.GetHandle<StdStream>(handle) is null ? Win32Error.InvalidHandle : Win32Error.AccessDenied);
        var chunk = new byte[(int)Math.Min(count, 65536u)];
        uint total = 0;
        try
        {
            while (total < count)
            {
                var n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, count - total));
                if (n == 0) break;
                c.Mem.Write(buffer + total, chunk.AsSpan(0, n));
                total += (uint)n;
            }
        }
        catch (IOException) { c.Mem.WriteUInt32(readPtr, total); return c.Ctx.Fail(Win32Error.InvalidFunction); }
        c.Mem.WriteUInt32(readPtr, total);
        return 1;
    }

    private static uint Write(Win32Call c)
    {
        uint handle = c[0], buffer = c[1], count = c[2], writtenPtr = c[3], overlapped = c[4];
        if (overlapped != 0) return c.Ctx.Fail(Win32Error.NotSupported);
        if (writtenPtr == 0 || (buffer == 0 && count != 0)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        Stream stream; bool flush = false;
        if (c.Ctx.GetHandle<FileHandle>(handle) is { } f) { if (!f.CanWrite) return c.Ctx.Fail(Win32Error.AccessDenied); stream = f.Stream; }
        else if (c.Ctx.GetHandle<StdStream>(handle) is { Slot: > 0 } std) { stream = c.Ctx.Files.HostStream(std); flush = true; }
        else return c.Ctx.Fail(c.Ctx.GetHandle<StdStream>(handle) is null ? Win32Error.InvalidHandle : Win32Error.AccessDenied);
        var chunk = new byte[(int)Math.Min(count, 65536u)];
        uint total = 0;
        try
        {
            while (total < count)
            {
                var n = (int)Math.Min(chunk.Length, count - total);
                c.Mem.Read(buffer + total, chunk.AsSpan(0, n));
                stream.Write(chunk, 0, n);
                total += (uint)n;
            }
            if (flush) stream.Flush();
        }
        catch (IOException) { c.Mem.WriteUInt32(writtenPtr, total); return c.Ctx.Fail(Win32Error.InvalidFunction); }
        c.Mem.WriteUInt32(writtenPtr, total);
        return 1;
    }

    private static string Wildcard(string pattern) => "^" + System.Text.RegularExpressions.Regex.Escape(pattern == "*.*" ? "*" : pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

    private static uint FindFirst(Win32Call c) => FindFirstNamed(c, c.Mem.ReadAnsiString(c[0], 1024), c[1], false);

    /// <summary>FindFirstFile with the pattern decoded; <paramref name="wide"/> selects WIN32_FIND_DATAW (592 bytes) over WIN32_FIND_DATAA (320 bytes).</summary>
    internal static uint FindFirstNamed(Win32Call c, string name, uint dataPtr, bool wide)
    {
        if (dataPtr == 0) return c.Ctx.Fail(Win32Error.InvalidParameter, InvalidHandle);
        var cut = name.LastIndexOfAny(['\\', '/']);
        var dirPart = cut < 0 ? "" : name[..(cut + 1)];
        var pattern = cut < 0 ? name : name[(cut + 1)..];
        if (pattern.Length == 0) return c.Ctx.Fail(Win32Error.FileNotFound, InvalidHandle);
        var (host, error) = c.Ctx.Files.Translate(dirPart.Length == 0 ? "." : dirPart);
        if (host is null) return c.Ctx.Fail(error, InvalidHandle);
        if (!Directory.Exists(host)) return c.Ctx.Fail(Win32Error.PathNotFound, InvalidHandle);
        var regex = new System.Text.RegularExpressions.Regex(Wildcard(pattern), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var infos = new DirectoryInfo(host).EnumerateFileSystemInfos().Where(i => regex.IsMatch(i.Name)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var entries = new List<FileSystemInfo?>(); var names = new List<string>();
        var isRoot = string.Equals(Path.GetFullPath(host).TrimEnd('/'), Path.GetFullPath(c.Ctx.Options.SandboxRoot!).TrimEnd('/'), StringComparison.Ordinal);
        if (!isRoot) foreach (var dot in new[] { ".", ".." }) if (regex.IsMatch(dot)) { entries.Add(null); names.Add(dot); }
        foreach (var i in infos) { entries.Add(i); names.Add(i.Name); }
        if (entries.Count == 0) return c.Ctx.Fail(Win32Error.FileNotFound, InvalidHandle);
        var find = new FindHandle(entries, names);
        FillFindData(c, find, dataPtr, wide);
        return c.Ctx.NewHandle(find);
    }

    internal static void FillFindData(Win32Call c, FindHandle find, uint data, bool wide)
    {
        var info = find.Entries[find.Next]; var name = find.Names[find.Next++];
        var d = new byte[wide ? 592 : 320];
        var isDir = info is null || info is DirectoryInfo;
        BitConverter.TryWriteBytes(d.AsSpan(0), isDir ? 0x10u : 0x80u);
        var file = info as FileInfo;
        var created = info?.CreationTimeUtc.ToFileTimeUtc() ?? 0; var access = info?.LastAccessTimeUtc.ToFileTimeUtc() ?? 0; var write = info?.LastWriteTimeUtc.ToFileTimeUtc() ?? 0;
        BitConverter.TryWriteBytes(d.AsSpan(4), created); BitConverter.TryWriteBytes(d.AsSpan(12), access); BitConverter.TryWriteBytes(d.AsSpan(20), write);
        var length = file?.Length ?? 0;
        BitConverter.TryWriteBytes(d.AsSpan(28), (uint)(length >> 32)); BitConverter.TryWriteBytes(d.AsSpan(32), (uint)length);
        var bytes = wide ? Nls.ToUtf16(name.Length > 259 ? name[..259] : name) : Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, name, (byte)'?', false, out _, out _);
        bytes.AsSpan(0, Math.Min(bytes.Length, 259)).CopyTo(d.AsSpan(44));
        c.Mem.Write(data, d);
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "GetStdHandle", 1, c => { var h = c.Ctx.Files.StdHandle(c[0]); return h == InvalidHandle ? c.Ctx.Fail(Win32Error.InvalidHandle, h) : h; });
        Std(K, "SetStdHandle", 2, c => c.Ctx.Files.SetStd(c[0], c[1]) ? 1u : c.Ctx.Fail(Win32Error.InvalidHandle));
        Std(K, "SetHandleCount", 1, c => c[0]); // obsolete: the documented return value is the requested count
        Std(K, "GetFileType", 1, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not null) return 1;
            if (c.Ctx.GetHandle<StdStream>(c[0]) is not null) return 2;
            if (c.Ctx.GetHandle<object>(c[0]) is not null) { c.Ctx.LastError = Win32Error.Success; return 0; }
            return c.Ctx.Fail(Win32Error.InvalidHandle);
        });
        Std(K, "CreateFileA", 7, Open);
        Std(K, "ReadFile", 5, Read);
        Std(K, "WriteFile", 5, Write);
        Std(K, "CloseHandle", 1, c =>
        {
            if (!c.Ctx.RemoveHandle(c[0], out var value)) return c.Ctx.Fail(Win32Error.InvalidHandle);
            (value as IDisposable)?.Dispose();
            return 1;
        });
        Std(K, "FlushFileBuffers", 1, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is { } f) { if (!f.CanWrite) return c.Ctx.Fail(Win32Error.AccessDenied); { if (f.Stream is FileStream fs) fs.Flush(true); else f.Stream.Flush(); } return 1; }
            if (c.Ctx.GetHandle<StdStream>(c[0]) is { Slot: > 0 } s) { c.Ctx.Files.HostStream(s).Flush(); return 1; }
            return c.Ctx.Fail(Win32Error.InvalidHandle);
        });
        Std(K, "GetFileSize", 2, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not { } f) return c.Ctx.Fail(c.Ctx.GetHandle<object>(c[0]) is null ? Win32Error.InvalidHandle : Win32Error.InvalidFunction, InvalidHandle);
            var length = f.Stream.Length;
            if (c[1] != 0) c.Mem.WriteUInt32(c[1], (uint)(length >> 32));
            c.Ctx.LastError = Win32Error.Success;
            return (uint)length;
        });
        Std(K, "SetFilePointer", 4, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not { } f) return c.Ctx.Fail(c.Ctx.GetHandle<object>(c[0]) is null ? Win32Error.InvalidHandle : Win32Error.InvalidFunction, InvalidHandle);
            if (c[3] > 2) return c.Ctx.Fail(Win32Error.InvalidParameter, InvalidHandle);
            var distance = c[2] != 0 ? (long)c.Mem.ReadUInt32(c[2]) << 32 | c[1] : (int)c[1];
            var origin = c[3] switch { 0 => 0L, 1 => f.Stream.Position, _ => f.Stream.Length };
            var target = origin + distance;
            if (target < 0) return c.Ctx.Fail(Win32Error.NegativeSeek, InvalidHandle);
            f.Stream.Position = target;
            if (c[2] != 0) c.Mem.WriteUInt32(c[2], (uint)(target >> 32));
            c.Ctx.LastError = Win32Error.Success;
            return (uint)target;
        });
        Std(K, "SetEndOfFile", 1, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not { } f) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (!f.CanWrite) return c.Ctx.Fail(Win32Error.AccessDenied);
            f.Stream.SetLength(f.Stream.Position);
            return 1;
        });
        Std(K, "FindFirstFileA", 2, FindFirst);
        Std(K, "FindNextFileA", 2, c =>
        {
            if (c.Ctx.GetHandle<FindHandle>(c[0]) is not { } find) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (find.Next >= find.Entries.Count) return c.Ctx.Fail(Win32Error.NoMoreFiles);
            FillFindData(c, find, c[1], false);
            return 1;
        });
        Std(K, "FindClose", 1, c => c.Ctx.GetHandle<FindHandle>(c[0]) is not null && c.Ctx.RemoveHandle(c[0], out _) ? 1u : c.Ctx.Fail(Win32Error.InvalidHandle));
        _ = Encoding.ASCII;
    }
}
