using System.IO;

namespace SrtEditor.Tests;

internal static class EditorTests
{
    public static void Run()
    {
        Check.True(SubtitleTime.TryParse(" 3,1 ", out long time) && time == 3100, "Shorthand seconds with comma.");
        Check.True(SubtitleTime.TryParse("3.12", out time) && time == 3120, "Shorthand seconds with two decimal places.");
        Check.True(SubtitleTime.TryParseClock("123:59:58.123", out time) && time == 446398123, "Clock with extended hours.");
        Check.True(SubtitleTime.TryParseClock(SubtitleTime.Format(long.MaxValue), out time) && time == long.MaxValue,
            "Maximum representable time round-trips.");
        foreach (string invalid in new[] { "", "00:60:00,000", "1:00:00,000", "1.0000", "1.", "-1", "１", "9223372036854775808" })
            Check.True(!SubtitleTime.TryParse(invalid, out _), $"Invalid time '{invalid}' was accepted.");

        var editor = new EditorViewModel(new VersionStore(Path.Combine(AppContext.BaseDirectory, "unused")));
        var row = editor.Add();
        var initialPreview = editor.PreviewRows;
        int scheduled = 0;
        int published = 0;
        editor.EditRefreshRequested += () => scheduled++;
        editor.CuesChanged += _ => published++;
        row.Text = "H";
        row.Text = "Hi";
        row.Start = "1.25";
        row.End = "2";
        Check.True(scheduled == 1 && published == 0 && ReferenceEquals(editor.PreviewRows, initialPreview),
            "A burst of edits should schedule one refresh.");
        Check.True(editor.Preview.Contains("Hi", StringComparison.Ordinal) && editor.IsDirty, "The latest draft is already available.");

        editor.FlushPendingEdits();
        Check.True(published == 1 && editor.PreviewRows[0].Current == row.Data && !row.HasError,
            "Refresh publishes the latest combined edit and validation.");
        editor.FlushPendingEdits();
        Check.True(published == 1, "A completed refresh is not repeated.");
        editor.EndEdit();
        row.End = "0";
        editor.Undo();
        editor.FlushPendingEdits();
        Check.True(published == 2 && !row.HasError && row.End == "2", "Undo consumes a pending refresh without a duplicate update.");

        NewTranscriptionIsAnUnsavedDraft();
    }

    private static void NewTranscriptionIsAnUnsavedDraft()
    {
        var editor = new EditorViewModel(new VersionStore(Path.Combine(AppContext.BaseDirectory, "unused")));
        Cue[] cues = [new("00:00:01,250", "00:00:02,000", "Привет.")];
        editor.NewTranscription(cues);
        Check.True(editor.FilePath is null && editor.IsDirty && editor.CanUndo && editor.Rows[0].Data == cues[0] &&
                   editor.Preview.Contains(cues[0].Text, StringComparison.Ordinal),
            "Recognized cues appear in an unsaved draft ready to edit.");

        editor.Undo();
        Check.True(editor.Rows.Count == 0 && !editor.IsDirty,
            "One undo returns the transcription draft to an empty document.");
    }
}
