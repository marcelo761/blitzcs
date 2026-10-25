using System.Text.Json;
using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Win32;

internal sealed class RegValue(string name, uint type, byte[] data)
{
    public string Name = name; public uint Type = type; public byte[] Data = data;
}

internal sealed class RegNode(string name, RegNode? parent)
{
    public readonly string Name = name;
    public readonly RegNode? Parent = parent;
    public readonly Dictionary<string, RegNode> Children = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<RegValue> Values = [];
    public bool Deleted;
    public long LastWrite = DateTime.UtcNow.ToFileTimeUtc();

    public RegValue? Find(string valueName) => Values.Find(v => string.Equals(v.Name, valueName, StringComparison.OrdinalIgnoreCase));

    public string FullPath => Parent is null ? Name : Parent.Parent is null ? Name : Parent.FullPath + "\\" + Name;
}

/// <summary>An open key handle (what RegOpenKeyEx/RegCreateKeyEx hand out). It refers to the node, not to a path, so it survives renames of nothing and reports ERROR_KEY_DELETED after RegDeleteKey.</summary>
internal sealed class RegKeyHandle(RegNode node) { public readonly RegNode Node = node; }

/// <summary>The virtual registry of one emulated process: four predefined roots, empty by default. Optionally persisted to a file under the sandbox root.</summary>
internal sealed class RegistryState
{
    public const uint HkeyClassesRoot = 0x80000000, HkeyCurrentUser = 0x80000001, HkeyLocalMachine = 0x80000002, HkeyUsers = 0x80000003;
    public const string PersistFileName = ".blitzcs-registry.json";
    private static readonly ConditionalWeakTable<Win32Context, RegistryState> states = new();

    public readonly object Gate = new();
    public readonly RegNode[] Roots =
    [
        new("HKEY_CLASSES_ROOT", null), new("HKEY_CURRENT_USER", null), new("HKEY_LOCAL_MACHINE", null), new("HKEY_USERS", null),
    ];
    private readonly string? file;

    public static RegistryState For(Win32Context ctx) => states.GetValue(ctx, c => new RegistryState(c.Options.SandboxRoot));

    private RegistryState(string? sandboxRoot)
    {
        if (sandboxRoot is null) return;
        file = Path.Combine(sandboxRoot, PersistFileName);
        if (File.Exists(file))
        {
            try { Load(); }
            catch (Exception e) when (e is JsonException or FormatException or IOException) { throw new Win32UsageException($"The persisted virtual registry '{file}' is corrupt: {e.Message}"); }
        }
    }

    // ---- persistence ----
    private sealed class JNode { public string Name { get; set; } = ""; public List<JValue> Values { get; set; } = []; public List<JNode> Children { get; set; } = []; }
    private sealed class JValue { public string Name { get; set; } = ""; public uint Type { get; set; } public string Data { get; set; } = ""; }

    private static JNode ToJ(RegNode n) => new()
    {
        Name = n.Name,
        Values = [.. n.Values.Select(v => new JValue { Name = v.Name, Type = v.Type, Data = Convert.ToBase64String(v.Data) })],
        Children = [.. n.Children.Values.Select(ToJ)],
    };

    private static void FromJ(JNode j, RegNode n)
    {
        foreach (var v in j.Values) n.Values.Add(new RegValue(v.Name, v.Type, Convert.FromBase64String(v.Data)));
        foreach (var c in j.Children) { var child = new RegNode(c.Name, n); n.Children[c.Name] = child; FromJ(c, child); }
    }

    private void Load()
    {
        var roots = JsonSerializer.Deserialize<List<JNode>>(File.ReadAllText(file!)) ?? [];
        foreach (var j in roots)
            foreach (var r in Roots)
                if (string.Equals(r.Name, j.Name, StringComparison.Ordinal)) FromJ(j, r);
    }

