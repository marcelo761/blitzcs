using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class GuestMemoryAnsiStringTests
{
    private static GuestModule CreateModule()
    {
        var guest = FakeGuest.Create();
        return GuestModule.Load(guest.Artifact, guest.Image, RuntimeLoaderTests.Resolver(), new GuestModuleOptions { RunEntryPoint = false });
    }

    [Fact]
    public void ReadAnsiStringFindsNulAtEveryVectorLaneAndAlignment()
    {
        using var module = CreateModule();
        using var buffer = module.Memory.Allocate(128);
        var bytes = Enumerable.Repeat((byte)'x', 48).ToArray();

        for (var alignment = 0; alignment < 16; alignment++)
        {
            for (var lane = 0; lane < 16; lane++)
            {
                var start = alignment + 16;
                Array.Fill(bytes, (byte)'x');
                bytes[start + lane] = 0;
                module.Memory.Write(buffer.Address, bytes);

                Assert.Equal(new string('x', lane), module.Memory.ReadAnsiString(buffer.Address + (uint)start));
            }
        }
    }

    [Fact]
    public void ReadAnsiStringPreservesLatin1AndInclusiveMaxLength()
    {
        using var module = CreateModule();
        using var buffer = module.Memory.Allocate(64);
        module.Memory.Write(buffer.Address, [(byte)0x80, (byte)0xFF, 0, (byte)'z']);

        Assert.Equal("\u0080\u00FF", module.Memory.ReadAnsiString(buffer.Address, maxLength: 2));
        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(buffer.Address, maxLength: 1));
        Assert.Equal(string.Empty, module.Memory.ReadAnsiString(buffer.Address + 2, maxLength: 0));
        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(buffer.Address, maxLength: -1));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(127)]
    public void ReadAnsiStringPreservesHighBytesAcrossVectorBlocksAndStopsAtTheLimit(int length)
    {
        using var module = CreateModule();
        using var buffer = module.Memory.Allocate(length + 1);
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(0x80 + i % 0x80)).ToArray();
        module.Memory.Write(buffer.Address, bytes);
        module.Memory.WriteByte(buffer.Address + (uint)length, 0);

        Assert.Equal(System.Text.Encoding.Latin1.GetString(bytes), module.Memory.ReadAnsiString(buffer.Address, length));
        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(buffer.Address, length - 1));
    }

    [Fact]
    public void ReadAnsiStringDoesNotReadPastProtectedPageWhenNulIsBeforeIt()
    {
        using var module = CreateModule();
        using var buffer = module.Memory.Allocate(8192);
        var address = buffer.Address + 4092;
        module.Memory.Write(address, [(byte)'a', (byte)'b', (byte)'c', 0]);
        Assert.True(module.Memory.TryProtect(buffer.Address + 4096, 4096, MemoryProtection.None, 1, out var refusal), refusal);

        Assert.Equal("abc", module.Memory.ReadAnsiString(address));
    }

    [Fact]
    public void ReadAnsiStringFaultsAtFirstProtectedByteIfNoNulPrecedesIt()
    {
        using var module = CreateModule();
        using var buffer = module.Memory.Allocate(8192);
        var address = buffer.Address + 4092;
        module.Memory.Write(address, [(byte)'a', (byte)'b', (byte)'c', (byte)'d']);
        Assert.True(module.Memory.TryProtect(buffer.Address + 4096, 4096, MemoryProtection.None, 1, out var refusal), refusal);

        var error = Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(address));
        Assert.Equal(address + 4, error.Address);
        Assert.Contains("of 1 byte(s)", error.Message);
    }

    [Fact]
    public void ReadAnsiStringFaultsAfterDisposal()
    {
        using var module = CreateModule();
        var buffer = module.Memory.Allocate(32);
        buffer.Write(0, [(byte)'x', 0]);
        var address = buffer.Address;
        buffer.Dispose();

        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(address));
    }

    [Fact]
    public void ReadAnsiStringUsesScalarChecksForRangesThatCouldWrapTheGuestAddressSpace()
    {
        using var module = CreateModule();
        const uint address = 0xFFFF_FFFCu;

        var error = Assert.Throws<GuestMemoryException>(() => module.Memory.ReadAnsiString(address, maxLength: 16));
        Assert.Equal(address, error.Address);
        Assert.Contains("of 1 byte(s)", error.Message);
    }
}
