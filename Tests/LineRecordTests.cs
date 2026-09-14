using Shared;

namespace Tests;

public class LineRecordTests
{
    [Fact]
    public void ParsesNumberAndText()
    {
        Assert.True(LineRecord.TryParse("415. Apple", out var record));
        Assert.Equal(415, record.Number);
        Assert.Equal("Apple", record.Text);
    }

    [Fact]
    public void SplitsOnFirstSeparatorOnly()
    {
        Assert.True(LineRecord.TryParse("32. Cherry is the best", out var record));
        Assert.Equal(32, record.Number);
        Assert.Equal("Cherry is the best", record.Text);
    }

    [Fact]
    public void KeepsLaterSeparatorsInsideText()
    {
        Assert.True(LineRecord.TryParse("7. 32. Cherry is the best", out var record));
        Assert.Equal(7, record.Number);
        Assert.Equal("32. Cherry is the best", record.Text);
    }

    [Fact]
    public void TreatsEmptyTextAsWellFormed()
    {
        Assert.True(LineRecord.TryParse("415. ", out var record));
        Assert.Equal(415, record.Number);
        Assert.Equal(string.Empty, record.Text);
    }

    [Theory]
    [InlineData("no separator at all")]
    [InlineData("")]
    [InlineData("12.NoSpaceAfterTheDot")]
    [InlineData(". Missing number")]
    [InlineData("abc. Not a number")]
    [InlineData("-5. Negative number")]
    [InlineData("1 000. Grouped number")]
    public void ReportsMalformedLineAsFailure(string line)
    {
        Assert.False(LineRecord.TryParse(line, out var record));
        Assert.Equal(0L, record.Number);
        Assert.Null(record.Text);
    }

    [Fact]
    public void ReportsNullLineAsFailure()
    {
        Assert.False(LineRecord.TryParse(null, out _));
    }

    [Fact]
    public void ParsesNumberAtLongMaxValue()
    {
        Assert.True(LineRecord.TryParse($"{long.MaxValue}. Apple", out var record));
        Assert.Equal(long.MaxValue, record.Number);
    }

    [Fact]
    public void ReportsNumberBeyondLongRangeAsFailure()
    {
        Assert.False(LineRecord.TryParse("9223372036854775808. Apple", out _));
        Assert.False(LineRecord.TryParse("99999999999999999999. Apple", out _));
    }
}
