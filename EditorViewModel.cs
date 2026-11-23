using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SrtEditor;

internal enum CueField { Start, End, Text }

internal abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    protected void Set<T>(ref T storage, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;

        storage = value;
        Notify(property);
    }
}

internal sealed class CueRow(EditorViewModel editor, Cue data) : ObservableModel
{
    private int number;
    private CueIssue? issue;

    public Cue Data { get; private set; } = data;

    public int Number
    {
        get => number;
        set
        {
            if (number == value)
                return;

            Set(ref number, value);
            Notify(nameof(HasPrevious));
        }
    }

    public bool HasPrevious => Number > 1;

    public string Start
    {
        get => Data.Start;
        set => editor.Edit(this, CueField.Start, value);
    }

    public string End
    {
        get => Data.End;
        set => editor.Edit(this, CueField.End, value);
    }

    public string Text
    {
        get => Data.Text;
        set => editor.Edit(this, CueField.Text, value);
    }

    public string IssueText => issue?.Message ?? "";
    public bool HasError => issue?.IsError == true;
    public bool HasWarning => issue?.IsError == false;

    public void SetIssue(CueIssue? value)
    {
        if (issue == value)
            return;

        issue = value;
        Notify(nameof(IssueText));
        Notify(nameof(HasError));
        Notify(nameof(HasWarning));
    }

    public void Replace(Cue value)
    {
        var previous = Data;
        Data = value;
        if (previous.Start != value.Start)
            Notify(nameof(Start));
        if (previous.End != value.End)
            Notify(nameof(End));
        if (previous.Text != value.Text)
            Notify(nameof(Text));
    }
}

internal sealed class EditorViewModel : ObservableModel
{
    private sealed record Snapshot(Cue[] Cues, int Selection);

    private readonly Stack<Snapshot> undo = new();
    private Cue[] saved = [];
    private SrtFormat format = SrtFormat.Default;
    private (CueRow Row, CueField Field)? editing;
    private Dictionary<int, string>? commandErrors;
    private CueRow? selected;
    private string preview = "";
    private string status = "";
    private string validation = "";
    private bool isDirty;

    public ObservableCollection<CueRow> Rows { get; } = [];
    public string? FilePath { get; private set; }
    public string FileName => FilePath is null ? "Новый файл" : Path.GetFileName(FilePath);
    public string WindowTitle => $"{FileName}{(IsDirty ? " *" : "")} — SRT Editor";
    public string FileLabel => IsDirty ? $"{FileName} *" : FileName;
    public string BlockCount => $"Блоков: {Rows.Count}";
    public bool IsEmpty => Rows.Count == 0;
    public bool HasMultipleCues => Rows.Count > 1;
    public bool HasSelection => Selected is not null;
    public bool CanUndo => undo.Count > 0;

    public CueRow? Selected
    {
        get => selected;
        set
        {
            if (selected == value)
                return;

            Set(ref selected, value);
            Notify(nameof(HasSelection));
        }
    }

