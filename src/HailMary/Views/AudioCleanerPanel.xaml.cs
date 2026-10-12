using HailMary.ViewModels;
using HailMary.Views.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HailMary.Views;

public sealed partial class AudioCleanerPanel : UserControl
{
    private readonly MediaPlayer _player = new() { IsLoopingEnabled = true };
    private bool _syncingAb;
    private bool _cleanedSelected = true;

    public AudioCleanerViewModel ViewModel { get; }

    public AudioCleanerPanel(AudioCleanerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        Unloaded += (_, _) => _player.Pause();
        SyncAbButtons();
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioCleanerViewModel.PreviewCleanedPath))
        {
            // New snippet: start straight away on the cleaned version.
            _cleanedSelected = true;
            SyncAbButtons();
            LoadAbSource(play: true);
        }
    }

    private void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Source is null)
        {
            LoadAbSource(play: true);
            return;
        }

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            SetPlayIcon(false);
        }
        else
        {
            _player.Play();
            SetPlayIcon(true);
        }
    }

    private void ABOriginal_Checked(object sender, RoutedEventArgs e) => SwitchAb(cleaned: false);

    private void ABCleaned_Checked(object sender, RoutedEventArgs e) => SwitchAb(cleaned: true);

    // Segmented control: clicking the active side keeps it selected.
    private void AB_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_syncingAb)
        {
            SyncAbButtons();
        }
    }

    private void SwitchAb(bool cleaned)
    {
        if (_syncingAb || cleaned == _cleanedSelected)
        {
            return;
        }

        _cleanedSelected = cleaned;
        SyncAbButtons();
        LoadAbSource(play: _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
    }

    /// <summary>Swap original/cleaned at the same position so the difference is easy to hear.</summary>
    private void LoadAbSource(bool play)
    {
        var path = _cleanedSelected ? ViewModel.PreviewCleanedPath : ViewModel.PreviewOriginalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var position = _player.Source is null ? TimeSpan.Zero : _player.PlaybackSession.Position;
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
        _player.PlaybackSession.Position = position;
        if (play)
        {
            _player.Play();
        }

        SetPlayIcon(play);
    }

    private void SyncAbButtons()
    {
        _syncingAb = true;
        try
        {
            ABOriginal.IsChecked = !_cleanedSelected;
            ABCleaned.IsChecked = _cleanedSelected;
        }
        finally
        {
            _syncingAb = false;
        }
    }

    private void SetPlayIcon(bool playing) => MediaTransport.ApplyPlayState(PreviewPlayButton, PreviewPlayIcon, playing);
}
