using System.Globalization;
using System.Text;

namespace SrtEditor;

internal readonly record struct RecognizedSegment(TimeSpan Start, TimeSpan End, string Text);

internal static class TranscriptionDraft
{
    private const int LineLength = 42;
    private const int DuplicateToleranceMilliseconds = 400;
    private const int ShortOverlapMilliseconds = 200;

    private readonly record struct DraftSegment(long Start, long End, string Text);

    public static Cue[] Create(IEnumerable<RecognizedSegment> segments, long durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
            return [];

        var ordered = segments.Select(segment => Normalize(segment, durationMilliseconds))
            .Where(segment => segment.HasValue).Select(segment => segment.GetValueOrDefault())
            .OrderBy(segment => segment.Start);
        var draft = new List<DraftSegment>();
        var latestByText = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in ordered)
        {
            if (latestByText.TryGetValue(segment.Text, out int index) && IsDuplicate(draft[index], segment))
            {
                var previous = draft[index];
                draft[index] = previous with { End = Math.Max(previous.End, segment.End) };
                continue;
            }

            latestByText[segment.Text] = draft.Count;
            draft.Add(segment);
        }

        var cues = new Cue[draft.Count];
        for (int index = 0; index < draft.Count; index++)
        {
            var segment = draft[index];
            long end = index + 1 < draft.Count ? TrimShortOverlap(segment, draft[index + 1]) : segment.End;
            cues[index] = new Cue(SubtitleTime.Format(segment.Start), SubtitleTime.Format(end), Wrap(segment.Text));
        }

        return cues;
    }

    private static DraftSegment? Normalize(RecognizedSegment segment, long durationMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(segment.Text) || segment.End <= TimeSpan.Zero || segment.End <= segment.Start)
            return null;

        long start = Math.Max(0, segment.Start.Ticks) / TimeSpan.TicksPerMillisecond;
        long end = Math.DivRem(segment.End.Ticks, TimeSpan.TicksPerMillisecond, out long remainder);
        end = Math.Min(durationMilliseconds, end + (remainder == 0 ? 0 : 1));
        if (start >= end)
            return null;

        // Recognition may insert line breaks. Collapsing whitespace keeps a single utterance
        // in one SRT block, even if it contains a standalone number or a timing-like phrase.
        string text = string.Join(' ', segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new DraftSegment(start, end, text);
    }

    private static bool IsDuplicate(DraftSegment previous, DraftSegment next)
    {
        long overlap = Math.Min(previous.End, next.End) - next.Start;
        long shorter = Math.Min(previous.End - previous.Start, next.End - next.Start);
        return next.Start - previous.Start <= DuplicateToleranceMilliseconds &&
               Math.Abs(next.End - previous.End) <= DuplicateToleranceMilliseconds && overlap * 5 >= shorter * 4;
    }

    private static long TrimShortOverlap(DraftSegment current, DraftSegment next)
    {
        long overlap = current.End - next.Start;
        return overlap is > 0 and <= ShortOverlapMilliseconds && next.Start > current.Start &&
               current.End <= next.End && overlap * 4 <= current.End - current.Start
            ? next.Start : current.End;
    }

    private static string Wrap(string text)
    {
        var result = new StringBuilder(text.Length);
        int lineLength = 0;
        foreach (string word in text.Split(' '))
        {
            int wordLength = new StringInfo(word).LengthInTextElements;
            if (lineLength > 0)
            {
                if (lineLength + 1 + wordLength > LineLength)
                {
                    result.Append("\r\n");
                    lineLength = 0;
                }
                else
                {
                    result.Append(' ');
                    lineLength++;
                }
            }

            result.Append(word);
            lineLength += wordLength;
        }

        return result.ToString();
    }
}
