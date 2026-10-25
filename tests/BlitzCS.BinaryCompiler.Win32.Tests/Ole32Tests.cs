using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

public sealed class Ole32Tests
{
    private const string O = "ole32.dll";
    private static readonly Guid Sample = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static uint GuidMem(World w, Guid g) { var p = w.Buf(16); w.Mem.Write(p, g.ToByteArray()); return p; }
    private static string Wide(World w, uint p) => w.Mem.ReadWideString(p);

    [Fact]
    public void ApartmentBookkeeping()
    {
        using var w = new World();
        Assert.Equal(0u, w.Call(O, "CoInitialize", 0));        // first: S_OK (STA)
        Assert.Equal(1u, w.Call(O, "CoInitialize", 0));        // same model again: S_FALSE
        Assert.Equal(1u, w.Call(O, "CoInitializeEx", 0, 2));   // STA again
        Assert.Equal(0x80010106u, w.Call(O, "CoInitializeEx", 0, 0)); // MTA on an STA thread: RPC_E_CHANGED_MODE
        Assert.Equal(0x80070057u, w.Call(O, "CoInitializeEx", w.Buf(4), 2)); // reserved must be NULL
        Assert.Equal(0x80070057u, w.Call(O, "CoInitializeEx", 0, 0x10));
        Assert.Equal(0x80070057u, w.Call(O, "CoInitialize", 4));
        for (var i = 0; i < 3; i++) w.Call(O, "CoUninitialize");  // balances the three successful calls
        w.Call(O, "CoUninitialize");                               // extra: no-op
        Assert.Equal(0u, w.Call(O, "CoInitializeEx", 0, 0));      // now free to pick MTA
        Assert.Equal(0x80010106u, w.Call(O, "CoInitialize", 0));
        w.Call(O, "CoUninitialize");
    }

    [Fact]
    public void ApartmentsArePerThread()
    {
        using var w = new World();
        Assert.Equal(0u, w.Call(O, "CoInitialize", 0));
        uint other = 99;
        var t = new Thread(() => other = w.Call(O, "CoInitializeEx", 0, 0));
        t.Start(); t.Join();
        Assert.Equal(0u, other); // this thread's STA does not affect the other thread
    }

