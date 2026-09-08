using System.Globalization;

namespace Shared;

/// <summary>
/// One line of the file in parsed form: "Number. Text", e.g. "415. Apple".
/// </summary>
public readonly struct LineRecord(long number, string text)
{
    public long Number { get; } = number;

    public string Text { get; } = text;

    public const string Separator = ". ";

    // Try-pattern, not exceptions: malformed lines are expected input that the caller skips and logs.
    public static bool TryParse(string? line, out LineRecord record)
    {
        record = default;

        if (line is null)
        {
            return false;
        }

        // First ". " only — the text part may itself contain dots, e.g. "32. Cherry is the best".
        int separatorIndex = line.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return false;
        }

        if (!long.TryParse(
                line.AsSpan(0, separatorIndex),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long number))
        {
            return false;
        }

        // An empty text part ("415. ") is treated as well-formed.
        record = new LineRecord(number, line[(separatorIndex + Separator.Length)..]);
        return true;
    }
}
