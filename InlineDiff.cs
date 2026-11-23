using System.Globalization;

namespace SrtEditor;

internal readonly record struct TextChange(string Text, string? Before = null)
{
    public bool Removed => Text.Length == 0 && Before is { Length: > 0 };
}

internal static class InlineDiff
{
    private const int MaxComparisonCells = 250_000;

    public static IEnumerable<TextChange> Compare(string before, string after)
    {
        if (before == after)
        {
            if (after.Length > 0)
                yield return new TextChange(after);
            yield break;
        }

        var oldText = SplitTextElements(before);
        var newText = SplitTextElements(after);
        int prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix])
            prefix++;

        int oldEnd = oldText.Length;
        int newEnd = newText.Length;
        while (oldEnd > prefix && newEnd > prefix && oldText[oldEnd - 1] == newText[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        if (prefix > 0)
            yield return new TextChange(string.Concat(newText[..prefix]));

        var oldMiddle = oldText[prefix..oldEnd];
        var newMiddle = newText[prefix..newEnd];
        if (oldMiddle.Length == 0 || newMiddle.Length == 0 ||
            (long)oldMiddle.Length * newMiddle.Length > MaxComparisonCells)
        {
            yield return new TextChange(string.Concat(newMiddle), string.Concat(oldMiddle));
        }
        else
        {
            foreach (var change in CompareMiddle(oldMiddle, newMiddle))
                yield return change;
        }

        if (newEnd < newText.Length)
            yield return new TextChange(string.Concat(newText[newEnd..]));
    }

    private static IEnumerable<TextChange> CompareMiddle(string[] before, string[] after)
    {
        var shared = new int[before.Length + 1, after.Length + 1];
        for (int oldIndex = before.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (int newIndex = after.Length - 1; newIndex >= 0; newIndex--)
            {
                shared[oldIndex, newIndex] = before[oldIndex] == after[newIndex]
                    ? 1 + shared[oldIndex + 1, newIndex + 1]
                    : Math.Max(shared[oldIndex + 1, newIndex], shared[oldIndex, newIndex + 1]);
            }
        }

        int oldPosition = 0;
        int newPosition = 0;
        while (oldPosition < before.Length || newPosition < after.Length)
        {
            int oldStart = oldPosition;
            int newStart = newPosition;
            while (Matches(before, after, oldPosition, newPosition))
            {
                oldPosition++;
                newPosition++;
            }

            if (newPosition > newStart)
            {
                yield return new TextChange(string.Concat(after[newStart..newPosition]));
                continue;
            }

            do
            {
                if (newPosition == after.Length || (oldPosition < before.Length &&
                    shared[oldPosition + 1, newPosition] >= shared[oldPosition, newPosition + 1]))
                    oldPosition++;
                else
                    newPosition++;
            }
            while ((oldPosition < before.Length || newPosition < after.Length) &&
                   !Matches(before, after, oldPosition, newPosition));

            yield return new TextChange(string.Concat(after[newStart..newPosition]),
                string.Concat(before[oldStart..oldPosition]));
        }
    }

    private static bool Matches(string[] before, string[] after, int oldPosition, int newPosition) =>
        oldPosition < before.Length && newPosition < after.Length && before[oldPosition] == after[newPosition];

    private static string[] SplitTextElements(string text)
    {
        var positions = StringInfo.ParseCombiningCharacters(text);
        var elements = new string[positions.Length];
        for (int index = 0; index < positions.Length; index++)
        {
            int end = index + 1 < positions.Length ? positions[index + 1] : text.Length;
            elements[index] = text[positions[index]..end];
        }
        return elements;
    }
}
