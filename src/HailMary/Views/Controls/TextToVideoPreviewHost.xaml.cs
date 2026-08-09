using System.Runtime.InteropServices.WindowsRuntime;
using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
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

    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;
    private bool _overlayDragging;
    private Pointer? _overlayPointer;

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

        if (e.NewValue is TextToVideoViewModel newVm)
        {
            newVm.PropertyChanged += host.ViewModel_OnPropertyChanged;
            host.ApplyPlaybackPresentation(newVm.IsPreviewPlaying);
            _ = host.LoadVideoAsync();
            _ = host.UpdatePreviewImageAsync();
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
            if (!ViewModel.IsPreviewPlaying)
            {
                _ = UpdatePreviewImageAsync();
            }
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.VideoPath)
                 || e.PropertyName == nameof(TextToVideoViewModel.HasVideo))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.IsPreviewPlaying))
        {
            ApplyPlaybackPresentation(ViewModel.IsPreviewPlaying);
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
    }

    private void ApplyPlaybackPresentation(bool playing)
    {
        // Play = smooth MediaPlayer video + lightweight text ghost.
        // Pause = FFmpeg still frame with burned-in overlay (accurate fonts/styles).
        VideoSurface.Opacity = playing ? 1 : 0;
        PreviewPresenter.Opacity = playing ? 0 : 1;
        HitLayer.IsHitTestVisible = !playing;
        if (playing)
        {
            EndOverlayDrag(commit: false);
            UpdatePlayGhost(ViewModel?.SliderValue ?? 0);
        }
        else
        {
            PlayGhost.Visibility = Visibility.Collapsed;
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
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            await Task.Delay(300);
            SeekTo(ViewModel?.SliderValue ?? 0);
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"TTV-Vorschau: {ex.Message}");
        }
    }

    private async Task UpdatePreviewImageAsync()
    {
        if (_overlayDragging || ViewModel?.IsPreviewPlaying == true)
        {
            return;
        }

        var bytes = ViewModel?.PreviewImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
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
        }
        catch
        {
            PreviewPresenter.ImageSource = null;
        }
    }

    private void HitLayer_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsPreviewPlaying || HitLayer.ActualWidth <= 0 || HitLayer.ActualHeight <= 0)
        {
            return;
        }

        HitLayer.CapturePointer(e.Pointer);
        _overlayPointer = e.Pointer;
        _overlayDragging = true;
        ViewModel.BeginOverlayPositionDrag();
        ApplyPointerPosition(e);
        ShowDragGhost(e.GetCurrentPoint(HitLayer).Position);
        e.Handled = true;
    }

    private void HitLayer_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_overlayDragging || ViewModel is null)
        {
            return;
        }

        ApplyPointerPosition(e);
        ShowDragGhost(e.GetCurrentPoint(HitLayer).Position);
        e.Handled = true;
    }

    private void HitLayer_OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_overlayDragging)
        {
            return;
        }

        ApplyPointerPosition(e);
        EndOverlayDrag(commit: true);
        e.Handled = true;
    }

    private void HitLayer_OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndOverlayDrag(commit: false);

    private void HitLayer_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayDragging)
        {
            EndOverlayDrag(commit: true);
        }
    }

    private void ApplyPointerPosition(PointerRoutedEventArgs e)
    {
        if (ViewModel is null || HitLayer.ActualWidth <= 0 || HitLayer.ActualHeight <= 0)
        {
            return;
        }

        var pos = e.GetCurrentPoint(HitLayer).Position;
        var nx = Math.Clamp(pos.X / HitLayer.ActualWidth, 0, 1);
        var ny = Math.Clamp(pos.Y / HitLayer.ActualHeight, 0, 1);
        ViewModel.SetPositionFromPreviewClick(nx, ny, deferPreviewRefresh: true);
    }

    private void ShowDragGhost(Windows.Foundation.Point localPos)
    {
        if (ViewModel is null)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(ViewModel.EditorSegment.Text)
            ? "•"
            : ViewModel.EditorSegment.Text.Replace('\n', ' ').Trim();
        if (text.Length > 48)
        {
            text = text[..45] + "…";
        }

        DragGhostText.Text = text;
        DragGhostText.FontSize = Math.Clamp(ViewModel.EditorSegment.Fontsize * 0.35, 12, 42);
        DragGhost.Visibility = Visibility.Visible;

        var x = Math.Clamp(localPos.X, 0, Math.Max(0, HitLayer.ActualWidth - 8));
        var y = Math.Clamp(localPos.Y, 0, Math.Max(0, HitLayer.ActualHeight - 8));
        Canvas.SetLeft(DragGhost, x);
        Canvas.SetTop(DragGhost, y);
    }

    private void EndOverlayDrag(bool commit)
    {
        if (!_overlayDragging && DragGhost.Visibility != Visibility.Visible)
        {
            return;
        }

        _overlayDragging = false;
        DragGhost.Visibility = Visibility.Collapsed;

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

        if (commit)
        {
            ViewModel?.EndOverlayPositionDrag();
        }
        else
        {
            ViewModel?.CancelOverlayPositionDrag();
        }
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

        // Prefer the segment currently being edited when it is visible.
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

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        // Cancel pending FFmpeg preview work so play stays smooth.
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
