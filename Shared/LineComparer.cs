namespace Shared;

/// <summary>
/// Sort order for <see cref="LineRecord"/>: text ascending, then number ascending on ties.
/// </summary>
public sealed class LineComparer : IComparer<LineRecord>
{
    public static readonly LineComparer Instance = new();

    public int Compare(LineRecord x, LineRecord y)
    {
        int byText = StringComparer.Ordinal.Compare(x.Text, y.Text);
        return byText != 0 ? byText : x.Number.CompareTo(y.Number);
    }
}
