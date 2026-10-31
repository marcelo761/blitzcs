using BlitzCS.BinaryCompiler.Runtime;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// KERNEL32 resource access (Microsoft Learn, "Resources Functions" and the PE format's ".rsrc Section"). The module's image is mapped in guest memory at its handle, so the resource
/// directory (IMAGE_DIRECTORY_ENTRY_RESOURCE) is parsed there and nothing is copied.
/// <list type="bullet">
/// <item>FindResourceA/W take (module, name, type); FindResourceExA/W take (module, type, name, language).</item>
/// <item>hModule NULL = the main executable; otherwise a mapped guest module (main image, guest DLL). An emulated system DLL has no resource section here: ERROR_RESOURCE_DATA_NOT_FOUND. Anything else: ERROR_INVALID_HANDLE.</item>
/// <item>Type and name are MAKEINTRESOURCE ids (pointer below 0x10000), "#123" decimal strings (also an id) or case-insensitive strings. A missing type is ERROR_RESOURCE_TYPE_NOT_FOUND, a missing name
/// ERROR_RESOURCE_NAME_NOT_FOUND, a missing language ERROR_RESOURCE_LANG_NOT_FOUND, a module without a resource directory ERROR_RESOURCE_DATA_NOT_FOUND, a malformed directory ERROR_BAD_EXE_FORMAT.</item>
/// <item>Language: FindResourceEx with a specific id takes that language, else the same primary language with the neutral sublanguage, else LANG_NEUTRAL. FindResource/language 0 prefers en-US (0x0409, the
/// reported UI language), then English, then neutral, then the first language in the directory (approximation of the thread/user language search).</item>
/// <item>The HRSRC is the address of the IMAGE_RESOURCE_DATA_ENTRY inside the mapped directory, LoadResource returns the address of the data in the image (the HGLOBAL of a loaded module), LockResource returns it
/// unchanged, SizeofResource reads the entry's Size. A handle outside the module's directory is ERROR_INVALID_PARAMETER. FreeResource does nothing and returns 0 (success), as it is obsolete for 32-bit modules.</item>
/// <item>Not supported: modules loaded as data files (refused earlier by LoadLibraryEx), EnumResource*, UpdateResource, string-table lookups by LoadString.</item>
/// </list>
/// </summary>
public sealed class Kernel32ResourceService : Win32Service
{
    public override string Name => "Kernel32.Resources";

    private const uint EnUs = 0x0409;
    private const int DirSize = 16, EntrySize = 8;

    private readonly record struct Rsrc(uint Base, uint Dir, uint Size);

    /// <summary>Resolves the module handle, returning its resource directory (or 0 and the error already set).</summary>
    private static bool TryModule(Win32Call c, uint handle, out Rsrc rsrc)
    {
        rsrc = default;
        var main = c.Ctx.Options.MainModuleBase;
        var b = handle == 0 ? main : handle;
        var known = b == main || c.Ctx.Options.GuestLibraries?.Find(b) is not null || c.Ctx.Process.GuestModules().Any(m => m.Handle == b);
        if (!known)
        {
            c.Ctx.LastError = c.Ctx.Process.Find(b) is not null ? Win32Error.ResourceDataNotFound : Win32Error.InvalidHandle;
            return false;
        }
        try
        {
            if (c.Mem.ReadUInt16(b) != 0x5A4D) { c.Ctx.LastError = Win32Error.BadExeFormat; return false; }
            var pe = b + c.Mem.ReadUInt32(b + 0x3C);
            if (c.Mem.ReadUInt32(pe) != 0x4550 || c.Mem.ReadUInt16(pe + 24) != 0x10B) { c.Ctx.LastError = Win32Error.BadExeFormat; return false; }
            var count = c.Mem.ReadUInt32(pe + 24 + 92);
            if (count <= 2) { c.Ctx.LastError = Win32Error.ResourceDataNotFound; return false; }
            var rva = c.Mem.ReadUInt32(pe + 24 + 96 + 16);
            var size = c.Mem.ReadUInt32(pe + 24 + 96 + 20);
            if (rva == 0 || size < DirSize) { c.Ctx.LastError = Win32Error.ResourceDataNotFound; return false; }
            rsrc = new Rsrc(b, b + rva, size);
            return true;
        }
        catch (GuestMemoryException) { c.Ctx.LastError = Win32Error.BadExeFormat; return false; }
    }

    /// <summary>Entries of one directory level: (name-or-id word, offset word).</summary>
    private static List<(uint Name, uint Offset)> Entries(Win32Call c, Rsrc r, uint offset)
    {
        if (offset > r.Size - DirSize) throw new BadResourceDirectory();
        var named = c.Mem.ReadUInt16(r.Dir + offset + 12);
        var ids = c.Mem.ReadUInt16(r.Dir + offset + 14);
        var total = (uint)(named + ids);
        if (offset + DirSize + (ulong)total * EntrySize > r.Size) throw new BadResourceDirectory();
        var list = new List<(uint, uint)>((int)total);
        for (uint i = 0; i < total; i++)
        {
            var at = r.Dir + offset + DirSize + i * EntrySize;
            list.Add((c.Mem.ReadUInt32(at), c.Mem.ReadUInt32(at + 4)));
        }
        return list;
    }

