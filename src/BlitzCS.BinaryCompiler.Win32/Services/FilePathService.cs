namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// KERNEL32 file-system and path contracts beyond open/read/write (Microsoft Learn semantics), all confined to the sandbox like <see cref="FileService"/>:
/// CopyFileA/W and CopyFileExA/W (COPY_FILE_FAIL_IF_EXISTS; ERROR_FILE_EXISTS when the target exists and failing was requested; the last-write time and the read-only attribute are copied;
/// a progress routine or cancel flag is refused with ERROR_NOT_SUPPORTED), DeleteFileA/W (files only; read-only files and directories are ERROR_ACCESS_DENIED),
/// MoveFileA/W and MoveFileExA/W (ERROR_ALREADY_EXISTS unless MOVEFILE_REPLACE_EXISTING; a file may be moved across directories and a directory renamed; MOVEFILE_DELAY_UNTIL_REBOOT is refused),
/// GetFileAttributesA/W, GetFileAttributesExA/W (GetFileExInfoStandard, WIN32_FILE_ATTRIBUTE_DATA), SetFileAttributesA/W (only FILE_ATTRIBUTE_READONLY is stored by the host file system),
/// GetFileTime/SetFileTime, CreateDirectoryA/W, RemoveDirectoryA/W, GetTempPathA/W ("C:\Temp\"), GetTempFileNameA/W, SearchPathA/W (an explicit directory, or the current directory, then the
/// PATH of the virtual process), GetLongPathNameA/W (restores the stored case of every existing component; the file system has no 8.3 names, so GetShortPathName is not provided).
/// </summary>
public sealed class FilePathService : Win32Service
{
    public override string Name => "Kernel32.FilePath";
    private const uint Invalid = 0xFFFFFFFF;
    private const uint ErrDirNotEmpty = 145, ErrDirectory = 267, ErrSharing = 32;

    private static string Read(Win32Call c, uint p, bool wide) => wide ? c.Mem.ReadWideString(p, 32767) : c.Mem.ReadAnsiString(p, 32767);

