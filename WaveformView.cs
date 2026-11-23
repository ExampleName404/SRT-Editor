using System.Windows;
using System.Windows.Media;

namespace SrtEditor;

public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(
        nameof(Waveform), typeof(AudioWaveform), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush WaveBrush = MakeBrush(255, 117, 193, 168);
    private static readonly Brush PlayedBrush = MakeBrush(32, 159, 235, 206);
    private static readonly Pen CenterLine = MakePen(91, 112, 124, 1);
    private static readonly Pen LaneDivider = MakePen(50, 58, 71, 1);
    private static readonly Brush PlayheadBrush = MakeBrush(255, 206, 255, 237);

    private AudioWaveform? renderedWave;
    private Size renderedSize;
    private DrawingGroup? cachedDrawing;

    public WaveformView() => IsEnabledChanged += (_, _) => InvalidateVisual();

    public AudioWaveform? Waveform
    {
        get => (AudioWaveform?)GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        var size = RenderSize;
        if (size.Width < 1 || size.Height < 1)
            return;

        if (!ReferenceEquals(renderedWave, Waveform) || renderedSize != size || cachedDrawing is null)
        {
            renderedWave = Waveform;
            renderedSize = size;
            cachedDrawing = DrawWaveform(Waveform, size);
        }
        context.DrawDrawing(cachedDrawing);

        double x = Math.Clamp(Progress, 0, 1) * size.Width;
        if (x > 0)
            context.DrawRectangle(PlayedBrush, null, new Rect(0, 0, x, size.Height));
        if (IsEnabled)
            DrawPlayhead(context, x, size.Height);
    }

    private static void DrawPlayhead(DrawingContext context, double x, double waveHeight)
    {
        const double lineHalfWidth = 0.75;
        const double markerHalfWidth = 5.5;
        const double markerHeight = 10;
        double tip = waveHeight + 1;
        double join = tip + markerHeight * lineHalfWidth / markerHalfWidth;
        double bottom = tip + markerHeight;

        // The line and marker share one outline and center, including across the frame border.
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(x - lineHalfWidth, 0), isFilled: true, isClosed: true);
            path.PolyLineTo([
                new Point(x + lineHalfWidth, 0),
                new Point(x + lineHalfWidth, join),
                new Point(x + markerHalfWidth, bottom),
                new Point(x - markerHalfWidth, bottom),
                new Point(x - lineHalfWidth, join)
            ], isStroked: false, isSmoothJoin: false);
        }
        shape.Freeze();
        context.DrawGeometry(PlayheadBrush, null, shape);
    }

    private static DrawingGroup DrawWaveform(AudioWaveform? wave, Size size)
    {
        var result = new DrawingGroup();
        using (var drawing = result.Open())
        {
            if (wave is { Count: > 0 })
            {
                int lanes = wave.Minimum.Length;
                double laneHeight = size.Height / lanes;
                double extent = Math.Max(0, laneHeight / 2 - 3);
                double scale = extent / Math.Max(0.08, wave.Peak);
                for (int lane = 0; lane < lanes; lane++)
                {
                    double top = lane * laneHeight;
                    double middle = top + laneHeight / 2;
                    if (lane > 0)
                        drawing.DrawLine(LaneDivider, new Point(0, top), new Point(size.Width, top));

                    drawing.DrawLine(CenterLine, new Point(0, middle), new Point(size.Width, middle));
                    var envelope = BuildEnvelope(wave, lane, size.Width, middle, scale);
                    drawing.DrawGeometry(WaveBrush, null, envelope);
                }
            }
        }
        result.Freeze();
        return result;
    }

    private static StreamGeometry BuildEnvelope(AudioWaveform wave, int lane, double width, double middle, double scale)
    {
        int columns = (int)Math.Clamp(Math.Ceiling(width), 1, wave.Count);
        var highs = new Point[columns + 1];
        var lows = new Point[columns + 1];
        for (int column = 0; column < columns; column++)
        {
            int first = column * wave.Count / columns;
            int end = (column + 1) * wave.Count / columns;
            float upper = 0;
            float lower = 0;
            for (int index = first; index < end; index++)
            {
                upper = Math.Max(upper, wave.Maximum[lane][index]);
                lower = Math.Min(lower, wave.Minimum[lane][index]);
            }

            double x = (double)column / columns * width;
            highs[column] = new Point(x, middle - upper * scale);
            lows[column] = new Point(x, middle - lower * scale);
        }
        highs[columns] = new Point(width, highs[columns - 1].Y);
        lows[columns] = new Point(width, lows[columns - 1].Y);

        var envelope = new StreamGeometry();
        using (var path = envelope.Open())
        {
            path.BeginFigure(highs[0], isFilled: true, isClosed: true);
            for (int index = 1; index <= columns; index++)
                path.LineTo(highs[index], isStroked: false, isSmoothJoin: false);
            for (int index = columns; index >= 0; index--)
                path.LineTo(lows[index], isStroked: false, isSmoothJoin: false);
        }
        envelope.Freeze();
        return envelope;
    }

    private static SolidColorBrush MakeBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(MakeBrush(255, red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }
}
