using System.Runtime.CompilerServices;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>HRESULTs used by the COM subset (winerror.h).</summary>
public static class Hresult
{
    public const uint Ok = 0, False = 1, InvalidArg = 0x80070057, OutOfMemory = 0x8007000E, ChangedMode = 0x80010106,
        ClassNotReg = 0x80040154, ClassNotAvailable = 0x80040111, NotInitialized = 0x800401F0, ClassString = 0x800401F3, IidString = 0x800401F4;
}

internal sealed class OleState
{
    private static readonly ConditionalWeakTable<Win32Context, OleState> states = new();
    public static OleState For(Win32Context ctx) => states.GetValue(ctx, _ => new OleState());

    /// <summary>Per-thread apartment: 0 = none, 1 = STA (COINIT_APARTMENTTHREADED), 2 = MTA (COINIT_MULTITHREADED), plus the nesting count.</summary>
    public readonly ThreadLocal<(int Mode, int Count)> Apartment = new(() => (0, 0));
    private GuestHeap? heap;
    private readonly object gate = new();

    /// <summary>Number of CoInitialize calls on this thread not yet balanced by CoUninitialize (tests).</summary>
    public int Depth => Apartment.Value.Count;

    public GuestHeap Heap(Win32Context ctx, IWin32Memory mem)
    {
        lock (gate) return heap ??= ctx.Heaps.Register(new GuestHeap(mem, 0, 0, 0)); // owned (and disposed) by the context
    }
}

/// <summary>
/// OLE32 COM subset. Apartments are tracked per thread: the first CoInitialize/CoInitializeEx returns S_OK, a repeat with the same model S_FALSE
/// (the count still rises and must be balanced), a different model RPC_E_CHANGED_MODE (count unchanged). CoUninitialize on a thread that is not initialised is a no-op.
/// CoTaskMem* allocate from a dedicated checked guest heap; freeing or reallocating a pointer this allocator did not hand out raises a usage fault.
/// GUID text is {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} with upper-case hex. CLSIDFromString also resolves a ProgID through the virtual registry (HKCR\ProgID\CLSID).
///
/// CoCreateInstance is deliberately truthful about what this component is: it loads no COM servers. It never returns S_OK. The thread must be
/// initialised (else CO_E_NOTINITIALIZED). A class absent from HKCR\CLSID, or lacking InprocServer32 for an in-proc request, gives REGDB_E_CLASSNOTREG; a class
/// whose server key exists but cannot be loaded here gives CLASS_E_CLASSNOTAVAILABLE. *ppv is always set to NULL. Hosting an in-proc server module through an
/// explicit host registration API is out of scope (see docs/binary-win32.md).
/// Microsoft Learn: CoInitializeEx, CoUninitialize, CoCreateInstance, CoTaskMemAlloc, StringFromCLSID, CLSIDFromString, IIDFromString, StringFromGUID2.
/// </summary>
public sealed class Ole32Service : Win32Service
{
    public override string Name => "Ole32.Com";

    private const uint ClsctxInprocServer = 1, ClsctxInprocHandler = 2, ClsctxLocalServer = 4, ClsctxRemoteServer = 16;

    /// <summary>Formats the 16-byte in-memory GUID (Data1 u32, Data2 u16, Data3 u16, Data4[8]) as {8-4-4-4-12}.</summary>
    internal static string FormatGuid(ReadOnlySpan<byte> g)
    {
        var d1 = BitConverter.ToUInt32(g); var d2 = BitConverter.ToUInt16(g[4..]); var d3 = BitConverter.ToUInt16(g[6..]);
        return $"{{{d1:X8}-{d2:X4}-{d3:X4}-{g[8]:X2}{g[9]:X2}-{g[10]:X2}{g[11]:X2}{g[12]:X2}{g[13]:X2}{g[14]:X2}{g[15]:X2}}}";
    }

