using HailMary.Services;
using HailMary.ViewModels;
using HailMary.Views.Helpers;
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
    private int _loadGeneration;
    // Set while a single scene is previewed; playback pauses there.
    private double? _sceneEnd;

    public SceneCutterVideoPreviewHost(SceneCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
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
        MediaTransport.FocusOnPointer(this, VideoSurface);
        Loaded += (_, _) =>
        {
            // Re-attach on every load: switching tools unloads and reloads this control.
            ViewModel.PlaySceneRequested -= PlayScene;
            ViewModel.PlaySceneRequested += PlayScene;
            _ = LoadVideoAsync();
        };
        Unloaded += (_, _) =>
        {
            _positionTimer.Stop();
            ViewModel.PlaySceneRequested -= PlayScene;
        };
    }

    public SceneCutterViewModel ViewModel { get; }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneCutterViewModel.InputPath))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName is nameof(SceneCutterViewModel.TimelineRanges)
                 or nameof(SceneCutterViewModel.WaveformPeaks))
        {
            RangeTimeline.Spans = ViewModel.TimelineRanges;
            RangeTimeline.Peaks = ViewModel.WaveformPeaks;
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
        var generation = ++_loadGeneration;
        StopPlayback();
        var path = ViewModel.InputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearSource();
            VideoSurface.ClearVideoDimensions();
            ViewModel.SetDuration(1);
            ViewModel.SetPosition(0);
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            if (generation != _loadGeneration)
            {
                return;
            }

            var source = await CreateMediaSourceAsync(path);
            if (generation != _loadGeneration)
            {
                source?.Dispose();
                return;
            }

            ClearSource();
            if (source is null)
            {
                throw new InvalidOperationException("Videoquelle konnte nicht erstellt werden.");
            }

            PreviewPlayer.Source = source;
            await Task.Delay(400);
            if (generation != _loadGeneration)
            {
                return;
            }

            var duration = Player?.NaturalDuration.TotalSeconds ?? 0;
            if (duration <= 0 || double.IsNaN(duration))
            {
                duration = await TryProbeDurationAsync(path) ?? 1;
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            ViewModel.SetDuration(duration);
            ViewModel.SetPosition(0);
            SeekTo(0);
            _ = ViewModel.LoadWaveformAsync(path);
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Vorschau: {ex.Message}");
        }
    }

    private void ClearSource()
    {
        var previous = PreviewPlayer.Source;
        PreviewPlayer.Source = null;
        (previous as IDisposable)?.Dispose();
    }

    private static async Task<MediaSource?> CreateMediaSourceAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            return MediaSource.CreateFromStorageFile(file);
        }
        catch
        {
            var uri = VideoPreviewUriHelper.ToMediaUri(path);
            return uri is null ? null : MediaSource.CreateFromUri(uri);
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

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (Player is null)
        {
            return;
        }

        if (Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            Player.Pause();
            _sceneEnd = null;
            SetPlaying(false);
            return;
        }

        Player.Play();
        SetPlaying(true);
    }

    private void SetPlaying(bool playing)
    {
        MediaTransport.ApplyPlayState(BtnPlayPause, PlayPauseIcon, playing);
        if (playing)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    /// <summary>Play one scene from its start and pause at its end.</summary>
    public void PlayScene(double start, double end)
    {
        if (Player is null)
        {
            return;
        }

        SeekTo(start);
        _sceneEnd = end;
        Player.Play();
        SetPlaying(true);
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

        if (MediaTransport.HandleKey(e.Key, Player, ViewModel.SliderMaximum, TogglePlayback, SeekTo))
        {
            e.Handled = true;
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
        _sceneEnd = null;
        SetPlaying(false);
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
        if (_sceneEnd is { } end && sec >= end)
        {
            Player.Pause();
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(end);
            sec = end;
            _sceneEnd = null;
        }

        _suppressSliderSeek = true;
        ViewModel.SetPosition(sec);
        _suppressSliderSeek = false;

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            SetPlaying(false);
        }
    }
}
