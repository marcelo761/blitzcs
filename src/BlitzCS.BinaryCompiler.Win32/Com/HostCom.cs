using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Win32.Com;

/// <summary>Unmanaged scratch memory that lives for one adapted call: 64-bit-addressable copies of guest structures handed to a native API, freed together.</summary>
public sealed unsafe class NativeScope : IDisposable
{
    private readonly List<nint> blocks = [];
    public nint AllocZeroed(int size)
    {
        var p = (nint)NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));
        blocks.Add(p);
        return p;
    }
    public nint Copy(ReadOnlySpan<byte> bytes)
    {
        var p = AllocZeroed(bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
        return p;
    }
    public void Dispose() { foreach (var b in blocks) NativeMemory.Free((void*)b); blocks.Clear(); }
}

/// <summary>
/// A native 64-bit COM interface pointer, called through its vtable with the host's native calling convention (Win64 on Windows: <c>this</c> in the first register, 64-bit
/// pointer-sized arguments). Every argument is passed as a pointer-sized integer: 32-bit values zero-extend, signed ones sign-extend (see <see cref="ComArgs"/>). The
/// returned HRESULT is the host's own, unchanged. Nothing in this class knows an interface: the caller names the slot.
/// </summary>
public sealed unsafe class HostComInterface
{
    private nint pointer;

    public HostComInterface(nint pointer, string name)
    {
        if (pointer == 0) throw new ArgumentException("A native COM interface pointer is NULL.", nameof(pointer));
        this.pointer = pointer; Name = name;
    }

    public string Name { get; }
    public nint Pointer => pointer != 0 ? pointer : throw new ObjectDisposedException(Name, "The native COM interface was already released.");
    public bool IsReleased => pointer == 0;

    /// <summary>Calls vtable slot <paramref name="slot"/> (0 QueryInterface, 1 AddRef, 2 Release, then the interface's methods) with <c>this</c> first. Up to 10 arguments.</summary>
    public uint Invoke(int slot, params ReadOnlySpan<nint> args)
    {
        var self = Pointer;
        var f = (*(nint**)self)[slot];
        if (args.Length > 8) throw new NotSupportedException($"{Name}: a native COM call with {args.Length} arguments is not supported (maximum 8).");
        Span<nint> all = stackalloc nint[1 + args.Length];
        all[0] = self;
        args.CopyTo(all[1..]);
        return (uint)HostBridge.HostInvoke.Call(f, all);
    }

    public uint QueryInterface(Guid iid, out nint result)
    {
        nint outPointer = 0;
        var iidCopy = iid;
        var hr = Invoke(0, (nint)(&iidCopy), (nint)(&outPointer));
        result = ComHr.Succeeded(hr) ? outPointer : 0;
        return hr;
    }

    public uint AddRef() => Invoke(1);

    /// <summary>Releases the native reference this wrapper owns; the wrapper is unusable afterwards. A second call is a no-op (the wrapper owns exactly one reference).</summary>
    public uint Release()
    {
        var p = Interlocked.Exchange(ref pointer, 0);
        if (p == 0) return 0;
        var f = (*(nint**)p)[2];
        return (uint)((delegate* unmanaged<nint, int>)f)(p);
    }
}

/// <summary>
/// Per-call state of an adapted host call: a COM method (<see cref="Call"/> is the guest COM call) or a flat DLL export of the host bridge (no COM object; the status passed to the
/// completions is an HRESULT-style value, S_OK when the export reported success).
/// </summary>
public sealed class ComCallScope : IDisposable
{
    private readonly List<Action<uint>> completions = [];
    private readonly GuestComCall? call;
    internal ComCallScope(GuestComCall call) { this.call = call; Mem = call.Mem; Ctx = call.Ctx; }
    internal ComCallScope(IWin32Memory mem, Win32Context ctx, uint[] guestArgs) { Mem = mem; Ctx = ctx; GuestArgs = guestArgs; }
    public GuestComCall Call => call ?? throw new InvalidOperationException("This adapted call is a flat export, not a COM method.");
    /// <summary>All guest argument words of a flat export call (an argument whose conversion depends on another one, such as a counted string, reads it here). Empty for COM calls.</summary>
    public uint[] GuestArgs { get; } = [];
    public IWin32Memory Mem { get; }
    public Win32Context Ctx { get; }
    public NativeScope Native { get; } = new();
    /// <summary>Registers work to run after the native call returned (receives the HRESULT): copying results back to the guest, wrapping returned interfaces.</summary>
    public void OnCompleted(Action<uint> action) => completions.Add(action);
    internal void Complete(uint hr) { foreach (var c in completions) c(hr); }
    public void Dispose() => Native.Dispose();
}

