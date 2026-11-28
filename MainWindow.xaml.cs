using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SrtEditor;

public partial class MainWindow : Window
{
    private const string FileFilter = "Субтитры SRT (*.srt)|*.srt|Все файлы (*.*)|*.*";
    private const string AudioFilter = "Аудиофайлы|*.wav;*.mp3;*.ogg;*.oga;*.opus;*.m4a;*.aac;*.flac;*.wma|Все файлы (*.*)|*.*";
    private const double DefaultAudioPanelHeight = 193;
    private const double MinimumAudioPanelHeight = 164;
    private readonly EditorViewModel editor = new(
        new VersionStore(Path.Combine(AppContext.BaseDirectory, "cache", "versions")))
    {
        SplitTimeInput = EditorPreferences.ReadSplitTimeInput()
    };
    private readonly DispatcherTimer editRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(80)
    };
    private Point dragOrigin;
    private CueRow? draggedCue;
    private ListBoxItem? dropMarker;
    private Guid? diffRestoreTarget;
    private GridLength? previousAudioHeight;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = editor;
        editor.PropertyChanged += Editor_PropertyChanged;
        editor.CuesChanged += SoundPanel.UpdateCues;
        editor.EditRefreshRequested += editRefreshTimer.Start;
        editRefreshTimer.Tick += (_, _) => RefreshPendingEdits();
        SoundPanel.OpenAudioRequested += OpenAudio;
        SoundPanel.TranscriptionRequested += TranscribeAudio;
        editor.New();
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewDocument();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenDocument();
    private void OpenAudio()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть аудиофайл",
            Filter = AudioFilter,
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(editor.FilePath) ?? ""
        };
        if (dialog.ShowDialog(this) == true)
            SoundPanel.Open(dialog.FileName, editor.Rows.Select(row => row.Data));
    }

    private void TranscribeAudio(string audioPath)
    {
        var dialog = new TranscriptionWindow(audioPath) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Result is not { Length: > 0 } cues)
            return;

        var resultWindow = new MainWindow();
        resultWindow.editor.NewTranscription(cues);
        resultWindow.Show();
        resultWindow.SetAudioPanelOpen(true);
        resultWindow.SoundPanel.Open(audioPath, cues);
    }

    private void ToggleAudioPanel_Click(object sender, RoutedEventArgs e) =>
        SetAudioPanelOpen(SoundPanel.Visibility == Visibility.Collapsed);

    private void SetAudioPanelOpen(bool open)
    {
        if (open)
        {
            if (previousAudioHeight is null)
            {
                double editorHeight = Math.Max(EditorRow.MinHeight, EditorRow.ActualHeight - DefaultAudioPanelHeight);
                EditorRow.Height = new GridLength(editorHeight, GridUnitType.Star);
            }

            AudioRow.Height = previousAudioHeight ?? new GridLength(DefaultAudioPanelHeight, GridUnitType.Star);
            AudioRow.MinHeight = MinimumAudioPanelHeight;
        }
        else
        {
            previousAudioHeight = AudioRow.Height;
            AudioRow.MinHeight = 0;
            AudioRow.Height = new GridLength(0);
        }

        SoundPanel.Visibility = AudioSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AudioDisclosure.Content = open ? "Скрыть аудио" : "Показать аудио";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveDocument();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveDocument(saveAs: true);
    private void Undo_Click(object sender, RoutedEventArgs e) => editor.Undo();
    private void Add_Click(object sender, RoutedEventArgs e) => AddCue();
    private void Delete_Click(object sender, RoutedEventArgs e) => editor.Delete();
    private void LatestComparison_Click(object sender, RoutedEventArgs e) => editor.UseComparisonVersion(null);

    private void Versions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new VersionHistoryWindow(editor.FileName, editor.ReadVersions()) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedVersion is not { } version)
                return;

            if (dialog.RestoreSelected)
            {
                editor.RestoreVersion(version);
                ScrollToSelected();
            }
            else
            {
                editor.UseComparisonVersion(version);
            }
        }
        catch (Exception exception) when (EditorViewModel.IsCacheError(exception))
        {
            ShowFileError("Не удалось открыть историю файла.", exception);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new EditorSettingsWindow(editor) { Owner = this }.ShowDialog();

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.PreviewRows))
            InlinePreview.Update(DiffPreview, editor.PreviewRows);
    }

    private void DiffPreview_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RefreshPendingEdits();
        var position = e.CursorLeft < 0
            ? DiffPreview.CaretPosition
            : DiffPreview.GetPositionFromPoint(Mouse.GetPosition(DiffPreview), snapToText: false);
        var row = position?.Paragraph?.Tag as PreviewCue;
        diffRestoreTarget = row is { Changed: true } ? row.Id : null;
        DiffRestoreItem.Visibility = DiffRestoreSeparator.Visibility =
            diffRestoreTarget.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (diffRestoreTarget.HasValue)
            DiffRestoreItem.Header = row!.RestoreLabel;
    }

    private void RestorePreviewCue_Click(object sender, RoutedEventArgs e)
    {
        if (diffRestoreTarget is Guid id)
        {
            editor.RestorePreviewCue(id);
            ScrollToSelected();
        }
    }

    private void NewDocument()
    {
        if (ConfirmDocumentChange())
            editor.New();
    }

    private void OpenDocument()
    {
        var dialog = new OpenFileDialog { Filter = FileFilter, DefaultExt = ".srt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || !ConfirmDocumentChange())
            return;

        try
        {
            editor.Open(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         FormatException or DecoderFallbackException)
        {
            ShowFileError("Не удалось открыть файл.", exception);
        }
    }

    private bool SaveDocument(bool saveAs = false)
    {
        RefreshPendingEdits();
        if (!editor.PrepareToSave())
        {
            ScrollToSelected();
            return false;
        }

        string? suggestedName = null;
        while (true)
        {
            string? path = editor.FilePath;
            if (saveAs || path is null)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = FileFilter,
                    DefaultExt = ".srt",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = suggestedName ?? (path is null ? "subtitles.srt" : Path.GetFileName(path))
                };
                if (dialog.ShowDialog(this) != true)
                    return false;

                path = dialog.FileName;
            }

            try
            {
                editor.Save(path);
                return true;
            }
            catch (ExternalSrtChangeException exception)
            {
                if (MessageBox.Show(this, $"{exception.Message}\n\nСохранить правки под другим именем?",
                    "Файл изменился на диске", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;

                suggestedName = Path.GetFileNameWithoutExtension(editor.FilePath) + " - правки.srt";
                saveAs = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EncoderFallbackException or FormatException)
            {
                ShowFileError("Не удалось сохранить файл.", exception);
                return false;
            }
        }
    }

    private void RefreshPendingEdits()
    {
        editRefreshTimer.Stop();
        editor.FlushPendingEdits();
    }

    private bool ConfirmDocumentChange()
    {
        RefreshPendingEdits();
        if (!editor.IsDirty)
            return true;

        var result = MessageBox.Show(this, $"Сохранить изменения в документе «{editor.FileName}»?",
            "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && SaveDocument());
    }

    private void ShowFileError(string message, Exception exception) =>
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", "SRT Editor",
            MessageBoxButton.OK, MessageBoxImage.Error);

    private void Window_Closing(object? sender, CancelEventArgs e) =>
        e.Cancel = !ConfirmDocumentChange();

    protected override void OnClosed(EventArgs e)
    {
        editRefreshTimer.Stop();
        SoundPanel.Close();
        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: NewDocument(); break;
                case Key.O: OpenDocument(); break;
                case Key.S: SaveDocument(); break;
                case Key.Z: editor.Undo(); break;
                case Key.Enter: AddCue(); break;
                default: return;
            }
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            SaveDocument(saveAs: true);
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Space && SoundPanel.CanTogglePlayback &&
                 e.OriginalSource is not (ButtonBase or TextBoxBase { IsReadOnly: false }))
        {
            if (!e.IsRepeat)
                SoundPanel.TogglePlaybackFromKeyboard();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void StartInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None ||
            sender is not TimecodeInput { DataContext: CueRow row })
            return;

        int next = editor.Rows.IndexOf(row) + 1;
        if (next < editor.Rows.Count)
            FocusTimeField(editor.Rows[next], "StartInput");
        else
            FocusTimeField(row, "EndInput");
        e.Handled = true;
    }

    private void AddCue()
    {
        var row = editor.Add();
        FocusTimeField(row, "StartInput");
    }

    private void FillEnds_Click(object sender, RoutedEventArgs e)
    {
        editor.FillEnds();
        ScrollToSelected();
    }

    private void CopyPreviousEnd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CueRow row })
            editor.CopyPreviousEnd(row);
    }

    private void FocusTimeField(CueRow row, string field)
    {
        CueList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (CueList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
                FindVisual<TimecodeInput>(item, input => input.Name == field)?.FocusEditor();
        }));
    }

    private void ScrollToSelected()
    {
        if (editor.Selected is not null)
            CueList.ScrollIntoView(editor.Selected);
    }

    private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TimecodeInput input && e.NewFocus is Visual next && input.IsAncestorOf(next))
            return;

        editor.EndEdit();
    }

    private void Cue_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: CueRow row })
            editor.Selected = row;
    }

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var grip = (FrameworkElement)sender;
        if (grip.DataContext is CueRow row)
            editor.Selected = row;

        dragOrigin = e.GetPosition(CueList);
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        var grip = (FrameworkElement)sender;
        if (!grip.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed ||
            grip.DataContext is not CueRow row)
            return;

        Vector distance = e.GetPosition(CueList) - dragOrigin;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        grip.ReleaseMouseCapture();
        draggedCue = row;
        try
        {
            DragDrop.DoDragDrop(grip, new DataObject(typeof(CueRow), row), DragDropEffects.Move);
        }
        finally
        {
            draggedCue = null;
            SetDropMarker(null);
        }
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e) =>
        ((FrameworkElement)sender).ReleaseMouseCapture();

    private void CueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (draggedCue is null)
            return;

        if (FindVisual<ScrollViewer>(CueList) is { } scroll)
        {
            double y = e.GetPosition(scroll).Y;
            if (y < 24)
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12);
            else if (y > scroll.ActualHeight - 24)
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12);
        }

        var (target, before) = DropTarget(e);
        SetDropMarker(target, before);
        e.Effects = DragDropEffects.Move;
    }

    private void CueList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (draggedCue is null)
            return;

        var (target, before) = DropTarget(e);
        if (target?.DataContext is CueRow row)
        {
            int index = editor.Rows.IndexOf(row);
            if (index >= 0)
                editor.Move(draggedCue, before ? index : index + 1);
        }

        SetDropMarker(null);
        e.Effects = DragDropEffects.Move;
    }

    private void CueList_DragLeave(object sender, DragEventArgs e) => SetDropMarker(null);

    private (ListBoxItem? Item, bool Before) DropTarget(DragEventArgs e)
    {
        ListBoxItem? last = null;
        if (FindVisual<VirtualizingStackPanel>(CueList) is { } panel)
        {
            foreach (var item in panel.Children.OfType<ListBoxItem>())
            {
                if (e.GetPosition(item).Y < item.ActualHeight / 2)
                    return (item, true);

                last = item;
            }
        }

        return (last, false);
    }

    private void SetDropMarker(ListBoxItem? item, bool before = false)
    {
        if (dropMarker != item)
            dropMarker?.ClearValue(TagProperty);

        dropMarker = item;
        if (item is not null)
            item.Tag = before ? "Before" : "After";
    }

    private static T? FindVisual<T>(DependencyObject root, Predicate<T>? matches = null)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found && (matches is null || matches(found)))
                return found;

            if (FindVisual(child, matches) is { } descendant)
                return descendant;
        }

        return null;
    }
}
