using System.Runtime.InteropServices;
using BlitzCS.LegacyRuntime;
using BlitzCS.NativeInterop;

namespace BlitzCS.NativeInterop.ManagedBinding;

/// <summary>Explicit demo ABI adapter for owned bank bytes, not an implementation of a window API.</summary>
public static class LegacyMemoryBindings
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FillRectangle(nint bytes, nuint length);

    private static readonly object Gate = new();
    private static NativeLibraryOwner? library;
    private static NativeFunction<FillRectangle>? fill;

    public static int WriteNativeRect(int bank)
    {
        return LegacyBanks.WithPointer(bank, 0, 16, (pointer, length) =>
        {
            lock (Gate)
            {
                EnsureBindings();
                return fill!.Invoke(call => call(pointer, (nuint)length));
            }
        }, alignment: 4);
    }

    public static int NativePointerBits() => IntPtr.Size * 8;

    public static int CloseNativeMemory()
    {
        lock (Gate)
        {
            if (library is null) return 0;
            library.Dispose();
            library = null;
            fill = null;
            return 1;
        }
    }

    private static void EnsureBindings()
    {
        if (library is not null) return;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The native memory example fixture currently requires Linux.");
        var loaded = NativeLibraryOwner.Load(Path.Combine(AppContext.BaseDirectory, "libblitzcs_native_buffer.so"));
        try
        {
            fill = loaded.Bind<FillRectangle>("blitz_rect_fill");
            library = loaded;
        }
        catch { loaded.Dispose(); throw; }
    }
}
