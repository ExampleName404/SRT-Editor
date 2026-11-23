using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SrtEditor;

internal static class InlinePreview
{
    private enum Mark { None, Changed, Removed, Moved }

    private static readonly Brush ChangeBackground = MakeBrush(42, 69, 65);
    private static readonly Brush ChangeText = MakeBrush(205, 245, 231);
    private static readonly Brush RemovalBackground = MakeBrush(75, 43, 48);
    private static readonly Brush RemovalText = MakeBrush(247, 181, 185);
    private static readonly Brush MoveBackground = MakeBrush(66, 52, 32);
    private static readonly Brush MoveText = MakeBrush(245, 202, 132);

    public static void Update(RichTextBox preview, IReadOnlyList<PreviewCue> rows)
    {
        preview.BeginChange();
        try
        {
            var blocks = preview.Document.Blocks;
            Block? existing = blocks.FirstBlock;
            foreach (var row in rows)
            {
                if (existing is null)
                {
                    blocks.Add(Render(row));
                    continue;
                }

                var next = existing.NextBlock;
                if (!Equals(existing.Tag, row))
                {
                    blocks.InsertBefore(existing, Render(row));
                    blocks.Remove(existing);
                }

                existing = next;
            }

            while (existing is not null)
            {
                var next = existing.NextBlock;
                blocks.Remove(existing);
                existing = next;
            }
        }
        finally
        {
            preview.EndChange();
        }
    }

    private static Paragraph Render(PreviewCue row)
    {
        var paragraph = new Paragraph { Tag = row, Margin = new Thickness(0) };
        if (row.Current is not { } current)
        {
            Append(paragraph, SrtFile.RenderBlock(row.Previous!, row.PreviousNumber, "\r\n"),
                Mark.Removed, "Удалённый блок");
        }
        else if (row.Previous is not { } previous)
        {
            Append(paragraph, SrtFile.RenderBlock(current, row.Number, "\r\n"), Mark.Changed, "Добавленный блок");
        }
        else if (row.Edited || row.Moved)
        {
            string number = row.Number.ToString(CultureInfo.InvariantCulture);
            Append(paragraph, number + "\r\n", row.Moved ? Mark.Moved : Mark.None,
                row.Moved ? $"Предыдущий номер: {row.PreviousNumber}" : null);

            var before = SrtFile.Normalize(previous);
            var after = SrtFile.Normalize(current);
            AppendDiff(paragraph, before.Start, after.Start);
            Append(paragraph, " --> ");
            AppendDiff(paragraph, before.End, after.End);
            AppendDiff(paragraph, before.Settings, after.Settings);
            paragraph.Inlines.Add(new LineBreak());
            AppendDiff(paragraph, before.Text, after.Text);
        }
        else
        {
            Append(paragraph, SrtFile.RenderBlock(current, row.Number, "\r\n"));
        }

        paragraph.Inlines.Add(new LineBreak());
        return paragraph;
    }

    private static void AppendDiff(Paragraph paragraph, string before, string after)
    {
        foreach (var change in InlineDiff.Compare(before, after))
        {
            if (change.Before is null)
            {
                Append(paragraph, change.Text);
            }
            else if (change.Removed)
            {
                Append(paragraph, change.Before, Mark.Removed, "Удалённый фрагмент", markLineBreaks: true);
            }
            else
            {
                string hint = change.Before.Length > 0
                    ? $"Было: {change.Before.Replace("\r\n", " ↵ ")}"
                    : "Добавленный фрагмент";
                Append(paragraph, change.Text, Mark.Changed, hint, markLineBreaks: true);
            }
        }
    }

    private static void Append(Paragraph paragraph, string text, Mark mark = Mark.None,
        string? hint = null, bool markLineBreaks = false)
    {
        var lines = text.Split("\r\n");
        for (int index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                if (markLineBreaks)
                    AddRun(paragraph, "↵", mark, hint);
                if (!markLineBreaks || mark != Mark.Removed)
                    paragraph.Inlines.Add(new LineBreak());
            }

            if (lines[index].Length > 0)
                AddRun(paragraph, lines[index], mark, hint);
        }
    }

    private static void AddRun(Paragraph paragraph, string text, Mark mark, string? hint)
    {
        var run = new Run(text) { ToolTip = hint };
        switch (mark)
        {
            case Mark.Changed:
                run.Background = ChangeBackground;
                run.Foreground = ChangeText;
                break;
            case Mark.Removed:
                run.Background = RemovalBackground;
                run.Foreground = RemovalText;
                run.TextDecorations = TextDecorations.Strikethrough;
                break;
            case Mark.Moved:
                run.Background = MoveBackground;
                run.Foreground = MoveText;
                break;
        }

        paragraph.Inlines.Add(run);
    }

    private static Brush MakeBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
