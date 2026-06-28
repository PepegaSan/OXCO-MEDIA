using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

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

        UpdateCutBarColumns();
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
        else if (e.PropertyName is nameof(IntroCutterViewModel.IntroBarWeight)
                 or nameof(IntroCutterViewModel.MainBarWeight)
                 or nameof(IntroCutterViewModel.OutroBarWeight))
        {
            UpdateCutBarColumns();
        }
    }

    private MediaPlayerElement PreviewPlayer => VideoSurface.Player;

    private MediaPlayer? Player => PreviewPlayer.MediaPlayer;

    private void UpdateCutBarColumns()
    {
        if (CutBarGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        CutBarGrid.ColumnDefinitions[0].Width = new GridLength(ViewModel.IntroBarWeight, GridUnitType.Star);
        CutBarGrid.ColumnDefinitions[1].Width = new GridLength(ViewModel.MainBarWeight, GridUnitType.Star);
        CutBarGrid.ColumnDefinitions[2].Width = new GridLength(ViewModel.OutroBarWeight, GridUnitType.Star);
    }

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
            _positionTimer.Stop();
        }
    }
}
