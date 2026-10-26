using System.Collections.Concurrent;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class BinaryLibraryWorkstreamBTests
{
    private static GuestModule CreateModule()
    {
        var b = TranslatorHarness.Translate(TranslatorHarness.Asm(a => a.ret()), OptimizationProfile.Release);
        return GuestModule.Load(b.Artifact, b.Image, new DictionaryGuestImportResolver(), new GuestModuleOptions { RunEntryPoint = false });
    }

    [Fact]
    public void GuestModuleInvokeRvaCallsFunctionAtRva()
    {
        using var m = CreateModule();
        var result = m.InvokeRva(PeFixtureBuilder.TextRva, "_f", GuestReturn.Eax, []);
        Assert.Equal(0u, result.Eax);
    }

    [Fact]
    public void GuestModuleInvokeRvaThrowsOnUnmappedRva()
    {
        using var m = CreateModule();
        Assert.Throws<GuestExportException>(() => m.InvokeRva(0xDEADBEEFu, "bad", GuestReturn.Eax, []));
    }

    [Fact]
    public void MarshalArgumentMarshalsObjectBufferAndCompletesWriteBack()
    {
        using var m = CreateModule();
        var mem = m.Memory;
        var writtenBack = false;
        byte[]? received = null;
        var table = new ConcurrentDictionary<int, (byte[] Bytes, Action<byte[]> WriteBack)>();
        AppContext.SetData("__BlitzCS.ObjectBuffers", table);

        byte[] original = [1, 2, 3, 4];
        table[-1] = (original, b => { writtenBack = true; received = b; });

        List<GuestBuffer>? buffers = null;
        List<(GuestBuffer Buffer, int Length, Action<byte[]> WriteBack)>? writeBacks = null;

        var ptr = BinaryLibrary.MarshalArgument(null, mem, BinaryArgument.FromPointer(-1), ref buffers, ref writeBacks);
        Assert.NotEqual(0u, ptr);
        Assert.NotNull(buffers);
        Assert.Single(buffers);
        Assert.NotNull(writeBacks);
        Assert.Single(writeBacks);

        // Verify bytes were copied to guest memory
        var fromGuest = mem.ReadBytes(ptr, 4);
        Assert.Equal(original, fromGuest);

        // Simulate guest modifying the memory
        mem.Write(ptr, [9, 8, 7, 6]);

        // Complete write back
        BinaryLibrary.CompleteWriteBacks(writeBacks);
        Assert.True(writtenBack);
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, received);

        // Clean up buffers
        foreach (var b in buffers) b.Dispose();
    }

    [Fact]
    public void MarshalArgumentPointerZeroReturnsZero()
    {
        using var m = CreateModule();
        var mem = m.Memory;
        List<GuestBuffer>? buffers = null;
        List<(GuestBuffer Buffer, int Length, Action<byte[]> WriteBack)>? writeBacks = null;

        var ptr = BinaryLibrary.MarshalArgument(null, mem, BinaryArgument.FromPointer(0), ref buffers, ref writeBacks);
        Assert.Equal(0u, ptr);
        Assert.Null(buffers);
        Assert.Null(writeBacks);
    }

    [Fact]
    public void TryResolveBankDataPointerValidatesLayout()
    {
        using var m = CreateModule();
        var mem = m.Memory;

        // Candidate below 0x10000 fails
        Assert.False(BinaryLibrary.TryResolveBankDataPointer(mem, 0x1000u, out _));

        // Allocate candidate block
        using var bank = mem.Allocate(16);
        using var vptr = mem.Allocate(16);
        using var data = mem.Allocate(64);

        mem.WriteUInt32(bank.Address, vptr.Address);
        mem.WriteUInt32(bank.Address + 4, data.Address);
        mem.WriteUInt32(bank.Address + 8, 32); // size
        mem.WriteUInt32(bank.Address + 12, 64); // capacity

        Assert.True(BinaryLibrary.TryResolveBankDataPointer(mem, bank.Address, out var resolvedData));
        Assert.Equal(data.Address, resolvedData);

        // Invalid capacity (< size) fails
        mem.WriteUInt32(bank.Address + 12, 16);
        Assert.False(BinaryLibrary.TryResolveBankDataPointer(mem, bank.Address, out _));
    }

    [Fact]
    public void ReadAnsiCStringReadsNulTerminatedString()
    {
        using var m = CreateModule();
        var mem = m.Memory;

        Assert.Equal("", BinaryLibrary.ReadAnsiCString(mem, 0));

        using var buf = mem.Allocate(16);
        mem.Write(buf.Address, [(byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0]);

        var str = BinaryLibrary.ReadAnsiCString(mem, buf.Address);
        Assert.Equal("Hello", str);
    }

    [Fact]
    public void Win32ImportResolverCallWithGuestMemoryWorks()
    {
        using var m = CreateModule();
        var mem = m.Memory;
        using var r = new Win32ImportResolver();
        using var buf = mem.Allocate(32);

        var ret = r.Call(mem, "KERNEL32.dll", "GlobalMemoryStatus", buf.Address);
        Assert.Equal(0u, ret);
        Assert.Equal(32u, mem.ReadUInt32(buf.Address));
        Assert.Equal(25u, mem.ReadUInt32(buf.Address + 4));
        Assert.Equal(0x7FFF0000u, mem.ReadUInt32(buf.Address + 8));
        Assert.Equal(0x60000000u, mem.ReadUInt32(buf.Address + 12));
    }
}
