using System.IO;
using System.Text;
using System.Text.Json;

namespace SrtEditor.Tests;

internal static class SrtDataTests
{
    public static void Run()
    {
        var folder = Directory.CreateTempSubdirectory("srt-data-");
        try
        {
            CheckRoundTrip(folder.FullName);
            CheckExternalChanges(folder.FullName);
            CheckCompetingSaves(folder.FullName);
            CheckLatestVersion(folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void CheckRoundTrip(string directory)
    {
        string path = Path.Combine(directory, "roundtrip.srt");
        var cue = new Cue("1", "2", "Стрелки\r\n\r\n2026\r\nA --> B");
        var second = new Cue("3", "5", "Следующая реплика");
        SrtFile.Write(path, [cue, second], SrtFormat.Default);
        var opened = SrtFile.Read(path);
        Check.True(opened.Cues.Length == 2 && opened.Cues[0].Text == cue.Text && opened.Cues[1].Text == second.Text,
            "A textual arrow after a number must stay inside its cue; the real next cue remains separate.");

        var editor = new EditorViewModel(new VersionStore(Path.Combine(directory, "roundtrip-history")));
        editor.Open(path);
        var row = editor.Rows[0];
        row.Text = "Двусмысленный текст\r\n\r\n2026\r\n00:00:10,000 --> 00:00:12,000\r\nПродолжение";
        Check.True(!editor.PrepareToSave() && row.IsErrorForSave(),
            "An embedded real timing header is rejected and the affected cue is marked.");
        try
        {
            SrtFile.Write(path, [row.Data], SrtFormat.Default);
            throw new InvalidOperationException("The low-level writer accepted ambiguous cue text.");
        }
        catch (FormatException)
        {
        }

        Check.True(SrtFile.Read(path).Fingerprint == opened.Fingerprint, "A rejected write keeps the original SRT intact.");
    }

    private static void CheckExternalChanges(string directory)
    {
        string path = Path.Combine(directory, "external.srt");
        var initial = new Cue("1", "2", "Исходный текст");
        SrtFile.Write(path, [initial], SrtFormat.Default);
        var editor = new EditorViewModel(new VersionStore(Path.Combine(directory, "external-history")));
        editor.Open(path);
        editor.Rows[0].Text = "Первая правка";
        editor.Save(path);
        editor.Rows[0].Text = "Вторая правка";
        editor.Save(path);
        Check.True(SrtFile.Read(path).Cues[0].Text == "Вторая правка", "Saving an unchanged source refreshes the fingerprint.");

        string draft = "Локальный черновик";
        editor.Rows[0].Text = draft;
        SrtFile.Write(path, [initial with { Text = "Изменено снаружи" }], SrtFormat.Default);
        AssertConflict(() => editor.Save(path));
        Check.True(SrtFile.Read(path).Cues[0].Text == "Изменено снаружи", "A valid external edit survives a conflicting save.");

        byte[] corrupted = [0xEF, 0xBB, 0xBF, 0xFF];
        File.WriteAllBytes(path, corrupted);
        AssertConflict(() => editor.Save(path));
        Check.True(File.ReadAllBytes(path).SequenceEqual(corrupted), "Original undecodable bytes survive a conflicting save.");
        File.Delete(path);
        AssertConflict(() => editor.Save(path));
        Check.True(!File.Exists(path) && editor.IsDirty && editor.Rows[0].Text == draft,
            "An externally deleted file stays deleted and the unsaved draft remains available.");

        string copy = Path.Combine(directory, "external-copy.srt");
        editor.Save(copy);
        Check.True(SrtFile.Read(copy).Cues[0].Text == draft && !editor.IsDirty, "Save As can preserve the draft at a new path.");

        var baseVersion = SrtFile.Read(copy);
        File.AppendAllText(copy, "\r\n", Encoding.UTF8);
        AssertConflict(() => SrtFile.Write(copy, baseVersion.Cues, SrtFormat.Default, baseVersion.Fingerprint));
        Check.True(SrtFile.Read(copy).Fingerprint != baseVersion.Fingerprint, "The writer itself checks changed raw bytes.");
    }

    private static void CheckCompetingSaves(string directory)
    {
        string path = Path.Combine(directory, "competing.srt");
        var cue = new Cue("1", "2", "Первая версия");
        string fingerprint = SrtFile.Write(path, [cue], SrtFormat.Default);
        using var start = new Barrier(2);
        var first = Task.Run(() => TrySave("Первая правка"));
        var second = Task.Run(() => TrySave("Вторая правка"));
        Task.WaitAll(first, second);
        Check.True(first.Result != second.Result, "Two guarded saves from the same original can commit only one edit.");
        Check.True(SrtFile.Read(path).Cues[0].Text == (first.Result ? "Первая правка" : "Вторая правка"),
            "The successful writer's text remains intact.");
        Check.True(!Directory.EnumerateFiles(directory, "srt-recovery-*.srt").Any(),
            "A successful guarded save removes its temporary recovery copy.");

        bool TrySave(string text)
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The concurrent test did not reach the write barrier.");

            try
            {
                SrtFile.Write(path, [cue with { Text = text }], SrtFormat.Default, fingerprint);
                return true;
            }
            catch (ExternalSrtChangeException)
            {
                return false;
            }
        }
    }

    private static void CheckLatestVersion(string directory)
    {
        var store = new VersionStore(Path.Combine(directory, "versions"));
        string path = Path.Combine(directory, "history.srt");
        var cue = new Cue("1", "2", "История");
        var older = store.Record(path, [cue], null);
        var newer = store.Record(path, [cue with { Text = "Новый снимок" }], older);
        string olderPath = Directory.EnumerateFiles(store.DirectoryPath, $"*-{older.Id:N}.srt-version.json").Single();
        string newerPath = Directory.EnumerateFiles(store.DirectoryPath, $"*-{newer.Id:N}.srt-version.json").Single();
        File.WriteAllText(olderPath, JsonSerializer.Serialize(new
        {
            older.Cues,
            older.Id,
            RecordedAt = newer.RecordedAt.AddDays(-1)
        }));
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddDays(1));
        Check.True(store.ReadLatest(path)?.Id == newer.Id && store.Read(path).Count == 2,
            "The latest snapshot follows the stored timestamp, independent of directory timestamps and legacy property ordering.");

        var serialized = File.ReadAllText(newerPath);
        File.WriteAllText(newerPath, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Check.True(store.ReadLatest(path)?.Id == newer.Id, "A legacy UTF-8 BOM does not hide a version.");
        File.WriteAllText(newerPath, serialized[..^3]);
        Check.True(store.ReadLatest(path)?.Id == older.Id && store.Read(path).Count == 1,
            "A corrupt latest snapshot falls back to the most recent usable snapshot.");
    }

    private static void AssertConflict(Action save)
    {
        try
        {
            save();
            throw new InvalidOperationException("A conflicting save succeeded.");
        }
        catch (ExternalSrtChangeException)
        {
        }
    }

    private static bool IsErrorForSave(this CueRow row) =>
        row.HasError && row.IssueText.Contains("пустую строку", StringComparison.Ordinal);
}