    /// <summary>Parses exactly {8-4-4-4-12} (any hex case) into the 16-byte in-memory layout.</summary>
    internal static bool TryParseGuid(string s, Span<byte> g)
    {
        if (s.Length != 38 || s[0] != '{' || s[37] != '}' || s[9] != '-' || s[14] != '-' || s[19] != '-' || s[24] != '-') return false;
        static bool Hex(string s, int at, int n, out uint v)
        {
            v = 0;
            for (var i = 0; i < n; i++)
            {
                var ch = s[at + i];
                var d = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
                if (d < 0) return false;
                v = v << 4 | (uint)d;
            }
            return true;
        }
        if (!Hex(s, 1, 8, out var d1) || !Hex(s, 10, 4, out var d2) || !Hex(s, 15, 4, out var d3)) return false;
        BitConverter.TryWriteBytes(g, d1); BitConverter.TryWriteBytes(g[4..], (ushort)d2); BitConverter.TryWriteBytes(g[6..], (ushort)d3);
        int[] at = [20, 22, 25, 27, 29, 31, 33, 35];
        for (var i = 0; i < 8; i++) { if (!Hex(s, at[i], 2, out var b)) return false; g[8 + i] = (byte)b; }
        return true;
    }

    private static uint Init(Win32Call c, uint reserved, uint coInit)
    {
        if (reserved != 0 || (coInit & ~0xFu) != 0) return Hresult.InvalidArg;
        var mode = (coInit & 2) != 0 ? 1 : 2;
        var cell = OleState.For(c.Ctx).Apartment;
        var (m, n) = cell.Value;
        if (n == 0) { cell.Value = (mode, 1); return Hresult.Ok; }
        if (m != mode) return Hresult.ChangedMode;
        cell.Value = (m, n + 1);
        return Hresult.False;
    }