    /// <summary>Writes the whole tree (caller holds <see cref="Gate"/>). No-op without a sandbox root.</summary>
    public void Persist()
    {
        if (file is null) return;
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Roots.Select(ToJ).ToList()));
        File.Move(tmp, file, true);
    }

    // ---- tree operations (caller holds Gate) ----

    /// <summary>Splits a sub-key path. Empty/NULL means "the key itself". A leading, trailing or doubled backslash is ERROR_BAD_PATHNAME; a component over 255 characters is ERROR_INVALID_PARAMETER.</summary>
    public static uint SplitPath(string? path, out string[] parts)
    {
        parts = [];
        if (string.IsNullOrEmpty(path)) return Win32Error.Success;
        var p = path.Split('\\');
        foreach (var s in p)
        {
            if (s.Length == 0) return Win32Error.BadPathname;
            if (s.Length > 255) return Win32Error.InvalidParameter;
        }
        parts = p;
        return Win32Error.Success;
    }

    public static RegNode? Walk(RegNode start, string[] parts)
    {
        var n = start;
        foreach (var p in parts) if (!n.Children.TryGetValue(p, out n)) return null;
        return n;
    }

    public static RegNode Create(RegNode start, string[] parts, out bool createdAny)
    {
        createdAny = false;
        var n = start;
        foreach (var p in parts)
        {
            if (!n.Children.TryGetValue(p, out var next))
            {
                next = new RegNode(p, n);
                n.Children[p] = next;
                n.LastWrite = next.LastWrite = DateTime.UtcNow.ToFileTimeUtc();
                createdAny = true;
            }
            n = next;
        }
        return n;
    }

    /// <summary>Test/host helper: the sub-key at <paramref name="path"/> under a root, or null.</summary>
    public RegNode? Lookup(uint root, string path)
    {
        lock (Gate)
        {
            if (SplitPath(path, out var parts) != 0) return null;
            return Walk(Roots[(int)(root - HkeyClassesRoot)], parts);
        }
    }
}

/// <summary>
/// ADVAPI32 registry API (ANSI variants) over an in-memory virtual registry, one per resolver. The registry is EMPTY by default (a clean machine): only the four
/// predefined roots HKEY_CLASSES_ROOT, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE and HKEY_USERS exist. Nothing is read from or written to the host registry. When
/// <see cref="Win32Options.SandboxRoot"/> is set the tree is loaded from and saved to the file ".blitzcs-registry.json" in it (saved after every mutation; the file
/// is visible to the guest's file APIs). Return values are the LONG error codes themselves (not SetLastError): ERROR_SUCCESS, ERROR_FILE_NOT_FOUND,
/// ERROR_MORE_DATA, ERROR_NO_MORE_ITEMS, ERROR_INVALID_HANDLE, ERROR_KEY_DELETED, ERROR_ACCESS_DENIED (delete of a key that has sub-keys).
/// Documented simplifications: HKEY_CLASSES_ROOT is a standalone root, not a merged view of HKLM\Software\Classes and HKCU\Software\Classes; security attributes,
/// access masks and REG_OPTION_VOLATILE are accepted and ignored; the other predefined keys (HKEY_CURRENT_CONFIG, ...) are not modelled and give ERROR_INVALID_HANDLE;
/// key names use '\' separators, no leading/trailing/doubled backslash (ERROR_BAD_PATHNAME); sub-keys enumerate in case-insensitive ordinal order, values in creation order;
/// REG_SZ data is stored exactly as the caller gave it (Windows does not add a terminator); the wide (W) variants are not implemented.
/// Microsoft Learn: Registry Functions; RegQueryValueEx, RegEnumKeyEx, RegEnumValue, RegCreateKeyEx, RegSetValueEx, RegDeleteKey.
/// </summary>
public sealed class Advapi32RegistryService : Win32Service
{
    public override string Name => "Advapi32.Registry";

    private const uint RegSz = 1;
    private const uint ErrorMoreData = 234, ErrorKeyDeleted = 1018, ErrorAccessDenied = Win32Error.AccessDenied;

    private static uint Node(Win32Call c, uint hKey, out RegNode node)
    {
        var st = RegistryState.For(c.Ctx);
        node = null!;
        if (hKey is >= RegistryState.HkeyClassesRoot and <= RegistryState.HkeyUsers) { node = st.Roots[(int)(hKey - RegistryState.HkeyClassesRoot)]; return 0; }
        if (c.Ctx.GetHandle<RegKeyHandle>(hKey) is not { } h) return Win32Error.InvalidHandle;
        if (h.Node.Deleted) return ErrorKeyDeleted;
        node = h.Node;
        return 0;
    }

    private static string? Str(Win32Call c, uint ptr) => ptr == 0 ? null : c.Mem.ReadAnsiString(ptr, 65536);

    private static uint OpenCommon(Win32Call c, uint hKey, uint subKeyPtr, uint phk, bool create, uint dispositionPtr)
    {
        if (phk == 0) return Win32Error.InvalidParameter;
        var st = RegistryState.For(c.Ctx);
        var sub = Str(c, subKeyPtr);
        lock (st.Gate)
        {
            if (Node(c, hKey, out var parent) is var e and not 0) return e;
            if (RegistryState.SplitPath(sub, out var parts) is var pe and not 0) return pe;
            RegNode? n; var created = false;
            if (create) { n = RegistryState.Create(parent, parts, out created); if (created) st.Persist(); }
            else if ((n = RegistryState.Walk(parent, parts)) is null) return Win32Error.FileNotFound;
            c.Mem.WriteUInt32(phk, c.Ctx.NewHandle(new RegKeyHandle(n)));
            if (dispositionPtr != 0) c.Mem.WriteUInt32(dispositionPtr, created ? 1u : 2u); // REG_CREATED_NEW_KEY / REG_OPENED_EXISTING_KEY
            return 0;
        }
    }

