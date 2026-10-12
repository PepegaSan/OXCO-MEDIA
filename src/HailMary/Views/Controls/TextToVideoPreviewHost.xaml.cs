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
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;

namespace HailMary.Views.Controls;

/// <summary>
/// Video preview with live text overlays. The overlays are the exact sprites the FFmpeg export
/// burns in, drawn over the paused frame or the playing video — so moving, restyling and
/// animating text is visible immediately and matches the exported file.
/// </summary>
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

    private const double SnapDistanceDip = 6;
    private const double HandleHitPadDip = 10;

    private readonly DispatcherTimer _positionTimer;
    private readonly List<Image> _spriteImages = [];
    private readonly Dictionary<string, BitmapImage> _bitmaps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bitmapsLoading = new(StringComparer.Ordinal);
    private readonly List<(PreviewOverlayItem Item, Rect Box)> _hitBoxes = [];
    private bool _sliderDragging;
    private bool _suppressSliderSeek;
    private DragMode _dragMode;
    private Pointer? _overlayPointer;
    private Point _grabOffsetNorm;
    private TextOverlaySegment? _subscribedEditor;

    public TextToVideoPreviewHost()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();
        RangeTimeline.SeekRequested += (_, seconds) =>
        {
            if (ViewModel is not null)
            {
                ViewModel.SliderValue = seconds;
                SeekTo(seconds);
            }
        };

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

        SizeChanged += (_, _) => RenderOverlays();
        PreviewPresenter.SizeChanged += (_, _) => RenderOverlays();
        VideoSurface.SizeChanged += (_, _) => RenderOverlays();
        HitLayer.SizeChanged += (_, _) => RenderOverlays();
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
            oldVm.PreviewOverlaysChanged -= host.RenderOverlays;
        }

        host.BindEditorSegment(null);

        if (e.NewValue is TextToVideoViewModel newVm)
        {
            newVm.PropertyChanged += host.ViewModel_OnPropertyChanged;
            newVm.PreviewOverlaysChanged += host.RenderOverlays;
            host.BindEditorSegment(newVm.EditorSegment);
            host.ApplyPlaybackPresentation(newVm.IsPreviewPlaying);
            _ = host.LoadVideoAsync();
            _ = host.UpdatePreviewImageAsync();
            host.RenderOverlays();
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

    // Position, size, timing and animation are applied client-side — redraw instantly.
    private void EditorSegment_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RenderOverlays();

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(TextToVideoViewModel.PreviewImageBytes):
                _ = UpdatePreviewImageAsync();
                break;
            case nameof(TextToVideoViewModel.VideoPath):
            case nameof(TextToVideoViewModel.HasVideo):
                _ = LoadVideoAsync();
                RenderOverlays();
                break;
            case nameof(TextToVideoViewModel.IsPreviewPlaying):
                ApplyPlaybackPresentation(ViewModel.IsPreviewPlaying);
                break;
            case nameof(TextToVideoViewModel.EditorSegment):
                BindEditorSegment(ViewModel.EditorSegment);
                RenderOverlays();
                break;
            case nameof(TextToVideoViewModel.SliderValue):
                if (!_sliderDragging && !_suppressSliderSeek && !ViewModel.IsPreviewPlaying)
                {
                    SeekTo(ViewModel.SliderValue);
                }

                RenderOverlays();
                break;
            case nameof(TextToVideoViewModel.TimelineRanges):
            case nameof(TextToVideoViewModel.WaveformPeaks):
                RangeTimeline.Spans = ViewModel.TimelineRanges;
                RangeTimeline.Peaks = ViewModel.WaveformPeaks;
                break;
            case nameof(TextToVideoViewModel.PreviewVideoWidth):
            case nameof(TextToVideoViewModel.PreviewVideoHeight):
                RenderOverlays();
                break;
        }
    }

    private void ApplyPlaybackPresentation(bool playing)
    {
        // Play = smooth MediaPlayer video; pause = decoded still frame. Text sprites stay on top in both.
        VideoSurface.Opacity = playing ? 1 : 0;
        PreviewPresenter.Opacity = playing ? 0 : 1;
        HitLayer.IsHitTestVisible = !playing;
        PlayPauseIcon.Glyph = playing ? "" : "";
        var playLabel = Loc.T(playing ? "common.pause" : "common.play");
        ToolTipService.SetToolTip(BtnPlayPause, playLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BtnPlayPause, playLabel);
        if (playing)
        {
            EndOverlayDrag(commit: false);
        }

        RenderOverlays();
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
            RenderOverlays();
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            await Task.Delay(300);
            SeekTo(ViewModel?.SliderValue ?? 0);
            RenderOverlays();
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"TTV-Vorschau: {ex.Message}");
        }
    }

    private async Task UpdatePreviewImageAsync()
    {
        var bytes = ViewModel?.PreviewImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
            RenderOverlays();
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
            RenderOverlays();
        }
        catch
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
        }
    }

    private double CurrentTime =>
        ViewModel is { IsPreviewPlaying: true } && Player is not null
            ? Player.PlaybackSession.Position.TotalSeconds
            : ViewModel?.SliderValue ?? 0;

    private void RenderOverlays() => RenderOverlays(CurrentTime);

    private void RenderOverlays(double timeSec)
    {
        _hitBoxes.Clear();
        var vm = ViewModel;
        var dw = HitLayer.ActualWidth;
        var dh = HitLayer.ActualHeight;
        if (vm is null || !vm.HasVideo || vm.PreviewVideoWidth <= 0 || vm.PreviewVideoHeight <= 0 || dw <= 1 || dh <= 1)
        {
            foreach (var img in _spriteImages)
            {
                img.Visibility = Visibility.Collapsed;
            }

            HideEditorChrome();
            return;
        }

        var sx = dw / vm.PreviewVideoWidth;
        var sy = dh / vm.PreviewVideoHeight;
        SpriteLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, dw, dh) };

        var items = vm.GetPreviewOverlays();
        while (_spriteImages.Count < items.Count)
        {
            var img = new Image
            {
                Stretch = Stretch.Fill,
                RenderTransform = new CompositeTransform(),
                Visibility = Visibility.Collapsed,
            };
            _spriteImages.Add(img);
            SpriteLayer.Children.Add(img);
        }

        Rect? editorBox = null;
        for (var i = 0; i < _spriteImages.Count; i++)
        {
            var img = _spriteImages[i];
            if (i >= items.Count)
            {
                img.Visibility = Visibility.Collapsed;
                continue;
            }

            var item = items[i];
            var seg = item.Segment;
            var sprite = item.Sprite;
            var (cw, ch) = ContentSize(seg, sprite);
            var box = new Rect(seg.PosX * sx, seg.PosY * sy, Math.Max(4, cw * sx), Math.Max(4, ch * sy));
            if (item.IsEditor)
            {
                editorBox = box;
            }

            var state = TextOverlayAnimation.Evaluate(seg, timeSec);
            if (item.IsEditor && state.Visible && !vm.IsPreviewPlaying)
            {
                // While editing, show the selected text fully; its animation plays back on Play.
                state = OverlayAnimationState.Static;
            }

            var bitmap = sprite is null ? null : GetBitmap(sprite);
            if (sprite is null || bitmap is null || !state.Visible || state.Opacity <= 0.001)
            {
                img.Visibility = Visibility.Collapsed;
                continue;
            }

            // A stale sprite (re-render pending) is stretched onto the current box so resizing feels live.
            var kx = cw / (double)sprite.ContentWidth;
            var ky = ch / (double)sprite.ContentHeight;
            img.Source = bitmap;
            img.Width = Math.Max(1, sprite.Width * kx * sx);
            img.Height = Math.Max(1, sprite.Height * ky * sy);
            Canvas.SetLeft(img, (seg.PosX - sprite.OriginX * kx) * sx);
            Canvas.SetTop(img, (seg.PosY - sprite.OriginY * ky) * sy);
            img.Opacity = state.Opacity;
            var transform = (CompositeTransform)img.RenderTransform;
            transform.ScaleX = state.Scale;
            transform.ScaleY = state.Scale;
            transform.CenterX = img.Width / 2;
            transform.CenterY = img.Height / 2;
            transform.TranslateY = state.OffsetY * sy;
            img.Visibility = Visibility.Visible;
            _hitBoxes.Add((item, box));
        }

        UpdateEditorChrome(editorBox);
    }

    private (int Width, int Height) ContentSize(TextOverlaySegment seg, TextOverlaySprite? sprite)
    {
        if (seg.LayoutWidth > 0 && seg.LayoutHeight > 0)
        {
            return (seg.LayoutWidth, seg.LayoutHeight);
        }

        if (sprite is not null)
        {
            return (sprite.ContentWidth, sprite.ContentHeight);
        }

        return ReferenceEquals(seg, ViewModel?.EditorSegment) ? ViewModel!.EditorOverlayBox() : (0, 0);
    }

    private void UpdateEditorChrome(Rect? editorBox)
    {
        if (ViewModel is null || ViewModel.IsPreviewPlaying)
        {
            HideEditorChrome();
            return;
        }

        // New, still empty draft: show where its text will appear.
        var box = editorBox ?? EditorFallbackBox();
        if (box is null)
        {
            HideEditorChrome();
            return;
        }

        var r = box.Value;
        Canvas.SetLeft(LayoutGhost, r.X);
        Canvas.SetTop(LayoutGhost, r.Y);
        LayoutGhost.Width = r.Width;
        LayoutGhost.Height = r.Height;
        LayoutGhost.Visibility = Visibility.Visible;

        // Keep the handle reachable even when the box fills the video.
        Canvas.SetLeft(
            ResizeHandle,
            Math.Clamp(r.Right - ResizeHandle.Width / 2, 0, Math.Max(0, HitLayer.ActualWidth - ResizeHandle.Width)));
        Canvas.SetTop(
            ResizeHandle,
            Math.Clamp(r.Bottom - ResizeHandle.Height / 2, 0, Math.Max(0, HitLayer.ActualHeight - ResizeHandle.Height)));
        ResizeHandle.Visibility = Visibility.Visible;
    }

    private Rect? EditorFallbackBox()
    {
        var vm = ViewModel;
        if (vm is null || vm.PreviewVideoWidth <= 0 || HitLayer.ActualWidth <= 1)
        {
            return null;
        }

        var sx = HitLayer.ActualWidth / vm.PreviewVideoWidth;
        var sy = HitLayer.ActualHeight / vm.PreviewVideoHeight;
        var (w, h) = vm.EditorOverlayBox();
        return new Rect(vm.EditorSegment.PosX * sx, vm.EditorSegment.PosY * sy, Math.Max(8, w * sx), Math.Max(8, h * sy));
    }

    private void HideEditorChrome()
    {
        LayoutGhost.Visibility = Visibility.Collapsed;
        ResizeHandle.Visibility = Visibility.Collapsed;
        HoverGhost.Visibility = Visibility.Collapsed;
        GuideVertical.Visibility = Visibility.Collapsed;
        GuideHorizontal.Visibility = Visibility.Collapsed;
    }

    private BitmapImage? GetBitmap(TextOverlaySprite sprite)
    {
        if (_bitmaps.TryGetValue(sprite.Key, out var cached))
        {
            return cached;
        }

        if (_bitmapsLoading.Add(sprite.Key))
        {
            _ = LoadBitmapAsync(sprite);
        }

        return null;
    }

    private async Task LoadBitmapAsync(TextOverlaySprite sprite)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(sprite.PngBytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (_bitmaps.Count > 200)
            {
                _bitmaps.Clear();
            }

            _bitmaps[sprite.Key] = bitmap;
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"TTV-Text-Sprite: {ex.Message}");
        }
        finally
        {
            _bitmapsLoading.Remove(sprite.Key);
        }

        RenderOverlays();
    }

    private void HitLayer_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || vm.IsPreviewPlaying || HitLayer.ActualWidth <= 1 || vm.PreviewVideoWidth <= 0)
        {
            return;
        }

        Focus(FocusState.Programmatic);
        var pos = e.GetCurrentPoint(HitLayer).Position;
        var mode = HitTestEditor(pos);
        if (mode == DragMode.None)
        {
            // Clicking another text selects it for editing and lets you drag it right away.
            var other = HitTestOther(pos);
            if (other is { SegmentIndex: >= 0 })
            {
                vm.SelectSegmentFromPreview(other.SegmentIndex);
                mode = DragMode.Move;
            }
        }

        if (mode == DragMode.None)
        {
            vm.BeginOverlayPositionDrag();
            vm.ApplyMoveFromPreview(Clamp01(pos.X / HitLayer.ActualWidth), Clamp01(pos.Y / HitLayer.ActualHeight));
            vm.EndOverlayPositionDrag();
            e.Handled = true;
            return;
        }

        _dragMode = mode;
        _overlayPointer = e.Pointer;
        HitLayer.CapturePointer(e.Pointer);
        HoverGhost.Visibility = Visibility.Collapsed;
        vm.BeginOverlayPositionDrag();

        var left = vm.EditorSegment.PosX / (double)vm.PreviewVideoWidth;
        var top = vm.EditorSegment.PosY / (double)vm.PreviewVideoHeight;
        _grabOffsetNorm = new Point(
            Clamp01(pos.X / HitLayer.ActualWidth) - left,
            Clamp01(pos.Y / HitLayer.ActualHeight) - top);

        RenderOverlays();
        e.Handled = true;
    }

    private void HitLayer_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(HitLayer).Position;
        var vm = ViewModel;
        if (_dragMode == DragMode.None || vm is null || _overlayPointer is null || !e.Pointer.Equals(_overlayPointer))
        {
            UpdateHover(pos);
            return;
        }

        var nx = Clamp01(pos.X / HitLayer.ActualWidth);
        var ny = Clamp01(pos.Y / HitLayer.ActualHeight);

        if (_dragMode == DragMode.Move)
        {
            var (leftN, topN) = SnapToCenter(nx - _grabOffsetNorm.X, ny - _grabOffsetNorm.Y);
            vm.ApplyMoveFromPreview(leftN, topN);
        }
        else if (_dragMode == DragMode.ResizeSe)
        {
            vm.ApplyResizeFromPreview(nx, ny);
        }

        e.Handled = true;
    }

    /// <summary>Snap the box center to the video center lines and show a guide while snapped.</summary>
    private (double Left, double Top) SnapToCenter(double leftN, double topN)
    {
        var vm = ViewModel!;
        var (w, h) = vm.EditorOverlayBox();
        var wN = w / (double)vm.PreviewVideoWidth;
        var hN = h / (double)vm.PreviewVideoHeight;
        var tolX = SnapDistanceDip / HitLayer.ActualWidth;
        var tolY = SnapDistanceDip / HitLayer.ActualHeight;

        var snapX = Math.Abs(leftN + wN / 2 - 0.5) <= tolX;
        var snapY = Math.Abs(topN + hN / 2 - 0.5) <= tolY;
        if (snapX)
        {
            leftN = 0.5 - wN / 2;
        }

        if (snapY)
        {
            topN = 0.5 - hN / 2;
        }

        GuideVertical.Height = HitLayer.ActualHeight;
        Canvas.SetLeft(GuideVertical, Math.Round(HitLayer.ActualWidth / 2));
        GuideVertical.Visibility = snapX ? Visibility.Visible : Visibility.Collapsed;
        GuideHorizontal.Width = HitLayer.ActualWidth;
        Canvas.SetTop(GuideHorizontal, Math.Round(HitLayer.ActualHeight / 2));
        GuideHorizontal.Visibility = snapY ? Visibility.Visible : Visibility.Collapsed;
        return (leftN, topN);
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

    private void HitLayer_OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            HoverGhost.Visibility = Visibility.Collapsed;
        }
    }

    private void EndOverlayDrag(bool commit)
    {
        GuideVertical.Visibility = Visibility.Collapsed;
        GuideHorizontal.Visibility = Visibility.Collapsed;
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

        RenderOverlays();
    }

    private DragMode HitTestEditor(Point pos)
    {
        if (LayoutGhost.Visibility != Visibility.Visible)
        {
            return DragMode.None;
        }

        var left = Canvas.GetLeft(LayoutGhost);
        var top = Canvas.GetTop(LayoutGhost);
        var right = left + LayoutGhost.Width;
        var bottom = top + LayoutGhost.Height;

        if (pos.X >= right - HandleHitPadDip && pos.X <= right + HandleHitPadDip
            && pos.Y >= bottom - HandleHitPadDip && pos.Y <= bottom + HandleHitPadDip)
        {
            return DragMode.ResizeSe;
        }

        if (pos.X >= left && pos.X <= right && pos.Y >= top && pos.Y <= bottom)
        {
            return DragMode.Move;
        }

        return DragMode.None;
    }

    /// <summary>Topmost visible overlay under the pointer that is not the one being edited.</summary>
    private PreviewOverlayItem? HitTestOther(Point pos)
    {
        for (var i = _hitBoxes.Count - 1; i >= 0; i--)
        {
            var (item, box) = _hitBoxes[i];
            if (!item.IsEditor && box.Contains(pos))
            {
                return item;
            }
        }

        return null;
    }

    private void UpdateHover(Point pos)
    {
        if (ViewModel?.IsPreviewPlaying != false)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            HoverGhost.Visibility = Visibility.Collapsed;
            return;
        }

        var mode = HitTestEditor(pos);
        var other = mode == DragMode.None ? HitTestOther(pos) : null;
        if (other is not null)
        {
            var box = _hitBoxes.First(h => ReferenceEquals(h.Item, other)).Box;
            Canvas.SetLeft(HoverGhost, box.X);
            Canvas.SetTop(HoverGhost, box.Y);
            HoverGhost.Width = box.Width;
            HoverGhost.Height = box.Height;
            HoverGhost.Visibility = Visibility.Visible;
        }
        else
        {
            HoverGhost.Visibility = Visibility.Collapsed;
        }

        ProtectedCursor = mode switch
        {
            DragMode.ResizeSe => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast),
            DragMode.Move => InputSystemCursor.Create(InputSystemCursorShape.SizeAll),
            _ when other is not null => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            _ => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
        };
    }

    private void RootHost_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasVideo)
        {
            return;
        }

        if (e.Key == VirtualKey.Space)
        {
            TogglePlayback();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsPreviewPlaying)
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
        e.Handled = true;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.IsPreviewPlaying)
        {
            Player?.Pause();
            ViewModel.IsPreviewPlaying = false;
            _positionTimer.Stop();
            ApplyPlaybackPresentation(false);
            ViewModel.SchedulePreviewRefresh(immediate: true);
            return;
        }

        ViewModel.CancelPendingPreview();
        Player?.Play();
        ViewModel.IsPreviewPlaying = true;
        ApplyPlaybackPresentation(true);
        _positionTimer.Start();
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
        RenderOverlays(sec);

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            _positionTimer.Stop();
            ViewModel.IsPreviewPlaying = false;
            ApplyPlaybackPresentation(false);
            ViewModel.SchedulePreviewRefresh(immediate: true);
        }
    }
}
