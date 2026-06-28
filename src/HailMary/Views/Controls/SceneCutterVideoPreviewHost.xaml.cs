using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class SceneCutterVideoPreviewHost : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;

    public SceneCutterVideoPreviewHost(SceneCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();

        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;

        TimelineSlider.PointerPressed += (_, _) => _sliderDragging = true;
        TimelineSlider.PointerReleased += (_, _) =>
        {
            _sliderDragging = false;
            SeekTo(ViewModel.SliderValue);
        };
        TimelineSlider.PointerCanceled += (_, _) => _sliderDragging = false;
        TimelineSlider.ValueChanged += (_, _) =>
        {
            if (_suppressSliderSeek || _sliderDragging)
            {
                return;
            }

            SeekTo(ViewModel.SliderValue);
        };

        IsTabStop = true;
        _ = LoadVideoAsync();
    }

    public SceneCutterViewModel ViewModel { get; }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneCutterViewModel.InputPath))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName == nameof(SceneCutterViewModel.SliderValue))
        {
            if (!_sliderDragging && !_suppressSliderSeek)
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
        var path = ViewModel.InputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewPlayer.Source = null;
            VideoSurface.ClearVideoDimensions();
            ViewModel.SetDuration(1);
            ViewModel.SetPosition(0);
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            await Task.Delay(400);

            var duration = Player?.NaturalDuration.TotalSeconds ?? 0;
            if (duration <= 0 || double.IsNaN(duration))
            {
                duration = await TryProbeDurationAsync(path) ?? 1;
            }

            ViewModel.SetDuration(duration);
            ViewModel.SetPosition(0);
            SeekTo(0);
        }
        catch (Exception ex)
        {
            HailMary.Services.AppServices.Log.Error($"Vorschau: {ex.Message}");
        }
    }

    private static async Task<double?> TryProbeDurationAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            var dur = props.Duration.TotalSeconds;
            return dur > 0 ? dur : null;
        }
        catch
        {
            return null;
        }
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        Player?.Play();
        _positionTimer.Start();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        Player?.Pause();
        _positionTimer.Stop();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(0);
    }

    private void BtnMarkIn_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MarkInAt(GetCurrentSeconds());

    private void BtnMarkOut_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MarkOutAt(GetCurrentSeconds());

    private void RootHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.HasVideo)
        {
            return;
        }

        if (e.Key == VirtualKey.I)
        {
            ViewModel.MarkInAt(GetCurrentSeconds());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.O)
        {
            ViewModel.MarkOutAt(GetCurrentSeconds());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.P)
        {
            ViewModel.AddSceneCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        if (Player is null)
        {
            return;
        }

        Player.Pause();
        Player.PlaybackSession.Position = TimeSpan.Zero;
    }

    private double GetCurrentSeconds() =>
        Player?.PlaybackSession.Position.TotalSeconds ?? ViewModel.SliderValue;

    private void SeekTo(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, ViewModel.SliderMaximum);
        if (Player is not null)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(clamped);
        }

        ViewModel.SetPosition(clamped);
    }

    private void SyncPositionFromPlayer()
    {
        if (_sliderDragging || Player is null)
        {
            return;
        }

        var sec = Player.PlaybackSession.Position.TotalSeconds;
        _suppressSliderSeek = true;
        ViewModel.SetPosition(sec);
        _suppressSliderSeek = false;

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            _positionTimer.Stop();
        }
    }
}
