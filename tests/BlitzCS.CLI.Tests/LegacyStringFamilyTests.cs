using System.Globalization;
using System.Text;
using Blitz3DCS.Runtime;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyStringFamilyTests
{
    [Theory]
    [InlineData("ab", 3, "ababab")]
    [InlineData("x", 0, "")]
    [InlineData("x", -1, "")]
    [InlineData("", int.MaxValue, "")]
    [InlineData("€\0", 2, "€\0€\0")]
    public void StringRepeatsWholeLegacyByteSequences(string text, int count, string expected) =>
        Assert.Equal(expected, LegacyStrings.String(text, count));

    [Fact]
    public void StringRejectsOutputLengthOverflow() =>
        Assert.Throws<OverflowException>(() => LegacyStrings.String("ab", int.MaxValue));

    [Theory]
    [InlineData(0, "", "")]
    [InlineData(2, "a€", "\0z")]
    [InlineData(99, "a€\0z", "a€\0z")]
    public void LeftAndRightClampToTheLegacyByteLength(int count, string left, string right)
    {
        Assert.Equal(left, LegacyStrings.Left("a€\0z", count));
        Assert.Equal(right, LegacyStrings.Right("a€\0z", count));
    }

    [Theory]
    [InlineData("aaaa", "aa", "b", "bb")]
    [InlineData("aaa", "aa", "b", "ba")]
    [InlineData("a", "a", "aa", "aa")]
    [InlineData("banana", "na", "", "ba")]
    [InlineData("AbA", "a", "x", "AbA")]
    [InlineData("€\0€", "€", "Œ", "Œ\0Œ")]
    [InlineData("", "", "x", "")]
    public void ReplaceIsOrdinalNonOverlappingAndDoesNotRescanInsertedText(string text, string from, string to, string expected) =>
        Assert.Equal(expected, LegacyStrings.Replace(text, from, to));

    [Fact]
    public void ReplaceRejectsAnEmptyNeedleOnNonemptyInput() =>
        Assert.Throws<ArgumentException>(() => LegacyStrings.Replace("abc", "", "x"));

    [Theory]
    [InlineData("banana", "na", 1, 3)]
    [InlineData("banana", "na", 4, 5)]
    [InlineData("banana", "NA", 1, 0)]
    [InlineData("abc", "", 4, 4)]
    [InlineData("abc", "", 5, 0)]
    [InlineData("", "", 1, 1)]
    [InlineData("abc", "a", int.MaxValue, 0)]
    [InlineData("€\0Œ", "\0", 1, 2)]
    public void InstrUsesOneBasedPositionsAndNativeEmptyNeedleBounds(string text, string find, int from, int expected) =>
        Assert.Equal(expected, LegacyStrings.Instr(text, find, from));

    [Fact]
    public void InstrDefaultsToTheFirstByte() => Assert.Equal(2, LegacyStrings.Instr("abc", "b"));

    [Theory]
    [InlineData(2, 2, "€\0")]
    [InlineData(2, -1, "€\0z")]
    [InlineData(2, int.MinValue, "€\0z")]
    [InlineData(2, 0, "")]
    [InlineData(2, int.MaxValue, "€\0z")]
    [InlineData(5, -1, "")]
    [InlineData(int.MaxValue, 2, "")]
    public void MidClampsStartsAndTreatsEveryNegativeCountAsTheRemainingSuffix(int start, int count, string expected) =>
        Assert.Equal(expected, LegacyStrings.Mid("a€\0z", start, count));

    [Fact]
    public void MidDefaultsToTheRemainingSuffix() => Assert.Equal("bc", LegacyStrings.Mid("abc", 2));

    [Fact]
    public void AsciiCaseConversionAndTrimPreserveInteriorControlBytes()
    {
        Assert.Equal("AZ\0 19!", LegacyStrings.Upper("aZ\0 19!"));
        Assert.Equal("az\0 19!", LegacyStrings.Lower("Az\0 19!"));
        Assert.Equal("a\0 b", LegacyStrings.Trim("\0\t\n a\0 b \r\u007f"));
        Assert.Equal("", LegacyStrings.Trim("\0\t\n\r \u007f"));
    }

    [Fact]
    public void NativeCrtCaseAndTrimTreatHighBytesAsNonGraphicalWithoutUnicodeCasing()
    {
        Assert.Equal("éÿŒ€", LegacyStrings.Upper("éÿŒ€"));
        Assert.Equal("ÉŸœ€", LegacyStrings.Lower("ÉŸœ€"));
        Assert.Equal("", LegacyStrings.Trim(LegacyStrings.Chr(233)));
        Assert.Equal("abc", LegacyStrings.Trim("é€ abc Œÿ"));
        Assert.Equal("aé€\0b", LegacyStrings.Trim("éaé€\0bÿ"));
    }

    [Theory]
    [InlineData(0, "", "")]
    [InlineData(2, "a€", "\0z")]
    [InlineData(4, "a€\0z", "a€\0z")]
    [InlineData(6, "a€\0z  ", "  a€\0z")]
    public void SetFunctionsTruncateOrPadWithSpaces(int size, string left, string right)
    {
        Assert.Equal(left, LegacyStrings.LSet("a€\0z", size));
        Assert.Equal(right, LegacyStrings.RSet("a€\0z", size));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeSizesRetainTheOriginalParameterError(int count)
    {
        foreach (var call in new Action[] {
            () => LegacyStrings.Left("abc", count), () => LegacyStrings.Right("abc", count),
            () => LegacyStrings.LSet("abc", count), () => LegacyStrings.RSet("abc", count) })
            Assert.Contains("parameter must be positive", Assert.Throws<ArgumentOutOfRangeException>(call).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveOffsetsRetainTheOriginalParameterError(int start)
    {
        Assert.Contains("parameter must be greater than 0", Assert.Throws<ArgumentOutOfRangeException>(() => LegacyStrings.Mid("abc", start)).Message);
        Assert.Contains("parameter must be greater than 0", Assert.Throws<ArgumentOutOfRangeException>(() => LegacyStrings.Instr("abc", "a", start)).Message);
    }

    [Theory]
    [InlineData(0, "\0")]
    [InlineData(128, "€")]
    [InlineData(140, "Œ")]
    [InlineData(255, "ÿ")]
    [InlineData(-1, "ÿ")]
    [InlineData(384, "€")]
    public void ChrUsesTheLowEightBitsAndAscReturnsUnsignedBytes(int code, string expected)
    {
        Assert.Equal(expected, LegacyStrings.Chr(code));
        Assert.Equal(code & 255, LegacyStrings.Asc(expected));
    }

    [Fact]
    public void AscAndLenHandleEmptyStringsAndEmbeddedNul()
    {
        Assert.Equal(-1, LegacyStrings.Asc(""));
        Assert.Equal(0, LegacyStrings.Asc("\0€"));
        Assert.Equal(128, LegacyStrings.Asc("€\0"));
        Assert.Equal(0, LegacyStrings.Len(""));
        Assert.Equal(4, LegacyStrings.Len("€\0Œÿ"));
    }

    [Fact]
    public void EveryLegacyByteRoundTripsThroughChrAscAndLen()
    {
        for (var value = 0; value <= 255; value++)
        {
            var text = LegacyStrings.Chr(value);
            Assert.Equal(value, LegacyStrings.Asc(text));
            Assert.Equal(1, LegacyStrings.Len(text));
        }
    }

    [Fact]
    public void AsciiCasingIsIndependentOfTheCallingCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("I", LegacyStrings.Upper("i"));
            Assert.Equal("i", LegacyStrings.Lower("I"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "00000000", "00000000000000000000000000000000")]
    [InlineData(1, "00000001", "00000000000000000000000000000001")]
    [InlineData(-1, "FFFFFFFF", "11111111111111111111111111111111")]
    [InlineData(int.MinValue, "80000000", "10000000000000000000000000000000")]
    [InlineData(int.MaxValue, "7FFFFFFF", "01111111111111111111111111111111")]
    [InlineData(0xABCDEF, "00ABCDEF", "00000000101010111100110111101111")]
    public void NumericFormatsUseFixedWidthTwosComplement(int value, string hex, string bin)
    {
        Assert.Equal(hex, LegacyStrings.Hex(value));
        Assert.Equal(bin, LegacyStrings.Bin(value));
    }

    [Theory]
    [InlineData("漢")]
    [InlineData("😀")]
    [InlineData("\ud800")]
    public void AllStringInputsRejectTextOutsideTheLegacyEncodingEvenOnEmptyResults(string unsupported)
    {
        foreach (var call in new Action[] {
            () => LegacyStrings.String(unsupported, 0), () => LegacyStrings.Left(unsupported, 0),
            () => LegacyStrings.Right(unsupported, 0), () => LegacyStrings.Mid(unsupported, int.MaxValue),
            () => LegacyStrings.LSet(unsupported, 0), () => LegacyStrings.RSet(unsupported, 0),
            () => LegacyStrings.Upper(unsupported), () => LegacyStrings.Lower(unsupported),
            () => LegacyStrings.Trim(unsupported), () => LegacyStrings.Asc("a" + unsupported),
            () => LegacyStrings.Len(unsupported), () => LegacyStrings.Instr("", unsupported),
            () => LegacyStrings.Instr(unsupported, ""), () => LegacyStrings.Replace(unsupported, "a", "b"),
            () => LegacyStrings.Replace("", unsupported, ""), () => LegacyStrings.Replace("", "", unsupported) })
            Assert.Throws<EncoderFallbackException>(call);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(4096)]
    public void LongAsciiPrefixesNeverHideAnInvalidCharacterOrDropValidLegacyBytes(int prefixLength)
    {
        var prefix = new string('a', prefixLength);
        Assert.Throws<EncoderFallbackException>(() => LegacyStrings.Left(prefix + "漢", 1));
        Assert.Throws<EncoderFallbackException>(() => LegacyStrings.Instr(prefix + "\ud800", "a"));
        Assert.Equal(prefixLength + 1, LegacyStrings.Len(prefix + "€"));
        Assert.Equal(128, LegacyStrings.Asc(LegacyStrings.Right(prefix + "€", 1)));
    }

    [Fact]
    public void CurrentDateAndTimeUseTheLocalClockAndInvariantNativeFormats()
    {
        var before = DateTime.Now;
        var date = LegacyStrings.CurrentDate();
        var time = LegacyStrings.CurrentTime();
        var after = DateTime.Now;
        Assert.True(DateTime.TryParseExact(date, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Assert.True(DateTime.TryParseExact(time, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Assert.Contains(date, new[] { before.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), after.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) });
        var firstSecond = before.AddTicks(-(before.Ticks % TimeSpan.TicksPerSecond));
        Assert.Contains(Enumerable.Range(0, checked((int)(after - firstSecond).TotalSeconds + 1)),
            seconds => firstSecond.AddSeconds(seconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture) == time);
    }
}
