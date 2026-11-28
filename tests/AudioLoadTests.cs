using System.Diagnostics;
using System.Windows.Threading;
using NAudio.Wave;

namespace SrtEditor.Tests;

internal static class AudioLoadTests
{
    public static void Run()
    {
        TimeoutCancelsLoadAndAllowsReopen();
        ReopenDiscardsLateReader();
    }

    private static void TimeoutCancelsLoadAndAllowsReopen()
    {
        var pending = new TaskCompletionSource<(WaveStream Reader, OggPcmBuffer? Buffer)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TestReader(2_000);
        CancellationToken interruptedLoad = default;
        var playback = new AudioPlayback(TimeSpan.FromMilliseconds(50), (path, cancellation) =>
        {
            if (path == "replacement.wav")
                return Task.FromResult<(WaveStream, OggPcmBuffer?)>((replacement, null));

            interruptedLoad = cancellation;
            return pending.Task.WaitAsync(cancellation);
        });

        try
        {
            playback.Open("slow.ogg", []);
            WaitUntil(() => playback.ErrorDetails is not null, "The open timeout displays an error.");
            Check.True(interruptedLoad.IsCancellationRequested && !playback.IsReady &&
                       playback.Status.Contains("не открылся за", StringComparison.Ordinal),
                "The timeout cancels the loader and leaves the loading state.");

            playback.Close();
            Check.True(!playback.HasSource && playback.ErrorDetails is null, "Closing after a timeout clears the audio UI.");
            playback.Open("replacement.wav", []);
            WaitUntil(() => playback.IsReady, "A file can open after a timeout.");
            Check.True(playback.FilePath == "replacement.wav" && playback.ErrorDetails is null &&
                       playback.TimelineLength == 2_000, "The replacement file has its own state.");
        }
        finally
        {
            playback.Close();
        }
    }

    private static void ReopenDiscardsLateReader()
    {
        var pending = new TaskCompletionSource<(WaveStream Reader, OggPcmBuffer? Buffer)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new TestReader(1_000);
        var replacement = new TestReader(3_000);
        CancellationToken interruptedLoad = default;
        var playback = new AudioPlayback(TimeSpan.FromSeconds(20), (path, cancellation) =>
        {
            if (path == "second.wav")
                return Task.FromResult<(WaveStream, OggPcmBuffer?)>((replacement, null));

            interruptedLoad = cancellation;
            return pending.Task;
        });

        try
        {
            playback.Open("first.wav", []);
            playback.Open("second.wav", []);
            Check.True(interruptedLoad.IsCancellationRequested, "Reopening cancels the previous load.");
            WaitUntil(() => playback.IsReady, "The replacement opens while the first reader is still pending.");

            pending.SetResult((stale, null));
            WaitUntil(() => stale.Disposed, "A late result from a canceled load is disposed.");
            Check.True(playback.IsReady && playback.FilePath == "second.wav" && playback.TimelineLength == 3_000 &&
                       playback.ErrorDetails is null && !replacement.Disposed, "A stale completion cannot change the replacement.");
        }
        finally
        {
            pending.TrySetResult((stale, null));
            playback.Close();
        }
        Check.True(replacement.Disposed, "Closing disposes the accepted reader.");
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        if (condition())
            return;

        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5);
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
        Check.True(condition(), message);
    }

    private sealed class TestReader(int milliseconds) : WaveStream
    {
        private volatile bool disposed;
        public bool Disposed => disposed;
        public override WaveFormat WaveFormat { get; } = new(8_000, 16, 1);
        public override long Length => WaveFormat.AverageBytesPerSecond * (long)milliseconds / 1_000;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => 0;

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }
    }
}
