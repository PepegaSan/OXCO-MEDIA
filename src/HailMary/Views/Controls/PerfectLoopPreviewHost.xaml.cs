using System.Runtime.InteropServices.WindowsRuntime;
using HailMary.Services;
using HailMary.ViewModels;
using HailMary.Views.Helpers;
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

/// <summary>
/// Source player with loop range editing, plus a "loop preview" mode that plays the rendered
/// loop with native looping so the seam can be judged before exporting.
/// </summary>
public sealed partial class PerfectLoopPreviewHost : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _sliderDragging;
    private bool _suppressSliderSeek;
    private string? _loadedPath;

    public PerfectLoopPreviewHost(PerfectLoopViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
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
        // (Re)attach on every load: switching tools unloads this control and shows it again later.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _positionTimer.Stop();
        };
        ApplyMode();
        UpdateQualityBadge();
        _ = UpdateSeamStripAsync();
    }

    public PerfectLoopViewModel ViewModel { get; }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PerfectLoopViewModel.InputPath):
            case nameof(PerfectLoopViewModel.SourceVideoPath):
            case nameof(PerfectLoopViewModel.LoopPreviewPath):
                _ = LoadCurrentAsync();
                break;
            case nameof(PerfectLoopViewModel.IsLoopPreviewMode):
                ApplyMode();
                break;
            case nameof(PerfectLoopViewModel.TimelineRanges):
            case nameof(PerfectLoopViewModel.WaveformPeaks):
                RangeTimeline.Spans = ViewModel.TimelineRanges;
                RangeTimeline.Peaks = ViewModel.WaveformPeaks;
                break;
            case nameof(PerfectLoopViewModel.SliderValue):
                if (!_sliderDragging && !_suppressSliderSeek && !ViewModel.IsLoopPreviewMode)
                {
                    SeekTo(ViewModel.SliderValue);
                }

                break;
            case nameof(PerfectLoopViewModel.QualityScore):
                UpdateQualityBadge();
                break;
            case nameof(PerfectLoopViewModel.SeamThumbs):
                _ = UpdateSeamStripAsync();
                break;
        }
    }

    private MediaPlayerElement PreviewPlayer => VideoSurface.Player;

    private MediaPlayer? Player => PreviewPlayer.MediaPlayer;

    private bool LoopMode => ViewModel.IsLoopPreviewMode;

    private bool _syncingMode;

    private void ApplyMode()
    {
        var loop = LoopMode;
        _syncingMode = true;
        try
        {
            ModeOriginalButton.IsChecked = !loop;
            ModeLoopButton.IsChecked = loop;
        }
        finally
        {
            _syncingMode = false;
        }

        var rangeVisibility = loop ? Visibility.Collapsed : Visibility.Visible;
        RangeEditor.Visibility = rangeVisibility;
        RangeButtons.Visibility = rangeVisibility;
        RangeHintText.Visibility = rangeVisibility;
        _ = LoadCurrentAsync();
    }

    // Checked (not Click) so keyboard, screen readers and UI automation switch modes too.
    private void ModeOriginal_Checked(object sender, RoutedEventArgs e)
    {
        if (!_syncingMode)
        {
            ViewModel.ShowOriginalModeCommand.Execute(null);
        }
    }

    private void ModeLoop_Checked(object sender, RoutedEventArgs e)
    {
        if (!_syncingMode)
        {
            ViewModel.ShowLoopModeCommand.Execute(null);
        }
    }

    // The two toggles act as a segmented control: clicking the active one keeps it selected.
    private void Mode_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_syncingMode)
        {
            ApplyMode();
        }
    }

    private void Candidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LoopCandidateItem item })
        {
            ViewModel.ApplyCandidateCommand.Execute(item);
            if (!LoopMode)
            {
                SeekTo(item.Start);
            }
        }
    }

    /// <summary>Load the source (original mode) or the rendered loop (loop mode).</summary>
    private async Task LoadCurrentAsync()
    {
        var loop = LoopMode;
        var path = loop ? ViewModel.LoopPreviewPath : ViewModel.SourceVideoPath;
        if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase) && PreviewPlayer.Source is not null)
        {
            if (Player is not null)
            {
                Player.IsLoopingEnabled = loop;
            }

            return;
        }

        StopPlayback();
        _loadedPath = path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // Loop preview still rendering: keep the surface empty instead of showing stale video.
            PreviewPlayer.Source = null;
            if (!loop)
            {
                VideoSurface.ClearVideoDimensions();
                ViewModel.ApplyDuration(1);
                ViewModel.SetPosition(0);
                _ = ViewModel.LoadWaveformAsync(null);
            }

            return;
        }

        try
        {
            await VideoSurface.SetVideoDimensionsFromPathAsync(path);
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            if (Player is not null)
            {
                Player.IsLoopingEnabled = loop;
            }

            if (loop)
            {
                // The point of the loop preview is watching it run — start right away.
                Player?.Play();
                ApplyPlayingState(Player is not null);
                return;
            }

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
            AppServices.Log.Error($"Loop-Vorschau: {ex.Message}");
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

    private void UpdateQualityBadge()
    {
        var score = ViewModel.QualityScore;
        Color color = score switch
        {
            < 0 => Color.FromArgb(0x60, 0x80, 0x80, 0x80),
            >= 0.7 => Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C),
            >= 0.4 => Color.FromArgb(0xFF, 0xF5, 0xB7, 0x42),
            _ => Color.FromArgb(0xFF, 0xF2, 0x5C, 0x54),
        };
        QualityBadge.BorderBrush = new SolidColorBrush(color);
        QualityBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
    }

    private async Task UpdateSeamStripAsync()
    {
        var thumbs = ViewModel.SeamThumbs;
        Image[] targets = [SeamThumb0, SeamThumb1, SeamThumb2, SeamThumb3];
        if (!ViewModel.HasSeamThumbs)
        {
            SeamStrip.Visibility = Visibility.Collapsed;
            return;
        }

        for (var i = 0; i < targets.Length; i++)
        {
            targets[i].Source = thumbs[i].Length > 0 ? await ToBitmapAsync(thumbs[i]) : null;
        }

        SeamStrip.Visibility = Visibility.Visible;
    }

    private static async Task<BitmapImage?> ToBitmapAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
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
            ApplyPlayingState(false);
            return;
        }

        if (!LoopMode && Player.PlaybackSession.Position.TotalSeconds + 0.04 >= ViewModel.EffectiveLoopEnd)
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(ViewModel.LoopStartSeconds);
        }

        Player.Play();
        ApplyPlayingState(true);
    }

    private void ApplyPlayingState(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "" : "";
        var label = Loc.T(playing ? "common.pause" : "common.play");
        ToolTipService.SetToolTip(BtnPlayPause, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BtnPlayPause, label);
        if (playing && !LoopMode)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (!LoopMode)
        {
            SeekTo(ViewModel.LoopStartSeconds);
        }
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

        switch (e.Key)
        {
            case VirtualKey.Space:
                TogglePlayback();
                e.Handled = true;
                break;
            case VirtualKey.L:
                ViewModel.IsLoopPreviewMode = !ViewModel.IsLoopPreviewMode;
                e.Handled = true;
                break;
            case VirtualKey.I when !LoopMode:
                ViewModel.MarkInAt(GetCurrentSeconds());
                e.Handled = true;
                break;
            case VirtualKey.O when !LoopMode:
                ViewModel.MarkOutAt(GetCurrentSeconds());
                e.Handled = true;
                break;
        }
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        ApplyPlayingState(false);
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
        if (LoopMode)
        {
            return;
        }

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
        if (_sliderDragging || Player is null || LoopMode)
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
            ApplyPlayingState(false);
        }
    }
}
