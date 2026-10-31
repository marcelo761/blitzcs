using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// KERNEL32 wide-character file, console, locale and environment functions used by the UCRT start-up and stdio (Microsoft Learn semantics):
/// <list type="bullet">
/// <item>CreateFileW, FindFirstFileExW (info levels standard/basic, search op name-match; limit-to-directories is a documented hint and is treated as name-match), FindNextFileW, GetFileSizeEx,
/// SetFilePointerEx: the same sandbox and handle table as the ANSI file APIs.</item>
/// <item>Console: the virtual process has a console only when <see cref="Win32Options.StdoutIsConsole"/> (or Stderr/Stdin) is set. Otherwise the standard handles are redirected streams: GetConsoleMode,
/// WriteConsoleA/W and ReadConsoleW fail with ERROR_INVALID_HANDLE exactly as on Windows, and the CRT falls back to WriteFile. GetConsoleCP/GetConsoleOutputCP report the OEM code page (a console
/// process keeps its console code page even when redirected). Console output is written to the host stream as the code page's bytes (WriteConsoleA) or UTF-8 (WriteConsoleW). ReadConsoleW reads one line
/// (cooked input, "\r\n" terminated) from the stdin stream.</item>
/// <item>Environment: SetEnvironmentVariableW/A, GetEnvironmentVariableW (the process environment is copied from the options at start and then owned by the process).
/// Current directory: SetCurrentDirectoryA/W, GetCurrentDirectoryA/W and GetFullPathNameA/W; relative file names resolve against it. The drive is always C:.</item>
/// <item>Locale: the virtual machine has exactly one locale, en-US (LCID 0x0409). GetUserDefaultLCID, IsValidLocale, EnumSystemLocalesW (calls the guest callback once with "00000409"),
/// GetLocaleInfoW (a documented subset of LCTYPEs; any other LCTYPE raises <see cref="Win32UsageException"/> naming it instead of inventing data), CompareStringW (code-unit order,
/// NORM_IGNORECASE and SORT_STRINGSORT only; other flags raise), LCMapStringEx (LCMapStringW semantics; locale name null, "", "en-US" or "en"), IsValidCodePage (the code pages this component converts).</item>
/// </list>
/// </summary>
public sealed class Kernel32WideService : Win32Service
{
    public override string Name => "Kernel32.Wide";
    private const uint InvalidHandle = 0xFFFFFFFF;
    private const uint EnLcid = 0x0409;

    private sealed class LineBuffer { public byte[] Pending = []; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Win32Context, LineBuffer> Lines = new();

    private static bool IsConsole(Win32Call c, uint handle) =>
        c.Ctx.GetHandle<StdStream>(handle) is { } s && s.Slot switch { 0 => c.Ctx.Options.StdinIsConsole, 1 => c.Ctx.Options.StdoutIsConsole, _ => c.Ctx.Options.StderrIsConsole };

    private static bool IsEnLocale(uint lcid) => lcid is EnLcid or 0x0400 or 0x0800 or 0x007F or 0x0009;
    private static bool IsEnName(string? name) => name is null or "" || name.Equals("en-US", StringComparison.OrdinalIgnoreCase) || name.Equals("en", StringComparison.OrdinalIgnoreCase);

