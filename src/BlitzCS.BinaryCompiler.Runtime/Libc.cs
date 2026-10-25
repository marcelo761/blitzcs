using System.Reflection;
using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Minimal libc surface (Linux x86-64 only). "libc" is resolved to libc.so.6 because libc.so is a linker script on dev machines.</summary>
internal static class Libc
{
    internal const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
    internal const int MAP_PRIVATE = 0x02, MAP_FIXED = 0x10, MAP_ANONYMOUS = 0x20, MAP_32BIT = 0x40, MAP_FIXED_NOREPLACE = 0x100000;
    internal static readonly nint MapFailed = -1;

    static Libc()
    {
        NativeLibrary.SetDllImportResolver(typeof(Libc).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "libc") return 0;
        return NativeLibrary.TryLoad("libc.so.6", out var h) ? h : 0;
    }

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int mprotect(nint addr, nuint length, int prot);

    internal static int PageSize => Environment.SystemPageSize;

    internal static nint Map(nint addr, nuint length, int prot, int flags, out int errno)
    {
        var r = mmap(addr, length, prot, flags | MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        errno = r == MapFailed ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    internal static void Unmap(nint addr, nuint length)
    {
        if (addr != 0) munmap(addr, length);
    }

    internal static void Protect(nint addr, nuint length, int prot)
    {
        if (mprotect(addr, length, prot) != 0)
            throw new GuestLoadException($"mprotect(0x{addr:X}, {length}, {prot}) failed with errno {Marshal.GetLastPInvokeError()}.");
    }
}
