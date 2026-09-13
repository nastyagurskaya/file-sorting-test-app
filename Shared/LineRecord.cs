using System.Globalization;

namespace Shared;

/// <summary>
/// One line of the file in parsed form: "Number. Text", e.g. "415. Apple".
/// </summary>
public readonly struct LineRecord(int number, string text)
{
    public const string Separator = ". ";

    public int Number { get; } = number;

    public string Text { get; } = text;

    public static bool TryParse(string? line, out LineRecord record)
    {
        record = default;

        if (line is null)
        {
            return false;
        }

        // First ". " only — the text part may itself contain dots, e.g. "32. Cherry is the best".
        var separatorIndex = line.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return false;
        }

        if (!int.TryParse(
                line.AsSpan(0, separatorIndex),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }

        record = new LineRecord(number, line[(separatorIndex + Separator.Length)..]);
        return true;
    }

    public override string ToString() => $"{Number}{Separator}{Text}";
}