    private static uint WriteConsole(Win32Call c, bool wide)
    {
        if (!IsConsole(c, c[0]) || c.Ctx.GetHandle<StdStream>(c[0]) is not { Slot: > 0 } std) return c.Ctx.Fail(Win32Error.InvalidHandle);
        if (c[3] != 0 && c[4] != 0 || c[1] == 0 && c[2] != 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var n = (int)c[2];
        var bytes = wide ? Encoding.UTF8.GetBytes(Nls.FromUtf16(c.Mem.ReadBytes(c[1], n * 2))) : c.Mem.ReadBytes(c[1], n);
        var s = c.Ctx.Files.HostStream(std);
        s.Write(bytes); s.Flush();
        if (c[3] != 0) c.Mem.WriteUInt32(c[3], (uint)n);
        return 1;
    }

    private static uint ReadConsoleW(Win32Call c)
    {
        if (!IsConsole(c, c[0]) || c.Ctx.GetHandle<StdStream>(c[0]) is not { Slot: 0 } std) return c.Ctx.Fail(Win32Error.InvalidHandle);
        var buf = Lines.GetOrCreateValue(c.Ctx);
        var stream = c.Ctx.Files.HostStream(std);
        var chars = (int)c[2];
        lock (buf)
        {
            if (buf.Pending.Length == 0)
            {
                var line = new List<byte>();
                for (var b = stream.ReadByte(); b >= 0; b = stream.ReadByte()) { line.Add((byte)b); if (b == '\n') break; }
                var text = Encoding.UTF8.GetString([.. line]);
                if (line.Count > 0 && !text.EndsWith('\n')) text += "\r\n"; else text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                buf.Pending = Nls.ToUtf16(text);
            }
            var take = Math.Min(buf.Pending.Length / 2, chars) * 2;
            c.Mem.Write(c[1], buf.Pending.AsSpan(0, take));
            buf.Pending = buf.Pending[take..];
            c.Mem.WriteUInt32(c[3], (uint)take / 2);
        }
        return 1;
    }

    internal static string GuestPath(Win32Call c, string name)
    {
        // Normalised Windows path "C:\dir\file" for a (possibly relative) name; no existence check.
        var path = name.Replace('/', '\\');
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path = path[2..];
        if (!path.StartsWith('\\')) path = c.Ctx.Process.CurrentDirectory.TrimEnd('\\') + "\\" + path;
        var parts = new List<string>();
        foreach (var p in path.Split('\\'))
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return "C:\\" + string.Join('\\', parts);
    }

    private static uint FullPath(Win32Call c, bool wide)
    {
        var name = wide ? c.Mem.ReadWideString(c[0], 32767) : c.Mem.ReadAnsiString(c[0], 32767);
        if (name.Length == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (name.StartsWith("\\\\") || name.StartsWith("//")) return c.Ctx.Fail(Win32Error.BadPathname);
        var full = GuestPath(c, name);
        var size = c[1];
        if (size <= full.Length) return (uint)full.Length + 1; // required size including the terminator
        c.Mem.Write(c[2], wide ? Nls.ToUtf16(full + "\0") : Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, full + "\0", (byte)'?', false, out _, out _));
        if (c[3] != 0)
        {
            var cut = full.LastIndexOf('\\');
            c.Mem.WriteUInt32(c[3], cut + 1 >= full.Length ? 0 : c[2] + (uint)(cut + 1) * (wide ? 2u : 1u));
        }
        return (uint)full.Length;
    }

    private static uint SetCurrentDirectory(Win32Call c, string name)
    {
        if (name.Length == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var full = GuestPath(c, name);
        var (host, error) = c.Ctx.Files.Translate(full);
        if (host is null) return c.Ctx.Fail(error);
        if (!Directory.Exists(host)) return c.Ctx.Fail(File.Exists(host) ? 267 /* ERROR_DIRECTORY */ : Win32Error.PathNotFound);
        c.Ctx.Process.CurrentDirectory = full[2..];
        return 1;
    }

    private static uint GetCurrentDirectory(Win32Call c, bool wide)
    {
        var text = "C:" + c.Ctx.Process.CurrentDirectory;
        if (c[0] <= text.Length) return (uint)text.Length + 1;
        c.Mem.Write(c[1], wide ? Nls.ToUtf16(text + "\0") : Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, text + "\0", (byte)'?', false, out _, out _));
        return (uint)text.Length;
    }

    private static uint SetEnv(Win32Call c, string name, string? value)
    {
        if (name.Length == 0 || name.Contains('=')) return c.Ctx.Fail(Win32Error.InvalidParameter);
        return c.Ctx.Process.SetEnvironmentVariable(name, value) ? 1u : c.Ctx.Fail(Win32Error.EnvVarNotFound);
    }

    private static uint LocaleInfo(Win32Call c)
    {
        if (!IsEnLocale(c[0])) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var type = c[1];
        var asNumber = (type & 0x20000000) != 0;
        type &= ~0x20000000u;
        string? text = type switch
        {
            0x59 => "en", 0x5A => "US", 0x5C => "en-US", 0x1001 => "English", 0x1002 => "United States",
            0x1004 => "1252", 0x0B => "437", 0x0E => ".", 0x0F => ",", 0x0C => ",", 0x11 => "2", 0x01 => "0409",
            _ => null,
        };
        if (text is null) throw new Win32UsageException($"GetLocaleInfoW: LCTYPE 0x{type:X} is not modelled (the virtual en-US locale provides only a documented subset).");
        if (asNumber)
        {
            if (!uint.TryParse(text, out var n)) return c.Ctx.Fail(Win32Error.InvalidFlags);
            if (c[3] < 4 || c[2] == 0) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
            c.Mem.WriteUInt32(c[2], n);
            return 2;
        }
        var need = (uint)text.Length + 1;
        if (c[3] == 0) return need;
        if (c[3] < need) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(c[2], Nls.ToUtf16(text + "\0"));
        return need;
    }

    private static uint CompareW(Win32Call c)
    {
        if (!IsEnLocale(c[0])) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var flags = c[1];
        if ((flags & ~(1u | 0x1000u)) != 0) throw new Win32UsageException($"CompareStringW: flags 0x{flags:X} are not modelled (only NORM_IGNORECASE and SORT_STRINGSORT).");
        if (c[2] == 0 || c[4] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var mem = c.Mem;
        string Read(uint p, int cch) => cch < 0 ? mem.ReadWideString(p) : Nls.FromUtf16(mem.ReadBytes(p, cch * 2));
        var r = string.Compare(Read(c[2], (int)c[3]), Read(c[4], (int)c[5]), (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return r < 0 ? 1u : r == 0 ? 2u : 3u;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "CreateFileW", 7, c => FileService.OpenNamed(c, c.Mem.ReadWideString(c[0], 1024)));
        Std(K, "FindFirstFileExW", 6, c =>
        {
            if (c[1] > 1 || c[3] > 2 || c[4] != 0 || (c[5] & ~3u) != 0) return c.Ctx.Fail(Win32Error.InvalidParameter, InvalidHandle);
            return FileService.FindFirstNamed(c, c.Mem.ReadWideString(c[0], 1024), c[2], true);
        });
        Std(K, "FindNextFileW", 2, c =>
        {
            if (c.Ctx.GetHandle<FindHandle>(c[0]) is not { } find) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (find.Next >= find.Entries.Count) return c.Ctx.Fail(Win32Error.NoMoreFiles);
            FileService.FillFindData(c, find, c[1], true);
            return 1;
        });
        Std(K, "GetFileSizeEx", 2, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not { } f) return c.Ctx.Fail(c.Ctx.GetHandle<object>(c[0]) is null ? Win32Error.InvalidHandle : Win32Error.InvalidFunction);
            if (c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Mem.WriteUInt64(c[1], (ulong)f.Stream.Length);
            return 1;
        });
        Std(K, "SetFilePointerEx", 5, c =>
        {
            if (c.Ctx.GetHandle<FileHandle>(c[0]) is not { } f) return c.Ctx.Fail(c.Ctx.GetHandle<object>(c[0]) is null ? Win32Error.InvalidHandle : Win32Error.InvalidFunction);
            if (c[4] > 2) return c.Ctx.Fail(Win32Error.InvalidParameter);
            var distance = (long)((ulong)c[2] << 32 | c[1]);
            var origin = c[4] switch { 0 => 0L, 1 => f.Stream.Position, _ => f.Stream.Length };
            var target = origin + distance;
            if (target < 0) return c.Ctx.Fail(Win32Error.NegativeSeek);
            f.Stream.Position = target;
            if (c[3] != 0) c.Mem.WriteUInt64(c[3], (ulong)target);
            return 1;
        });

        Std(K, "GetConsoleMode", 2, c =>
        {
            if (!IsConsole(c, c[0])) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Mem.WriteUInt32(c[1], c.Ctx.GetHandle<StdStream>(c[0])!.Slot == 0 ? 0x7u /* PROCESSED|LINE|ECHO */ : 0x3u /* PROCESSED_OUTPUT|WRAP_AT_EOL */);
            return 1;
        });
        Std(K, "GetConsoleOutputCP", 0, c => (uint)c.Ctx.Options.OemCodePage);
        Std(K, "GetConsoleCP", 0, c => (uint)c.Ctx.Options.OemCodePage);
        Std(K, "WriteConsoleA", 5, c => WriteConsole(c, false));
        Std(K, "WriteConsoleW", 5, c => WriteConsole(c, true));
        Std(K, "ReadConsoleW", 4, ReadConsoleW);

        Std(K, "GetFullPathNameA", 4, c => FullPath(c, false));
        Std(K, "GetFullPathNameW", 4, c => FullPath(c, true));
        Std(K, "SetCurrentDirectoryA", 1, c => SetCurrentDirectory(c, c.Mem.ReadAnsiString(c[0], 1024)));
        Std(K, "SetCurrentDirectoryW", 1, c => SetCurrentDirectory(c, c.Mem.ReadWideString(c[0], 1024)));
        Std(K, "GetCurrentDirectoryA", 2, c => GetCurrentDirectory(c, false));
        Std(K, "GetCurrentDirectoryW", 2, c => GetCurrentDirectory(c, true));
        Std(K, "SetEnvironmentVariableW", 2, c => SetEnv(c, c.Mem.ReadWideString(c[0], 32767), c[1] == 0 ? null : c.Mem.ReadWideString(c[1], 32767)));
        Std(K, "SetEnvironmentVariableA", 2, c => SetEnv(c, c.Mem.ReadAnsiString(c[0], 32767), c[1] == 0 ? null : c.Mem.ReadAnsiString(c[1], 32767)));
        Std(K, "GetEnvironmentVariableW", 3, c =>
        {
            var value = c.Ctx.Process.GetEnvironmentVariable(c.Mem.ReadWideString(c[0], 32767));
            if (value is null) return c.Ctx.Fail(Win32Error.EnvVarNotFound);
            if (c[2] <= value.Length) return (uint)value.Length + 1;
            c.Mem.Write(c[1], Nls.ToUtf16(value + "\0"));
            c.Ctx.LastError = Win32Error.Success;
            return (uint)value.Length;
        });

        Std(K, "GetUserDefaultLCID", 0, _ => EnLcid);
        Std(K, "IsValidLocale", 2, c => c[1] is 1 or 2 && IsEnLocale(c[0]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "EnumSystemLocalesW", 2, c =>
        {
            if (c[0] == 0 || (c[1] & ~7u) == 0 && c[1] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            var text = c.Ctx.Process.ThreadBuffer(c.Mem, "enumlocale", 32);
            c.Mem.Write(text, Nls.ToUtf16("00000409\0"));
            c.CallGuest(c[0], text);
            return 1;
        });
        Std(K, "GetLocaleInfoW", 4, LocaleInfo);
        Std(K, "CompareStringW", 6, CompareW);
        Std(K, "LCMapStringEx", 9, c =>
        {
            if (c[0] != 0 && !IsEnName(c.Mem.ReadWideString(c[0], 85))) return c.Ctx.Fail(Win32Error.InvalidParameter);
            return StringService.LcMapW(c);
        });
        Std(K, "IsValidCodePage", 1, c => Nls.IsSupported((int)c[0]) ? 1u : 0u);
    }
}
