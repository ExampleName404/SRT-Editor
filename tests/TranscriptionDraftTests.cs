using System.Globalization;

namespace SrtEditor.Tests;

internal static class TranscriptionDraftTests
{
    public static void Run()
    {
        CheckDraftRanges();
        CheckBoundaryOverlap();
        CheckTextWrapping();
    }

    private static void CheckDraftRanges()
    {
        var draft = TranscriptionDraft.Create([
            Segment(1500.125, 2400, "こんにちは 👋"),
            Segment(30, 80, " \t\n "),
            Segment(-120, 129.5, "  Начало\t записи! "),
            Segment(900, 901, "Короткая реплика"),
            Segment(-300, -1, "До записи"),
            Segment(2000, 2100, "За концом записи"),
            Segment(1200, 1199, "Перепутанные границы"),
            Segment(1300, 1300, "Нулевая длительность")
        ], 2000);

        Check.True(draft.Length == 3, "The draft drops empty and out-of-range recognition segments.");
        Check.True(draft[0].Start == "00:00:00,000" && draft[0].End == "00:00:00,130" &&
                   draft[0].Text == "Начало записи!", "The first cue clamps to zero and preserves normalized speech.");
        Check.True(draft[1].Start == "00:00:00,900" && draft[1].End == "00:00:00,901",
            "The draft keeps short positive intervals.");
        Check.True(draft[2].Start == "00:00:01,500" && draft[2].End == "00:00:02,000" &&
                   draft[2].Text == "こんにちは 👋", "The last cue clamps to media duration and preserves Unicode.");
        Check.True(CueTiming.Validate(draft).All(issue => issue?.IsError != true),
            "Every generated cue is editable and has a valid SRT interval.");
        Check.True(TranscriptionDraft.Create([Segment(0, 100, "Текст")], 0).Length == 0,
            "An empty recording cannot contain a positive cue interval.");
    }

    private static void CheckBoundaryOverlap()
    {
        var draft = TranscriptionDraft.Create([
            Segment(100, 1000, "Да, понял"),
            Segment(140, 1030, "Да,  понял"),
            Segment(970, 1700, "Следующая фраза"),
            Segment(1750, 2050, "Да, понял"),
            Segment(1980, 2000, "Вложенная фраза"),
            Segment(2020, 3000, "Отдельная фраза")
        ], 3000);

        Check.True(draft.Length == 5 && draft.Count(cue => cue.Text == "Да, понял") == 2,
            "Only a near-coincident exact repeat is deduplicated.");
        Check.True(draft[0].End == draft[1].Start && draft[1].Start == "00:00:00,970",
            "A short tail overlap is trimmed without shifting the following cue.");
        Check.True(draft[2].End == "00:00:02,050" && draft[3].Text == "Вложенная фраза" &&
                   draft[4].Text == "Отдельная фраза", "Distinct nested and overlapping speech remains in the draft.");
    }

    private static void CheckTextWrapping()
    {
        string word = "сло́во👨‍👩‍👧‍👦";
        string speech = string.Join(' ', Enumerable.Repeat(word, 15));
        var draft = TranscriptionDraft.Create([
            Segment(0, 1200, speech),
            Segment(1200, 2500, "Число\n\n2026\n00:00:01,000 --> 00:00:02,000")
        ], 3000);

        var lines = draft[0].Text.Split("\r\n");
        Check.True(lines.Length > 1 && lines.All(line => new StringInfo(line).LengthInTextElements <= 42) &&
                   string.Join(' ', lines) == speech, "Word wrapping keeps grapheme clusters and uses readable lines.");
        Check.True(draft.Length == 2 && draft[0].End == "00:00:01,200" &&
                   !SrtFile.HasAmbiguousCueText(draft[1].Text), "Wrapping preserves timestamps and cannot create a phantom SRT block.");
    }

    private static RecognizedSegment Segment(double start, double end, string text) =>
        new(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), text);
}
