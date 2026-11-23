namespace SrtEditor;

internal readonly record struct WaveformViewport(double Start, double Length)
{
    public static WaveformViewport Full { get; } = new(0, 1);
    public const double MinimumLength = 1.0 / 64;

    public double End => Start + Length;
    public double MaximumStart => 1 - Length;

    public bool Contains(double position) => position >= Start - 1e-9 && position <= End + 1e-9;
    public double ToTrack(double fraction) => Start + Math.Clamp(fraction, 0, 1) * Length;
    public double ToView(double position) => (position - Start) / Length;

    public WaveformViewport PanTo(double start) => new(Math.Clamp(start, 0, MaximumStart), Length);

    public WaveformViewport Zoom(double factor, double anchor)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        double length = Math.Clamp(Length / factor, MinimumLength, 1);
        return new WaveformViewport(0, length).PanTo(ToTrack(anchor) - anchor * length);
    }
}
