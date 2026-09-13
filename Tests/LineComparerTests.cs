using Shared;

namespace Tests;

public class LineComparerTests
{
    [Fact]
    public void OrdersByTextBeforeNumber()
    {
        LineRecord apple = new(30432, "Apple");
        LineRecord banana = new(1, "Banana");

        Assert.True(LineComparer.Instance.Compare(apple, banana) < 0);
    }

    [Fact]
    public void BreaksTiesByNumberAscending()
    {
        LineRecord first = new(1, "Apple");
        LineRecord second = new(415, "Apple");

        Assert.True(LineComparer.Instance.Compare(first, second) < 0);
        Assert.True(LineComparer.Instance.Compare(second, first) > 0);
        Assert.Equal(0, LineComparer.Instance.Compare(first, first));
    }

    [Fact]
    public void SortsNumbersNumericallyNotLexicographically()
    {
        LineRecord[] records = [new(100, "Apple"), new(3, "Apple"), new(20, "Apple"), new(9, "Apple")];

        Array.Sort(records, LineComparer.Instance);

        Assert.Equal([3, 9, 20, 100], records.Select(record => record.Number));
    }

    [Fact]
    public void ComparesTextCaseSensitively()
    {
        LineRecord upper = new(1, "Apple");
        LineRecord lower = new(1, "apple");

        // Ordinal puts every uppercase letter before every lowercase one.
        Assert.True(LineComparer.Instance.Compare(upper, lower) < 0);
    }

    [Fact]
    public void SortsTheBriefsExampleIntoExpectedOrder()
    {
        LineRecord[] records =
        [
            new(415, "Apple"),
            new(30432, "Something something something"),
            new(1, "Apple"),
            new(32, "Cherry is the best"),
            new(2, "Banana is yellow")
        ];

        Array.Sort(records, LineComparer.Instance);

        string[] expected =
        [
            "1. Apple",
            "415. Apple",
            "2. Banana is yellow",
            "32. Cherry is the best",
            "30432. Something something something"
        ];

        Assert.Equal(expected, records.Select(record => $"{record.Number}. {record.Text}"));
    }
}
