using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SrtEditor;

internal sealed record SrtFormat(Encoding Encoding, string NewLine)
{
    public static readonly SrtFormat Default = new(new UTF8Encoding(false, true), "\r\n");
}

internal sealed record SrtDocument(Cue[] Cues, SrtFormat Format, string Fingerprint);

internal sealed class ExternalSrtChangeException(string path, string? recoveryPath = null)
    : IOException(recoveryPath is null
        ? $"Файл «{Path.GetFileName(path)}» изменился или был удалён после открытия или сохранения."
        : $"Файл «{Path.GetFileName(path)}» изменился во время замены. Внешняя версия сохранена в «{recoveryPath}».");

internal static class SrtFile
{
    private static readonly UTF8Encoding Utf8Bom = new(true, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, true, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, true, true);
    private static readonly Regex NewLines = new(@"\r\n|\n|\r");
    private static readonly Regex Index = new(@"^\s*[0-9]+\s*$");
    private static readonly Regex Timing = new(
        @"^\s*(?<start>[^\s]+?)[ \t]*-->[ \t]*(?<end>[^\s]+)(?<settings>[ \t]+.*)?$");

    public static SrtDocument Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encoding) = Decode(bytes);
        var firstNewLine = NewLines.Match(text);
        string newLine = firstNewLine.Success ? firstNewLine.Value : SrtFormat.Default.NewLine;
        return new SrtDocument(Parse(text), new SrtFormat(encoding, newLine),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static string Render(IReadOnlyList<Cue> cues, string newLine)
    {
        var result = new StringBuilder();
        for (int index = 0; index < cues.Count; index++)
        {
            if (index > 0)
                result.Append(newLine);

            result.Append(RenderBlock(cues[index], index + 1, newLine)).Append(newLine);
        }

        return result.ToString();
    }

    public static string RenderBlock(Cue cue, int number, string newLine) =>
        string.Concat(number.ToString(CultureInfo.InvariantCulture), newLine,
            FormatTiming(cue.Start), " --> ", FormatTiming(cue.End), cue.Settings, newLine,
            NewLines.Replace(cue.Text, newLine));

    public static Cue Normalize(Cue cue) => cue with
    {
        Start = FormatTiming(cue.Start),
        End = FormatTiming(cue.End),
        Text = NewLines.Replace(cue.Text, "\r\n")
    };

    public static bool HasAmbiguousCueText(string text)
    {
        if (!text.Contains("-->", StringComparison.Ordinal))
            return false;

        var lines = NewLines.Split(text);
        for (int line = 1; line + 1 < lines.Length; line++)
        {
            if (StartsCue(lines, line))
                return true;
        }

        return false;
    }

    public static void EnsureUnchanged(string path, string fingerprint)
    {
        using var stream = OpenUnchanged(path, fingerprint);
    }

    private static FileStream OpenUnchanged(string path, string fingerprint)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (Convert.ToHexString(SHA256.HashData(stream)) != fingerprint)
                throw new ExternalSrtChangeException(path);
            return stream;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            stream?.Dispose();
            throw new ExternalSrtChangeException(path);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public static string Write(string path, IReadOnlyList<Cue> cues, SrtFormat format, string? expectedFingerprint = null)
    {
        if (cues.Any(cue => HasAmbiguousCueText(cue.Text)))
            throw new FormatException("Пустая строка перед номером и таймингом внутри текста создаёт лишний блок SRT.");

        string contents = Render(cues, format.NewLine);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        bool ownsTemporary = false;
        try
        {
            string fingerprint;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ownsTemporary = true;
                using (var writer = new StreamWriter(stream, format.Encoding, leaveOpen: true))
                    writer.Write(contents);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                fingerprint = Convert.ToHexString(SHA256.HashData(stream));
            }

            Commit(temporary, path, expectedFingerprint);
            ownsTemporary = false;
            return fingerprint;
        }
        finally
        {
            if (ownsTemporary)
                DeleteTemporary(temporary);
        }
    }

    private static void Commit(string temporary, string path, string? expectedFingerprint)
    {
        // Serialize the check and replacement between editor windows saving to the same path.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, $"Local\\SrtEditor.Save.{key}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("Файл сохраняется в другом окне. Повторите сохранение.");
        }
        catch (AbandonedMutexException)
        {
            // The previous editor stopped; this thread now owns the mutex.
        }

        try
        {
            if (expectedFingerprint is null)
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }

            using var original = OpenUnchanged(path, expectedFingerprint);
            string recovery = Path.Combine(Path.GetDirectoryName(temporary)!, $"srt-recovery-{Guid.NewGuid():N}.srt");
            try
            {
                File.Replace(temporary, path, recovery);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ExternalSrtChangeException(path, File.Exists(recovery) ? recovery : null);
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && File.Exists(recovery))
            {
                throw new IOException($"Не удалось заменить файл. Копия прежней версии находится в «{recovery}».", exception);
            }

            // A different application can atomically rename a file without sharing our mutex.
            try { EnsureUnchanged(recovery, expectedFingerprint); }
            catch (ExternalSrtChangeException) { throw new ExternalSrtChangeException(path, recovery); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Не удалось проверить заменённый файл. Копия сохранена в «{recovery}».", exception);
            }

            DeleteTemporary(recovery);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Cue[] Parse(string text)
    {
        var lines = NewLines.Split(text);
        var cues = new List<Cue>();
        int line = 0;
        while (line < lines.Length && string.IsNullOrWhiteSpace(lines[line]))
            line++;

        while (line < lines.Length)
        {
            if (!Index.IsMatch(lines[line]))
                throw new FormatException($"Строка {line + 1} — ожидается номер субтитра.");

            line++;
            if (line == lines.Length)
                throw new FormatException($"Строка {line + 1} — отсутствует тайминг субтитра.");

            if (!TryReadTiming(lines[line], out long start, out long end, out string settings))
                throw new FormatException($"Строка {line + 1} — неверный формат тайминга.");

            int textStart = ++line;
            while (line < lines.Length)
            {
                if (line > textStart && StartsCue(lines, line))
                    break;

                line++;
            }

            int textEnd = line;
            if (textEnd > textStart &&
                (line < lines.Length ? string.IsNullOrWhiteSpace(lines[textEnd - 1]) : lines[textEnd - 1].Length == 0))
                textEnd--;

            cues.Add(new Cue(SubtitleTime.Format(start), SubtitleTime.Format(end),
                string.Join("\r\n", lines, textStart, textEnd - textStart), settings));
        }

        return cues.ToArray();
    }

    private static bool StartsCue(string[] lines, int line) =>
        line + 1 < lines.Length && string.IsNullOrWhiteSpace(lines[line - 1]) && Index.IsMatch(lines[line]) &&
        TryReadTiming(lines[line + 1], out _, out _, out _);

    private static bool TryReadTiming(string line, out long start, out long end, out string settings)
    {
        start = end = 0;
        settings = "";
        var timing = Timing.Match(line);
        if (!timing.Success ||
            !SubtitleTime.TryParseClock(timing.Groups["start"].Value, out start) ||
            !SubtitleTime.TryParseClock(timing.Groups["end"].Value, out end))
            return false;

        settings = timing.Groups["settings"].Value;
        return true;
    }

    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        foreach (var encoding in new Encoding[] { Utf8Bom, Utf16Le, Utf16Be })
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
                return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding);
        }

        try
        {
            return (SrtFormat.Default.Encoding.GetString(bytes), SrtFormat.Default.Encoding);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(1251).GetString(bytes), Utf8Bom);
        }
    }

    private static string FormatTiming(string input) =>
        SubtitleTime.TryParse(input, out long time) ? SubtitleTime.Format(time) : input.Trim();
}
