namespace SrtEditor;

internal sealed record CueIssue(string Message, bool IsError);

internal static class CueTiming
{
    public static CueIssue?[] Validate(IReadOnlyList<Cue> cues)
    {
        var issues = new CueIssue?[cues.Count];
        var intervals = new List<(int Index, long Start, long End)>();
        for (int index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var errors = new List<string>();
            string? startError = ReadTime(cue.Start, "начало", out long start);
            string? endError = ReadTime(cue.End, "конец", out long end);
            if (startError is not null)
                errors.Add(startError);
            if (endError is not null)
                errors.Add(endError);
            if (startError is null && endError is null)
            {
                if (end <= start)
                    errors.Add("Конец должен быть позже начала");
                else
                    intervals.Add((index, start, end));
            }

            if (string.IsNullOrWhiteSpace(cue.Text))
                errors.Add("Введите текст");
            if (errors.Count > 0)
                issues[index] = new CueIssue(string.Join(". ", errors) + ".", true);
        }

        int longestIndex = -1;
        long longestEnd = 0;
        var overlap = new CueIssue("Тайминг пересекается с другим блоком.", false);
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (longestIndex >= 0 && interval.Start < longestEnd)
            {
                issues[longestIndex] ??= overlap;
                issues[interval.Index] ??= overlap;
            }

            if (longestIndex < 0 || interval.End > longestEnd)
            {
                longestIndex = interval.Index;
                longestEnd = interval.End;
            }
        }

        return issues;
    }

    public static (Cue[] Cues, Dictionary<int, string> Errors) FillEnds(IReadOnlyList<Cue> cues)
    {
        var errors = new Dictionary<int, string>();
        var starts = new long?[cues.Count];
        for (int index = 0; index < cues.Count; index++)
        {
            string? error = ReadTime(cues[index].Start, "начало", out long start);
            if (error is null)
                starts[index] = start;
            else
                errors.Add(index, error + ".");
        }

        var updated = cues.ToArray();
        for (int index = 0; index < cues.Count - 1; index++)
        {
            if (starts[index] is not long start || starts[index + 1] is not long nextStart)
                continue;

            if (nextStart <= start)
                errors.Add(index, $"Начало блока {index + 2} должно быть позже начала этого блока.");
            else
                updated[index] = updated[index] with { End = SubtitleTime.Format(nextStart) };
        }

        return (errors.Count == 0 ? updated : cues.ToArray(), errors);
    }

    private static string? ReadTime(string value, string field, out long time)
    {
        if (SubtitleTime.TryParse(value, out time))
            return null;

        return string.IsNullOrWhiteSpace(value)
            ? $"Укажите {field}"
            : $"Проверьте формат поля «{field}»";
    }
}
