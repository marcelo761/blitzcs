using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class VerifiedRuntimeStringsTests
{
    [Theory]
    [InlineData(239600u, "Left", 2, true)]
    [InlineData(239792u, "Right", 2, true)]
    [InlineData(240624u, "Mid", 3, true)]
    [InlineData(240912u, "Upper", 1, true)]
    [InlineData(241248u, "Lower", 1, true)]
    [InlineData(241584u, "Trim", 1, true)]
    [InlineData(240320u, "Instr", 3, false)]
    [InlineData(242992u, "Asc", 1, false)]
    [InlineData(243184u, "Len", 1, false)]
    public void AsciiDispatchRequiresExactCommandIdentityAndReturnKind(uint rva, string name, int count, bool returnsString)
    {
        Assert.Equal(returnsString, VerifiedRuntimeStrings.IsAsciiStringCommand(rva, name, count));
        Assert.Equal(!returnsString, VerifiedRuntimeStrings.IsAsciiIntCommand(rva, name, count));
        Assert.False(VerifiedRuntimeStrings.IsAsciiStringCommand(rva + 1, name, count));
        Assert.False(VerifiedRuntimeStrings.IsAsciiIntCommand(rva + 1, name, count));
        Assert.False(VerifiedRuntimeStrings.IsAsciiStringCommand(rva, name, count + 1));
        Assert.False(VerifiedRuntimeStrings.IsAsciiIntCommand(rva, name, count + 1));
        Assert.False(VerifiedRuntimeStrings.IsAsciiStringCommand(rva, name.ToLowerInvariant(), count));
        Assert.False(VerifiedRuntimeStrings.IsAsciiIntCommand(rva, name.ToLowerInvariant(), count));
    }

    [Fact]
    public void ChrEligibilityRequiresTheVerifiedImageAndExactNumericCommandShape()
    {
        ReadOnlySpan<BinaryArgument> valid = [BinaryArgument.FromInt32(65)];
        Assert.True(VerifiedRuntimeStrings.IsEligibleChr(VerifiedRuntimeStrings.RuntimeSha256, VerifiedRuntimeStrings.ChrRva, "Chr", 1, valid));
        Assert.True(VerifiedRuntimeStrings.IsEligibleChr(VerifiedRuntimeStrings.RuntimeSha256.ToUpperInvariant(), VerifiedRuntimeStrings.ChrRva, "Chr", 1, valid));

        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(new string('0', 64), VerifiedRuntimeStrings.ChrRva, "Chr", 1, valid));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(VerifiedRuntimeStrings.RuntimeSha256, VerifiedRuntimeStrings.ChrRva + 1, "Chr", 1, valid));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(VerifiedRuntimeStrings.RuntimeSha256, VerifiedRuntimeStrings.ChrRva, "chr", 1, valid));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(VerifiedRuntimeStrings.RuntimeSha256, VerifiedRuntimeStrings.ChrRva, "Chr", 2, valid));
    }

    [Fact]
    public void ChrEligibilityRejectsTextFloatPointerAndWrongArity()
    {
        ReadOnlySpan<BinaryArgument> text = [BinaryArgument.FromString("A")];
        ReadOnlySpan<BinaryArgument> number = [BinaryArgument.FromInt32(65)];
        ReadOnlySpan<BinaryArgument> floatingPoint = [BinaryArgument.FromSingle(65)];
        ReadOnlySpan<BinaryArgument> pointer = [BinaryArgument.FromPointer(65)];
        ReadOnlySpan<BinaryArgument> none = [];
        var hash = VerifiedRuntimeStrings.RuntimeSha256;

        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(hash, VerifiedRuntimeStrings.ChrRva, "Chr", 1, text));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(hash, VerifiedRuntimeStrings.ChrRva, "Chr", 1, floatingPoint));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(hash, VerifiedRuntimeStrings.ChrRva, "Chr", 1, pointer));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(hash, VerifiedRuntimeStrings.ChrRva, "Chr", 1, none));
        Assert.False(VerifiedRuntimeStrings.IsEligibleChr(hash, VerifiedRuntimeStrings.ChrRva, "Chr", 0, number));
    }

    [Fact]
    public void ChrUsesCachedCp1252StringsAndDoesNotAllocateAfterWarmup()
    {
        Assert.Equal("€", VerifiedRuntimeStrings.GetChr(128));
        Assert.Equal("Œ", VerifiedRuntimeStrings.GetChr(140));
        Assert.Equal("ÿ", VerifiedRuntimeStrings.GetChr(255));
        Assert.Equal("\0", VerifiedRuntimeStrings.GetChr(0));

        for (var value = 0; value < 256; value++)
            _ = VerifiedRuntimeStrings.GetChr((uint)value);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            GC.KeepAlive(VerifiedRuntimeStrings.GetChr((uint)i));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
    }
}
