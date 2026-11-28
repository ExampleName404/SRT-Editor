using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SrtEditor;

public partial class TranscriptionWindow : Window
{
    private readonly string audioPath;
    private CancellationTokenSource? request;

    internal TranscriptionWindow(string audioPath)
    {
        this.audioPath = audioPath;
        InitializeComponent();
        AudioName.Text = Path.GetFileName(audioPath);
        AudioName.ToolTip = audioPath;
    }

    internal Cue[]? Result { get; private set; }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (request is not null || LanguageSelector.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        using var operation = new CancellationTokenSource();
        request = operation;
        SetRunning(true);
        Status.Text = "Подготовка…";
        ErrorMessage.Visibility = Visibility.Collapsed;
        var progress = new Progress<TranscriptionProgress>(update =>
        {
            if (request != operation || operation.IsCancellationRequested)
                return;

            Status.Text = update.Message;
            ProgressBar.IsIndeterminate = update.Percent is null;
            if (update.Percent is { } percent)
                ProgressBar.Value = Math.Clamp(percent, 0, 100);
        });

        try
        {
            var cues = await TranscriptionService.TranscribeAsync(audioPath, language, progress, operation.Token);
            if (!operation.IsCancellationRequested)
            {
                if (cues.Length > 0)
                    Result = cues;
                else
                    ShowMessage("Речь в записи не найдена.");
            }
        }
        catch (Exception exception)
        {
            if (!operation.IsCancellationRequested)
                ShowMessage($"Не удалось распознать речь. {exception.Message}");
        }
        finally
        {
            // A cancellation callback can complete the task inside the Closing event.
            if (operation.IsCancellationRequested)
                await System.Windows.Threading.Dispatcher.Yield();

            request = null;
            SetRunning(false);
        }

        if (Result is not null || operation.IsCancellationRequested)
            Close();
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = LanguageSelector.IsEnabled = !running;
        CancelButton.Content = running ? "Отмена" : "Закрыть";
        CancelButton.IsEnabled = true;
        Status.Visibility = ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = running;
        ProgressBar.Value = 0;
    }

    private void ShowMessage(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !LanguageSelector.IsDropDownOpen)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (request is not { } operation)
            return;

        e.Cancel = true;
        if (operation.IsCancellationRequested)
            return;

        CancelButton.IsEnabled = false;
        Status.Text = "Отмена…";
        ProgressBar.IsIndeterminate = true;
        operation.Cancel();
    }
}
