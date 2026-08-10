using System.Runtime.InteropServices.WindowsRuntime;
using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace HailMary.Views.Controls;

public sealed partial class TextToVideoPreviewHost : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(TextToVideoViewModel),
            typeof(TextToVideoPreviewHost),
            new PropertyMetadata(null, OnViewModelChanged));

    private enum DragMode
    {
        None,
        Move,
        ResizeSe,
    }

    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;
    private DragMode _dragMode;
    private Pointer? _overlayPointer;
    private Windows.Foundation.Point _grabOffsetNorm;
    private TextOverlaySegment? _subscribedEditor;

    public TextToVideoPreviewHost()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();

        TimelineSlider.PointerPressed += (_, _) => _sliderDragging = true;
        TimelineSlider.PointerReleased += (_, _) =>
        {
            _sliderDragging = false;
            SeekTo(ViewModel?.SliderValue ?? 0);
            if (ViewModel is not null && !ViewModel.IsPreviewPlaying)
            {
                ViewModel.SchedulePreviewRefresh(immediate: true);
            }
        };
        TimelineSlider.PointerCanceled += (_, _) => _sliderDragging = false;
        TimelineSlider.ValueChanged += (_, _) =>
        {
            if (_suppressSliderSeek || _sliderDragging || ViewModel is null)
            {
                return;
            }

            SeekTo(ViewModel.SliderValue);
        };

        SizeChanged += (_, _) => UpdateLayoutGhost();
        PreviewPresenter.SizeChanged += (_, _) => UpdateLayoutGhost();
        VideoSurface.SizeChanged += (_, _) => UpdateLayoutGhost();
        HitLayer.SizeChanged += (_, _) => UpdateLayoutGhost();
    }

    public TextToVideoViewModel? ViewModel
    {
        get => (TextToVideoViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextToVideoPreviewHost host)
        {
            return;
        }

        if (e.OldValue is TextToVideoViewModel oldVm)
        {
            oldVm.PropertyChanged -= host.ViewModel_OnPropertyChanged;
        }

        host.BindEditorSegment(null);

        if (e.NewValue is TextToVideoViewModel newVm)
        {
            newVm.PropertyChanged += host.ViewModel_OnPropertyChanged;
            host.BindEditorSegment(newVm.EditorSegment);
            host.ApplyPlaybackPresentation(newVm.IsPreviewPlaying);
            _ = host.LoadVideoAsync();
            _ = host.UpdatePreviewImageAsync();
            host.UpdateLayoutGhost();
        }
    }

    private void BindEditorSegment(TextOverlaySegment? seg)
    {
        if (_subscribedEditor is not null)
        {
            _subscribedEditor.PropertyChanged -= EditorSegment_OnPropertyChanged;
        }

        _subscribedEditor = seg;
        if (_subscribedEditor is not null)
        {
            _subscribedEditor.PropertyChanged += EditorSegment_OnPropertyChanged;
        }
    }

    private void EditorSegment_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextOverlaySegment.PosX)
            or nameof(TextOverlaySegment.PosY)
            or nameof(TextOverlaySegment.Fontsize)
            or nameof(TextOverlaySegment.Text)
            or nameof(TextOverlaySegment.LineSpacing)
            or nameof(TextOverlaySegment.BoxEnabled)
            or nameof(TextOverlaySegment.BoxBorder)
            or nameof(TextOverlaySegment.Color)
            or nameof(TextOverlaySegment.Bold)
            or nameof(TextOverlaySegment.Italic))
        {
            UpdateLayoutGhost();
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(TextToVideoViewModel.PreviewImageBytes))
        {
            if (!ViewModel.IsPreviewPlaying && _dragMode == DragMode.None)
            {
                _ = UpdatePreviewImageAsync();
            }
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.VideoPath)
                 || e.PropertyName == nameof(TextToVideoViewModel.HasVideo))
        {
            _ = LoadVideoAsync();
            UpdateLayoutGhost();
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.IsPreviewPlaying))
        {
            ApplyPlaybackPresentation(ViewModel.IsPreviewPlaying);
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.EditorSegment))
        {
            BindEditorSegment(ViewModel.EditorSegment);
            UpdateLayoutGhost();
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.SliderValue))
        {
            if (!_sliderDragging && !_suppressSliderSeek && !ViewModel.IsPreviewPlaying)
            {
                SeekTo(ViewModel.SliderValue);
            }

            if (ViewModel.IsPreviewPlaying)
            {
                UpdatePlayGhost(ViewModel.SliderValue);
            }
        }
        else if (e.PropertyName is nameof(TextToVideoViewModel.PreviewVideoWidth)
                 or nameof(TextToVideoViewModel.PreviewVideoHeight))
        {
            UpdateLayoutGhost();
        }
    }

    private void ApplyPlaybackPresentation(bool playing)
    {
        // Play = smooth MediaPlayer video + lightweight text ghost.
        // Pause = FFmpeg still frame with burned-in overlay (accurate fonts/styles) + layout handles.
        VideoSurface.Opacity = playing ? 1 : 0;
        PreviewPresenter.Opacity = playing ? 0 : 1;
        HitLayer.IsHitTestVisible = !playing;
        if (playing)
        {
            EndOverlayDrag(commit: false);
            LayoutGhost.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            UpdatePlayGhost(ViewModel?.SliderValue ?? 0);
        }
        else
        {
            PlayGhost.Visibility = Visibility.Collapsed;
            UpdateLayoutGhost();
        }
    }

    private MediaPlayerElement PreviewPlayer => VideoSurface.Player;

    private MediaPlayer? Player => PreviewPlayer.MediaPlayer;

    private async Task LoadVideoAsync()
    {
        StopPlayback();
        var path = ViewModel?.VideoPath;
        if (ViewModel is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewPlayer.Source = null;
            VideoSurface.ClearVideoDimensions();
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
            UpdateLayoutGhost();
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            await Task.Delay(300);
            SeekTo(ViewModel?.SliderValue ?? 0);
            UpdateLayoutGhost();
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"TTV-Vorschau: {ex.Message}");
        }
    }

    private async Task UpdatePreviewImageAsync()
    {
        if (_dragMode != DragMode.None || ViewModel?.IsPreviewPlaying == true)
        {
            return;
        }

        var bytes = ViewModel?.PreviewImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
            UpdateLayoutGhost();
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PreviewPresenter.SetContentDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
            PreviewPresenter.ImageSource = bitmap;
            UpdateLayoutGhost();
        }
        catch
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
        }
    }

    private void UpdateLayoutGhost()
    {
        var vm = ViewModel;
        if (vm is null
            || vm.IsPreviewPlaying
            || !vm.HasVideo
            || vm.PreviewVideoWidth <= 0
            || vm.PreviewVideoHeight <= 0
            || HitLayer.ActualWidth <= 1)
        {
            LayoutGhost.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            return;
        }

        var seg = vm.EditorSegment;
        var dw = HitLayer.ActualWidth;
        var dh = HitLayer.ActualHeight;
        var sx = dw / vm.PreviewVideoWidth;
        var sy = dh / vm.PreviewVideoHeight;
        var (wPx, hPx) = vm.EstimateEditorOverlayBox();
        // Prefer live TextBlock measure when wider — keeps the cyan frame outside the glyphs.
        var measured = MeasureOverlayBoxVideoPixels(seg);
        if (measured.Width > wPx)
        {
            wPx = measured.Width;
        }

        if (measured.Height > hPx)
        {
            hPx = measured.Height;
        }

        var left = seg.PosX * sx;
        var top = seg.PosY * sy;
        var width = Math.Max(8, wPx * sx);
        var height = Math.Max(8, hPx * sy);

        // Outline only — FFmpeg still already has the real text; drawing it again looks doubled.
        Canvas.SetLeft(LayoutGhost, left);
        Canvas.SetTop(LayoutGhost, top);
        LayoutGhost.Width = width;
        LayoutGhost.Height = height;
        LayoutGhost.Visibility = Visibility.Visible;

        Canvas.SetLeft(ResizeHandle, left + width - ResizeHandle.Width / 2);
        Canvas.SetTop(ResizeHandle, top + height - ResizeHandle.Height / 2);
        ResizeHandle.Visibility = Visibility.Visible;
    }

    /// <summary>Measure editor text in source pixels (WinUI font metrics; used to widen the layout frame).</summary>
    private static (int Width, int Height) MeasureOverlayBoxVideoPixels(TextOverlaySegment seg)
    {
        var lines = (seg.Text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length == 0)
        {
            lines = [""];
        }

        var fs = Math.Clamp(seg.Fontsize, 8, 400);
        var ls = Math.Clamp(seg.LineSpacing, -120, 120);
        var lineStep = Math.Max(4, fs + ls);
        FontFamily? family = null;
        if (!string.IsNullOrWhiteSpace(seg.FontPath) && File.Exists(seg.FontPath))
        {
            try
            {
                family = new FontFamily($"file:///{seg.FontPath.Replace('\\', '/')}");
            }
            catch
            {
                family = null;
            }
        }

        double maxW = fs;
        foreach (var line in lines)
        {
            var tb = new TextBlock
            {
                Text = string.IsNullOrEmpty(line) ? "\u00a0" : line,
                FontSize = fs,
                FontWeight = seg.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = seg.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                TextWrapping = TextWrapping.NoWrap,
            };
            if (family is not null)
            {
                tb.FontFamily = family;
            }

            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            maxW = Math.Max(maxW, tb.DesiredSize.Width);
        }

        var h = fs + (Math.Max(1, lines.Length) - 1) * lineStep + Math.Max(2, fs / 6);
        // Extra slack: some faces render wider in FFmpeg than WinUI reports
        var w = maxW + Math.Max(12, fs * 1.6);
        if (seg.Italic)
        {
            w += Math.Max(6, fs / 4.0);
        }

        if (seg.BoxEnabled)
        {
            var bb = Math.Max(0, seg.BoxBorder);
            w += bb * 2 + Math.Max(8, fs / 6.0);
            h += bb * 2 + Math.Max(6, fs / 8);
        }

        if (seg.Bold || seg.Italic || seg.Strike || !string.IsNullOrWhiteSpace(seg.FontPath))
        {
            w += Math.Max(12, fs / 3.0);
            h += Math.Max(6, fs / 8);
        }

        return ((int)Math.Ceiling(w), (int)Math.Ceiling((double)h));
    }

    private void HitLayer_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsPreviewPlaying || HitLayer.ActualWidth <= 1)
        {
            return;
        }

        Focus(FocusState.Programmatic);
        var pos = e.GetCurrentPoint(HitLayer).Position;
        var mode = HitTest(pos);
        if (mode == DragMode.None)
        {
            var nx = Clamp01(pos.X / HitLayer.ActualWidth);
            var ny = Clamp01(pos.Y / HitLayer.ActualHeight);
            ViewModel.BeginOverlayPositionDrag();
            ViewModel.ApplyMoveFromPreview(nx, ny);
            ViewModel.EndOverlayPositionDrag();
            UpdateLayoutGhost();
            e.Handled = true;
            return;
        }

        _dragMode = mode;
        _overlayPointer = e.Pointer;
        HitLayer.CapturePointer(e.Pointer);
        ViewModel.BeginOverlayPositionDrag();

        var left = ViewModel.EditorSegment.PosX / (double)ViewModel.PreviewVideoWidth;
        var top = ViewModel.EditorSegment.PosY / (double)ViewModel.PreviewVideoHeight;
        var nx0 = Clamp01(pos.X / HitLayer.ActualWidth);
        var ny0 = Clamp01(pos.Y / HitLayer.ActualHeight);
        _grabOffsetNorm = new Windows.Foundation.Point(nx0 - left, ny0 - top);

        UpdateLayoutGhost();
        e.Handled = true;
    }

    private void HitLayer_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None || ViewModel is null || _overlayPointer is null || !e.Pointer.Equals(_overlayPointer))
        {
            UpdateCursor(e.GetCurrentPoint(HitLayer).Position);
            return;
        }

        var pos = e.GetCurrentPoint(HitLayer).Position;
        var nx = Clamp01(pos.X / HitLayer.ActualWidth);
        var ny = Clamp01(pos.Y / HitLayer.ActualHeight);

        if (_dragMode == DragMode.Move)
        {
            ViewModel.ApplyMoveFromPreview(nx - _grabOffsetNorm.X, ny - _grabOffsetNorm.Y);
        }
        else if (_dragMode == DragMode.ResizeSe)
        {
            ViewModel.ApplyResizeFromPreview(nx, ny);
        }

        UpdateLayoutGhost();
        e.Handled = true;
    }

    private void HitLayer_OnPointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndOverlayDrag(commit: true);

    private void HitLayer_OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndOverlayDrag(commit: false);

    private void HitLayer_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            EndOverlayDrag(commit: true);
        }
    }

    private void EndOverlayDrag(bool commit)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }

        if (_overlayPointer is not null)
        {
            try
            {
                HitLayer.ReleasePointerCapture(_overlayPointer);
            }
            catch
            {
                // already released
            }

            _overlayPointer = null;
        }

        _dragMode = DragMode.None;

        if (ViewModel is null)
        {
            return;
        }

        if (commit)
        {
            ViewModel.EndOverlayPositionDrag();
        }
        else
        {
            ViewModel.CancelOverlayPositionDrag();
        }

        UpdateLayoutGhost();
    }

    private DragMode HitTest(Windows.Foundation.Point pos)
    {
        if (LayoutGhost.Visibility != Visibility.Visible)
        {
            return DragMode.None;
        }

        var left = Canvas.GetLeft(LayoutGhost);
        var top = Canvas.GetTop(LayoutGhost);
        var right = left + LayoutGhost.Width;
        var bottom = top + LayoutGhost.Height;
        const double handlePad = 10;

        if (pos.X >= right - handlePad && pos.X <= right + handlePad
            && pos.Y >= bottom - handlePad && pos.Y <= bottom + handlePad)
        {
            return DragMode.ResizeSe;
        }

        if (pos.X >= left && pos.X <= right && pos.Y >= top && pos.Y <= bottom)
        {
            return DragMode.Move;
        }

        return DragMode.None;
    }

    private void UpdateCursor(Windows.Foundation.Point pos)
    {
        if (ViewModel?.IsPreviewPlaying == true)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            return;
        }

        var mode = HitTest(pos);
        ProtectedCursor = mode switch
        {
            DragMode.ResizeSe => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast),
            DragMode.Move => InputSystemCursor.Create(InputSystemCursorShape.SizeAll),
            _ => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
        };
    }

    private void RootHost_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasVideo || ViewModel.IsPreviewPlaying)
        {
            return;
        }

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = ctrl ? 10 : 1;

        if (shift && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            var dFs = e.Key == VirtualKey.Up ? step : -step;
            ViewModel.NudgeOverlay(0, 0, dFs);
            UpdateLayoutGhost();
            e.Handled = true;
            return;
        }

        var dx = e.Key switch
        {
            VirtualKey.Left => -step,
            VirtualKey.Right => step,
            _ => 0,
        };
        var dy = e.Key switch
        {
            VirtualKey.Up => -step,
            VirtualKey.Down => step,
            _ => 0,
        };
        if (dx == 0 && dy == 0)
        {
            return;
        }

        ViewModel.NudgeOverlay(dx, dy, 0);
        UpdateLayoutGhost();
        e.Handled = true;
    }

    private void UpdatePlayGhost(double timeSec)
    {
        if (ViewModel is null || !ViewModel.IsPreviewPlaying)
        {
            PlayGhost.Visibility = Visibility.Collapsed;
            return;
        }

        var seg = FindVisibleOverlay(timeSec);
        var vw = ViewModel.PreviewVideoWidth;
        var vh = ViewModel.PreviewVideoHeight;
        var dw = HitLayer.ActualWidth > 0 ? HitLayer.ActualWidth : VideoSurface.ActualWidth;
        var dh = HitLayer.ActualHeight > 0 ? HitLayer.ActualHeight : VideoSurface.ActualHeight;
        if (seg is null || vw <= 0 || vh <= 0 || dw <= 0 || dh <= 0)
        {
            PlayGhost.Visibility = Visibility.Collapsed;
            return;
        }

        PlayGhostText.Text = seg.Text;
        PlayGhostText.Foreground = new SolidColorBrush(ParseHexColor(seg.Color));
        var scale = dh / vh;
        PlayGhostText.FontSize = Math.Clamp(seg.Fontsize * scale, 10, 96);
        PlayGhostText.FontWeight = seg.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        PlayGhostText.FontStyle = seg.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

        Canvas.SetLeft(PlayGhost, Math.Clamp(seg.PosX * (dw / vw), 0, Math.Max(0, dw - 8)));
        Canvas.SetTop(PlayGhost, Math.Clamp(seg.PosY * (dh / vh), 0, Math.Max(0, dh - 8)));
        PlayGhost.Visibility = Visibility.Visible;
    }

    private TextOverlaySegment? FindVisibleOverlay(double timeSec)
    {
        if (ViewModel is null)
        {
            return null;
        }

        if (IsVisibleAt(ViewModel.EditorSegment, timeSec)
            && !string.IsNullOrWhiteSpace(ViewModel.EditorSegment.Text))
        {
            return ViewModel.EditorSegment;
        }

        foreach (var seg in ViewModel.Segments)
        {
            if (!string.IsNullOrWhiteSpace(seg.Text) && IsVisibleAt(seg, timeSec))
            {
                return seg;
            }
        }

        return null;
    }

    private static bool IsVisibleAt(TextOverlaySegment seg, double timeSec)
    {
        var hasFrom = TimeFieldHelper.TryParseSeconds(seg.From, out var fromSec);
        var hasTo = TimeFieldHelper.TryParseSeconds(seg.To, out var toSec);
        if (!hasFrom && !hasTo)
        {
            return true;
        }

        if (hasFrom && hasTo)
        {
            var a = Math.Min(fromSec, toSec);
            var b = Math.Max(fromSec, toSec);
            return timeSec >= a && timeSec <= b;
        }

        if (hasFrom)
        {
            return timeSec >= fromSec;
        }

        return timeSec <= toSec;
    }

    private static Color ParseHexColor(string hex)
    {
        hex = (hex ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return Microsoft.UI.Colors.White;
        }

        try
        {
            return Color.FromArgb(
                255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        catch
        {
            return Microsoft.UI.Colors.White;
        }
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.CancelPendingPreview();
        Player?.Play();
        ViewModel.IsPreviewPlaying = true;
        ApplyPlaybackPresentation(true);
        _positionTimer.Start();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        Player?.Pause();
        if (ViewModel is not null)
        {
            ViewModel.IsPreviewPlaying = false;
        }

        ApplyPlaybackPresentation(false);
        _positionTimer.Stop();
        ViewModel?.SchedulePreviewRefresh(immediate: true);
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(0);
        ApplyPlaybackPresentation(false);
        ViewModel?.SchedulePreviewRefresh(immediate: true);
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        if (ViewModel is not null)
        {
            ViewModel.IsPreviewPlaying = false;
        }

        ApplyPlaybackPresentation(false);

        if (Player is null)
        {
            return;
        }

        Player.Pause();
        Player.PlaybackSession.Position = TimeSpan.Zero;
    }

    private void SeekTo(double seconds)
    {
        if (ViewModel is null)
        {
            return;
        }

        var clamped = Math.Clamp(seconds, 0, ViewModel.SliderMaximum);
        if (Player is not null)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(clamped);
        }

        _suppressSliderSeek = true;
        ViewModel.SetPreviewPosition(clamped);
        _suppressSliderSeek = false;
    }

    private void SyncPositionFromPlayer()
    {
        if (_sliderDragging || Player is null || ViewModel is null)
        {
            return;
        }

        var sec = Player.PlaybackSession.Position.TotalSeconds;
        _suppressSliderSeek = true;
        ViewModel.SetPreviewPosition(sec);
        _suppressSliderSeek = false;
        UpdatePlayGhost(sec);

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            _positionTimer.Stop();
            ViewModel.IsPreviewPlaying = false;
            ApplyPlaybackPresentation(false);
            ViewModel.SchedulePreviewRefresh(immediate: true);
        }
    }
}
