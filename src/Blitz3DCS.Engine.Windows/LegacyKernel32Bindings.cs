using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.NativeInterop;

namespace Blitz3DCS.Engine.Windows;

/// <summary>kernel32 declarations whose original userlib contracts take BASIC Type instances by pointer.</summary>
public static class LegacyKernel32Bindings
{
    private const int LegacyMemoryStatusLength = 32;

    // MEMORYSTATUS: two DWORDs followed by six SIZE_T fields (4 bytes on Win32, 8 on Win64).
    private static readonly NativeStructLayout MemoryStatus = NativeStructLayout.Sequential(
        [NativeStructField.UInt32, NativeStructField.UInt32, .. Enumerable.Repeat(NativeStructField.UIntPtrSaturating, 6)]);

    /// <summary><c>GlobalMemoryStatus(buffer*)</c> over a BASIC Type with eight Integer fields.</summary>
    /// <remarks>Fields keep the 32-bit MEMORYSTATUS layout on both targets. Win64 SIZE_T values above
    /// 0xFFFFFFFF saturate to that value (BASIC -1), as the 32-bit API reports overflow, and dwLength
    /// reports the 32-byte legacy structure size. Null and shorter Types are rejected.</remarks>
    public static void GlobalMemoryStatus(Span<byte> buffer)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GlobalMemoryStatus requires the Windows host.");
        if (buffer.IsEmpty) throw new ArgumentNullException(nameof(buffer), "GlobalMemoryStatus requires a MEMORYSTATUS object, not Null.");
        MemoryStatus.Invoke(buffer, NativeStructDirection.InOut, pointer =>
        {
            Native.GlobalMemoryStatus(pointer);
            return 0;
        });
        BinaryPrimitives.WriteInt32LittleEndian(buffer, LegacyMemoryStatusLength);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", ExactSpelling = true)] public static extern void GlobalMemoryStatus(nint buffer);
    }
}