    private static uint Out(Win32Call c, string text, uint buffer, uint size, bool wide)
    {
        if (size <= text.Length) return (uint)text.Length + 1; // required size including the terminator
        c.Mem.Write(buffer, wide ? Nls.ToUtf16(text + "\0") : Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, text + "\0", (byte)'?', false, out _, out _));
        return (uint)text.Length;
    }

    private static uint Missing(string host) => Directory.Exists(Path.GetDirectoryName(host) ?? "") ? Win32Error.FileNotFound : Win32Error.PathNotFound;

    internal static uint AttributesOf(string host)
    {
        var a = File.GetAttributes(host);
        var r = (a & FileAttributes.ReadOnly) != 0 ? 1u : 0u;
        if (Directory.Exists(host)) return 0x10 | r;
        return r != 0 ? r : 0x80;
    }

    private static (string? Host, uint Error) Resolve(Win32Call c, string name)
    {
        if (name.Length == 0) return (null, Win32Error.PathNotFound);
        return c.Ctx.Files.Translate(name);
    }

    private static uint Copy(Win32Call c, string from, string to, bool failIfExists)
    {
        var (src, e1) = Resolve(c, from); if (src is null) return c.Ctx.Fail(e1);
        var (dst, e2) = Resolve(c, to); if (dst is null) return c.Ctx.Fail(e2);
        if (Directory.Exists(src)) return c.Ctx.Fail(Win32Error.AccessDenied);
        if (!File.Exists(src)) return c.Ctx.Fail(Missing(src));
        if (Directory.Exists(dst)) return c.Ctx.Fail(Win32Error.AccessDenied);
        var exists = File.Exists(dst);
        if (!exists && !Directory.Exists(Path.GetDirectoryName(dst) ?? "")) return c.Ctx.Fail(Win32Error.PathNotFound);
        if (exists && string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.Ordinal)) return c.Ctx.Fail(ErrSharing);
        if (exists && failIfExists) return c.Ctx.Fail(Win32Error.FileExists);
        try
        {
            if (exists && (File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0) return c.Ctx.Fail(Win32Error.AccessDenied);
            File.Copy(src, dst, true);
            File.SetAttributes(dst, FileAttributes.Normal); // Windows hosts copy the read-only bit, which would block the timestamp update
            var info = new FileInfo(src);
            File.SetLastWriteTimeUtc(dst, info.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(dst, info.LastAccessTimeUtc);
            File.SetAttributes(dst, (info.Attributes & FileAttributes.ReadOnly) != 0 ? FileAttributes.ReadOnly : FileAttributes.Normal);
            c.Ctx.Files.InvalidatePathCache();
            return 1;
        }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
        catch (IOException) { return c.Ctx.Fail(ErrSharing); }
    }

    private static uint CopyEx(Win32Call c, bool wide)
    {
        if (c[2] != 0 || c[4] != 0) return c.Ctx.Fail(Win32Error.NotSupported); // progress routine / cancel flag
        if ((c[5] & ~1u) != 0) return c.Ctx.Fail(Win32Error.NotSupported);      // only COPY_FILE_FAIL_IF_EXISTS
        return Copy(c, Read(c, c[0], wide), Read(c, c[1], wide), (c[5] & 1) != 0);
    }

    private static uint Delete(Win32Call c, string name)
    {
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e);
        if (Directory.Exists(host)) return c.Ctx.Fail(Win32Error.AccessDenied);
        if (!File.Exists(host)) return c.Ctx.Fail(Missing(host));
        if ((File.GetAttributes(host) & FileAttributes.ReadOnly) != 0) return c.Ctx.Fail(Win32Error.AccessDenied);
        try { File.Delete(host); c.Ctx.Files.InvalidatePathCache(); return 1; }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
        catch (IOException) { return c.Ctx.Fail(ErrSharing); }
    }

    private static uint Move(Win32Call c, string from, string to, uint flags)
    {
        if ((flags & 4) != 0) return c.Ctx.Fail(Win32Error.NotSupported); // MOVEFILE_DELAY_UNTIL_REBOOT
        var (src, e1) = Resolve(c, from); if (src is null) return c.Ctx.Fail(e1);
        var (dst, e2) = Resolve(c, to); if (dst is null) return c.Ctx.Fail(e2);
        var srcDir = Directory.Exists(src);
        if (!srcDir && !File.Exists(src)) return c.Ctx.Fail(Missing(src));
        if (!Directory.Exists(Path.GetDirectoryName(dst) ?? "")) return c.Ctx.Fail(Win32Error.PathNotFound);
        var dstExists = File.Exists(dst) || Directory.Exists(dst);
        if (dstExists && string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.Ordinal)) return 1;
        try
        {
            if (dstExists)
            {
                if ((flags & 1) == 0) return c.Ctx.Fail(Win32Error.AlreadyExists);
                if (srcDir || Directory.Exists(dst)) return c.Ctx.Fail(Win32Error.AccessDenied); // directories are never replaced
                if ((File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0) return c.Ctx.Fail(Win32Error.AccessDenied);
                File.Move(src, dst, true);
            }
            else if (srcDir) Directory.Move(src, dst);
            else File.Move(src, dst);
            c.Ctx.Files.InvalidatePathCache();
            return 1;
        }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
        catch (IOException) { return c.Ctx.Fail(ErrSharing); }
    }

    private static uint GetAttributes(Win32Call c, string name)
    {
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e, Invalid);
        if (!Directory.Exists(host) && !File.Exists(host)) return c.Ctx.Fail(Missing(host), Invalid);
        c.Ctx.LastError = Win32Error.Success;
        return AttributesOf(host);
    }

    private static uint GetAttributesEx(Win32Call c, string name)
    {
        if (c[1] != 0) return c.Ctx.Fail(Win32Error.InvalidParameter); // only GetFileExInfoStandard
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e);
        FileSystemInfo info = Directory.Exists(host) ? new DirectoryInfo(host) : new FileInfo(host);
        if (!info.Exists) return c.Ctx.Fail(Missing(host));
        var d = new byte[36];
        BitConverter.TryWriteBytes(d.AsSpan(0), AttributesOf(host));
        BitConverter.TryWriteBytes(d.AsSpan(4), info.CreationTimeUtc.ToFileTimeUtc());
        BitConverter.TryWriteBytes(d.AsSpan(12), info.LastAccessTimeUtc.ToFileTimeUtc());
        BitConverter.TryWriteBytes(d.AsSpan(20), info.LastWriteTimeUtc.ToFileTimeUtc());
        var len = info is FileInfo f ? f.Length : 0;
        BitConverter.TryWriteBytes(d.AsSpan(28), (uint)(len >> 32)); BitConverter.TryWriteBytes(d.AsSpan(32), (uint)len);
        c.Mem.Write(c[2], d);
        return 1;
    }

    private static uint SetAttributes(Win32Call c, string name, uint attributes)
    {
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e);
        if (!Directory.Exists(host) && !File.Exists(host)) return c.Ctx.Fail(Missing(host));
        try
        {
            if (File.Exists(host)) File.SetAttributes(host, (attributes & 1) != 0 ? FileAttributes.ReadOnly : FileAttributes.Normal);
            return 1;
        }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
    }

    private static uint CreateDir(Win32Call c, string name)
    {
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e);
        if (Directory.Exists(host) || File.Exists(host)) return c.Ctx.Fail(Win32Error.AlreadyExists);
        if (!Directory.Exists(Path.GetDirectoryName(host) ?? "")) return c.Ctx.Fail(Win32Error.PathNotFound);
        try { Directory.CreateDirectory(host); c.Ctx.Files.InvalidatePathCache(); return 1; }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
    }

    private static uint RemoveDir(Win32Call c, string name)
    {
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e);
        if (File.Exists(host)) return c.Ctx.Fail(ErrDirectory);
        if (!Directory.Exists(host)) return c.Ctx.Fail(Missing(host));
        if (string.Equals(Path.GetFullPath(host).TrimEnd('/'), Path.GetFullPath(c.Ctx.Options.SandboxRoot!).TrimEnd('/'), StringComparison.Ordinal)) return c.Ctx.Fail(Win32Error.AccessDenied);
        if (Directory.EnumerateFileSystemEntries(host).Any()) return c.Ctx.Fail(ErrDirNotEmpty);
        try { Directory.Delete(host); c.Ctx.Files.InvalidatePathCache(); return 1; }
        catch (UnauthorizedAccessException) { return c.Ctx.Fail(Win32Error.AccessDenied); }
        catch (IOException) { return c.Ctx.Fail(ErrSharing); }
    }

    private static uint GetTempFileName(Win32Call c, bool wide)
    {
        var dir = Read(c, c[0], wide); var prefix = Read(c, c[1], wide); var unique = c[2];
        if (prefix.Length > 3) prefix = prefix[..3];
        var (host, e) = Resolve(c, dir); if (host is null) return c.Ctx.Fail(e, 0);
        if (!Directory.Exists(host)) return c.Ctx.Fail(Win32Error.PathNotFound, 0);
        var sep = dir.EndsWith('\\') || dir.EndsWith('/') ? "" : "\\";
        string Make(uint u) => dir + sep + prefix + u.ToString("x") + ".tmp";
        if (unique == 0)
        {
            // the system picks a number from the clock and creates the (empty) file; an existing name is never reused
            for (var u = (uint)Environment.TickCount % 0xFFFF + 1; u <= 0xFFFF; u++)
            {
                var (h, _) = Resolve(c, Make(u));
                if (h is null || File.Exists(h) || Directory.Exists(h)) continue;
                File.Create(h).Dispose();
                c.Ctx.Files.InvalidatePathCache();
                unique = u; break;
            }
            if (unique == 0) return c.Ctx.Fail(Win32Error.AccessDenied, 0);
        }
        var name = Make(unique & 0xFFFF);
        var needed = name.Length + 1;
        c.Mem.Write(c[3], wide ? Nls.ToUtf16(name + "\0") : Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, name + "\0", (byte)'?', false, out _, out _));
        _ = needed;
        return unique & 0xFFFF;
    }

    private static uint SearchPath(Win32Call c, bool wide)
    {
        var file = Read(c, c[1], wide);
        if (file.Length == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var ext = c[2] != 0 ? Read(c, c[2], wide) : "";
        var dirs = new List<string>();
        if (c[0] != 0) dirs.Add(Read(c, c[0], wide));
        else
        {
            dirs.Add("C:" + c.Ctx.Process.CurrentDirectory);
            if (c.Ctx.Process.GetEnvironmentVariable("PATH") is { } path) dirs.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        var hasExt = Path.GetExtension(file).Length > 0;
        var name = hasExt || ext.Length == 0 ? file : file + ext;
        var rooted = name.Contains(':') || name.StartsWith('\\') || name.StartsWith('/');
        foreach (var dir in rooted ? [""] : dirs)
        {
            var candidate = rooted ? name : dir.TrimEnd('\\', '/') + "\\" + name;
            var (host, e) = Resolve(c, candidate);
            if (host is null || !File.Exists(host)) continue;
            var full = Kernel32WideService.GuestPath(c, candidate);
            var n = Out(c, full, c[4], c[3], wide);
            if (n == (uint)full.Length && c[5] != 0)
            {
                var cut = full.LastIndexOf('\\');
                c.Mem.WriteUInt32(c[5], c[4] + (uint)(cut + 1) * (wide ? 2u : 1u));
            }
            return n;
        }
        return c.Ctx.Fail(Win32Error.FileNotFound, 0);
    }

    private static uint LongPath(Win32Call c, bool wide)
    {
        var name = Read(c, c[0], wide);
        if (name.Length == 0) return c.Ctx.Fail(Win32Error.PathNotFound, 0);
        var (host, e) = Resolve(c, name); if (host is null) return c.Ctx.Fail(e, 0);
        if (!File.Exists(host) && !Directory.Exists(host)) return c.Ctx.Fail(Missing(host), 0);
        // Walk the guest path component by component and take the stored spelling of each entry from its directory.
        var current = Path.GetFullPath(c.Ctx.Options.SandboxRoot!);
        var parts = new List<string>();
        foreach (var part in Kernel32WideService.GuestPath(c, name)[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = c.Ctx.Files.GetDirectoryListing(current).ByName.TryGetValue(part, out var m) ? m : part;
            parts.Add(match);
            current = Path.Combine(current, match);
        }
        var full = "C:\\" + string.Join('\\', parts);
        return Out(c, full, c[1], c[2], wide);
    }

    private static void Put(Win32Call c, uint p, DateTime t) { if (p != 0) c.Mem.Write(p, BitConverter.GetBytes(t.ToFileTimeUtc())); }

    private static DateTime? GetTime(Win32Call c, uint p)
    {
        if (p == 0) return null;
        var v = BitConverter.ToInt64(c.Mem.ReadBytes(p, 8));
        return v is 0 or -1 ? null : DateTime.FromFileTimeUtc(v);
    }

    private static FileHandle? FileOf(Win32Call c, uint handle)
    {
        if (c.Ctx.GetHandle<FileHandle>(handle) is { } f) return f;
        c.Ctx.Fail(c.Ctx.GetHandle<object>(handle) is null ? Win32Error.InvalidHandle : Win32Error.InvalidFunction);
        return null;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        foreach (var wide in new[] { false, true })
        {
            var w = wide; var s = wide ? "W" : "A";
            Std(K, "CopyFile" + s, 3, c => Copy(c, Read(c, c[0], w), Read(c, c[1], w), c[2] != 0));
            Std(K, "CopyFileEx" + s, 6, c => CopyEx(c, w));
            Std(K, "DeleteFile" + s, 1, c => Delete(c, Read(c, c[0], w)));
            Std(K, "MoveFile" + s, 2, c => Move(c, Read(c, c[0], w), Read(c, c[1], w), 0));
            Std(K, "MoveFileEx" + s, 3, c => Move(c, Read(c, c[0], w), c[1] == 0 ? "" : Read(c, c[1], w), c[2]));
            Std(K, "GetFileAttributes" + s, 1, c => GetAttributes(c, Read(c, c[0], w)));
            Std(K, "GetFileAttributesEx" + s, 3, c => GetAttributesEx(c, Read(c, c[0], w)));
            Std(K, "SetFileAttributes" + s, 2, c => SetAttributes(c, Read(c, c[0], w), c[1]));
            Std(K, "CreateDirectory" + s, 2, c => CreateDir(c, Read(c, c[0], w)));
            Std(K, "RemoveDirectory" + s, 1, c => RemoveDir(c, Read(c, c[0], w)));
            Std(K, "GetTempFileName" + s, 4, c => GetTempFileName(c, w));
            Std(K, "SearchPath" + s, 6, c => SearchPath(c, w));
            Std(K, "GetLongPathName" + s, 3, c => LongPath(c, w));
            Std(K, "GetTempPath" + s, 2, c => Out(c, "C:\\Temp\\", c[1], c[0], w));
        }
        Std(K, "GetFileTime", 4, c =>
        {
            if (FileOf(c, c[0]) is not { } f) return 0;
            var info = new FileInfo(f.Path);
            Put(c, c[1], info.CreationTimeUtc); Put(c, c[2], info.LastAccessTimeUtc); Put(c, c[3], info.LastWriteTimeUtc);
            return 1;
        });
        Std(K, "SetFileTime", 4, c =>
        {
            if (FileOf(c, c[0]) is not { } f) return 0;
            if (!f.CanWrite) return c.Ctx.Fail(Win32Error.AccessDenied);
            // A null pointer, or a FILETIME of 0 / -1, leaves that time unchanged.
            if (GetTime(c, c[1]) is { } cr) File.SetCreationTimeUtc(f.Path, cr);
            if (GetTime(c, c[2]) is { } ac) File.SetLastAccessTimeUtc(f.Path, ac);
            if (GetTime(c, c[3]) is { } wr) File.SetLastWriteTimeUtc(f.Path, wr);
            return 1;
        });
    }
}
