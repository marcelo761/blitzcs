using System.Buffers.Binary;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyKernel32BindingsTests
{
    [Fact]
    public void GlobalMemoryStatus_requires_the_windows_host_elsewhere()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => LegacyKernel32Bindings.GlobalMemoryStatus(new byte[32]));
        Assert.Throws<PlatformNotSupportedException>(() => LegacyWindowBindings.api_GetFocus());
    }

    [Fact]
    public void GlobalMemoryStatus_fills_the_legacy_32_bit_layout_on_windows() => RunSmoke();

    /// <summary>Runs on Win32 and Win64 processes; also invoked by the --system-smoke runner.</summary>
    internal static void RunSmoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(32), 0x5A5A5A5A);
        LegacyKernel32Bindings.GlobalMemoryStatus(image);
        int Slot(int index) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(index * 4));
        Assert.Equal(32, Slot(0));
        Assert.InRange(Slot(1), 0, 100);
        var totalPhys = unchecked((uint)Slot(2));
        Assert.NotEqual(0u, totalPhys);
        Assert.True(unchecked((uint)Slot(3)) <= totalPhys);
        Assert.Equal(0x5A5A5A5A, Slot(8)); // Bytes beyond the 32-byte structure are untouched.
        Assert.Throws<ArgumentException>(() => LegacyKernel32Bindings.GlobalMemoryStatus(new byte[28]));
        Assert.Throws<ArgumentNullException>(() => LegacyKernel32Bindings.GlobalMemoryStatus(Span<byte>.Empty));
    }
}