    private static uint WriteWide(Win32Call c, uint address, string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, bytes);
        c.Mem.Write(address, bytes);
        return 0;
    }

    private static uint TaskAlloc(Win32Call c, uint size) => size > 0x7FFFFFE0 ? 0 : OleState.For(c.Ctx).Heap(c.Ctx, c.Mem).Alloc(size, false);

    protected override void Define()
    {
        const string O = "ole32.dll";
        Std(O, "CoInitialize", 1, c => Init(c, c[0], 2));
        Std(O, "CoInitializeEx", 2, c => Init(c, c[0], c[1]));
        Std(O, "CoUninitialize", 0, c =>
        {
            var cell = OleState.For(c.Ctx).Apartment;
            var (m, n) = cell.Value;
            if (n > 0) cell.Value = (n == 1 ? 0 : m, n - 1);
            return 0;
        });

        Std(O, "CoTaskMemAlloc", 1, c => TaskAlloc(c, c[0]));
        Std(O, "CoTaskMemFree", 1, c =>
        {
            if (c[0] == 0) return 0;
            if (!OleState.For(c.Ctx).Heap(c.Ctx, c.Mem).Free(c[0])) throw new Win32UsageException($"CoTaskMemFree(0x{c[0]:X8}): not a live block of the COM task allocator (foreign pointer or double free).");
            return 0;
        });
        Std(O, "CoTaskMemRealloc", 2, c =>
        {
            if (c[0] == 0) return TaskAlloc(c, c[1]);
            var heap = OleState.For(c.Ctx).Heap(c.Ctx, c.Mem);
            if (c[1] == 0)
            {
                if (!heap.Free(c[0])) throw new Win32UsageException($"CoTaskMemRealloc(0x{c[0]:X8}, 0): not a live block of the COM task allocator.");
                return 0;
            }
            if (c[1] > 0x7FFFFFE0) return 0; // too large: the original block stays valid
            switch (heap.Realloc(c[0], c[1], false, false, out var moved))
            {
                case GuestHeap.ReallocResult.Ok: return moved;
                case GuestHeap.ReallocResult.NoMemory: return 0;
                default: throw new Win32UsageException($"CoTaskMemRealloc(0x{c[0]:X8}): not a live block of the COM task allocator.");
            }
        });

        Std(O, "StringFromGUID2", 3, c =>
        {
            if (c[0] == 0 || c[1] == 0) return 0;
            var text = FormatGuid(c.Mem.ReadBytes(c[0], 16));
            if (c[2] < text.Length + 1) return 0; // buffer too small: 0, nothing written
            WriteWide(c, c[1], text);
            return (uint)text.Length + 1;
        });
        Std(O, "StringFromCLSID", 2, c => StringFromGuid(c));
        Std(O, "StringFromIID", 2, c => StringFromGuid(c));

        Std(O, "IIDFromString", 2, c =>
        {
            if (c[1] == 0) return Hresult.InvalidArg;
            if (c[0] == 0) return Hresult.InvalidArg;
            Span<byte> g = stackalloc byte[16];
            if (!TryParseGuid(c.Mem.ReadWideString(c[0], 64), g)) return Hresult.IidString;
            c.Mem.Write(c[1], g);
            return 0;
        });
        Std(O, "CLSIDFromString", 2, c =>
        {
            if (c[1] == 0) return Hresult.InvalidArg;
            Span<byte> g = stackalloc byte[16];
            if (c[0] == 0) { g.Clear(); c.Mem.Write(c[1], g); return 0; } // NULL string yields CLSID_NULL
            var s = c.Mem.ReadWideString(c[0], 4096);
            if (s.Length > 0 && s[0] == '{') { if (!TryParseGuid(s, g)) return Hresult.ClassString; }
            else
            {
                // ProgID: HKCR\<progid>\CLSID (default value) names the class
                var st = RegistryState.For(c.Ctx);
                string? text;
                lock (st.Gate)
                {
                    var node = RegistryState.SplitPath(s + "\\CLSID", out var parts) == 0 && !s.Contains('\\') ? RegistryState.Walk(st.Roots[0], parts) : null;
                    text = node?.Find("") is { } v ? Encoding.Latin1.GetString(v.Data).TrimEnd('\0') : null;
                }
                if (text is null || !TryParseGuid(text, g)) return Hresult.ClassString;
            }
            c.Mem.Write(c[1], g);
            return 0;
        });

        Std(O, "CoCreateInstance", 5, c =>
        {
            if (c[4] == 0) return Hresult.InvalidArg;
            c.Mem.WriteUInt32(c[4], 0); // *ppv is NULL on every failure
            if (c[0] == 0 || c[3] == 0) return Hresult.InvalidArg;
            if ((c[2] & (ClsctxInprocServer | ClsctxInprocHandler | ClsctxLocalServer | ClsctxRemoteServer)) == 0) return Hresult.InvalidArg;
            if (OleState.For(c.Ctx).Apartment.Value.Count == 0) return Hresult.NotInitialized;
            var clsid = FormatGuid(c.Mem.ReadBytes(c[0], 16));
            var st = RegistryState.For(c.Ctx);
            bool inproc, local;
            lock (st.Gate)
            {
                var cls = RegistryState.Walk(st.Roots[0], ["CLSID", clsid]);
                inproc = cls is not null && cls.Children.ContainsKey("InprocServer32");
                local = cls is not null && cls.Children.ContainsKey("LocalServer32");
            }
            var wantsInproc = (c[2] & (ClsctxInprocServer | ClsctxInprocHandler)) != 0;
            var wantsLocal = (c[2] & (ClsctxLocalServer | ClsctxRemoteServer)) != 0;
            if ((wantsInproc && inproc) || (wantsLocal && local)) return Hresult.ClassNotAvailable; // registered, but this component hosts no COM servers
            return Hresult.ClassNotReg;
        });
    }

    private static uint StringFromGuid(Win32Call c)
    {
        if (c[0] == 0 || c[1] == 0) return Hresult.InvalidArg;
        var text = FormatGuid(c.Mem.ReadBytes(c[0], 16));
        var p = TaskAlloc(c, (uint)(text.Length + 1) * 2);
        if (p == 0) { c.Mem.WriteUInt32(c[1], 0); return Hresult.OutOfMemory; }
        WriteWide(c, p, text);
        c.Mem.WriteUInt32(c[1], p);
        return 0;
    }
}
