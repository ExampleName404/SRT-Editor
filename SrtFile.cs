using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SrtEditor;

internal sealed record SrtFormat(Encoding Encoding, string NewLine)
{
    public static readonly SrtFormat Default = new(new UTF8Encoding(false, true), "\r\n");
}

internal sealed record SrtDocument(Cue[] Cues, SrtFormat Format);

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
        return new SrtDocument(Parse(text), new SrtFormat(encoding, newLine));
    }

    public static string Render(IReadOnlyList<Cue> cues, string newLine)
    {
        var result = new StringBuilder();
        for (int index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (index > 0)
                result.Append(newLine);

            result.Append(index + 1).Append(newLine)
                .Append(FormatTiming(cue.Start)).Append(" --> ").Append(FormatTiming(cue.End))
                .Append(cue.Settings).Append(newLine)
                .Append(NewLines.Replace(cue.Text, newLine)).Append(newLine);
        }

        return result.ToString();
    }

    public static void Write(string path, IReadOnlyList<Cue> cues, SrtFormat format)
    {
        string contents = Render(cues, format.NewLine);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                using (var writer = new StreamWriter(stream, format.Encoding, leaveOpen: true))
                    writer.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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

            var timing = Timing.Match(lines[line]);
            if (!timing.Success ||
                !SubtitleTime.TryParseClock(timing.Groups["start"].Value, out long start) ||
                !SubtitleTime.TryParseClock(timing.Groups["end"].Value, out long end))
            {
                throw new FormatException($"Строка {line + 1} — неверный формат тайминга.");
            }

            int textStart = ++line;
            while (line < lines.Length)
            {
                bool afterSeparator = line > textStart && string.IsNullOrWhiteSpace(lines[line - 1]);
                bool followedByTiming = line + 1 < lines.Length &&
                    lines[line + 1].Contains("-->", StringComparison.Ordinal);
                if (afterSeparator && followedByTiming && Index.IsMatch(lines[line]))
                    break;

                line++;
            }

            int textEnd = line;
            if (textEnd > textStart &&
                (line < lines.Length ? string.IsNullOrWhiteSpace(lines[textEnd - 1]) : lines[textEnd - 1].Length == 0))
                textEnd--;

            cues.Add(new Cue(SubtitleTime.Format(start), SubtitleTime.Format(end),
                string.Join("\r\n", lines, textStart, textEnd - textStart), timing.Groups["settings"].Value));
        }

        return cues.ToArray();
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
