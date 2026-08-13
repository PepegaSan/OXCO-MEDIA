using System.Runtime.InteropServices.WindowsRuntime;
using HailMary.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class MediaOverlayPreviewHost : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MediaOverlayViewModel),
            typeof(MediaOverlayPreviewHost),
            new PropertyMetadata(null, OnViewModelChanged));

    private enum DragMode
    {
        None,
        Move,
        ResizeSe,
    }

    private DragMode _dragMode;
    private Pointer? _pointer;
    private bool _sliderDragging;
    private Windows.Foundation.Point _grabOffsetNorm;

    public MediaOverlayPreviewHost()
    {
        InitializeComponent();
        RangeTimeline.SeekRequested += (_, seconds) =>
        {
            if (ViewModel is not null)
            {
                ViewModel.SliderValue = seconds;
            }
        };
        TimelineSlider.PointerPressed += (_, _) => _sliderDragging = true;
        TimelineSlider.PointerReleased += (_, _) =>
        {
            _sliderDragging = false;
            ViewModel?.SchedulePreviewRefresh(immediate: true);
        };
        TimelineSlider.PointerCanceled += (_, _) => _sliderDragging = false;
        SizeChanged += (_, _) => UpdateGhost();
        PreviewPresenter.SizeChanged += (_, _) => UpdateGhost();
    }

    public MediaOverlayViewModel? ViewModel
    {
        get => (MediaOverlayViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MediaOverlayPreviewHost host)
        {
            return;
        }

        if (e.OldValue is MediaOverlayViewModel oldVm)
        {
            oldVm.PropertyChanged -= host.ViewModel_OnPropertyChanged;
        }

        if (e.NewValue is MediaOverlayViewModel newVm)
        {
            newVm.PropertyChanged += host.ViewModel_OnPropertyChanged;
            _ = host.UpdatePreviewImageAsync();
            host.UpdateGhost();
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MediaOverlayViewModel.PreviewImageBytes):
                if (_dragMode == DragMode.None)
                {
                    _ = UpdatePreviewImageAsync();
                }

                break;
            case nameof(MediaOverlayViewModel.PosX):
            case nameof(MediaOverlayViewModel.PosY):
            case nameof(MediaOverlayViewModel.WidthPercent):
            case nameof(MediaOverlayViewModel.HasVideo):
            case nameof(MediaOverlayViewModel.OverlayAspect):
            case nameof(MediaOverlayViewModel.LayoutSummary):
                UpdateGhost();
                break;
            case nameof(MediaOverlayViewModel.TimelineRanges):
            case nameof(MediaOverlayViewModel.WaveformPeaks):
                RangeTimeline.Spans = ViewModel.TimelineRanges;
                RangeTimeline.Peaks = ViewModel.WaveformPeaks;
                break;
        }
    }

    private async Task UpdatePreviewImageAsync()
    {
        var bytes = ViewModel?.PreviewImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
            UpdateGhost();
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
            UpdateGhost();
        }
        catch
        {
            PreviewPresenter.ImageSource = null;
            PreviewPresenter.ClearContentDimensions();
        }
    }

    private void UpdateGhost()
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasVideo || vm.VideoPixelWidth <= 0 || HitLayer.ActualWidth <= 1)
        {
            OverlayGhost.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            return;
        }

        var dw = HitLayer.ActualWidth;
        var dh = HitLayer.ActualHeight;
        var sx = dw / vm.VideoPixelWidth;
        var sy = dh / vm.VideoPixelHeight;

        var wPx = vm.OverlayWidthPixels();
        var hPx = vm.OverlayHeightPixels(wPx);
        var left = vm.PosX * sx;
        var top = vm.PosY * sy;
        var width = Math.Max(8, wPx * sx);
        var height = Math.Max(8, hPx * sy);

        Canvas.SetLeft(OverlayGhost, left);
        Canvas.SetTop(OverlayGhost, top);
        OverlayGhost.Width = width;
        OverlayGhost.Height = height;
        OverlayGhost.Visibility = Visibility.Visible;

        Canvas.SetLeft(ResizeHandle, left + width - ResizeHandle.Width / 2);
        Canvas.SetTop(ResizeHandle, top + height - ResizeHandle.Height / 2);
        ResizeHandle.Visibility = Visibility.Visible;
    }

    private void HitLayer_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasVideo || HitLayer.ActualWidth <= 1)
        {
            return;
        }

        Focus(FocusState.Programmatic);
        var pos = e.GetCurrentPoint(HitLayer).Position;
        var mode = HitTest(pos);
        if (mode == DragMode.None)
        {
            // Click empty: place top-left at pointer (precise placement)
            var nx = Clamp01(pos.X / HitLayer.ActualWidth);
            var ny = Clamp01(pos.Y / HitLayer.ActualHeight);
            ViewModel.BeginLayoutDrag();
            ViewModel.ApplyMoveFromPreview(nx, ny);
            ViewModel.EndLayoutDrag();
            UpdateGhost();
            return;
        }

        _dragMode = mode;
        _pointer = e.Pointer;
        HitLayer.CapturePointer(e.Pointer);
        ViewModel.BeginLayoutDrag();

        var left = ViewModel.PosX / ViewModel.VideoPixelWidth;
        var top = ViewModel.PosY / ViewModel.VideoPixelHeight;
        var nx0 = Clamp01(pos.X / HitLayer.ActualWidth);
        var ny0 = Clamp01(pos.Y / HitLayer.ActualHeight);
        _grabOffsetNorm = new Windows.Foundation.Point(nx0 - left, ny0 - top);

        UpdateGhost();
        e.Handled = true;
    }

    private void HitLayer_OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None || ViewModel is null || _pointer is null || !e.Pointer.Equals(_pointer))
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

        UpdateGhost();
        e.Handled = true;
    }

    private void HitLayer_OnPointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndDrag(commit: true, e);

    private void HitLayer_OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndDrag(commit: false, e);

    private void HitLayer_OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            EndDrag(commit: true, e);
        }
    }

    private void EndDrag(bool commit, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }

        if (_pointer is not null)
        {
            HitLayer.ReleasePointerCapture(_pointer);
        }

        _pointer = null;
        _dragMode = DragMode.None;
        if (ViewModel is null)
        {
            return;
        }

        if (commit)
        {
            ViewModel.EndLayoutDrag();
        }
        else
        {
            ViewModel.CancelLayoutDrag();
        }

        UpdateGhost();
        e.Handled = true;
    }

    private DragMode HitTest(Windows.Foundation.Point pos)
    {
        if (OverlayGhost.Visibility != Visibility.Visible)
        {
            return DragMode.None;
        }

        var left = Canvas.GetLeft(OverlayGhost);
        var top = Canvas.GetTop(OverlayGhost);
        var right = left + OverlayGhost.Width;
        var bottom = top + OverlayGhost.Height;
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
        if (ViewModel is null || !ViewModel.HasVideo)
        {
            return;
        }

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = ctrl ? 10 : 1;
        if (shift && e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            var dW = e.Key == VirtualKey.Right ? step : -step;
            ViewModel.NudgeLayout(0, 0, dW);
            UpdateGhost();
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

        ViewModel.NudgeLayout(dx, dy, 0);
        UpdateGhost();
        e.Handled = true;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}
