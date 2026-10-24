using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Resolves a PE import to a managed implementation. Return null for "not provided": loading then fails with <see cref="GuestImportException"/>.</summary>
public interface IGuestImportResolver
{
    GuestImport? Resolve(string dll, string? name, ushort? ordinal);
}

/// <summary>Arguments of one guest-to-managed call. Stack arguments start at ESP+4 (ESP points at the guest return address).</summary>
public readonly struct GuestCall
{
    internal GuestCall(GuestMemory memory, uint esp) { Memory = memory; Esp = esp; }
    public GuestMemory Memory { get; }
    public uint Esp { get; }
    public uint Arg(int index) => Memory.ReadUInt32(Esp + 4 + 4u * (uint)index);
    public float ArgSingle(int index) => BitConverter.UInt32BitsToSingle(Arg(index));
}

public delegate uint GuestImportHandler(GuestCall call);

/// <summary>Float/pointer value helpers (guest values travel as 32-bit words).</summary>
public static class GuestValue
{
    public static uint FromSingle(float value) => BitConverter.SingleToUInt32Bits(value);
    public static float ToSingle(uint bits) => BitConverter.UInt32BitsToSingle(bits);
}

/// <summary>
/// A managed callee exposed to the guest as <c>ulong Import(uint guestEsp)</c> (low 32 = EAX, high 32 = bytes popped by the callee).
/// Instances keep their delegate alive; hold them (the module does) for as long as the guest may call them.
/// </summary>
public sealed unsafe class GuestImport
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong NativeThunk(uint guestEsp);

    private readonly object? keepAlive;

    private GuestImport(nint functionPointer, object? keepAlive)
    {
        FunctionPointer = functionPointer;
        this.keepAlive = keepAlive;
    }

    public nint FunctionPointer { get; }

    /// <summary>Advanced: wraps a raw [UnmanagedCallersOnly] pointer. It gets no exception containment: it must never throw.</summary>
    public static GuestImport FromFunctionPointer(delegate* unmanaged<uint, ulong> function)
    {
        if (function == null) throw new ArgumentNullException(nameof(function));
        return new((nint)function, null);
    }

    /// <summary>
    /// Design: one reverse-P/Invoke thunk (Marshal.GetFunctionPointerForDelegate) per import, closing over the handler.
    /// UnmanagedCallersOnly methods cannot capture state and the import ABI carries no context argument, so a
    /// per-import thunk is the simplest correct way to support lambdas. Exceptions are contained and recorded.
    /// </summary>
    public static GuestImport Create(GuestImportHandler handler, uint popBytes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (popBytes % 4 != 0) throw new ArgumentException("popBytes must be a multiple of 4.", nameof(popBytes));
        NativeThunk thunk = esp => GuestModule.DispatchImport(handler, popBytes, esp);
        return new(Marshal.GetFunctionPointerForDelegate(thunk), thunk);
    }

    internal object? KeepAlive => keepAlive;
}

/// <summary>Authors imports from typed lambdas. Arguments are read from the guest stack with explicit cdecl/stdcall semantics.</summary>
public static class GuestImportBuilder
{
    public static GuestImport CdeclCall(GuestImportHandler handler) => GuestImport.Create(handler, 0);
    public static GuestImport StdcallCall(int argCount, GuestImportHandler handler) => GuestImport.Create(handler, checked((uint)(4 * argCount)));

    public static GuestImport Cdecl(Func<uint> f) => CdeclCall(_ => f());
    public static GuestImport Cdecl(Func<uint, uint> f) => CdeclCall(c => f(c.Arg(0)));
    public static GuestImport Cdecl(Func<uint, uint, uint> f) => CdeclCall(c => f(c.Arg(0), c.Arg(1)));
    public static GuestImport Cdecl(Func<uint, uint, uint, uint> f) => CdeclCall(c => f(c.Arg(0), c.Arg(1), c.Arg(2)));
    public static GuestImport Cdecl(Func<uint, uint, uint, uint, uint> f) => CdeclCall(c => f(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));

    public static GuestImport Stdcall(Func<uint> f) => StdcallCall(0, _ => f());
    public static GuestImport Stdcall(Func<uint, uint> f) => StdcallCall(1, c => f(c.Arg(0)));
    public static GuestImport Stdcall(Func<uint, uint, uint> f) => StdcallCall(2, c => f(c.Arg(0), c.Arg(1)));
    public static GuestImport Stdcall(Func<uint, uint, uint, uint> f) => StdcallCall(3, c => f(c.Arg(0), c.Arg(1), c.Arg(2)));
    public static GuestImport Stdcall(Func<uint, uint, uint, uint, uint> f) => StdcallCall(4, c => f(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
}

/// <summary>Dictionary-backed resolver. DLL names compare case-insensitively (Windows rules); function names are case-sensitive.</summary>
public sealed class DictionaryGuestImportResolver : IGuestImportResolver
{
    private readonly Dictionary<string, GuestImport> map = new(StringComparer.Ordinal);

    public DictionaryGuestImportResolver Add(string dll, string name, GuestImport import)
    {
        map[Key(dll, name)] = import;
        return this;
    }

    public DictionaryGuestImportResolver Add(string dll, ushort ordinal, GuestImport import)
    {
        map[Key(dll, "#" + ordinal)] = import;
        return this;
    }

    public GuestImport? Resolve(string dll, string? name, ushort? ordinal) =>
        map.TryGetValue(Key(dll, name ?? "#" + ordinal), out var import) ? import : null;

    private static string Key(string dll, string name) => dll.ToLowerInvariant() + "!" + name;
}