/// <summary>Conversion of ONE guest argument (a 32-bit word) into the pointer-sized value the native method receives, and, for out arguments, the way back.</summary>
public abstract class ComArg
{
    public abstract nint ToHost(ComCallScope scope, uint guestValue);
}

/// <summary>Plain <see cref="ComArg"/> from delegates.</summary>
public sealed class DelegateComArg(Func<ComCallScope, uint, nint> toHost) : ComArg
{
    public override nint ToHost(ComCallScope scope, uint guestValue) => toHost(scope, guestValue);
}

/// <summary>The conversion kinds an adapter table can name. No kind forwards a guest pointer as-is: a pointer becomes a native copy, a native temporary or an explicit handle mapping.</summary>
public static unsafe class ComArgs
{
    /// <summary>A 32-bit unsigned value (DWORD, flags, counts).</summary>
    public static readonly ComArg Value = new DelegateComArg((_, v) => (nint)v);
    /// <summary>A 32-bit signed value (LONG: volume, pan), sign-extended.</summary>
    public static readonly ComArg Signed = new DelegateComArg((_, v) => (nint)(int)v);
    /// <summary>A pointer the host must receive as NULL: only valid for a NULL guest value (IUnknown* pUnkOuter); anything else is E_INVALIDARG.</summary>
    public static ComArg MustBeNull(string what) => new DelegateComArg((_, v) => v == 0 ? 0 : throw new ComArgumentException($"{what} must be NULL (aggregation is not supported)."));