    /// <summary>Copies <paramref name="data"/> following RegQueryValueEx/RegEnumValue buffer rules: size always reported when the pointer exists; ERROR_MORE_DATA when the buffer is too small (nothing copied).</summary>
    private static uint CopyData(Win32Call c, byte[] data, uint dataPtr, uint cbPtr)
    {
        if (dataPtr != 0 && cbPtr == 0) return Win32Error.InvalidParameter;
        if (cbPtr == 0) return 0;
        var capacity = c.Mem.ReadUInt32(cbPtr);
        c.Mem.WriteUInt32(cbPtr, (uint)data.Length);
        if (dataPtr == 0) return 0;
        if (capacity < data.Length) return ErrorMoreData;
        c.Mem.Write(dataPtr, data);
        return 0;
    }

    private static uint SetValue(Win32Call c, RegNode node, string name, uint type, byte[] data)
    {
        if (name.Length > 16383) return Win32Error.InvalidParameter;
        var v = node.Find(name);
        if (v is null) node.Values.Add(new RegValue(name, type, data)); else { v.Type = type; v.Data = data; }
        node.LastWrite = DateTime.UtcNow.ToFileTimeUtc();
        RegistryState.For(c.Ctx).Persist();
        return 0;
    }

    /// <summary>One entry of <see cref="Snapshot"/>: a key (<see cref="ValueName"/> null) or one of its values. <see cref="Path"/> starts with the root name.</summary>
    public sealed record RegistryEntry(string Path, string? ValueName, uint Type, byte[] Data);

