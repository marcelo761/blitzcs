using BlitzCS.BinaryCompiler.Win32.Com;

namespace BlitzCS.BinaryCompiler.Win32.HostBridge;

/// <summary>How the native result of a bridged export becomes the guest's EAX.</summary>
public enum HostResult
{
    /// <summary>BOOL, int, UINT, DWORD, COLORREF, ATOM: the low 32 bits (the upper half of RAX is undefined for a 32-bit return).</summary>
    Int32,
    /// <summary>A USER/GDI handle: checked to be 32-bit significant (<see cref="HostHandles.ToGuest"/>).</summary>
    Handle,
    /// <summary>LRESULT / LONG_PTR: must fit a 32-bit word (sign- or zero-extended), otherwise it is a host pointer and the call faults by name.</summary>
    Pointer,
}

/// <summary>
/// One row of the flat-export adapter table: the host DLL and export, one <see cref="ComArg"/> conversion per guest stdcall argument (the guest argument count is <c>Args.Count</c>) and the
/// result kind. Pointers never cross as-is: they become native copies, native temporaries copied back on success, or explicit handle mappings, exactly like the COM tables
/// (<see cref="HostComMethod"/>). Structures whose 32-bit and 64-bit layouts differ get a hand-written converter in the argument instead of a byte copy.
/// </summary>
public sealed class HostFunction
{
    public HostFunction(string dll, string name, HostResult result, params ComArg[] args) { Dll = dll; Name = name; Result = result; Args = args; }
    public string Dll { get; }
    public string Name { get; }
    public HostResult Result { get; }
    public IReadOnlyList<ComArg> Args { get; }
    /// <summary>Decides from the native result whether output arguments are copied back (default: a non-zero result).</summary>
    public Func<nint, bool> Succeeded { get; init; } = r => (int)r != 0;
    /// <summary>The value the guest receives when an argument cannot be adapted (the API's documented failure value; the last error is ERROR_INVALID_PARAMETER).</summary>
    public uint FailureValue { get; init; }

    public uint Invoke(Win32Call c)
    {
        var guestArgs = c.Args.ToArray();
        using var scope = new ComCallScope(c.Mem, c.Ctx, guestArgs);
        Span<nint> native = stackalloc nint[Args.Count];
        try
        {
            for (var i = 0; i < Args.Count; i++) native[i] = Args[i].ToHost(scope, guestArgs[i]);
        }
        catch (ComArgumentException e)
        {
            c.Ctx.Options.DebugOutput?.Invoke($"{Dll}!{Name}: {e.Message}");
            return c.Ctx.Fail(Win32Error.InvalidParameter, FailureValue);
        }
        var r = CallNative(c.Ctx, HostExports.Get(Dll, Name), native);
        scope.Complete(Succeeded(r) ? ComHr.Ok : ComHr.Fail);
        return ToGuest(r, Result, $"{Dll}!{Name}");
    }

    /// <summary>Calls a native export with last-error passing and surfaces a fault raised by guest code that the host re-entered during the call (a window procedure).</summary>
    internal static nint CallNative(Win32Context ctx, nint function, ReadOnlySpan<nint> args)
    {
        HostLastError.Before(ctx);
        nint r;
        try { r = HostInvoke.Call(function, args); }
        finally { HostLastError.After(ctx); }
        WindowProcBridge.ThrowPendingCallbackFault();
        return r;
    }

    internal static uint ToGuest(nint r, HostResult kind, string what) => kind switch
    {
        HostResult.Int32 => (uint)r,
        HostResult.Handle => HostHandles.ToGuest(r, what),
        _ => HostHandles.Fits(r) ? (uint)r : throw new Win32UsageException($"{what} returned 0x{r:X}, a 64-bit value (a host pointer) that cannot be handed to 32-bit code."),
    };
}

/// <summary>Argument conversions of the flat bridge, in addition to the COM kinds of <see cref="ComArgs"/>.</summary>
public static class HostArgs
{
    /// <summary>A USER/GDI handle (HWND, HDC, HGDIOBJ, HCURSOR, HMENU ...): sign-extended, as Windows does for 32-bit handles.</summary>
    public static readonly ComArg Handle = new DelegateComArg((_, v) => HostHandles.ToHost(v));
    /// <summary>A WPARAM / UINT_PTR value: zero-extended.</summary>
    public static readonly ComArg WParam = ComArgs.Value;
    /// <summary>An LPARAM / LONG_PTR / int value: sign-extended.</summary>
    public static readonly ComArg LParam = ComArgs.Signed;

    /// <summary>A string or an integer resource/atom identifier (MAKEINTRESOURCE / MAKEINTATOM: a value below 0x10000 passes as that value).</summary>
    public static ComArg AnsiOrId(bool optional = false) => new DelegateComArg((s, v) =>
        v < 0x10000 ? (v == 0 && !optional ? throw new ComArgumentException("A string or resource identifier must not be NULL.") : (nint)v) : s.Native.Copy([.. s.Mem.ReadCString(v), 0]));

    /// <summary>A byte string whose length is the guest argument <paramref name="countIndex"/> (not NUL-terminated): a native copy of exactly that many bytes.</summary>
    public static ComArg CountedAnsi(int countIndex) => new DelegateComArg((s, v) =>
    {
        var count = (int)s.GuestArgs[countIndex];
        if (count < 0) throw new ComArgumentException("A negative string length.");
        if (v == 0) return count == 0 ? 0 : throw new ComArgumentException("A NULL string with a non-zero length.");
        return s.Native.Copy([.. s.Mem.ReadBytes(v, count), 0]);
    });

    /// <summary>An optional input array of <paramref name="elementSize"/>-byte elements whose element count is guest argument <paramref name="countIndex"/>, identical layouts.</summary>
    public static ComArg CountedArray(int countIndex, int elementSize, bool optional = true) => new DelegateComArg((s, v) =>
    {
        if (v == 0) return optional ? 0 : throw new ComArgumentException("An array pointer must not be NULL.");
        return s.Native.Copy(s.Mem.ReadBytes(v, checked((int)s.GuestArgs[countIndex] * elementSize)));
    });

    /// <summary>An output array (identical layout) of <paramref name="elementSize"/>-byte elements whose count is computed from the guest arguments; copied back on success.</summary>
    public static ComArg OutArray(Func<uint[], int> count, int elementSize) => new DelegateComArg((s, v) =>
    {
        var n = count(s.GuestArgs);
        if (n <= 0 || n > 65536) throw new ComArgumentException($"An output array of {n} elements is not plausible.");
        return ComArgs.OutBytes(n * elementSize).ToHost(s, v);
    });
}