    [Fact]
    public void TaskMemoryIsCheckedAndReallocPreservesData()
    {
        using var w = new World();
        var p = w.Call(O, "CoTaskMemAlloc", 16);
        Assert.NotEqual(0u, p);
        w.Mem.Write(p, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        var q = w.Call(O, "CoTaskMemRealloc", p, 100000);
        Assert.NotEqual(0u, q);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), w.Bytes(q, 16));
        Assert.NotEqual(0u, w.Call(O, "CoTaskMemAlloc", 0));             // zero-length item has a valid pointer
        Assert.Equal(0u, w.Call(O, "CoTaskMemAlloc", 0xFFFFFFF0));       // impossible request
        Assert.NotEqual(0u, w.Call(O, "CoTaskMemRealloc", 0, 8));        // NULL acts like alloc
        Assert.Equal(0u, w.Call(O, "CoTaskMemRealloc", q, 0));           // size 0 frees
        Assert.Throws<Win32UsageException>(() => w.Call(O, "CoTaskMemFree", q)); // double free
        Assert.Throws<Win32UsageException>(() => w.Call(O, "CoTaskMemFree", w.Buf(16)));
        Assert.Equal(0u, w.Call(O, "CoTaskMemFree", 0));
    }

    [Fact]
    public void StringFromGuidFormatsAgainstDotNet()
    {
        using var w = new World();
        var g = GuidMem(w, Sample);
        var pp = w.Buf(4);
        Assert.Equal(0u, w.Call(O, "StringFromCLSID", g, pp));
        Assert.Equal("{00112233-4455-6677-8899-AABBCCDDEEFF}", Wide(w, w.Mem.ReadUInt32(pp)));
        Assert.Equal(Sample.ToString("B").ToUpperInvariant(), Wide(w, w.Mem.ReadUInt32(pp)));
        Assert.Equal(0u, w.Call(O, "CoTaskMemFree", w.Mem.ReadUInt32(pp)));      // allocated by CoTaskMemAlloc
        var buf = w.Buf(80);
        Assert.Equal(39u, w.Call(O, "StringFromGUID2", g, buf, 39));
        Assert.Equal("{00112233-4455-6677-8899-AABBCCDDEEFF}", Wide(w, buf));
        Assert.Equal(0u, w.Call(O, "StringFromGUID2", g, buf, 38));                 // too small
        Assert.Equal(0x80070057u, w.Call(O, "StringFromCLSID", 0, pp));
    }

    [Fact]
    public void ParseGuidStrings()
    {
        using var w = new World();
        var o = w.Buf(16);
        Assert.Equal(0u, w.Call(O, "IIDFromString", w.Wide("{00112233-4455-6677-8899-aabbccddeeff}"), o));
        Assert.Equal(Sample.ToByteArray(), w.Bytes(o, 16));
        Assert.Equal(0x800401F4u, w.Call(O, "IIDFromString", w.Wide("00112233-4455-6677-8899-aabbccddeeff"), o));
        Assert.Equal(0x800401F4u, w.Call(O, "IIDFromString", w.Wide("{0011223-34455-6677-8899-aabbccddeeff}"), o));
        Assert.Equal(0x800401F4u, w.Call(O, "IIDFromString", w.Wide("{0011223G-4455-6677-8899-aabbccddeeff}"), o));
        Assert.Equal(0u, w.Call(O, "CLSIDFromString", w.Wide("{00112233-4455-6677-8899-AABBCCDDEEFF}"), o));
        Assert.Equal(0x800401F3u, w.Call(O, "CLSIDFromString", w.Wide("{nope}"), o));
        Assert.Equal(0x800401F3u, w.Call(O, "CLSIDFromString", w.Wide("Unknown.ProgId"), o));
        w.Mem.Write(o, Enumerable.Repeat((byte)7, 16).ToArray());
        Assert.Equal(0u, w.Call(O, "CLSIDFromString", 0, o));
        Assert.Equal(new byte[16], w.Bytes(o, 16)); // CLSID_NULL
    }

    [Fact]
    public void ClsidFromProgIdReadsTheVirtualRegistry()
    {
        using var w = new World();
        const string A = "ADVAPI32.dll";
        Assert.Equal(0u, w.Call(A, "RegSetValueA", 0x80000000, w.Str("Vendor.Thing\\CLSID"), 1, w.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), 0));
        var o = w.Buf(16);
        Assert.Equal(0u, w.Call(O, "CLSIDFromString", w.Wide("Vendor.Thing"), o));
        Assert.Equal(Sample.ToByteArray(), w.Bytes(o, 16));
    }

    [Fact]
    public void CoCreateInstanceNeverSucceedsAndIsTruthfulAboutWhy()
    {
        using var w = new World();
        const string A = "ADVAPI32.dll";
        var clsid = GuidMem(w, Sample); var iid = GuidMem(w, Guid.Empty); var ppv = w.Buf(4);
        w.Mem.WriteUInt32(ppv, 0xBAD);
        Assert.Equal(0x800401F0u, w.Call(O, "CoCreateInstance", clsid, 0, 1, iid, ppv)); // CO_E_NOTINITIALIZED
        Assert.Equal(0u, w.Mem.ReadUInt32(ppv));
        w.Call(O, "CoInitialize", 0);
        Assert.Equal(0x80040154u, w.Call(O, "CoCreateInstance", clsid, 0, 1, iid, ppv)); // REGDB_E_CLASSNOTREG
        Assert.Equal(0x80070057u, w.Call(O, "CoCreateInstance", clsid, 0, 0, iid, ppv)); // no context
        Assert.Equal(0x80070057u, w.Call(O, "CoCreateInstance", clsid, 0, 1, iid, 0));
        // registering the class in the virtual registry does not make a server appear
        Assert.Equal(0u, w.Call(A, "RegSetValueA", 0x80000000, w.Str("CLSID\\{00112233-4455-6677-8899-AABBCCDDEEFF}\\InprocServer32"), 1, w.Str("C:\\x.dll"), 0));
        Assert.Equal(0x80040111u, w.Call(O, "CoCreateInstance", clsid, 0, 1, iid, ppv)); // CLASS_E_CLASSNOTAVAILABLE
        Assert.Equal(0x80040154u, w.Call(O, "CoCreateInstance", clsid, 0, 4, iid, ppv)); // no LocalServer32
        Assert.Equal(0u, w.Mem.ReadUInt32(ppv));
    }
}
