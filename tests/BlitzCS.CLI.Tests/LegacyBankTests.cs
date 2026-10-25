using System.Runtime.InteropServices;
using BlitzCS.LegacyRuntime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyBankTests
{
    [Fact]
    public void PositiveHandlesAreStableAcrossResizeAndNeverReusedAfterFree()
    {
        using var banks = new BankStore();
        var first = banks.CreateBank();
        Assert.True(first > 0);
        Assert.Equal(0, banks.BankSize(first));
        banks.ResizeBank(first, 16);
        Assert.Equal(16, banks.BankSize(first));
        banks.FreeBank(first);
        banks.FreeBank(first);
        banks.FreeBank(0);
        banks.FreeBank(123);
        var next = banks.CreateBank(4);
        Assert.NotEqual(first, next);
        Assert.Throws<InvalidOperationException>(() => banks.BankSize(first));
        Assert.Throws<InvalidOperationException>(() => banks.BankSize(1));
    }

    [Fact]
    public void PrimitivesUseLittleEndianUnalignedBytesAndLegacyNarrowing()
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(24);
        banks.PokeByte(bank, 0, -1);
        Assert.Equal(255, banks.PeekByte(bank, 0));
        banks.PokeByte(bank, 0, 258);
        Assert.Equal(2, banks.PeekByte(bank, 0));
        banks.PokeShort(bank, 1, -2);
        Assert.Equal(65534, banks.PeekShort(bank, 1));
        banks.PokeInt(bank, 3, unchecked((int)0x89abcdef));
        Assert.Equal(unchecked((int)0x89abcdef), banks.PeekInt(bank, 3));
        Assert.Equal(new[] { 239, 205, 171, 137 }, Enumerable.Range(3, 4).Select(i => banks.PeekByte(bank, i)));
        banks.PokeFloat(bank, 9, 1.5f);
        Assert.Equal(1.5f, banks.PeekFloat(bank, 9));
        Assert.Equal(1069547520, banks.PeekInt(bank, 9));
    }

    [Fact]
    public void ResizePreservesPrefixAndClearsEveryNewlyVisibleByte()
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(4);
        banks.PokeInt(bank, 0, 0x01020304);
        banks.ResizeBank(bank, 32);
        Assert.Equal(0x01020304, banks.PeekInt(bank, 0));
        Assert.All(Enumerable.Range(4, 28), i => Assert.Equal(0, banks.PeekByte(bank, i)));
        banks.PokeByte(bank, 31, 123);
        banks.ResizeBank(bank, 4);
        banks.ResizeBank(bank, 32);
        Assert.Equal(0, banks.PeekByte(bank, 31));
    }

    [Fact]
    public void CopyBankHasMemmoveSemanticsAndValidatesBeforeMutation()
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(8);
        for (var i = 0; i < 8; i++) banks.PokeByte(bank, i, i);
        banks.CopyBank(bank, 0, bank, 2, 6);
        Assert.Equal(new[] { 0, 1, 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 8).Select(i => banks.PeekByte(bank, i)));
        banks.CopyBank(bank, 2, bank, 0, 6);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 4, 5 }, Enumerable.Range(0, 8).Select(i => banks.PeekByte(bank, i)));
        var target = banks.CreateBank(8);
        banks.CopyBank(bank, 0, target, 0, 8);
        Assert.Equal(5, banks.PeekByte(target, 7));
        banks.CopyBank(bank, 8, target, 8, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => banks.CopyBank(bank, 0, target, 4, 8));
        Assert.Equal(5, banks.PeekByte(target, 7));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void EveryIntAccessChecksTheCompleteRange(int offset)
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => banks.PeekInt(bank, offset));
        Assert.Throws<ArgumentOutOfRangeException>(() => banks.PokeInt(bank, offset, 42));
        Assert.Equal(0, banks.PeekInt(bank, 0));
    }

    [Fact]
    public void NativeBorrowWritesOwnedBytesAndRejectsLifetimeChangesUntilItReturns()
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(16);
        banks.WithPointer(bank, 0, 16, (pointer, length) =>
        {
            Assert.Equal(16, length);
            Assert.Equal(0, (long)pointer % 4);
            Assert.Throws<InvalidOperationException>(() => banks.FreeBank(bank));
            Assert.Throws<InvalidOperationException>(() => banks.ResizeBank(bank, 8));
            Assert.Throws<InvalidOperationException>(() => banks.Clear());
            Assert.Throws<InvalidOperationException>(() => banks.Dispose());
            Marshal.WriteInt32(pointer, 0, 10);
            Marshal.WriteInt32(pointer, 4, 20);
            Marshal.WriteInt32(pointer, 8, 110);
            Marshal.WriteInt32(pointer, 12, 220);
            return 0;
        }, alignment: 4);
        Assert.Equal(110, banks.PeekInt(bank, 8));
        banks.FreeBank(bank);
        Assert.Equal(0, banks.LiveBankCount);
    }

    [Fact]
    public void CallbackFailureDoesNotLeaveTheBankPinnedOrBorrowed()
    {
        using var banks = new BankStore();
        var bank = banks.CreateBank(4);
        Assert.Throws<ApplicationException>(() => banks.WithPointer<int>(bank, 0, 4,
            (_, _) => throw new ApplicationException("native call failed")));
        banks.ResizeBank(bank, 8);
        banks.FreeBank(bank);
        Assert.Equal(0, banks.LiveBankCount);
    }

    [Fact]
    public void FreeAndDisposeRemoveAllRootsAndCannotReviveTokens()
    {
        var banks = new BankStore(maximumBankSize: 64);
        var old = banks.CreateBank(16);
        Assert.Throws<ArgumentOutOfRangeException>(() => banks.CreateBank(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => banks.CreateBank(65));
        for (var i = 0; i < 100_000; i++)
        {
            var bank = banks.CreateBank(16);
            banks.FreeBank(bank);
        }
        Assert.Equal(1, banks.LiveBankCount);
        banks.Clear();
        Assert.Equal(0, banks.LiveBankCount);
        var next = banks.CreateBank();
        Assert.NotEqual(old, next);
        banks.Dispose();
        banks.Dispose();
        Assert.Throws<ObjectDisposedException>(() => banks.BankSize(next));
        Assert.Throws<ObjectDisposedException>(() => banks.CreateBank());
    }
}
