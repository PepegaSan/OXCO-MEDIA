using HailMary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace HailMary.Views.Controls;

/// <summary>
/// Oxco compare result: per-frame metric (log scale) with the pixel threshold, and a strip that shows what is
/// kept (green) and cut (red). X axis = source frame number; Metric/Segments are drawn shifted by MetricOffset.
/// </summary>
public sealed partial class OxcoResultTimeline : UserControl
{
    private const double StripHeight = 14;

    public static readonly DependencyProperty MetricProperty = Register(nameof(Metric), typeof(int[]), null);
    public static readonly DependencyProperty SegmentsProperty = Register(nameof(Segments), typeof(object), null);
    public static readonly DependencyProperty ThresholdProperty = Register(nameof(Threshold), typeof(double), 0d);
    public static readonly DependencyProperty PixelMaxProperty = Register(nameof(PixelMax), typeof(double), 0d);
    public static readonly DependencyProperty FrameCountProperty = Register(nameof(FrameCount), typeof(int), 0);
    public static readonly DependencyProperty MetricOffsetProperty = Register(nameof(MetricOffset), typeof(int), 0);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), typeof(string), string.Empty);

    public static readonly DependencyProperty PositionFrameProperty =
        DependencyProperty.Register(
            nameof(PositionFrame),
            typeof(double),
            typeof(OxcoResultTimeline),
            new PropertyMetadata(0d, (d, _) => ((OxcoResultTimeline)d).UpdatePlayhead()));

    private static readonly SolidColorBrush CurveFill = new(Color.FromArgb(90, 79, 195, 247));
    private static readonly SolidColorBrush CurveStroke = new(Color.FromArgb(230, 79, 195, 247));
    private static readonly SolidColorBrush ThresholdBrush = new(Color.FromArgb(230, 255, 167, 38));
    private static readonly SolidColorBrush MaxBrush = new(Color.FromArgb(160, 186, 104, 200));
    private static readonly SolidColorBrush KeepBrush = new(Color.FromArgb(200, 58, 160, 92));
    private static readonly SolidColorBrush CutBrush = new(Color.FromArgb(240, 229, 72, 77));

    public event EventHandler<int>? SeekRequested;

    private bool _dragging;

    public OxcoResultTimeline()
    {
        InitializeComponent();
    }

    public int[]? Metric
    {
        get => (int[]?)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>IReadOnlyList&lt;OxcoSegment&gt; in metric indices.</summary>
    public object? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public double PixelMax
    {
        get => (double)GetValue(PixelMaxProperty);
        set => SetValue(PixelMaxProperty, value);
    }

    /// <summary>Total source frames (x range). Falls back to metric length + offset.</summary>
    public int FrameCount
    {
        get => (int)GetValue(FrameCountProperty);
        set => SetValue(FrameCountProperty, value);
    }

    /// <summary>Source frame of metric index 0 (leading frames skipped by the sync offset).</summary>
    public int MetricOffset
    {
        get => (int)GetValue(MetricOffsetProperty);
        set => SetValue(MetricOffsetProperty, value);
    }

    public double PositionFrame
    {
        get => (double)GetValue(PositionFrameProperty);
        set => SetValue(PositionFrameProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(OxcoResultTimeline),
            new PropertyMetadata(defaultValue, (d, _) => ((OxcoResultTimeline)d).Redraw()));

    private int TotalFrames
    {
        get
        {
            var metricEnd = (Metric?.Length ?? 0) + Math.Max(0, MetricOffset);
            return Math.Max(FrameCount, metricEnd);
        }
    }

    private void Root_OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        CurveLayer.Children.Clear();
        StripLayer.Children.Clear();
        var w = HitGrid.ActualWidth;
        var h = HitGrid.ActualHeight;
        var metric = Metric;
        var hasMetric = metric is { Length: > 0 };
        EmptyHint.Text = hasMetric ? string.Empty : EmptyText;
        if (w < 8 || h < 24 || TotalFrames <= 0)
        {
            UpdatePlayhead();
            return;
        }

        if (hasMetric)
        {
            DrawCurve(metric!, w, h - StripHeight - 2);
            DrawStrip(w, h);
        }

        UpdatePlayhead();
    }

    private double FrameToX(double frame, double w) => Math.Clamp(frame / Math.Max(1, TotalFrames), 0, 1) * w;

    private void DrawCurve(int[] metric, double w, double curveHeight)
    {
        var maxValue = Math.Max(1, Math.Max(metric.Max(), Threshold * 1.5));
        var logMax = Math.Log10(1 + maxValue);
        double Y(double v) => 3 + (curveHeight - 6) * (1 - Math.Log10(1 + Math.Max(0, v)) / logMax);

        // One point per pixel column (max of the frames in that column), so spikes stay visible.
        var columns = Math.Max(2, (int)Math.Ceiling(w));
        var total = TotalFrames;
        var offset = Math.Max(0, MetricOffset);
        var points = new PointCollection { new Point(FrameToX(offset, w), Y(0)) };
        for (var c = 0; c < columns; c++)
        {
            var f0 = (int)((long)c * total / columns) - offset;
            var f1 = (int)((long)(c + 1) * total / columns) - offset;
            if (f1 <= 0 || f0 >= metric.Length)
            {
                continue;
            }

            f0 = Math.Max(0, f0);
            f1 = Math.Clamp(f1, f0 + 1, metric.Length);
            var m = 0;
            for (var i = f0; i < f1; i++)
            {
                if (metric[i] > m)
                {
                    m = metric[i];
                }
            }

            points.Add(new Point(c + 0.5, Y(m)));
        }

        points.Add(new Point(FrameToX(offset + metric.Length, w), Y(0)));
        CurveLayer.Children.Add(new Polygon { Points = points, Fill = CurveFill, Stroke = CurveStroke, StrokeThickness = 1 });

        AddHorizontalLine(Y(Threshold), w, ThresholdBrush, dashed: false);
        if (PixelMax > 0)
        {
            AddHorizontalLine(Y(PixelMax), w, MaxBrush, dashed: true);
        }
    }

    private void AddHorizontalLine(double y, double w, Brush brush, bool dashed)
    {
        var line = new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1.5 };
        if (dashed)
        {
            line.StrokeDashArray = [4, 3];
        }

        CurveLayer.Children.Add(line);
    }

    private void DrawStrip(double w, double h)
    {
        var top = h - StripHeight - 1;
        var offset = Math.Max(0, MetricOffset);
        if (Segments is not IReadOnlyList<OxcoSegment> segments || segments.Count == 0)
        {
            return;
        }

        foreach (var s in segments)
        {
            var x0 = FrameToX(s.Start + offset, w);
            var x1 = FrameToX(s.End + offset, w);
            var rect = new Rectangle
            {
                Width = Math.Max(s.Keep ? 0.5 : 2, x1 - x0),
                Height = StripHeight,
                Fill = s.Keep ? KeepBrush : CutBrush,
            };
            Canvas.SetLeft(rect, s.Keep ? x0 : Math.Min(x0, w - 2));
            Canvas.SetTop(rect, top);
            StripLayer.Children.Add(rect);
        }
    }

    private void UpdatePlayhead()
    {
        var w = HitGrid.ActualWidth;
        var h = HitGrid.ActualHeight;
        if (w < 4 || TotalFrames <= 0)
        {
            Playhead.Height = 0;
            return;
        }

        Playhead.Height = h;
        Playhead.Margin = new Thickness(FrameToX(PositionFrame, w), 0, 0, 0);
    }

    private void HitGrid_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        HitGrid.CapturePointer(e.Pointer);
        SeekFromPoint(e.GetCurrentPoint(HitGrid).Position);
        e.Handled = true;
    }

    private void HitGrid_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        SeekFromPoint(e.GetCurrentPoint(HitGrid).Position);
        e.Handled = true;
    }

    private void HitGrid_OnPointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void HitGrid_OnPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void HitGrid_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        try
        {
            HitGrid.ReleasePointerCapture(e.Pointer);
        }
        catch
        {
            // already released
        }

        e.Handled = true;
    }

    private void SeekFromPoint(Point pos)
    {
        var total = TotalFrames;
        if (total <= 0 || HitGrid.ActualWidth < 2)
        {
            return;
        }

        var frame = (int)Math.Round(Math.Clamp(pos.X / HitGrid.ActualWidth, 0, 1) * (total - 1));
        SeekRequested?.Invoke(this, frame);
    }
}