    private sealed class BadResourceDirectory : Exception;

    private static string EntryName(Win32Call c, Rsrc r, uint nameWord)
    {
        var off = nameWord & 0x7FFFFFFF;
        if (off > r.Size - 2) throw new BadResourceDirectory();
        int len = c.Mem.ReadUInt16(r.Dir + off);
        if (off + 2 + (ulong)len * 2 > r.Size) throw new BadResourceDirectory();
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++) sb.Append((char)c.Mem.ReadUInt16(r.Dir + off + 2 + 2u * (uint)i));
        return sb.ToString();
    }

    /// <summary>A MAKEINTRESOURCE id or a name; "#123" is an id.</summary>
    private readonly record struct Key(uint? Id, string? Name)
    {
        public static Key From(Win32Call c, uint arg, bool wide)
        {
            if (arg >> 16 == 0) return new Key(arg, null);
            var s = wide ? c.Mem.ReadWideString(arg, 4096) : c.Mem.ReadAnsiString(arg, 4096);
            if (s.Length > 1 && s[0] == '#')
            {
                if (uint.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.None, null, out var id) && id < 0x10000) return new Key(id, null);
                return new Key(null, s);
            }
            return new Key(null, s);
        }
    }

    private static (uint Name, uint Offset)? Match(Win32Call c, Rsrc r, List<(uint Name, uint Offset)> entries, Key key)
    {
        foreach (var e in entries)
        {
            var isName = (e.Name & 0x80000000) != 0;
            if (key.Id is { } id ? !isName && e.Name == id : isName && string.Equals(EntryName(c, r, e.Name), key.Name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private static uint Find(Win32Call c, uint hModule, uint type, uint name, uint language, bool wide, bool exact)
    {
        if (!TryModule(c, hModule, out var r)) return 0;
        try
        {
            var typeEntry = Match(c, r, Entries(c, r, 0), Key.From(c, type, wide));
            if (typeEntry is not { } t || (t.Offset & 0x80000000) == 0) return c.Ctx.Fail(Win32Error.ResourceTypeNotFound);
            var nameEntry = Match(c, r, Entries(c, r, t.Offset & 0x7FFFFFFF), Key.From(c, name, wide));
            if (nameEntry is not { } n || (n.Offset & 0x80000000) == 0) return c.Ctx.Fail(Win32Error.ResourceNameNotFound);
            var langs = Entries(c, r, n.Offset & 0x7FFFFFFF);
            if (langs.Count == 0) return c.Ctx.Fail(Win32Error.ResourceLangNotFound);
            uint[] order = exact && language != 0
                ? [language, language & 0x3FF, 0]
                : [EnUs, 0x0009, 0];
            (uint Name, uint Offset)? chosen = null;
            foreach (var want in order)
            {
                chosen = langs.Cast<(uint Name, uint Offset)?>().FirstOrDefault(e => (e!.Value.Name & 0x80000000) == 0 && e.Value.Name == want);
                if (chosen is not null) break;
            }
            if (chosen is null && !(exact && language != 0)) chosen = langs[0];
            if (chosen is not { } l || (l.Offset & 0x80000000) != 0) return c.Ctx.Fail(Win32Error.ResourceLangNotFound);
            if (l.Offset > r.Size - 16) return c.Ctx.Fail(Win32Error.BadExeFormat);
            return r.Dir + l.Offset;
        }
        catch (BadResourceDirectory) { return c.Ctx.Fail(Win32Error.BadExeFormat); }
        catch (GuestMemoryException) { return c.Ctx.Fail(Win32Error.BadExeFormat); }
    }

    private static bool InDirectory(Rsrc r, uint hRsrc) => hRsrc >= r.Dir && hRsrc <= r.Dir + r.Size - 16 && ((hRsrc - r.Dir) & 3) == 0;

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "FindResourceA", 3, c => Find(c, c[0], c[2], c[1], 0, false, false)  /* FindResource takes (module, name, type) */);
        Std(K, "FindResourceW", 3, c => Find(c, c[0], c[2], c[1], 0, true, false));
        Std(K, "FindResourceExA", 4, c => Find(c, c[0], c[1], c[2], c[3] & 0xFFFF, false, true));
        Std(K, "FindResourceExW", 4, c => Find(c, c[0], c[1], c[2], c[3] & 0xFFFF, true, true));
        Std(K, "LoadResource", 2, c =>
        {
            if (!TryModule(c, c[0], out var r)) return 0;
            if (!InDirectory(r, c[1])) return c.Ctx.Fail(Win32Error.InvalidParameter);
            var rva = c.Mem.ReadUInt32(c[1]);
            return r.Base + rva;
        });
        Std(K, "SizeofResource", 2, c =>
        {
            if (!TryModule(c, c[0], out var r)) return 0;
            if (!InDirectory(r, c[1])) return c.Ctx.Fail(Win32Error.InvalidParameter);
            return c.Mem.ReadUInt32(c[1] + 4);
        });
        Std(K, "LockResource", 1, c => c[0]);
        Std(K, "FreeResource", 1, _ => 0);
    }
}
