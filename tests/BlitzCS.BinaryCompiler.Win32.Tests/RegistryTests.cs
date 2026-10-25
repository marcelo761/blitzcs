using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class RegistryTests
{
    private const string A = "ADVAPI32.dll";
    private const uint HKCR = 0x80000000, HKCU = 0x80000001, HKLM = 0x80000002;
    private const uint Success = 0, NotFound = 2, MoreData = 234, NoMore = 259, InvalidHandle = 6;

    private static uint Create(World w, uint root, string path)
    {
        var phk = w.Buf(4);
        Assert.Equal(Success, w.Call(A, "RegCreateKeyA", root, w.Str(path), phk));
        return w.Mem.ReadUInt32(phk);
    }

    private static uint SetSz(World w, uint key, string name, string value, uint type = 1)
    {
        var bytes = Encoding.Latin1.GetBytes(value + "\0");
        var p = w.Buf(bytes.Length); w.Mem.Write(p, bytes);
        return w.Call(A, "RegSetValueExA", key, name.Length == 0 ? 0 : w.Str(name), 0, type, p, (uint)bytes.Length);
    }

    [Fact]
    public void StartsEmptyAndOpenOfMissingKeyIsFileNotFound()
    {
        using var w = new World();
        var phk = w.Buf(4);
        w.Mem.WriteUInt32(phk, 0xDEAD);
        Assert.Equal(NotFound, w.Call(A, "RegOpenKeyExA", HKLM, w.Str("SOFTWARE\\Nothing"), 0, 0xF003F, phk));
        Assert.Equal(0xDEADu, w.Mem.ReadUInt32(phk)); // untouched on failure
        foreach (var root in new[] { HKCR, HKCU, HKLM, 0x80000003u })
            Assert.Equal(NoMore, w.Call(A, "RegEnumKeyExA", root, 0, w.Buf(300), w.Buf(4), 0, 0, 0, 0));
    }

    [Fact]
    public void CreateReportsDispositionAndCaseInsensitiveReopen()
    {
        using var w = new World();
        var phk = w.Buf(4); var disp = w.Buf(4);
        Assert.Equal(Success, w.Call(A, "RegCreateKeyExA", HKCU, w.Str("Software\\Vendor\\App"), 0, 0, 0, 0xF003F, 0, phk, disp));
        Assert.Equal(1u, w.Mem.ReadUInt32(disp)); // REG_CREATED_NEW_KEY
        var first = w.Mem.ReadUInt32(phk);
        Assert.Equal(Success, w.Call(A, "RegCreateKeyExA", HKCU, w.Str("SOFTWARE\\vendor\\APP"), 0, 0, 0, 0xF003F, 0, phk, disp));
        Assert.Equal(2u, w.Mem.ReadUInt32(disp)); // REG_OPENED_EXISTING_KEY
        Assert.NotEqual(first, w.Mem.ReadUInt32(phk)); // each open is a distinct handle
        Assert.Equal(Success, w.Call(A, "RegOpenKeyExA", HKCU, w.Str("Software\\Vendor"), 0, 0, phk));
        Assert.Equal(Success, w.Call(A, "RegCloseKey", w.Mem.ReadUInt32(phk)));
    }

    [Fact]
    public void BadPathsAreRejected()
    {
        using var w = new World();
        var phk = w.Buf(4);
        Assert.Equal(Win32Error.BadPathname, w.Call(A, "RegCreateKeyA", HKCU, w.Str("\\Leading"), phk));
        Assert.Equal(Win32Error.BadPathname, w.Call(A, "RegCreateKeyA", HKCU, w.Str("a\\\\b"), phk));
        Assert.Equal(Win32Error.InvalidParameter, w.Call(A, "RegCreateKeyA", HKCU, w.Str(new string('x', 256)), phk));
        Assert.Equal(Win32Error.InvalidParameter, w.Call(A, "RegCreateKeyA", HKCU, w.Str("ok"), 0));
    }

    [Fact]
    public void EmptySubKeyOpensANewHandleToTheSameKey()
    {
        using var w = new World();
        var k = Create(w, HKCU, "S");
        Assert.Equal(Success, SetSz(w, k, "v", "1"));
        var phk = w.Buf(4);
        Assert.Equal(Success, w.Call(A, "RegOpenKeyExA", k, 0, 0, 0, phk));
        var k2 = w.Mem.ReadUInt32(phk);
        Assert.NotEqual(k, k2);
        var cb = w.Buf(4); w.Mem.WriteUInt32(cb, 0);
        Assert.Equal(Success, w.Call(A, "RegQueryValueExA", k2, w.Str("V"), 0, 0, 0, cb));
        Assert.Equal(2u, w.Mem.ReadUInt32(cb));
    }

    [Fact]
    public void CloseSemantics()
    {
        using var w = new World();
        var k = Create(w, HKCU, "S");
        Assert.Equal(Success, w.Call(A, "RegCloseKey", k));
        Assert.Equal(InvalidHandle, w.Call(A, "RegCloseKey", k)); // handles are never reissued
        Assert.Equal(InvalidHandle, w.Call(A, "RegFlushKey", k));
        Assert.Equal(Success, w.Call(A, "RegCloseKey", HKCU)); // predefined: no effect
        Assert.Equal(Success, w.Call(A, "RegFlushKey", HKCU));
        Assert.Equal(InvalidHandle, w.Call(A, "RegCloseKey", 0x80000005)); // HKEY_CURRENT_CONFIG is not modelled
        Assert.Equal(InvalidHandle, w.Call(A, "RegCloseKey", 0x1234));
    }

    [Fact]
    public void QueryValueBufferSemantics()
    {
        using var w = new World();
        var k = Create(w, HKLM, "SOFTWARE\\T");
        Assert.Equal(Success, SetSz(w, k, "Name", "hello"));
        var type = w.Buf(4); var cb = w.Buf(4); var data = w.Buf(32);
        // size query
        Assert.Equal(Success, w.Call(A, "RegQueryValueExA", k, w.Str("name"), 0, type, 0, cb));
        Assert.Equal(1u, w.Mem.ReadUInt32(type)); Assert.Equal(6u, w.Mem.ReadUInt32(cb));
        // too small
        w.Mem.WriteUInt32(cb, 5);
        Assert.Equal(MoreData, w.Call(A, "RegQueryValueExA", k, w.Str("Name"), 0, type, data, cb));
        Assert.Equal(6u, w.Mem.ReadUInt32(cb));
        // exact
        w.Mem.WriteUInt32(cb, 6);
        Assert.Equal(Success, w.Call(A, "RegQueryValueExA", k, w.Str("Name"), 0, type, data, cb));
        Assert.Equal("hello", w.Cstr(data));
        // larger buffer: cb reports actual size
        w.Mem.WriteUInt32(cb, 32);
        Assert.Equal(Success, w.Call(A, "RegQueryValueExA", k, w.Str("Name"), 0, 0, data, cb));
        Assert.Equal(6u, w.Mem.ReadUInt32(cb));
        // data pointer without size pointer
        Assert.Equal(Win32Error.InvalidParameter, w.Call(A, "RegQueryValueExA", k, w.Str("Name"), 0, 0, data, 0));
        Assert.Equal(NotFound, w.Call(A, "RegQueryValueExA", k, w.Str("Missing"), 0, 0, 0, cb));
        Assert.Equal(NotFound, w.Call(A, "RegQueryValueExA", k, 0, 0, 0, 0, cb)); // no default value
    }

    [Fact]
    public void ValueTypesRoundTripBinary()
    {
        using var w = new World();
        var k = Create(w, HKCU, "Types");
        var dword = w.Buf(4); w.Mem.WriteUInt32(dword, 0xCAFEBABE);
        Assert.Equal(Success, w.Call(A, "RegSetValueExA", k, w.Str("d"), 0, 4, dword, 4));
        var bin = w.Buf(5); w.Mem.Write(bin, [0, 1, 2, 0, 255]);
        Assert.Equal(Success, w.Call(A, "RegSetValueExA", k, w.Str("b"), 0, 3, bin, 5));
        Assert.Equal(Success, SetSz(w, k, "m", "a\0b\0", 7));
        Assert.Equal(Success, SetSz(w, k, "e", "%PATH%", 2));
        var type = w.Buf(4); var cb = w.Buf(4); var buf = w.Buf(64);
        void Get(string n, uint t, byte[] expected)
        {
            w.Mem.WriteUInt32(cb, 64);
            Assert.Equal(Success, w.Call(A, "RegQueryValueExA", k, w.Str(n), 0, type, buf, cb));
            Assert.Equal(t, w.Mem.ReadUInt32(type));
            Assert.Equal(expected, w.Bytes(buf, (int)w.Mem.ReadUInt32(cb)));
        }
        Get("d", 4, [0xBE, 0xBA, 0xFE, 0xCA]);
        Get("b", 3, [0, 1, 2, 0, 255]);
        Get("m", 7, Encoding.Latin1.GetBytes("a\0b\0\0"));
        Get("e", 2, Encoding.Latin1.GetBytes("%PATH%\0"));
        // overwrite changes type
        Assert.Equal(Success, w.Call(A, "RegSetValueExA", k, w.Str("d"), 0, 3, bin, 2));
        Get("d", 3, [0, 1]);
        Assert.Equal(Win32Error.InvalidParameter, w.Call(A, "RegSetValueExA", k, w.Str("x"), 0, 3, 0, 4));
        Assert.Equal(Success, w.Call(A, "RegSetValueExA", k, w.Str("empty"), 0, 3, 0, 0));
        Get("empty", 3, []);
    }

    [Fact]
    public void SetValueAAndQueryValueAUseTheDefaultValueOfASubKey()
    {
        using var w = new World();
        var cb = w.Buf(4); var buf = w.Buf(32);
        Assert.Equal(Win32Error.InvalidParameter, w.Call(A, "RegSetValueA", HKCR, w.Str("x"), 4, w.Str("v"), 0));
        Assert.Equal(Success, w.Call(A, "RegSetValueA", HKCR, w.Str("CLSID\\{1}"), 1, w.Str("My Class"), 99)); // creates the sub-key; cbData ignored
        w.Mem.WriteUInt32(cb, 32);
        Assert.Equal(Success, w.Call(A, "RegQueryValueA", HKCR, w.Str("clsid\\{1}"), buf, cb));
        Assert.Equal("My Class", w.Cstr(buf)); Assert.Equal(9u, w.Mem.ReadUInt32(cb));
        w.Mem.WriteUInt32(cb, 3);
        Assert.Equal(MoreData, w.Call(A, "RegQueryValueA", HKCR, w.Str("CLSID\\{1}"), buf, cb));
        Assert.Equal(9u, w.Mem.ReadUInt32(cb));
        // existing key without a default value: empty string
        Assert.Equal(Success, w.Call(A, "RegCreateKeyA", HKCR, w.Str("Empty"), w.Buf(4)));
        w.Mem.WriteUInt32(cb, 32);
        Assert.Equal(Success, w.Call(A, "RegQueryValueA", HKCR, w.Str("Empty"), buf, cb));
        Assert.Equal("", w.Cstr(buf)); Assert.Equal(1u, w.Mem.ReadUInt32(cb));
        Assert.Equal(NotFound, w.Call(A, "RegQueryValueA", HKCR, w.Str("Nope"), buf, cb));
    }

    [Fact]
    public void EnumKeysSortedWithBufferRules()
    {
        using var w = new World();
        var root = Create(w, HKCU, "Root");
        foreach (var n in new[] { "beta", "Alpha", "gamma" }) Assert.Equal(Success, w.Call(A, "RegCreateKeyA", root, w.Str(n), w.Buf(4)));
        var name = w.Buf(32); var cch = w.Buf(4); var ft = w.Buf(8);
        var seen = new List<string>();
        for (uint i = 0; ; i++)
        {
            w.Mem.WriteUInt32(cch, 32);
            var r = w.Call(A, "RegEnumKeyExA", root, i, name, cch, 0, 0, 0, ft);
            if (r == NoMore) break;
            Assert.Equal(Success, r);
            seen.Add(w.Cstr(name)); Assert.Equal((uint)seen[^1].Length, w.Mem.ReadUInt32(cch));
        }
        Assert.Equal(["Alpha", "beta", "gamma"], seen);
        w.Mem.WriteUInt32(cch, 5); // "Alpha" needs 6 including the terminator
        Assert.Equal(MoreData, w.Call(A, "RegEnumKeyExA", root, 0, name, cch, 0, 0, 0, 0));
        w.Mem.WriteUInt32(cch, 6);
        Assert.Equal(Success, w.Call(A, "RegEnumKeyExA", root, 0, name, cch, 0, 0, 0, ft));
        Assert.NotEqual(0ul, BitConverter.ToUInt64(w.Bytes(ft, 8)));
        Assert.Equal(Success, w.Call(A, "RegEnumKeyA", root, 2, name, 32));
        Assert.Equal("gamma", w.Cstr(name));
    }

    [Fact]
    public void EnumValuesIncludeDefaultAndReportMoreData()
    {
        using var w = new World();
        var k = Create(w, HKCU, "V");
        Assert.Equal(Success, SetSz(w, k, "", "dflt"));
        Assert.Equal(Success, SetSz(w, k, "second", "xyz"));
        var name = w.Buf(32); var cch = w.Buf(4); var type = w.Buf(4); var data = w.Buf(32); var cb = w.Buf(4);
        w.Mem.WriteUInt32(cch, 32); w.Mem.WriteUInt32(cb, 32);
        Assert.Equal(Success, w.Call(A, "RegEnumValueA", k, 0, name, cch, 0, type, data, cb));
        Assert.Equal("", w.Cstr(name)); Assert.Equal("dflt", w.Cstr(data)); Assert.Equal(5u, w.Mem.ReadUInt32(cb));
        w.Mem.WriteUInt32(cch, 32); w.Mem.WriteUInt32(cb, 2);
        Assert.Equal(MoreData, w.Call(A, "RegEnumValueA", k, 1, name, cch, 0, type, data, cb));
        Assert.Equal("second", w.Cstr(name)); Assert.Equal(4u, w.Mem.ReadUInt32(cb));
        w.Mem.WriteUInt32(cch, 3);
        Assert.Equal(MoreData, w.Call(A, "RegEnumValueA", k, 1, name, cch, 0, type, 0, 0));
        w.Mem.WriteUInt32(cch, 32);
        Assert.Equal(NoMore, w.Call(A, "RegEnumValueA", k, 2, name, cch, 0, 0, 0, 0));
    }

    [Fact]
    public void DeleteRulesAndDeletedHandles()
    {
        using var w = new World();
        var root = Create(w, HKCU, "D");
        var child = Create(w, root, "child");
        Assert.Equal(Success, w.Call(A, "RegCreateKeyA", child, w.Str("grand"), w.Buf(4)));
        Assert.Equal(Win32Error.AccessDenied, w.Call(A, "RegDeleteKeyA", root, w.Str("child"))); // has a sub-key
        Assert.Equal(Success, w.Call(A, "RegDeleteKeyA", child, w.Str("grand")));
        Assert.Equal(NotFound, w.Call(A, "RegDeleteKeyA", child, w.Str("grand")));
        Assert.Equal(Success, SetSz(w, child, "v", "1"));
        Assert.Equal(Success, w.Call(A, "RegDeleteValueA", child, w.Str("V")));
        Assert.Equal(NotFound, w.Call(A, "RegDeleteValueA", child, w.Str("v")));
        Assert.Equal(Success, w.Call(A, "RegDeleteKeyA", root, w.Str("child")));
        Assert.Equal(1018u, SetSz(w, child, "v", "1")); // ERROR_KEY_DELETED
        Assert.Equal(Success, w.Call(A, "RegCloseKey", child));
        Assert.Equal(NotFound, w.Call(A, "RegOpenKeyExA", root, w.Str("child"), 0, 0, w.Buf(4)));
    }

    [Fact]
    public void PersistsToTheSandboxRootAndReloadsInAnotherResolver()
    {
        var dir = Directory.CreateTempSubdirectory("blitzcs-reg").FullName;
        try
        {
            using (var w = new World(new Win32Options { SandboxRoot = dir }))
            {
                var k = Create(w, HKLM, "SOFTWARE\\Persist");
                var bin = w.Buf(3); w.Mem.Write(bin, [9, 0, 8]);
                Assert.Equal(Success, w.Call(A, "RegSetValueExA", k, w.Str("blob"), 0, 3, bin, 3));
            }
            Assert.True(File.Exists(Path.Combine(dir, ".blitzcs-registry.json")));
            using (var w = new World(new Win32Options { SandboxRoot = dir }))
            {
                var phk = w.Buf(4);
                Assert.Equal(Success, w.Call(A, "RegOpenKeyExA", HKLM, w.Str("software\\persist"), 0, 0, phk));
                var cb = w.Buf(4); var buf = w.Buf(8); w.Mem.WriteUInt32(cb, 8);
                Assert.Equal(Success, w.Call(A, "RegQueryValueExA", w.Mem.ReadUInt32(phk), w.Str("blob"), 0, 0, buf, cb));
                Assert.Equal(new byte[] { 9, 0, 8 }, w.Bytes(buf, 3));
            }
            File.WriteAllText(Path.Combine(dir, ".blitzcs-registry.json"), "not json");
            Assert.Throws<Win32UsageException>(() => new World(new Win32Options { SandboxRoot = dir }).Call("ADVAPI32.dll", "RegFlushKey", 0x80000000));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WithoutSandboxRootNothingTouchesTheDisk()
    {
        using var w = new World();
        Create(w, HKCU, "Mem");
        Assert.False(File.Exists(".blitzcs-registry.json"));
    }
}
