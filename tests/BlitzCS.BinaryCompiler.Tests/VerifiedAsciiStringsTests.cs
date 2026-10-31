using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class VerifiedAsciiStringsTests
{
    [Fact]
    public void LenAndAscAcceptAsciiAndEmbeddedNul()
    {
        AssertInt("Len", [BinaryArgument.FromString("")], 0);
        AssertInt("Len", [BinaryArgument.FromString("a\0b")], 3);
        AssertInt("Asc", [BinaryArgument.FromString("")], -1);
        AssertInt("Asc", [BinaryArgument.FromString("\0a")], 0);
        AssertInt("Asc", [BinaryArgument.FromString("~")], 126);
    }

    [Fact]
    public void MidUsesOneBasedOffsetsAndKeepsEmbeddedNul()
    {
        AssertString("Mid", [BinaryArgument.FromString("a\0bc"), BinaryArgument.FromInt32(2), BinaryArgument.FromInt32(2)], "\0b");
        AssertString("Mid", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(2), BinaryArgument.FromInt32(-2)], "bcd");
        AssertString("Mid", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(2), BinaryArgument.FromInt32(0)], "");
        AssertString("Mid", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(int.MaxValue), BinaryArgument.FromInt32(int.MaxValue)], "");
    }

    [Fact]
    public void InstrUsesOneBasedPositionsAndNativeEmptyNeedleBoundsWithNul()
    {
        Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString(""), BinaryArgument.FromString(""), BinaryArgument.FromInt32(1)], out _));
        AssertInt("Instr", [BinaryArgument.FromString("a\0ba\0b"), BinaryArgument.FromString("\0b"), BinaryArgument.FromInt32(1)], 2);
        AssertInt("Instr", [BinaryArgument.FromString("ababa"), BinaryArgument.FromString("ba"), BinaryArgument.FromInt32(3)], 4);
        AssertInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString("z"), BinaryArgument.FromInt32(1)], 0);
        AssertInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString(""), BinaryArgument.FromInt32(4)], 4);
        AssertInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString(""), BinaryArgument.FromInt32(5)], 0);
    }

    [Fact]
    public void LeftRightAndAsciiCaseTrimMatchTheBoundedSubset()
    {
        AssertString("Left", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(2)], "ab");
        AssertString("Left", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(0)], "");
        AssertString("Right", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(2)], "cd");
        AssertString("Right", [BinaryArgument.FromString("abcd"), BinaryArgument.FromInt32(9)], "abcd");
        AssertString("Upper", [BinaryArgument.FromString("aZ09!? ")], "AZ09!? ");
        AssertString("Lower", [BinaryArgument.FromString("aZ09!? ")], "az09!? ");
        AssertString("Trim", [BinaryArgument.FromString("\t\n abc \r\u007f")], "abc");
        AssertString("Trim", [BinaryArgument.FromString("\t\n \r\u007f")], "");
        AssertString("Upper", [BinaryArgument.FromString("ABC!? ")], "ABC!? ");
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Upper")]
    [InlineData("Lower")]
    [InlineData("Trim")]
    public void ConservativeStringCommandsRejectEmbeddedNul(string name)
    {
        var args = name is "Left" or "Right"
            ? new[] { BinaryArgument.FromString("a\0b"), BinaryArgument.FromInt32(1) }
            : [BinaryArgument.FromString("a\0b")];
        Assert.False(VerifiedAsciiStrings.TryCallString(name, args, out _));
    }

    [Fact]
    public void NonAsciiUnicodeAndSurrogatesFallBack()
    {
        foreach (var input in new[] { "é", "😀", "\ud800", "a漢b" })
        {
            Assert.False(VerifiedAsciiStrings.TryCallInt("Len", [BinaryArgument.FromString(input)], out _));
            Assert.False(VerifiedAsciiStrings.TryCallString("Upper", [BinaryArgument.FromString(input)], out _));
            Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString("a"), BinaryArgument.FromString(input), BinaryArgument.FromInt32(1)], out _));
        }
    }

    [Fact]
    public void InvalidOffsetsAndNegativeRangeCountsFallBack()
    {
        Assert.False(VerifiedAsciiStrings.TryCallString("Mid", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(0), BinaryArgument.FromInt32(-1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Mid", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(-1), BinaryArgument.FromInt32(-1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString("a"), BinaryArgument.FromInt32(0)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString("a"), BinaryArgument.FromInt32(-1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Left", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(-1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Right", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(-1)], out _));
    }

    [Fact]
    public void UnknownNamesWrongAritiesAndArgumentTypesFallBack()
    {
        Assert.False(VerifiedAsciiStrings.TryCallString("Replace", [BinaryArgument.FromString("a"), BinaryArgument.FromString("a"), BinaryArgument.FromString("b")], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Mid", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromString("a")], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Left", [BinaryArgument.FromString("abc"), BinaryArgument.FromSingle(1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Left", [BinaryArgument.FromString("abc"), BinaryArgument.FromPointer(1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallString("Left", [BinaryArgument.FromInt32(1), BinaryArgument.FromInt32(1)], out _));
        Assert.False(VerifiedAsciiStrings.TryCallInt("Instr", [BinaryArgument.FromString("abc"), BinaryArgument.FromInt32(1), BinaryArgument.FromInt32(1)], out _));
    }

    private static void AssertString(string name, ReadOnlySpan<BinaryArgument> args, string expected)
    {
        Assert.True(VerifiedAsciiStrings.TryCallString(name, args, out var actual));
        Assert.Equal(expected, actual);
    }

    private static void AssertInt(string name, ReadOnlySpan<BinaryArgument> args, int expected)
    {
        Assert.True(VerifiedAsciiStrings.TryCallInt(name, args, out var actual));
        Assert.Equal(expected, actual);
    }
}
