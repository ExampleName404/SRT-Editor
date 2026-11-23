using System.Text.RegularExpressions;

namespace SrtEditor;

internal sealed class SubtitlePlaybackTrack
{
    private sealed record Interval(long Start, long End, string Text);

    private static readonly Regex FormattingTags = new(@"</?(?:b|i|u|s|font)(?=[\s/>])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Interval[] intervals;
    private readonly long[] boundaries;
    private int previousSegment = int.MinValue;
    private string previousText = "";

    public SubtitlePlaybackTrack(IEnumerable<Cue> cues)
    {
        var validIntervals = new List<Interval>();
        foreach (var cue in cues)
        {
            if (!SubtitleTime.TryParse(cue.Start, out long start) ||
                !SubtitleTime.TryParse(cue.End, out long end) || end <= start)
                continue;

            string text = FormattingTags.Replace(cue.Text, "");
            if (!string.IsNullOrWhiteSpace(text))
                validIntervals.Add(new Interval(start, end, text));
        }

        intervals = validIntervals.ToArray();
        boundaries = intervals.SelectMany(interval => new[] { interval.Start, interval.End })
            .Distinct().Order().ToArray();
    }

    public string At(long position)
    {
        int segment = Array.BinarySearch(boundaries, position);
        if (segment < 0)
            segment = ~segment - 1;
        if (segment == previousSegment)
            return previousText;

        previousSegment = segment;
        previousText = string.Join("\n\n", intervals
            .Where(interval => interval.Start <= position && position < interval.End)
            .Select(interval => interval.Text));
        return previousText;
    }
}