    /// <summary>Host view of the whole virtual registry (depth-first, sub-keys in case-insensitive order). Used by hosts and tests to check what a guest wrote.</summary>
    public static IReadOnlyList<RegistryEntry> Snapshot(Win32Context ctx)
    {
        var st = RegistryState.For(ctx);
        var list = new List<RegistryEntry>();
        void Visit(RegNode n, string path)
        {
            if (n.Parent is not null) list.Add(new RegistryEntry(path, null, 0, []));
            foreach (var v in n.Values) list.Add(new RegistryEntry(path, v.Name, v.Type, [.. v.Data]));
            foreach (var k in n.Children.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)) Visit(k, path + "\\" + k.Name);
        }
        lock (st.Gate) foreach (var r in st.Roots) Visit(r, r.Name);
        return list;
    }

    protected override void Define()
    {
        const string A = "ADVAPI32.dll";
        Std(A, "RegOpenKeyExA", 5, c => OpenCommon(c, c[0], c[1], c[4], false, 0));
        Std(A, "RegOpenKeyA", 3, c => OpenCommon(c, c[0], c[1], c[2], false, 0));
        Std(A, "RegCreateKeyA", 3, c => OpenCommon(c, c[0], c[1], c[2], true, 0));
        Std(A, "RegCreateKeyExA", 9, c => OpenCommon(c, c[0], c[1], c[7], true, c[8]));

        Std(A, "RegCloseKey", 1, c =>
        {
            if (c[0] is >= RegistryState.HkeyClassesRoot and <= RegistryState.HkeyUsers) return 0; // closing a predefined key has no effect
            return c.Ctx.RemoveHandle(c[0], out var o) && o is RegKeyHandle ? 0u : Win32Error.InvalidHandle;
        });

        Std(A, "RegFlushKey", 1, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate) { if (Node(c, c[0], out _) is var e and not 0) return e; st.Persist(); return 0; }
        });

        Std(A, "RegSetValueExA", 6, c =>
        {
            var st = RegistryState.For(c.Ctx);
            var name = Str(c, c[1]) ?? "";
            if (c[5] > 0 && c[4] == 0) return Win32Error.InvalidParameter;
            var data = c[5] == 0 ? [] : c.Mem.ReadBytes(c[4], checked((int)c[5]));
            lock (st.Gate) { if (Node(c, c[0], out var n) is var e and not 0) return e; return SetValue(c, n, name, c[3], data); }
        });

        Std(A, "RegSetValueA", 5, c =>
        {
            if (c[2] != RegSz) return Win32Error.InvalidParameter; // only REG_SZ is allowed by RegSetValue
            if (c[3] == 0) return Win32Error.InvalidParameter;
            var st = RegistryState.For(c.Ctx);
            var text = c.Mem.ReadCString(c[3]);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var parent) is var e and not 0) return e;
                if (RegistryState.SplitPath(Str(c, c[1]), out var parts) is var pe and not 0) return pe;
                var n = RegistryState.Create(parent, parts, out _); // a missing sub-key is created
                return SetValue(c, n, "", RegSz, [.. text, (byte)0]); // cbData is ignored: the string's own terminator decides
            }
        });

        Std(A, "RegQueryValueExA", 6, c =>
        {
            var st = RegistryState.For(c.Ctx);
            var name = Str(c, c[1]) ?? "";
            lock (st.Gate)
            {
                if (Node(c, c[0], out var n) is var e and not 0) return e;
                var v = n.Find(name);
                if (v is null) return Win32Error.FileNotFound;
                if (c[3] != 0) c.Mem.WriteUInt32(c[3], v.Type);
                return CopyData(c, v.Data, c[4], c[5]);
            }
        });

        Std(A, "RegQueryValueA", 4, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var parent) is var e and not 0) return e;
                if (RegistryState.SplitPath(Str(c, c[1]), out var parts) is var pe and not 0) return pe;
                var n = RegistryState.Walk(parent, parts);
                if (n is null) return Win32Error.FileNotFound;
                // a key without a default value yields an empty string (documented behaviour of RegQueryValue, also in Wine/ReactOS)
                return CopyData(c, n.Find("")?.Data ?? [0], c[2], c[3]);
            }
        });

        Std(A, "RegEnumKeyExA", 8, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var n) is var e and not 0) return e;
                if (c[2] == 0 || c[3] == 0) return Win32Error.InvalidParameter;
                var keys = n.Children.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (c[1] >= keys.Count) return Win32Error.NoMoreItems;
                var k = keys[(int)c[1]];
                if (c.Mem.ReadUInt32(c[3]) < k.Name.Length + 1) return ErrorMoreData; // size is in characters including the terminator; lpcchName is left unchanged
                c.Mem.WriteCString(c[2], k.Name);
                c.Mem.WriteUInt32(c[3], (uint)k.Name.Length);
                if (c[5] != 0 && c[6] != 0) { if (c.Mem.ReadUInt32(c[6]) >= 1) c.Mem.WriteByte(c[5], 0); c.Mem.WriteUInt32(c[6], 0); } // keys here have no class
                if (c[7] != 0) c.Mem.WriteUInt64(c[7], (ulong)k.LastWrite);
                return 0;
            }
        });

        Std(A, "RegEnumKeyA", 4, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var n) is var e and not 0) return e;
                if (c[2] == 0) return Win32Error.InvalidParameter;
                var keys = n.Children.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (c[1] >= keys.Count) return Win32Error.NoMoreItems;
                var k = keys[(int)c[1]];
                if (c[3] < k.Name.Length + 1) return ErrorMoreData;
                c.Mem.WriteCString(c[2], k.Name);
                return 0;
            }
        });

        Std(A, "RegEnumValueA", 8, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var n) is var e and not 0) return e;
                if (c[2] == 0 || c[3] == 0) return Win32Error.InvalidParameter;
                if (c[1] >= n.Values.Count) return Win32Error.NoMoreItems;
                var v = n.Values[(int)c[1]];
                if (c.Mem.ReadUInt32(c[3]) < v.Name.Length + 1) return ErrorMoreData;
                c.Mem.WriteCString(c[2], v.Name);
                c.Mem.WriteUInt32(c[3], (uint)v.Name.Length);
                if (c[5] != 0) c.Mem.WriteUInt32(c[5], v.Type);
                return CopyData(c, v.Data, c[6], c[7]);
            }
        });

        Std(A, "RegDeleteKeyA", 2, c =>
        {
            var st = RegistryState.For(c.Ctx);
            lock (st.Gate)
            {
                if (Node(c, c[0], out var parent) is var e and not 0) return e;
                if (c[1] == 0) return Win32Error.InvalidParameter;
                if (RegistryState.SplitPath(Str(c, c[1]), out var parts) is var pe and not 0) return pe;
                if (parts.Length == 0) return Win32Error.InvalidParameter;
                var n = RegistryState.Walk(parent, parts);
                if (n is null) return Win32Error.FileNotFound;
                if (n.Children.Count > 0) return ErrorAccessDenied; // RegDeleteKey cannot delete a key that has sub-keys
                n.Deleted = true;
                n.Parent!.Children.Remove(n.Name);
                n.Parent.LastWrite = DateTime.UtcNow.ToFileTimeUtc();
                st.Persist();
                return 0;
            }
        });

        Std(A, "RegDeleteValueA", 2, c =>
        {
            var st = RegistryState.For(c.Ctx);
            var name = Str(c, c[1]) ?? "";
            lock (st.Gate)
            {
                if (Node(c, c[0], out var n) is var e and not 0) return e;
                var v = n.Find(name);
                if (v is null) return Win32Error.FileNotFound;
                n.Values.Remove(v);
                n.LastWrite = DateTime.UtcNow.ToFileTimeUtc();
                st.Persist();
                return 0;
            }
        });
    }
}