    /// <summary>A pointer to a GUID: a native copy (NULL stays NULL when <paramref name="optional"/>).</summary>
    public static ComArg Guid(bool optional) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("A GUID pointer must not be NULL.");
        return s.Native.Copy(s.Mem.ReadBytes(v, 16));
    });

    /// <summary>A NUL-terminated ANSI string: a native copy.</summary>
    public static ComArg AnsiString(bool optional) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("A string pointer must not be NULL.");
        return s.Native.Copy(s.Mem.ReadCString(v).Append((byte)0).ToArray());
    });

    /// <summary>An input structure whose guest and host layouts are byte-identical (only 32-bit fields): a native copy of <paramref name="size"/> bytes.</summary>
    public static ComArg InStruct(int size, bool optional = false) => new DelegateComArg((s, v) =>
        v == 0 ? (optional ? 0 : throw new ComArgumentException("A structure pointer must not be NULL.")) : s.Native.Copy(s.Mem.ReadBytes(v, size)));

    /// <summary>An input structure with a layout that differs between the 32-bit guest and the 64-bit host (pointers inside): <paramref name="convert"/> builds the native image.</summary>
    public static ComArg InStruct(Func<ComCallScope, uint, nint> convert, bool optional = false) => new DelegateComArg((s, v) =>
        v == 0 ? (optional ? 0 : throw new ComArgumentException("A structure pointer must not be NULL.")) : convert(s, v));

    /// <summary>An output DWORD/LONG: a native temporary whose value is copied to the guest only when the call succeeded.</summary>
    public static ComArg OutU32(bool optional = false) => OutBytes(4, optional);

    /// <summary>An output buffer of <paramref name="size"/> bytes with identical layouts (copied back on success).</summary>
    public static ComArg OutBytes(int size, bool optional = false) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("An output pointer must not be NULL.");
        var native = s.Native.AllocZeroed(size);
        s.OnCompleted(hr =>
        {
            if (ComHr.Succeeded(hr)) s.Mem.Write(v, new ReadOnlySpan<byte>((void*)native, size));
        });
        return native;
    });

    /// <summary>An in/out structure with identical layouts (DSCAPS: dwSize in, the rest out): copied in, copied back on success.</summary>
    public static ComArg InOutStruct(int size) => new DelegateComArg((s, v) =>
    {
        if (v == 0) throw new ComArgumentException("A structure pointer must not be NULL.");
        var native = s.Native.Copy(s.Mem.ReadBytes(v, size));
        s.OnCompleted(hr =>
        {
            if (ComHr.Succeeded(hr)) s.Mem.Write(v, new ReadOnlySpan<byte>((void*)native, size));
        });
        return native;
    });

    /// <summary>
    /// An output interface pointer (<c>IFoo** ppv</c>): the host receives a native temporary; when the call succeeded and it holds a pointer, <paramref name="wrap"/> turns it into a guest COM object
    /// and returns the guest interface pointer to store. On failure the guest slot is set to NULL.
    /// </summary>
    public static ComArg OutInterface(Func<ComCallScope, nint, uint> wrap) => new DelegateComArg((s, v) =>
    {
        if (v == 0) throw new ComArgumentException("An interface output pointer must not be NULL.");
        var slot = s.Native.AllocZeroed(sizeof(nint));
        s.Mem.WriteUInt32(v, 0);
        s.OnCompleted(hr =>
        {
            if (!ComHr.Succeeded(hr)) return;
            var native = *(nint*)slot;
            if (native != 0) s.Mem.WriteUInt32(v, wrap(s, native));
        });
        return slot;
    });

    /// <summary>A handle translated by an explicit mapping (guest HWND -> host window).</summary>
    public static ComArg Handle(Func<uint, nint> map) => new DelegateComArg((_, v) => map(v));
}

/// <summary>A guest argument that cannot be adapted (a bad pointer, a required NULL): the call answers E_INVALIDARG instead of reaching the native API.</summary>
public sealed class ComArgumentException(string message) : Exception(message);

/// <summary>
/// One explicit row of an interface adapter table: the guest-visible method name, the native vtable slot, and one conversion per guest argument (the argument count of the guest
/// method is <c>Args.Count</c>). There is no generic "forward everything": a method that is not in the table does not exist for the guest.
/// </summary>
public sealed class HostComMethod
{
    public HostComMethod(string name, int hostSlot, params ComArg[] args) { Name = name; HostSlot = hostSlot; Args = args; }
    public string Name { get; }
    public int HostSlot { get; }
    public IReadOnlyList<ComArg> Args { get; }

    /// <summary>Converts the guest arguments, calls the native method, completes the out arguments and returns the host HRESULT unchanged (E_INVALIDARG for an unadaptable argument).</summary>
    public uint Invoke(HostComInterface host, GuestComCall call)
    {
        using var scope = new ComCallScope(call);
        Span<nint> native = stackalloc nint[Args.Count];
        try
        {
            for (var i = 0; i < Args.Count; i++) native[i] = Args[i].ToHost(scope, call.Arg(i));
        }
        catch (ComArgumentException e)
        {
            call.Ctx.Options.DebugOutput?.Invoke($"{host.Name}::{Name}: {e.Message}");
            return ComHr.InvalidArg;
        }
        var hr = host.Invoke(HostSlot, native);
        HostBridge.WindowProcBridge.ThrowPendingCallbackFault();   // the native method may have sent messages to a guest window procedure
        scope.Complete(hr);
        return hr;
    }

    /// <summary>The guest method row: <paramref name="hostOf"/> finds the native interface behind the guest object.</summary>
    public GuestComMethod ToGuest(Func<GuestComObject, HostComInterface> hostOf) => new(Name, Args.Count, call => Invoke(hostOf(call.Object), call));
}
