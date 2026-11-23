using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SrtEditor;

public partial class AudioPanel : UserControl
{
    private readonly AudioPlayback playback = new();

    public AudioPanel()
    {
        InitializeComponent();
        PositionSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => playback.BeginScrub()));
        PositionSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, e) => playback.EndScrub(e.Canceled)));
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioPlayback.Caption))
                CaptionScroll.ScrollToTop();
        };
        DataContext = playback;
    }

    internal void Open(string path, IEnumerable<Cue> cues) => playback.Open(path, cues);
    internal void UpdateCues(IEnumerable<Cue> cues) => playback.ReplaceCues(cues);
    internal void Close() => playback.Close();

    private void Play_Click(object sender, RoutedEventArgs e) => playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => playback.Stop();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue != playback.Position)
            playback.Seek(e.NewValue);
    }

    private void Waveform_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!playback.CanSeek || !WaveDisplay.CaptureMouse())
            return;

        playback.BeginScrub();
        SeekOnWaveform(e);
        e.Handled = true;
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (WaveDisplay.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SeekOnWaveform(e);
    }

    private void Waveform_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!WaveDisplay.IsMouseCaptured)
            return;

        SeekOnWaveform(e);
        playback.EndScrub(canceled: false);
        WaveDisplay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Waveform_LostCapture(object sender, MouseEventArgs e) => playback.EndScrub(canceled: true);

    private void SeekOnWaveform(MouseEventArgs e)
    {
        if (WaveDisplay.ActualWidth > 0)
            playback.Seek(e.GetPosition(WaveDisplay).X / WaveDisplay.ActualWidth * playback.TimelineLength);
    }
}
