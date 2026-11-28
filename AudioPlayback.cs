using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SrtEditor;

internal sealed class AudioPlayback : ObservableModel
{
    private enum PlaybackState { Closed, Loading, Ready, Playing, Ended, Failed }

    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(30)
    };
    private readonly DispatcherTimer loadTimer;
    private readonly Func<string, CancellationToken, Task<(WaveStream Reader, OggPcmBuffer? Buffer)>> openAudio;
    private CancellationTokenSource? loadRequest;
    private CancellationTokenSource? waveformRequest;
    private CancellationTokenSource? playbackRead;
    private WasapiPlayer? player;
    private VolumeSampleProvider? volumeProvider;
    private EventHandler<StoppedEventArgs>? playbackStopped;
    private WaveStream? audio;
    private OggPcmBuffer? ogg;
    private SubtitlePlaybackTrack track = new([]);
    private PlaybackState state;
    private string? path;
    private string caption = "";
    private string? errorDetails;
    private AudioWaveform? waveform;
    private string waveformMessage = "";
    private double duration;
    private double position;
    private double playbackStart;
    private double playOrigin;
    private double volume = 0.8;
    private double? scrubOrigin;

    public AudioPlayback() : this(TimeSpan.FromSeconds(30), OpenAudioSourceAsync)
    {
    }

    internal AudioPlayback(TimeSpan loadTimeout,
        Func<string, CancellationToken, Task<(WaveStream Reader, OggPcmBuffer? Buffer)>> openAudio)
    {
        this.openAudio = openAudio;
        timer.Tick += (_, _) => ReadPosition();
        loadTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = loadTimeout };
        loadTimer.Tick += (_, _) =>
        {
            if (loadRequest is not null)
                FailLoad($"Аудиофайл не открылся за {loadTimeout.TotalSeconds:0.###} секунд.");
        };
    }

    public bool HasSource => FilePath is not null;
    public bool IsReady => state is PlaybackState.Ready or PlaybackState.Playing or PlaybackState.Ended;
    public bool CanSeek => IsReady && duration > 0;
    internal bool IsPlaying => state == PlaybackState.Playing;
    public string PlayLabel => state == PlaybackState.Playing ? "Пауза" : "Воспроизвести";
    public string? FilePath => path;
    public string FileName => Path.GetFileName(path) ?? "";
    public double Position => position;
    public double TimelineLength => duration > 0 ? duration : 1;
    public double Progress => duration > 0 ? position / duration : 0;
    public string Caption { get => caption; private set => Set(ref caption, value); }
    public string? ErrorDetails { get => errorDetails; private set => Set(ref errorDetails, value); }
    public AudioWaveform? Waveform { get => waveform; private set => Set(ref waveform, value); }
    public string WaveformMessage
    {
        get => waveformMessage;
        private set
        {
            Set(ref waveformMessage, value);
            Notify(nameof(HasWaveformMessage));
        }
    }
    public bool HasWaveformMessage => WaveformMessage.Length > 0;
    public string PositionTimeLabel => FormatTime(position);
    public string DurationTimeLabel => duration > 0 ? FormatTime(duration) : "—";
    public string Status => state switch
    {
        PlaybackState.Loading => "Загрузка аудио…",
        PlaybackState.Failed => ErrorDetails ?? "Не удалось воспроизвести аудио.",
        _ => ErrorDetails ?? ""
    };
    public bool HasStatus => Status.Length > 0;

    public double Volume
    {
        get => volume;
        set
        {
            Set(ref volume, value);
            if (volumeProvider is not null)
                volumeProvider.Volume = (float)value;
        }
    }

    public void Open(string filePath, IEnumerable<Cue> cues)
    {
        Close();
        track = new SubtitlePlaybackTrack(cues);
        SetFile(filePath);
        SetState(PlaybackState.Loading);
        _ = LoadAudioAsync(filePath);
    }

    public void ReplaceCues(IEnumerable<Cue> cues)
    {
        if (!HasSource)
            return;

        track = new SubtitlePlaybackTrack(cues);
        UpdateCaption();
    }

    public void TogglePlay(bool rewindOnPause = false)
    {
        if (!IsReady)
            return;

        if (state == PlaybackState.Playing)
        {
            if (!rewindOnPause && !scrubOrigin.HasValue)
                ReadPosition();
            StopOutput();
            SetState(PlaybackState.Ready);
            if (rewindOnPause)
                UpdatePosition(playOrigin);
            return;
        }

        if (state == PlaybackState.Ended || position >= duration)
            Stop();

        playOrigin = position;
        SetState(PlaybackState.Playing);
        if (!scrubOrigin.HasValue)
            StartOutput();
    }

    public void Stop()
    {
        if (!IsReady)
            return;

        StopOutput();
        scrubOrigin = null;
        SetState(PlaybackState.Ready);
        UpdatePosition(0);
    }

    public void BeginScrub()
    {
        if (!CanSeek || scrubOrigin.HasValue)
            return;

        scrubOrigin = position;
        StopOutput();
    }

    public void EndScrub(bool canceled)
    {
        if (scrubOrigin is not double origin)
            return;

        scrubOrigin = null;
        Seek(canceled ? origin : position);
    }

    public void Seek(double milliseconds)
    {
        if (!CanSeek)
            return;

        double target = Math.Clamp(milliseconds, 0, duration);
        if (state == PlaybackState.Ended && target < duration)
            SetState(PlaybackState.Ready);
        if (!scrubOrigin.HasValue && state == PlaybackState.Playing)
        {
            StopOutput();
            UpdatePosition(target);
            StartOutput();
            return;
        }

        UpdatePosition(target);
    }

    public void Close()
    {
        ReleaseAudio();
        SetError(null);
        SetState(PlaybackState.Closed);
        SetFile(null);
        SetDuration(0);
        UpdatePosition(0);
        track = new SubtitlePlaybackTrack([]);
    }

    private async Task LoadAudioAsync(string filePath)
    {
        using var request = new CancellationTokenSource();
        loadRequest = request;
        loadTimer.Start();
        WaveStream? openedAudio = null;
        OggPcmBuffer? openedOgg = null;
        Exception? failure = null;
        double length = 0;
        try
        {
            (openedAudio, openedOgg) = await openAudio(filePath, request.Token).ConfigureAwait(false);
            length = openedAudio.TotalTime.TotalMilliseconds;
            if (length <= 0)
                throw new InvalidDataException("В файле нет звуковых данных.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (loadRequest != request)
                    return;

                // Stop the timer and detach the token before the worker can dispose it.
                loadRequest = null;
                loadTimer.Stop();
                if (failure is not null)
                {
                    FailLoad(failure.Message);
                    return;
                }

                audio = openedAudio;
                ogg = openedOgg;
                openedAudio = null;
                openedOgg = null;
                SetDuration(length);
                SetState(PlaybackState.Ready);
                UpdatePosition(0);
                _ = LoadWaveformAsync(filePath, ogg);
            });
        }
        catch (OperationCanceledException) when (dispatcher.HasShutdownStarted)
        {
        }
        finally
        {
            openedAudio?.Dispose();
            openedOgg?.Dispose();
        }
    }

    private static async Task<(WaveStream Reader, OggPcmBuffer? Buffer)> OpenAudioSourceAsync(string filePath,
        CancellationToken cancellation)
    {
        if (!OggAudio.IsOggFile(filePath))
            return (await Task.Run(() => new MediaFoundationReader(filePath), cancellation).ConfigureAwait(false), null);

        var buffer = await OggPcmBuffer.OpenAsync(filePath, cancellation).ConfigureAwait(false);
        return (buffer.OpenReader(), buffer);
    }

    private void FailLoad(string details)
    {
        ReleaseAudio();
        SetError(details);
        SetState(PlaybackState.Failed);
        SetDuration(0);
        UpdatePosition(0);
    }

    private void ReleaseAudio()
    {
        loadTimer.Stop();
        var previousLoad = loadRequest;
        loadRequest = null;
        previousLoad?.Cancel();
        var previousWaveform = waveformRequest;
        waveformRequest = null;
        previousWaveform?.Cancel();
        ReleaseOutput();
        audio?.Dispose();
        audio = null;
        ogg?.Dispose();
        ogg = null;
        scrubOrigin = null;
        Waveform = null;
        WaveformMessage = "";
    }

    private void StartOutput()
    {
        if (audio is not { } reader)
            return;

        try
        {
            SetError(null);
            if (player is null)
            {
                volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = (float)volume };
                player = new WasapiPlayerBuilder().WithLatency(60).Build();
                player.Init(new SampleToWaveProvider(volumeProvider));
            }

            var run = new CancellationTokenSource();
            playbackRead = run;
            if (reader is OggPcmBuffer.Reader oggReader)
                oggReader.Cancellation = run.Token;
            reader.CurrentTime = TimeSpan.FromMilliseconds(position);
            playbackStart = reader.CurrentTime.TotalMilliseconds;
            playbackStopped = (_, args) =>
            {
                _ = dispatcher.InvokeAsync(() =>
                {
                    if (playbackRead != run)
                        return;
                    if (args.Exception is { } error)
                        FailOutput(error.Message);
                    else
                    {
                        StopOutput();
                        SetState(PlaybackState.Ended);
                        UpdatePosition(duration);
                    }
                });
            };
            player.PlaybackStopped += playbackStopped;
            player.Play();
            timer.Start();
        }
        catch (Exception exception)
        {
            FailOutput(exception.Message);
        }
    }

    private void StopOutput()
    {
        timer.Stop();
        var run = playbackRead;
        playbackRead = null;
        if (run is null)
            return;

        run.Cancel();
        player!.PlaybackStopped -= playbackStopped;
        playbackStopped = null;
        player.Stop();
        run.Dispose();
    }

    private void ReleaseOutput()
    {
        StopOutput();
        player?.Dispose();
        player = null;
        volumeProvider = null;
    }

    private void FailOutput(string details)
    {
        ReleaseOutput();
        SetError($"Ошибка воспроизведения. {details}");
        SetState(PlaybackState.Ready);
    }

    private void ReadPosition()
    {
        if (player is not { } output || output.PlaybackState != NAudio.Wave.PlaybackState.Playing)
            return;

        try
        {
            var milliseconds = 1000.0 * output.GetPosition() / output.OutputWaveFormat.AverageBytesPerSecond;
            UpdatePosition(playbackStart + milliseconds);
        }
        catch (Exception exception)
        {
            FailOutput(exception.Message);
        }
    }

    private async Task LoadWaveformAsync(string filePath, OggPcmBuffer? buffer)
    {
        using var request = new CancellationTokenSource();
        waveformRequest = request;
        WaveformMessage = "Построение звуковой волны…";
        try
        {
            var result = await Task.Run(() =>
            {
                if (buffer is null)
                    return AudioWaveformReader.Read(filePath, request.Token);

                using var reader = buffer.OpenReader();
                reader.Cancellation = request.Token;
                return AudioWaveformReader.Read(reader, request.Token);
            }, request.Token);
            if (waveformRequest == request)
            {
                Waveform = result.Count > 0 ? result : null;
                WaveformMessage = Waveform is null ? "В файле нет данных для построения волны." : "";
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (waveformRequest == request)
                WaveformMessage = "Волна недоступна для этого файла.";
        }
        finally
        {
            if (waveformRequest == request)
                waveformRequest = null;
        }
    }

    private void SetError(string? details)
    {
        ErrorDetails = details;
        Notify(nameof(Status));
        Notify(nameof(HasStatus));
    }

    private void UpdatePosition(double milliseconds)
    {
        double upperBound = duration > 0 ? duration : double.MaxValue;
        double current = Math.Clamp(milliseconds, 0, upperBound);
        if (position != current)
        {
            Set(ref position, current, nameof(Position));
            Notify(nameof(PositionTimeLabel));
            Notify(nameof(Progress));
        }
        UpdateCaption();
    }

    private void UpdateCaption() =>
        Caption = IsReady && (state != PlaybackState.Ended || scrubOrigin.HasValue)
            ? track.At((long)position)
            : "";

    private void SetDuration(double milliseconds)
    {
        duration = milliseconds;
        Notify(nameof(TimelineLength));
        Notify(nameof(DurationTimeLabel));
        Notify(nameof(CanSeek));
        Notify(nameof(Progress));
    }

    private void SetState(PlaybackState value)
    {
        if (state == value)
            return;

        state = value;
        Notify(nameof(IsReady));
        Notify(nameof(CanSeek));
        Notify(nameof(PlayLabel));
        Notify(nameof(Status));
        Notify(nameof(HasStatus));
    }

    private void SetFile(string? filePath)
    {
        Set(ref path, filePath, nameof(FilePath));
        Notify(nameof(HasSource));
        Notify(nameof(FileName));
    }

    private static string FormatTime(double milliseconds) => SubtitleTime.Format((long)milliseconds);
}

