using System.Runtime.InteropServices;

namespace BlitzCS.OriginalRuntime;

/// <summary>Real Win32 calls for the original runtime's Windows x86 process and banks.</summary>
public static class Win32
{
    private static readonly string[] MemoryStatusFields = ["dwlength", "dwmemoryload", "dwtotalphys", "dwavailphys", "dwtotalpagefile", "dwavailpagefile", "dwtotalvirtual", "dwavailvirtual"];

    public static void GlobalMemoryStatus(object data)
    {
        RequireWindowsX86();
        ArgumentNullException.ThrowIfNull(data);
        var fields = MemoryStatusFields.Select(name => data.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)).ToArray();
        if (fields.Any(field => field is null || field.FieldType != typeof(int)))
            throw new ArgumentException("GlobalMemoryStatus requires the eight Int32 MEMORYSTATUS fields.", nameof(data));
        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            for (var i = 0; i < fields.Length; i++) Marshal.WriteInt32(buffer, i * 4, (int)fields[i]!.GetValue(data)!);
            Marshal.WriteInt32(buffer, 32);
            NativeGlobalMemoryStatus(buffer);
            for (var i = 0; i < fields.Length; i++) fields[i]!.SetValue(data, Marshal.ReadInt32(buffer, i * 4));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static int GetModuleFileName(int module, int bank, int size)
    {
        RequireWindowsX86();
        if (bank == 0 || size <= 0 || size > Commands.BankSize(bank))
            throw new ArgumentException("GetModuleFileName requires an original-runtime bank at least nSize bytes long.", nameof(bank));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, unchecked((byte)Commands.PeekByte(bank, i)));
            var result = NativeGetModuleFileName(module, buffer, size);
            for (var i = 0; i < size; i++) Commands.PokeByte(bank, i, Marshal.ReadByte(buffer, i));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void RequireWindowsX86()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("Original Blitz3D Win32 bindings require Windows .NET x86 (or Proton).");
    }

    public static int GetDesktopWindow()
    {
        RequireWindowsX86();
        return NativeGetDesktopWindow();
    }

    public static int GetFocus()
    {
        RequireWindowsX86();
        return NativeGetFocus();
    }

    public static int SetWindowLong(int hwnd, int index, int value)
    {
        RequireWindowsX86();
        return NativeSetWindowLong(hwnd, index, value);
    }

    public static int SetWindowPos(int hwnd, int insertAfter, int x, int y, int width, int height, int flags)
    {
        RequireWindowsX86();
        return NativeSetWindowPos(hwnd, insertAfter, x, y, width, height, flags);
    }

    public static int GetClientRect(int hwnd, int bank)
    {
        RequireWindowsX86();
        const int rectangleBytes = 16;
        if (bank == 0)
            throw new ArgumentException("GetClientRect requires a live original-runtime bank.", nameof(bank));
        if (Commands.BankSize(bank) < rectangleBytes)
            throw new ArgumentException("GetClientRect requires an original-runtime bank of at least 16 bytes.", nameof(bank));
        var bytes = new byte[rectangleBytes];
        // Preserve the bank if Win32 fails or leaves bytes untouched; never inspect C++ bank layout.
        for (var i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)Commands.PeekByte(bank, i));
        var buffer = Marshal.AllocHGlobal(rectangleBytes);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var result = NativeGetClientRect(hwnd, buffer);
            if (result != 0)
            {
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                for (var i = 0; i < bytes.Length; i++) Commands.PokeByte(bank, i, bytes[i]);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow", ExactSpelling = true)]
    private static extern int NativeGetDesktopWindow();
    [DllImport("user32.dll", EntryPoint = "GetFocus", ExactSpelling = true)]
    private static extern int NativeGetFocus();
    [DllImport("user32.dll", EntryPoint = "GetClientRect", ExactSpelling = true)]
    private static extern int NativeGetClientRect(int hwnd, nint rectangle);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongA", ExactSpelling = true)]
    private static extern int NativeSetWindowLong(int hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", ExactSpelling = true)]
    private static extern int NativeSetWindowPos(int hwnd, int insertAfter, int x, int y, int width, int height, int flags);
    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatus", ExactSpelling = true)]
    private static extern void NativeGlobalMemoryStatus(nint status);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameA", ExactSpelling = true)]
    private static extern int NativeGetModuleFileName(int module, nint filename, int size);
}
