using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class NativeStringRuntimeTests
{
    [Theory]
    [InlineData(65, "A")]
    [InlineData(321, "A")]
    [InlineData(128, "€")]
    [InlineData(-1, "ÿ")]
    [InlineData(256, "\0")]
    public void ChrUsesLowByteAndExplicitLegacyEncoding(int code, string expected)
    {
        Assert.Equal(expected, LegacyStrings.Chr(code));
        Assert.Equal(1, LegacyStrings.Len(LegacyStrings.Chr(code)));
    }

    [Fact]
    public void LenCountsLegacyBytesIncludingEmbeddedZero()
    {
        for (var code = 0; code < 256; code++)
            Assert.Equal(1, LegacyStrings.Len(LegacyStrings.Chr(code)));
        Assert.Equal(0, LegacyStrings.Len(""));
        Assert.Equal(5, LegacyStrings.Len("abc\r\n"));
        Assert.Equal(2, LegacyStrings.Len("\0x"));
        Assert.Throws<System.Text.EncoderFallbackException>(() => LegacyStrings.Len("😀"));
    }
}