    public string Preview { get => preview; private set => Set(ref preview, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Validation { get => validation; private set => Set(ref validation, value); }

    public bool IsDirty
    {
        get => isDirty;
        private set
        {
            Set(ref isDirty, value);
            Notify(nameof(WindowTitle));
            Notify(nameof(FileLabel));
        }
    }

    public void New() => Load([], null, SrtFormat.Default, "Создан новый документ.");

    public void Open(string path)
    {
        var document = SrtFile.Read(path);
        Load(document.Cues, path, document.Format, "Файл открыт.");
    }

    public bool PrepareToSave()
    {
        EndEdit();
        commandErrors = null;
        int firstError = Refresh(Capture());
        if (firstError < 0)
            return true;

        Selected = Rows[firstError];
        Status = $"Исправьте поля перед сохранением. Первый блок с ошибкой — {firstError + 1}.";
        return false;
    }

    public void Save(string path)
    {
        var cues = Capture();
        SrtFile.Write(path, cues, format);
        FilePath = path;
        saved = cues;
        IsDirty = false;
        Status = "Файл сохранён.";
    }

    public CueRow Add()
    {
        Remember();
        int index = Selected is null ? Rows.Count : Rows.IndexOf(Selected) + 1;
        var row = new CueRow(this, new Cue());
        Rows.Insert(index, row);
        Selected = row;
        Changed();
        return row;
    }

    public void Delete()
    {
        if (Selected is null)
            return;

        Remember();
        int index = Rows.IndexOf(Selected);
        Rows.RemoveAt(index);
        Selected = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        Changed();
    }

    public void Move(CueRow row, int insertionIndex)
    {
        int from = Rows.IndexOf(row);
        int to = insertionIndex > from ? insertionIndex - 1 : insertionIndex;
        if (from < 0 || from == to)
            return;

        Remember();
        Rows.Move(from, to);
        Selected = row;
        Changed();
    }

    public void Edit(CueRow row, CueField field, string value)
    {
        var previous = row.Data;
        var updated = field switch
        {
            CueField.Start => previous with { Start = value },
            CueField.End => previous with { End = value },
            _ => previous with { Text = value }
        };
        if (previous == updated)
            return;

        if (editing != (row, field))
        {
            Remember();
            editing = (row, field);
        }

        row.Replace(updated);
        Changed();
    }

    public void EndEdit() => editing = null;

    public void CopyPreviousEnd(CueRow row)
    {
        EndEdit();
        int index = Rows.IndexOf(row);
        if (index <= 0)
            return;

        Selected = row;
        if (!SubtitleTime.TryParse(Rows[index - 1].End, out long previousEnd))
        {
            Status = $"Укажите корректный конец блока {index}.";
            return;
        }

        string start = SubtitleTime.Format(previousEnd);
        if (row.Start != start)
        {
            Remember();
            row.Replace(row.Data with { Start = start });
            Changed();
        }

        Status = $"Начало блока {index + 1} взято из конца блока {index}.";
    }

    public void FillEnds()
    {
        EndEdit();
        var current = Capture();
        var (updated, errors) = CueTiming.FillEnds(current);
        if (errors.Count > 0)
        {
            commandErrors = errors;
            int first = errors.Keys.Min();
            Selected = Rows[first];
            Refresh(current);
            Status = $"Проверьте выделенные блоки, начиная с {first + 1}, и повторите заполнение.";
            return;
        }

        if (!current.SequenceEqual(updated))
        {
            Remember();
            for (int index = 0; index < Rows.Count; index++)
                Rows[index].Replace(updated[index]);
        }

        Changed();
        Status = $"Концы заполнены для блоков с 1 по {Rows.Count - 1}.";
    }

    public void Undo()
    {
        EndEdit();
        if (!undo.TryPop(out var snapshot))
            return;

        Restore(snapshot.Cues, snapshot.Selection);
        Changed();
        Status = "Действие отменено.";
        Notify(nameof(CanUndo));
    }

    private Cue[] Capture() => Rows.Select(row => row.Data).ToArray();

    private void Load(Cue[] cues, string? path, SrtFormat newFormat, string message)
    {
        EndEdit();
        undo.Clear();
        saved = cues;
        FilePath = path;
        format = newFormat;
        Restore(cues, 0);
        Changed();
        Status = message;
        Notify(nameof(CanUndo));
    }

    private void Restore(Cue[] cues, int selection)
    {
        if (Rows.Count == cues.Length)
        {
            for (int index = 0; index < cues.Length; index++)
                Rows[index].Replace(cues[index]);
        }
        else
        {
            Rows.Clear();
            foreach (var cue in cues)
                Rows.Add(new CueRow(this, cue));
        }

        Selected = selection >= 0 && selection < Rows.Count ? Rows[selection] : null;
    }

    private void Remember()
    {
        EndEdit();
        undo.Push(new Snapshot(Capture(), Selected is null ? -1 : Rows.IndexOf(Selected)));
        Notify(nameof(CanUndo));
    }

    private void Changed()
    {
        commandErrors = null;
        Status = "";
        Refresh(Capture());
    }

    private int Refresh(Cue[] cues)
    {
        var issues = CueTiming.Validate(cues);
        int firstError = -1;
        int errors = 0;
        int overlaps = 0;
        for (int index = 0; index < Rows.Count; index++)
        {
            var issue = issues[index];
            if (commandErrors?.TryGetValue(index, out string? message) == true)
                issue = new CueIssue(message, true);

            Rows[index].Number = index + 1;
            Rows[index].SetIssue(issue);
            if (issue?.IsError == true)
            {
                if (firstError < 0)
                    firstError = index;
                errors++;
            }
            if (issue?.IsError == false)
                overlaps++;
        }

        Preview = SrtFile.Render(cues, format.NewLine);
        IsDirty = !cues.SequenceEqual(saved);
        Validation = errors > 0
            ? $"Блоков с ошибками: {errors}"
            : overlaps > 0 ? $"Блоков с пересечениями: {overlaps}" : "";
        Notify(nameof(BlockCount));
        Notify(nameof(IsEmpty));
        Notify(nameof(HasMultipleCues));
        return firstError;
    }
}
