namespace SrtEditor;

internal sealed record PreviewCue(Cue? Current, int Number, Cue? Previous, int PreviousNumber, bool Moved)
{
    public Guid Id => (Current ?? Previous)!.Id;
    public bool Added => Previous is null;
    public bool Removed => Current is null;
    public bool Edited => Current is not null && Previous is not null && !CueComparison.SameContent(Current, Previous);
    public bool Changed => Added || Removed || Edited || Moved;
    public string RestoreLabel => Added ? "Убрать добавленный блок" : "Вернуть блок из версии";
}

internal static class CueComparison
{
    public static PreviewCue[] Compare(IReadOnlyList<Cue> reference, IReadOnlyList<Cue> current)
    {
        var oldById = reference.Select((cue, index) => (cue, index))
            .ToDictionary(entry => entry.cue.Id);
        var currentIds = current.Select(cue => cue.Id).ToHashSet();
        var relativeOrder = current.Where(cue => oldById.ContainsKey(cue.Id))
            .Select(cue => oldById[cue.Id].index).ToArray();
        var unchangedOrder = FindOrderedIndices(relativeOrder);

        var removedBefore = new Dictionary<Guid, Stack<PreviewCue>>();
        Guid anchor = Guid.Empty;
        for (int index = reference.Count - 1; index >= 0; index--)
        {
            var cue = reference[index];
            if (currentIds.Contains(cue.Id))
            {
                anchor = cue.Id;
                continue;
            }

            if (!removedBefore.TryGetValue(anchor, out var removed))
                removedBefore[anchor] = removed = new Stack<PreviewCue>();
            removed.Push(new PreviewCue(null, 0, cue, index + 1, false));
        }

        var result = new List<PreviewCue>();
        for (int index = 0; index < current.Count; index++)
        {
            var cue = current[index];
            if (removedBefore.TryGetValue(cue.Id, out var removed))
                result.AddRange(removed);

            if (oldById.TryGetValue(cue.Id, out var previous))
                result.Add(new PreviewCue(cue, index + 1, previous.cue, previous.index + 1,
                    !unchangedOrder.Contains(previous.index)));
            else
                result.Add(new PreviewCue(cue, index + 1, null, 0, false));
        }

        if (removedBefore.TryGetValue(Guid.Empty, out var trailing))
            result.AddRange(trailing);
        return result.ToArray();
    }

    public static bool SameContent(Cue first, Cue second) =>
        first == second || ContentKey(first) == ContentKey(second);

    public static bool HasSameContent(IReadOnlyList<Cue> first, IReadOnlyList<Cue> second) =>
        first.Count == second.Count && first.Zip(second, SameContent).All(equal => equal);

    public static Cue[] ReuseIds(Cue[] cues, IReadOnlyList<Cue> reference)
    {
        var exact = reference.GroupBy(ContentKey)
            .ToDictionary(group => group.Key, group => new Queue<Guid>(group.Select(cue => cue.Id)));
        var matched = new HashSet<Guid>();
        var result = cues.ToArray();
        var pending = new List<int>();
        for (int index = 0; index < result.Length; index++)
        {
            if (exact.TryGetValue(ContentKey(result[index]), out var ids) && ids.TryDequeue(out var id))
            {
                result[index] = result[index] with { Id = id };
                matched.Add(id);
            }
            else
            {
                pending.Add(index);
            }
        }

        MatchUnique(result, pending, reference, matched, cue => cue.Text);
        MatchUnique(result, pending, reference, matched, cue => (cue.Start, cue.End));
        foreach (int index in pending)
        {
            if (matched.Contains(result[index].Id))
                result[index] = result[index] with { Id = Guid.NewGuid() };
        }

        return result;
    }

    public static int RestorePosition(IReadOnlyList<Cue> reference, Guid id, IReadOnlyList<Cue> current)
    {
        var anchors = Compare(reference, current).Where(row => !row.Added && !row.Removed && !row.Moved)
            .ToDictionary(row => row.Id, row => row.Number - 1);
        int position = 0;
        while (position < reference.Count && reference[position].Id != id)
            position++;
        for (int next = position + 1; next < reference.Count; next++)
        {
            if (anchors.TryGetValue(reference[next].Id, out int index))
                return index;
        }
        for (int previous = position - 1; previous >= 0; previous--)
        {
            if (anchors.TryGetValue(reference[previous].Id, out int index))
                return index + 1;
        }

        return Math.Min(position, current.Count);
    }

    private static Cue ContentKey(Cue cue) => SrtFile.Normalize(cue) with { Id = Guid.Empty };

    private static void MatchUnique<TKey>(Cue[] cues, List<int> pending, IReadOnlyList<Cue> reference,
        HashSet<Guid> matched, Func<Cue, TKey> key) where TKey : notnull
    {
        if (pending.Count == 0)
            return;

        var uniquePrevious = reference.Where(cue => !matched.Contains(cue.Id)).GroupBy(key)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Id);
        var resolved = new HashSet<int>();
        foreach (var group in pending.GroupBy(index => key(cues[index])))
        {
            if (group.Count() != 1 || !uniquePrevious.TryGetValue(group.Key, out var id))
                continue;

            int index = group.Single();
            cues[index] = cues[index] with { Id = id };
            matched.Add(id);
            resolved.Add(index);
        }
        pending.RemoveAll(resolved.Contains);
    }

    // A longest increasing subsequence leaves unchanged neighbors unmarked when a cue moves.
    private static HashSet<int> FindOrderedIndices(int[] order)
    {
        var tails = new int[order.Length];
        var predecessor = new int[order.Length];
        int length = 0;
        for (int index = 0; index < order.Length; index++)
        {
            int low = 0;
            int high = length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (order[tails[middle]] < order[index])
                    low = middle + 1;
                else
                    high = middle;
            }

            predecessor[index] = low == 0 ? -1 : tails[low - 1];
            tails[low] = index;
            if (low == length)
                length++;
        }

        var result = new HashSet<int>();
        for (int index = length == 0 ? -1 : tails[length - 1]; index >= 0; index = predecessor[index])
            result.Add(order[index]);
        return result;
    }
}
