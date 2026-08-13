using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class PerfectLoopPreviewHost : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;

    public PerfectLoopPreviewHost(PerfectLoopViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();

        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        RangeTimeline.SeekRequested += (_, seconds) => SeekTo(seconds);

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
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _positionTimer.Stop();
        };
        _ = LoadVideoAsync();
    }

    public PerfectLoopViewModel ViewModel { get; }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerfectLoopViewModel.InputPath))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName is nameof(PerfectLoopViewModel.TimelineRanges)
                 or nameof(PerfectLoopViewModel.WaveformPeaks))
        {
            RangeTimeline.Spans = ViewModel.TimelineRanges;
            RangeTimeline.Peaks = ViewModel.WaveformPeaks;
        }
        else if (e.PropertyName == nameof(PerfectLoopViewModel.SliderValue))
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
        var path = ViewModel.SourceVideoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewPlayer.Source = null;
            VideoSurface.ClearVideoDimensions();
            ViewModel.ApplyDuration(1);
            ViewModel.SetPosition(0);
            _ = ViewModel.LoadWaveformAsync(null);
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
                duration = await TryProbeDurationAsync(path) ?? ViewModel.DurationSeconds;
            }

            if (duration > 0)
            {
                ViewModel.ApplyDuration(duration);
            }

            var start = ViewModel.LoopStartSeconds;
            ViewModel.SetPosition(start);
            SeekTo(start);
            _ = ViewModel.LoadWaveformAsync(path);
        }
        catch (Exception ex)
        {
            HailMary.Services.AppServices.Log.Error($"Loop-Vorschau: {ex.Message}");
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
        if (Player is null)
        {
            return;
        }

        if (Player.PlaybackSession.Position.TotalSeconds + 0.04 >= ViewModel.EffectiveLoopEnd)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(ViewModel.LoopStartSeconds);
        }

        Player.Play();
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
        SeekTo(ViewModel.LoopStartSeconds);
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

        _suppressSliderSeek = true;
        ViewModel.SetPosition(clamped);
        _suppressSliderSeek = false;
    }

    private void SyncPositionFromPlayer()
    {
        if (_sliderDragging || Player is null)
        {
            return;
        }

        var sec = Player.PlaybackSession.Position.TotalSeconds;
        var start = ViewModel.LoopStartSeconds;
        var end = ViewModel.EffectiveLoopEnd;
        if (end > start + 0.05 && sec + 0.04 >= end)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(start);
            sec = start;
            if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                Player.Play();
            }
        }
        else if (sec + 0.02 < start)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(start);
            sec = start;
        }

        _suppressSliderSeek = true;
        ViewModel.SetPosition(sec);
        _suppressSliderSeek = false;

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing
            && Player.PlaybackSession.PlaybackState != MediaPlaybackState.Opening)
        {
            _positionTimer.Stop();
        }
    }
}
