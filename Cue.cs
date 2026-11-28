namespace SrtEditor;

internal sealed record Cue(string Start = "", string End = "", string Text = "", string Settings = "")
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

internal static class SubtitleTime
{
    public static bool TryParse(string input, out long milliseconds)
    {
        var time = input.AsSpan().Trim();
        if (TryParseClock(time, out milliseconds))
            return true;

        int separator = time.IndexOfAny(',', '.');
        if (separator < 0)
            return ReadNumber(time, out long wholeSeconds) && TryTotal(wholeSeconds, 0, out milliseconds);

        var fraction = time[(separator + 1)..];
        if (fraction.Length is < 1 or > 3 || !ReadNumber(time[..separator], out long seconds) ||
            !ReadNumber(fraction, out long fractionValue))
            return false;

        long fractionalMilliseconds = fraction.Length switch { 1 => fractionValue * 100, 2 => fractionValue * 10, _ => fractionValue };
        return TryTotal(seconds, fractionalMilliseconds, out milliseconds);
    }

    public static bool TryParseClock(ReadOnlySpan<char> input, out long milliseconds)
    {
        milliseconds = 0;
        int separator = input.IndexOf(':');
        if (separator < 2 || input.Length - separator != 10 ||
            !ReadNumber(input[..separator], out long hours))
            return false;

        var tail = input[(separator + 1)..];
        if (tail[2] != ':' || tail[5] is not (',' or '.') ||
            !ReadNumber(tail[..2], out long minutes) || minutes >= 60 ||
            !ReadNumber(tail[3..5], out long seconds) || seconds >= 60 ||
            !ReadNumber(tail[6..], out long fraction) || hours > (long.MaxValue - minutes * 60 - seconds) / 3600)
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

    private static bool ReadNumber(ReadOnlySpan<char> input, out long value)
    {
        value = 0;
        foreach (char character in input)
        {
            int digit = character - '0';
            if (digit is < 0 or > 9 || value > (long.MaxValue - digit) / 10)
                return false;

            value = value * 10 + digit;
        }
        return !input.IsEmpty;
    }

    private static bool TryTotal(long seconds, long fraction, out long milliseconds)
    {
        milliseconds = 0;
        if (seconds > (long.MaxValue - fraction) / 1000)
            return false;

        milliseconds = seconds * 1000 + fraction;
        return true;
    }
}
