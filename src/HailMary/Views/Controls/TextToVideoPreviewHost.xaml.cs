using System.Runtime.InteropServices.WindowsRuntime;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

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

    public TextToVideoPreviewHost()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();

        TimelineSlider.PointerPressed += (_, _) => _sliderDragging = true;
        TimelineSlider.PointerReleased += (_, _) =>
        {
            _sliderDragging = false;
            SeekTo(ViewModel?.SliderValue ?? 0);
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
            _ = UpdatePreviewImageAsync();
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.VideoPath)
                 || e.PropertyName == nameof(TextToVideoViewModel.HasVideo))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName == nameof(TextToVideoViewModel.SliderValue))
        {
            if (!_sliderDragging && !_suppressSliderSeek && !ViewModel.IsPreviewPlaying)
            {
                SeekTo(ViewModel.SliderValue);
            }
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
            HailMary.Services.AppServices.Log.Error($"TTV-Vorschau: {ex.Message}");
        }
    }

    private async Task UpdatePreviewImageAsync()
    {
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

    private void PreviewPresenter_OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || PreviewPresenter.ActualWidth <= 0 || PreviewPresenter.ActualHeight <= 0)
        {
            return;
        }

        var pos = e.GetCurrentPoint(PreviewPresenter).Position;
        ViewModel.SetPositionFromPreviewClick(pos.X / PreviewPresenter.ActualWidth, pos.Y / PreviewPresenter.ActualHeight);
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        Player?.Play();
        ViewModel.IsPreviewPlaying = true;
        _positionTimer.Start();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        Player?.Pause();
        if (ViewModel is not null)
        {
            ViewModel.IsPreviewPlaying = false;
        }

        _positionTimer.Stop();
        ViewModel?.SchedulePreviewRefresh();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(0);
        ViewModel?.SchedulePreviewRefresh();
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        if (ViewModel is not null)
        {
            ViewModel.IsPreviewPlaying = false;
        }

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
        ViewModel.SchedulePreviewRefresh();

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            _positionTimer.Stop();
            ViewModel.IsPreviewPlaying = false;
        }
    }
}
