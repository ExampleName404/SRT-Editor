using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SrtEditor;

public partial class AudioPanel : UserControl
{
    private readonly AudioPlayback playback = new();

    public AudioPanel()
    {
        InitializeComponent();
        PositionInput.TextChanged += (_, _) => ClearPositionError();
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioPlayback.Caption))
                CaptionScroll.ScrollToTop();
        };
        DataContext = playback;
    }

    internal bool CanTogglePlayback => playback.IsReady;
    internal void Open(string path, IEnumerable<Cue> cues)
    {
        EndPositionEdit();
        playback.Open(path, cues);
    }

    internal void UpdateCues(IEnumerable<Cue> cues) => playback.ReplaceCues(cues);
    internal void Close()
    {
        EndPositionEdit();
        playback.Close();
    }

    internal void TogglePlaybackFromKeyboard() => playback.TogglePlay(rewindOnPause: true);

    private void Play_Click(object sender, RoutedEventArgs e) => playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => playback.Stop();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PositionReadout_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || !playback.CanSeek)
            return;

        PositionInput.Text = playback.PositionTimeLabel;
        PositionReadout.Visibility = Visibility.Hidden;
        PositionInput.Visibility = Visibility.Visible;
        PositionInput.Focus();
        PositionInput.SelectAll();
        e.Handled = true;
    }

    private void PositionInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
            return;

        e.Handled = true;
        if (e.Key == Key.Enter)
        {
            if (!SubtitleTime.TryParse(PositionInput.Text, out long milliseconds))
            {
                ShowPositionError("Введите секунды или время в формате 00:00:12,345.");
                return;
            }
            if (milliseconds > playback.TimelineLength)
            {
                ShowPositionError("Указанное время за пределами записи.");
                return;
            }

            playback.Seek(milliseconds);
        }

        EndPositionEdit();
        PositionSlider.Focus();
    }

    private void PositionInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndPositionEdit();

    private void ClearPositionError()
    {
        PositionError.Visibility = Visibility.Collapsed;
        PositionInput.ClearValue(BorderBrushProperty);
    }

    private void ShowPositionError(string message)
    {
        PositionError.Text = message;
        PositionError.Visibility = Visibility.Visible;
        PositionInput.SetResourceReference(BorderBrushProperty, "Error");
    }

    private void EndPositionEdit()
    {
        if (PositionInput.Visibility == Visibility.Collapsed)
            return;

        PositionInput.Visibility = Visibility.Collapsed;
        PositionReadout.Visibility = Visibility.Visible;
        PositionInput.Clear();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue != playback.Position)
            playback.Seek(e.NewValue);
    }

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!playback.CanSeek || !((UIElement)sender).CaptureMouse())
            return;

        PositionSlider.Focus();
        playback.BeginScrub();
        SeekOnTimeline(e);
        e.Handled = true;
    }

    private void Scrub_MouseMove(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured == sender && e.LeftButton == MouseButtonState.Pressed)
            SeekOnTimeline(e);
    }

    private void Scrub_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured != sender)
            return;

        SeekOnTimeline(e);
        playback.EndScrub(canceled: false);
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Scrub_LostCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == sender)
            playback.EndScrub(canceled: true);
    }

    private void SeekOnTimeline(MouseEventArgs e)
    {
        if (WaveDisplay.ActualWidth > 0)
            playback.Seek(e.GetPosition(WaveDisplay).X / WaveDisplay.ActualWidth * playback.TimelineLength);
    }
}
