using System.Globalization;
using SpaceTree.Core.Native;
using SpaceTree.Core.Util;
using Xunit;

namespace SpaceTree.Core.Tests;

public class SizeFormatterTests
{
    public SizeFormatterTests()
    {
        // Pin the culture so separator expectations hold on any machine.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 bytes")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1024 * 1024, "1.00 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TB")]
    [InlineData(-2048, "-2.00 KB")]
    public void Format_Binary(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes));

    [Fact]
    public void Format_KeepsThreeSignificantDigits()
    {
        Assert.Equal("9.77 KB", SizeFormatter.Format(10_000));
        Assert.Equal("97.7 KB", SizeFormatter.Format(100_000));
        Assert.Equal("977 KB", SizeFormatter.Format(1_000_000));
    }

    [Fact]
    public void Format_Decimal_UsesPowersOfOneThousand() =>
        Assert.Equal("1.00 MB", SizeFormatter.Format(1_000_000, SizeUnitSystem.Decimal));

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(0, "0 bytes")]
    [InlineData(1234567, "1,234,567 bytes")]
    public void FormatExact_UsesSeparators(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.FormatExact(bytes));

    [Theory]
    [InlineData(1.0, "100 %")]
    [InlineData(0.5, "50.0 %")]
    [InlineData(0.0123, "1.23 %")]
    [InlineData(0, "0.00 %")]
    public void FormatPercent(double fraction, string expected) =>
        Assert.Equal(expected, SizeFormatter.FormatPercent(fraction));

    [Fact]
    public void FormatPercent_HandlesNaN() => Assert.Equal("-", SizeFormatter.FormatPercent(double.NaN));

    [Fact]
    public void FormatDate_BlankForUnknown() => Assert.Equal(string.Empty, SizeFormatter.FormatDate(DateTime.MinValue));

    [Theory]
    [InlineData(250, "250 ms")]
    [InlineData(2_500, "2.5 s")]
    public void FormatDuration_ShortSpans(int ms, string expected) =>
        Assert.Equal(expected, SizeFormatter.FormatDuration(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void FormatDuration_LongSpans()
    {
        Assert.Equal("2m 05s", SizeFormatter.FormatDuration(TimeSpan.FromSeconds(125)));
        Assert.Equal("1h 01m 05s", SizeFormatter.FormatDuration(TimeSpan.FromSeconds(3665)));
    }
}

public class LongPathTests
{
    [Theory]
    [InlineData(@"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"\\server\share", @"\\?\UNC\server\share")]
    [InlineData(@"\\?\C:\already", @"\\?\C:\already")]
    public void ToExtended(string input, string expected) =>
        Assert.Equal(expected, LongPath.ToExtended(input));

    [Theory]
    [InlineData(@"\\?\C:\Windows", @"C:\Windows")]
    [InlineData(@"\\?\UNC\server\share", @"\\server\share")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void ToDisplay(string input, string expected) =>
        Assert.Equal(expected, LongPath.ToDisplay(input));

    [Fact]
    public void ToExtended_RoundTripsThroughToDisplay()
    {
        const string original = @"C:\Users\test\Documents";
        Assert.Equal(original, LongPath.ToDisplay(LongPath.ToExtended(original)));
    }

    [Theory]
    [InlineData(@"C:\a", "b", @"C:\a\b")]
    [InlineData(@"C:\a\", "b", @"C:\a\b")]
    [InlineData(@"\\?\C:\", "Windows", @"\\?\C:\Windows")]
    public void Combine(string dir, string name, string expected) =>
        Assert.Equal(expected, LongPath.Combine(dir, name));

    [Theory]
    [InlineData("C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Temp\", @"C:\Temp")]
    [InlineData(@"  C:\Temp  ", @"C:\Temp")]
    [InlineData("\"C:\\Temp\"", @"C:\Temp")]
    [InlineData("C:/Temp/sub", @"C:\Temp\sub")]
    public void NormalizeRoot(string input, string expected) =>
        Assert.Equal(expected, LongPath.NormalizeRoot(input));

    [Fact]
    public void SearchPattern_AppendsStar() =>
        Assert.Equal(@"\\?\C:\Temp\*", LongPath.SearchPattern(@"\\?\C:\Temp"));
}
