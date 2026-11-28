using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SrtEditor;

public partial class AudioPanel : UserControl
{
    private readonly AudioPlayback playback = new();
    private WaveformViewport viewport = WaveformViewport.Full;
    private (double PointerX, WaveformViewport Viewport)? waveformPan;
    private double previousProgress;
    private bool updatingPositionSlider;

    internal event Action? OpenAudioRequested;
    internal event Action<string>? TranscriptionRequested;

    public AudioPanel()
    {
        InitializeComponent();
        PositionInput.TextChanged += (_, _) => ClearPositionError();
        playback.PropertyChanged += Playback_PropertyChanged;
        DataContext = playback;
        UpdateZoomControls();
    }

    internal bool CanTogglePlayback => playback.IsReady;
    internal void Open(string path, IEnumerable<Cue> cues)
    {
        EndPositionEdit();
        EndWaveformPan();
        SetViewport(WaveformViewport.Full);
        playback.Open(path, cues);
    }

    internal void UpdateCues(IEnumerable<Cue> cues) => playback.ReplaceCues(cues);
    internal void Close()
    {
        EndPositionEdit();
        EndWaveformPan();
        SetViewport(WaveformViewport.Full);
        playback.Close();
    }

    internal void TogglePlaybackFromKeyboard() => TogglePlay(rewindOnPause: true);

    private void OpenAudio_Click(object sender, RoutedEventArgs e) => OpenAudioRequested?.Invoke();
    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (playback.IsReady && playback.FilePath is { } audioPath)
            TranscriptionRequested?.Invoke(audioPath);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay(rewindOnPause: false);
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        playback.Stop();
        RevealPlayhead();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TogglePlay(bool rewindOnPause)
    {
        playback.TogglePlay(rewindOnPause);
        RevealPlayhead();
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AudioPlayback.Caption):
                CaptionScroll.ScrollToTop();
                break;
            case nameof(AudioPlayback.Position):
                // Let the user inspect another part of the recording while audio plays.
                if (!playback.IsPlaying || viewport.Contains(previousProgress))
                    RevealPlayhead();
                previousProgress = playback.Progress;
                UpdatePositionSlider();
                break;
            case nameof(AudioPlayback.TimelineLength):
                UpdatePositionSlider(updateRange: true);
                break;
            case nameof(AudioPlayback.CanSeek):
            case nameof(AudioPlayback.Waveform):
                UpdateZoomControls();
                break;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(2, ZoomAnchor());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(0.5, ZoomAnchor());
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetViewport(WaveformViewport.Full);

    private double ZoomAnchor() => viewport.Contains(playback.Progress) ? viewport.ToView(playback.Progress) : 0.5;

    private void Zoom(double factor, double anchor)
    {
        if (playback.CanSeek && playback.Waveform is not null)
            SetViewport(viewport.Zoom(factor, anchor));
    }

    private void Waveform_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!playback.CanSeek || playback.Waveform is null || WaveDisplay.ActualWidth <= 0 || waveformPan is not null ||
            e.LeftButton == MouseButtonState.Pressed)
            return;

        double steps = e.Delta / 120.0;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            SetViewport(viewport.PanTo(viewport.Start - steps * viewport.Length / 3));
        else
            Zoom(Math.Pow(2, steps), e.GetPosition(WaveDisplay).X / WaveDisplay.ActualWidth);

        e.Handled = true;
    }

    private void WaveformScroll_Scroll(object sender, ScrollEventArgs e) => SetViewport(viewport.PanTo(e.NewValue));

    private void RevealPlayhead()
    {
        if (waveformPan is null && !viewport.Contains(playback.Progress))
            SetViewport(viewport.PanTo(playback.Progress - viewport.Length / 2));
    }

    private void SetViewport(WaveformViewport value)
    {
        if (viewport == value)
            return;

        viewport = value;
        WaveDisplay.SetViewport(viewport);
        WaveformScroll.Maximum = viewport.MaximumStart;
        WaveformScroll.ViewportSize = viewport.Length;
        WaveformScroll.SmallChange = viewport.Length / 8;
        WaveformScroll.LargeChange = viewport.Length * 0.8;
        WaveformScroll.Value = viewport.Start;
        WaveformScroll.Visibility = viewport.Length < 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateZoomControls();
        UpdatePositionSlider(updateRange: true);
    }

    private void UpdateZoomControls()
    {
        bool available = playback.CanSeek && playback.Waveform is not null;
        ZoomInButton.IsEnabled = available && viewport.Length > WaveformViewport.MinimumLength;
        ZoomOutButton.IsEnabled = ZoomResetButton.IsEnabled = available && viewport.Length < 1;
        ZoomResetButton.Content = $"{1 / viewport.Length:0.#}×";
    }

    private void UpdatePositionSlider(bool updateRange = false)
    {
        updatingPositionSlider = true;
        try
        {
            if (updateRange)
            {
                PositionSlider.Minimum = viewport.Start * playback.TimelineLength;
                PositionSlider.Maximum = viewport.End * playback.TimelineLength;
            }
            PositionSlider.Value = Math.Clamp(playback.Position, PositionSlider.Minimum, PositionSlider.Maximum);
        }
        finally
        {
            updatingPositionSlider = false;
        }
    }

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
            RevealPlayhead();
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
        ClearPositionError();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingPositionSlider && e.NewValue != playback.Position)
            playback.Seek(e.NewValue);
    }

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (waveformPan is not null || !playback.CanSeek || !((UIElement)sender).CaptureMouse())
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
        if (waveformPan is not null || Mouse.Captured != sender)
            return;

        SeekOnTimeline(e);
        playback.EndScrub(canceled: false);
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Timeline_LostCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource != sender)
            return;

        if (waveformPan is not null && sender == WaveSurface)
            EndWaveformPan();
        else
            playback.EndScrub(canceled: true);
    }

    private void Waveform_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!playback.CanSeek || viewport.MaximumStart == 0 || WaveDisplay.ActualWidth <= 0 ||
            Mouse.Captured is not null || !WaveSurface.CaptureMouse())
            return;

        waveformPan = (e.GetPosition(WaveDisplay).X, viewport);
        WaveSurface.Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (waveformPan is null)
            Scrub_MouseMove(sender, e);
        else if (WaveSurface.IsMouseCaptured && e.RightButton == MouseButtonState.Pressed)
            PanWaveform(e.GetPosition(WaveDisplay).X);
    }

    private void Waveform_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (waveformPan is null || !WaveSurface.IsMouseCaptured)
            return;

        PanWaveform(e.GetPosition(WaveDisplay).X);
        EndWaveformPan();
        e.Handled = true;
    }

    private void PanWaveform(double pointerX)
    {
        if (waveformPan is not { } origin || WaveDisplay.ActualWidth <= 0)
            return;

        double fraction = (pointerX - origin.PointerX) / WaveDisplay.ActualWidth;
        SetViewport(origin.Viewport.PanTo(origin.Viewport.Start - fraction * origin.Viewport.Length));
    }

    private void EndWaveformPan()
    {
        if (waveformPan is null)
            return;

        waveformPan = null;
        WaveSurface.Cursor = Cursors.Hand;
        if (WaveSurface.IsMouseCaptured)
            WaveSurface.ReleaseMouseCapture();
    }

    private void SeekOnTimeline(MouseEventArgs e)
    {
        if (WaveDisplay.ActualWidth > 0)
        {
            double fraction = e.GetPosition(WaveDisplay).X / WaveDisplay.ActualWidth;
            playback.Seek(viewport.ToTrack(fraction) * playback.TimelineLength);
        }
    }
}
