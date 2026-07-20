using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class MarkerUpdaterVideoPreviewHost : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;
    private MediaPlayer? _player;
    private TaskCompletionSource<bool>? _openTcs;
    private int _loadGeneration;
    private bool _playerInitialized;

    public MarkerUpdaterVideoPreviewHost(MarkerUpdaterViewModel viewModel)
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
        PointerPressed += (_, _) => Focus(FocusState.Programmatic);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MarkerUpdaterViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsurePlayerInitialized();
        _ = LoadVideoAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _positionTimer.Stop();
        if (_player is null)
        {
            return;
        }

        _player.MediaOpened -= Player_OnMediaOpened;
        _player.MediaFailed -= Player_OnMediaFailed;
        VideoSurface.Player.SetMediaPlayer(null);
        _player.Dispose();
        _player = null;
        _playerInitialized = false;
    }

    private void EnsurePlayerInitialized()
    {
        if (_playerInitialized)
        {
            return;
        }

        _player = new MediaPlayer { AutoPlay = false };
        VideoSurface.Player.SetMediaPlayer(_player);
        _player.MediaOpened += Player_OnMediaOpened;
        _player.MediaFailed += Player_OnMediaFailed;
        _playerInitialized = true;
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarkerUpdaterViewModel.VideoPath):
                if (_playerInitialized)
                {
                    _ = LoadVideoAsync();
                }

                break;
            case nameof(MarkerUpdaterViewModel.SliderValue):
                if (!_sliderDragging && !_suppressSliderSeek)
                {
                    SeekTo(ViewModel.SliderValue);
                }

                break;
            case nameof(MarkerUpdaterViewModel.PlaybackSpeed):
                ApplyPlaybackSpeed();
                break;
        }
    }

    private MediaPlayerElement PreviewPlayer => VideoSurface.Player;

    private MediaPlayer? Player => _player ?? PreviewPlayer.MediaPlayer;

    private async Task LoadVideoAsync()
    {
        EnsurePlayerInitialized();

        var generation = ++_loadGeneration;
        StopPlayback();
        var path = ViewModel.VideoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            PreviewPlayer.Source = null;
            VideoSurface.ClearVideoDimensions();
            ViewModel.SetDuration(0);
            ViewModel.SetPosition(0);
            ViewModel.MarkVideoLoaded(false);
            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);

            MediaSource? source = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                source = MediaSource.CreateFromStorageFile(file);
            }
            catch
            {
                var uri = VideoPreviewUriHelper.ToMediaUri(path);
                if (uri is not null)
                {
                    source = MediaSource.CreateFromUri(uri);
                }
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            if (source is null)
            {
                throw new InvalidOperationException("Videoquelle konnte nicht erstellt werden.");
            }

            PreviewPlayer.Source = source;
            var opened = await WaitForMediaOpenedAsync(TimeSpan.FromSeconds(10));

            if (generation != _loadGeneration)
            {
                return;
            }

            var duration = opened ? Player?.NaturalDuration.TotalSeconds ?? 0 : 0;
            if (duration <= 0 || double.IsNaN(duration))
            {
                duration = await FfprobeHelper.ProbeDurationSecondsAsync(path)
                           ?? await TryProbeDurationAsync(path)
                           ?? 0;
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            if (duration <= 0)
            {
                throw new InvalidOperationException("Videodauer konnte nicht ermittelt werden.");
            }

            ViewModel.SetDuration(duration);
            ViewModel.SetPosition(0);
            ViewModel.MarkVideoLoaded(true);
            ApplyPlaybackSpeed();
            SeekTo(0);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            ViewModel.MarkVideoLoaded(false);
            AppServices.Log.Error($"Vorschau: {ex.Message} ({path})");
        }
    }

    private async Task<bool> WaitForMediaOpenedAsync(TimeSpan timeout)
    {
        _openTcs = new TaskCompletionSource<bool>();
        var completed = await Task.WhenAny(_openTcs.Task, Task.Delay(timeout));
        return completed == _openTcs.Task && await _openTcs.Task;
    }

    private void Player_OnMediaOpened(MediaPlayer sender, object args) =>
        _openTcs?.TrySetResult(true);

    private void Player_OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        AppServices.Log.Error($"Vorschau MediaFailed: {args.ErrorMessage}");
        _openTcs?.TrySetResult(false);
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

    private void ApplyPlaybackSpeed()
    {
        if (Player is null)
        {
            return;
        }

        Player.PlaybackSession.PlaybackRate = Math.Clamp(ViewModel.PlaybackSpeed, 0.1, 4);
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        ApplyPlaybackSpeed();
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

    private void BtnQuickIn_Click(object sender, RoutedEventArgs e) =>
        ViewModel.QuickMarkInAt(GetCurrentSeconds());

    private async void BtnQuickOut_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.QuickMarkOutAndCreateAsync(GetCurrentSeconds());

    private void QuickPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HailMary.Models.MarkerQuickPreset preset })
        {
            ViewModel.SelectQuickPresetCommand.Execute(preset);
        }
    }

    private async void RootHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.HasVideo)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Left)
        {
            ViewModel.NudgePosition(shift ? -ViewModel.CoarseStepSeconds : -ViewModel.FineStepSeconds);
            SeekTo(ViewModel.SliderValue);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Right)
        {
            ViewModel.NudgePosition(shift ? ViewModel.CoarseStepSeconds : ViewModel.FineStepSeconds);
            SeekTo(ViewModel.SliderValue);
            e.Handled = true;
            return;
        }

        // Quick Markers (wie Stash-Plugin): Shift+I/O Range, Shift+1–9 Instant
        if (shift && e.Key == VirtualKey.I)
        {
            ViewModel.QuickMarkInAt(GetCurrentSeconds());
            e.Handled = true;
            return;
        }

        if (shift && e.Key == VirtualKey.O)
        {
            await ViewModel.QuickMarkOutAndCreateAsync(GetCurrentSeconds());
            e.Handled = true;
            return;
        }

        if (shift && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
        {
            var slot = (int)e.Key - (int)VirtualKey.Number0;
            await ViewModel.QuickInstantCreateAsync(GetCurrentSeconds(), slot);
            e.Handled = true;
            return;
        }

        if (shift && e.Key is >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9)
        {
            var slot = (int)e.Key - (int)VirtualKey.NumberPad0;
            await ViewModel.QuickInstantCreateAsync(GetCurrentSeconds(), slot);
            e.Handled = true;
            return;
        }

        if (shift && e.Key == VirtualKey.PageDown)
        {
            ViewModel.CycleQuickPresetCommand.Execute(1);
            e.Handled = true;
            return;
        }

        if (shift && e.Key == VirtualKey.PageUp)
        {
            ViewModel.CycleQuickPresetCommand.Execute(-1);
            e.Handled = true;
            return;
        }

        // Ohne Shift: nur Felder setzen (kein Create)
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

