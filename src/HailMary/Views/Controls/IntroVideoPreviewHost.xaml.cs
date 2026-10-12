using HailMary.ViewModels;
using HailMary.Views.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class IntroVideoPreviewHost : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;

    public IntroVideoPreviewHost(IntroCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _positionTimer.Tick += (_, _) => SyncPositionFromPlayer();

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

        MediaTransport.FocusOnPointer(this, VideoSurface);
        // (Re)attach on every load: switching tools unloads this control and shows it again later.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            SetPlaying(false);
        };
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        _ = LoadVideoAsync();
    }

    public IntroCutterViewModel ViewModel { get; }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntroCutterViewModel.PreviewVideoPath))
        {
            _ = LoadVideoAsync();
        }
        else if (e.PropertyName == nameof(IntroCutterViewModel.SliderValue))
        {
            if (!_sliderDragging && !_suppressSliderSeek)
            {
                SeekTo(ViewModel.SliderValue);
            }
        }
        else if (e.PropertyName is nameof(IntroCutterViewModel.TimelineRanges)
                 or nameof(IntroCutterViewModel.WaveformPeaks))
        {
            RangeTimeline.Spans = ViewModel.TimelineRanges;
            RangeTimeline.Peaks = ViewModel.WaveformPeaks;
        }
    }

    private MediaPlayerElement PreviewPlayer => VideoSurface.Player;

    private MediaPlayer? Player => PreviewPlayer.MediaPlayer;

    private async Task LoadVideoAsync()
    {
        StopPlayback();
        var path = ViewModel.PreviewVideoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewPlayer.Source = null;
            VideoSurface.ClearVideoDimensions();
            ViewModel.SetDuration(0);
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
                duration = await TryProbeDurationAsync(path) ?? 0;
            }

            ViewModel.SetDuration(duration);
            ViewModel.SetPosition(0);
            SeekTo(0);
            _ = ViewModel.LoadWaveformAsync(path);
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

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(0);
    }

    private void TogglePlayback()
    {
        if (Player is null)
        {
            return;
        }

        if (Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            Player.Pause();
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

    private void RootHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.HasVideo)
        {
            return;
        }

        if (MediaTransport.HandleKey(e.Key, Player, ViewModel.SliderMaximum, TogglePlayback, SeekTo))
        {
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.I)
        {
            ViewModel.SetIntroFromPositionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.O)
        {
            ViewModel.SetOutroFromPositionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void StopPlayback()
    {
        SetPlaying(false);
        if (Player is null)
        {
            return;
        }

        Player.Pause();
        Player.PlaybackSession.Position = TimeSpan.Zero;
    }

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
        _suppressSliderSeek = true;
        ViewModel.SetPosition(sec);
        _suppressSliderSeek = false;

        if (Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            SetPlaying(false);
        }
    }
}
