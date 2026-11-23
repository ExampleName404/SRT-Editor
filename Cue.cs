using System.Globalization;
using System.Text.RegularExpressions;

namespace SrtEditor;

internal sealed record Cue(string Start = "", string End = "", string Text = "", string Settings = "")
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

internal static class SubtitleTime
{
    private static readonly Regex Clock = new(
        @"^(?<hours>[0-9]{2,}):(?<minutes>[0-5][0-9]):(?<seconds>[0-5][0-9])[,.](?<fraction>[0-9]{3})$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Seconds = new(
        @"^(?<seconds>[0-9]+)(?:[,.](?<fraction>[0-9]{1,3}))?$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string input, out long milliseconds)
    {
        input = input.Trim();
        if (TryParseClock(input, out milliseconds))
            return true;

        var match = Seconds.Match(input);
        if (!ReadNumber(match.Groups["seconds"].Value, out long seconds))
            return false;

        var fraction = match.Groups["fraction"].Value.PadRight(3, '0');
        return TryTotal(seconds, long.Parse(fraction, CultureInfo.InvariantCulture), out milliseconds);
    }

    public static bool TryParseClock(string input, out long milliseconds)
    {
        milliseconds = 0;
        var match = Clock.Match(input);
        if (!ReadNumber(match.Groups["hours"].Value, out long hours))
            return false;

        long minutes = long.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        long seconds = long.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        long fraction = long.Parse(match.Groups["fraction"].Value, CultureInfo.InvariantCulture);
        if (hours > (long.MaxValue - minutes * 60 - seconds) / 3600)
            return false;

        return TryTotal(hours * 3600 + minutes * 60 + seconds, fraction, out milliseconds);
    }

    public static string Format(long milliseconds)
    {
        long hours = milliseconds / 3_600_000;
        long minutes = milliseconds / 60_000 % 60;
        long seconds = milliseconds / 1000 % 60;
        long fraction = milliseconds % 1000;
        return FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00},{fraction:000}");
    }

    private static bool ReadNumber(string input, out long value) =>
        long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryTotal(long seconds, long fraction, out long milliseconds)
    {
        milliseconds = 0;
        if (seconds > (long.MaxValue - fraction) / 1000)
            return false;

        milliseconds = seconds * 1000 + fraction;
        return true;
    }
}
