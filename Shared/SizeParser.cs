using System.Globalization;

namespace Shared;

public static class SizeParser
{
    public static bool TryParse(string? value, out long bytes)
    {
        bytes = 0;

        if (value is null)
        {
            return false;
        }

        string text = value.Trim().ToUpperInvariant();

        // KB/MB/GB are binary multiples; a bare number is bytes.
        long multiplier = text switch
        {
            _ when text.EndsWith("GB") => 1024L * 1024 * 1024,
            _ when text.EndsWith("MB") => 1024L * 1024,
            _ when text.EndsWith("KB") => 1024L,
            _ => 1L
        };

        string number = multiplier == 1 ? text : text[..^2];

        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long amount)
            || amount <= 0
            || amount > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = amount * multiplier;
        return true;
    }
}
