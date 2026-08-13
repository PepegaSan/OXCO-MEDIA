using System.Collections.Generic;
using HailMary.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace HailMary.Views.Controls;

public sealed partial class VideoRangeTimeline : UserControl
{
    public static readonly DependencyProperty DurationSecondsProperty =
        DependencyProperty.Register(
            nameof(DurationSeconds),
            typeof(double),
            typeof(VideoRangeTimeline),
            new PropertyMetadata(0d, OnVisualPropertyChanged));

    public static readonly DependencyProperty PositionSecondsProperty =
        DependencyProperty.Register(
            nameof(PositionSeconds),
            typeof(double),
            typeof(VideoRangeTimeline),
            new PropertyMetadata(0d, OnPositionChanged));

    public static readonly DependencyProperty PeaksProperty =
        DependencyProperty.Register(
            nameof(Peaks),
            typeof(float[]),
            typeof(VideoRangeTimeline),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty SpansProperty =
        DependencyProperty.Register(
            nameof(Spans),
            typeof(object),
            typeof(VideoRangeTimeline),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public event EventHandler<double>? SeekRequested;

    private bool _dragging;

    public VideoRangeTimeline()
    {
        InitializeComponent();
    }

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public object? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoRangeTimeline timeline)
        {
            timeline.Redraw();
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoRangeTimeline timeline)
        {
            timeline.UpdatePlayhead();
        }
    }

    private void Root_OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        WaveformLayer.Children.Clear();
        RangesLayer.Children.Clear();
        var w = HitGrid.ActualWidth;
        var h = HitGrid.ActualHeight;
        if (w < 4 || h < 8)
        {
            return;
        }

        DrawWaveform(w, h);
        DrawSpans(w, h);
        UpdatePlayhead();
    }

    private void DrawWaveform(double w, double h)
    {
        var peaks = Peaks;
        if (peaks is null || peaks.Length < 2)
        {
            return;
        }

        var mid = h / 2;
        var barW = Math.Max(1, w / peaks.Length);
        var fill = new SolidColorBrush(Color.FromArgb(160, 158, 158, 158));
        for (var i = 0; i < peaks.Length; i++)
        {
            var amp = Math.Clamp(peaks[i], 0, 1);
            var bh = Math.Max(2, amp * (h - 8));
            var bar = new Rectangle
            {
                Width = Math.Max(1, barW - 0.4),
                Height = bh,
                Fill = fill,
            };
            Canvas.SetLeft(bar, i * barW);
            Canvas.SetTop(bar, mid - (bh / 2));
            WaveformLayer.Children.Add(bar);
        }
    }

    private void DrawSpans(double w, double h)
    {
        if (DurationSeconds <= 0.001 || Spans is not IEnumerable<TimelineSpan> spans)
        {
            return;
        }

        foreach (var span in spans)
        {
            var start = Math.Clamp(span.StartSeconds, 0, DurationSeconds);
            var end = Math.Clamp(span.EndSeconds, start, DurationSeconds);
            var left = start / DurationSeconds * w;
            var width = Math.Max(3, (end - start) / DurationSeconds * w);
            var (fill, stroke) = span.Kind switch
            {
                TimelineSpanKind.Pending => (
                    new SolidColorBrush(Color.FromArgb(70, 255, 193, 7)),
                    new SolidColorBrush(Color.FromArgb(220, 255, 193, 7))),
                TimelineSpanKind.Loop => (
                    new SolidColorBrush(Color.FromArgb(90, 58, 143, 92)),
                    new SolidColorBrush(Color.FromArgb(230, 79, 195, 247))),
                _ => (
                    new SolidColorBrush(Color.FromArgb(80, 79, 195, 247)),
                    new SolidColorBrush(Color.FromArgb(220, 79, 195, 247))),
            };

            var rect = new Rectangle
            {
                Width = width,
                Height = h - 4,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.5,
                RadiusX = 3,
                RadiusY = 3,
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, 2);
            RangesLayer.Children.Add(rect);

            if (!string.IsNullOrWhiteSpace(span.Label) && width > 36)
            {
                var label = new TextBlock
                {
                    Text = span.Label,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, left + 6);
                Canvas.SetTop(label, 8);
                RangesLayer.Children.Add(label);
            }
        }
    }

    private void UpdatePlayhead()
    {
        var w = HitGrid.ActualWidth;
        var h = HitGrid.ActualHeight;
        if (w < 4 || DurationSeconds <= 0.001)
        {
            Playhead.Height = 0;
            return;
        }

        Playhead.Height = h;
        var x = Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) * w;
        Playhead.Margin = new Thickness(x, 0, 0, 0);
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

    private void HitGrid_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e);
    }

    private void HitGrid_OnPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void HitGrid_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
    }

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
        if (DurationSeconds <= 0.001 || HitGrid.ActualWidth < 2)
        {
            return;
        }

        var t = Math.Clamp(pos.X / HitGrid.ActualWidth, 0, 1) * DurationSeconds;
        SeekRequested?.Invoke(this, t);
    }
}
