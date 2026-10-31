using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Win32.HostBridge;

/// <summary>
/// Calls a native host function through its address with the host's native convention (Win64: the first four integer arguments in RCX, RDX, R8, R9, the rest on the stack, 32 bytes of
/// shadow space). Every argument and the result travel as pointer-sized integers; how a guest word becomes one (zero or sign extension, a native copy, a handle) is decided by the caller's
/// descriptor, never here. Up to 14 arguments (CreateFontA has 14).
/// </summary>
public static unsafe class HostInvoke
{
    public const int MaxArguments = 14;

    public static nint Call(nint f, ReadOnlySpan<nint> a)
    {
        if (f == 0) throw new ArgumentException("A native function address is NULL.", nameof(f));
        return a.Length switch
        {
            0 => ((delegate* unmanaged<nint>)f)(),
            1 => ((delegate* unmanaged<nint, nint>)f)(a[0]),
            2 => ((delegate* unmanaged<nint, nint, nint>)f)(a[0], a[1]),
            3 => ((delegate* unmanaged<nint, nint, nint, nint>)f)(a[0], a[1], a[2]),
            4 => ((delegate* unmanaged<nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3]),
            5 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4]),
            6 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5]),
            7 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6]),
            8 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]),
            9 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]),
            10 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9]),
            11 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10]),
            12 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11]),
            13 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12]),
            14 => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13]),
            _ => throw new NotSupportedException($"A native call with {a.Length} arguments is not supported (maximum {MaxArguments})."),
        };
    }
}

/// <summary>Resolves exports of the host's own system DLLs (user32, gdi32, ddraw ...) on first use. A missing DLL or export is a named <see cref="Win32UsageException"/>, never a silent NULL.</summary>
public static class HostExports
{
    private sealed class ExportKeyComparer : IEqualityComparer<(string Dll, string Name)>
    {
        public static readonly ExportKeyComparer Instance = new();
        public bool Equals((string Dll, string Name) x, (string Dll, string Name) y) =>
            string.Equals(x.Dll, y.Dll, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Dll, string Name) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Dll),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    private static readonly ConcurrentDictionary<string, nint> libraries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string, string), nint> exports = new(ExportKeyComparer.Instance);

    public static nint Get(string dll, string name) => exports.GetOrAdd((dll, name), static key =>
    {
        var (dll, name) = key;
        var lib = libraries.GetOrAdd(dll, static d => NativeLibrary.TryLoad(d, out var h) ? h : 0);
        if (lib == 0) throw new Win32UsageException($"The host has no loadable {dll}, so {dll}!{name} cannot be bridged.");
        return NativeLibrary.TryGetExport(lib, name, out var f) ? f : throw new Win32UsageException($"The host's {dll} does not export {name}.");
    });

    /// <summary>The host executable's module handle: the HINSTANCE under which guest window classes are registered on the host.</summary>
    public static nint HostInstance => hostInstance.Value;
    private static readonly Lazy<nint> hostInstance = new(() => HostInvoke.Call(Get("kernel32.dll", "GetModuleHandleW"), [0]));
}

/// <summary>
/// Handle policy of the bridge. USER and GDI handles (HWND, HDC, HGDIOBJ, HCURSOR, HICON, HMENU, HMONITOR ...) are 32-bit significant: Windows documents that 64-bit Windows uses 32-bit handles
/// for interoperability, so a 64-bit handle is truncated when passed to 32-bit code and sign-extended on the way back. The bridge relies on exactly that and checks it: a host value that does
/// not survive the round trip is not a USER/GDI handle and faults by name instead of being cut. Everything else that the Win32 API calls a handle but that is an address (HINSTANCE/HMODULE,
/// HGLOBAL, function pointers) never takes this path.
/// </summary>
public static class HostHandles
{
    public static nint ToHost(uint guest) => (int)guest;

    public static uint ToGuest(nint host, string what)
    {
        if (host != (int)host) throw new Win32UsageException($"{what}: the host returned 0x{host:X}, which is not a 32-bit significant handle; it cannot be handed to 32-bit code.");
        return (uint)host;
    }

    /// <summary>True when <paramref name="host"/> is representable as a 32-bit guest word (zero- or sign-extended).</summary>
    public static bool Fits(nint host) => host == (int)host || (ulong)host <= uint.MaxValue;
}

/// <summary>The thread's last-error value travels with each bridged call: the guest's value is the host thread's value before the call, and the host's value is the guest's after it.</summary>
internal static class HostLastError
{
    public static void Before(Win32Context ctx) => Marshal.SetLastSystemError((int)ctx.LastError);
    public static void After(Win32Context ctx) => ctx.LastError = (uint)Marshal.GetLastSystemError();
}
