using HailMary.ViewModels;
using HailMary.Views.Controls;
using HailMary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HailMary.Views.Shell;

public sealed partial class ToolActionBar : UserControl
{
    private static readonly SolidColorBrush ConnectedBrush = new(Windows.UI.Color.FromArgb(255, 16, 124, 16));
    private static readonly SolidColorBrush DisconnectedBrush = new(Windows.UI.Color.FromArgb(255, 196, 43, 28));

    private IToolShellHost? _host;
    private Flyout? _settingsFlyout;

    public ToolActionBar()
    {
        InitializeComponent();
        AppServices.JobProgress.PropertyChanged += JobProgress_OnPropertyChanged;
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
        RefreshProgress();
        RefreshChromeLabels();
    }

    private void OnLanguageChanged() => UiDispatcher.Run(RefreshChromeLabels);

    private void RefreshChromeLabels()
    {
        ToolTipService.SetToolTip(SettingsButton, AppServices.Localization.Get("actionBar.settingsTooltip"));
        ToolTipService.SetToolTip(OverflowButton, AppServices.Localization.Get("actionBar.moreActions"));
        if (_host is null)
        {
            PrimaryButton.Content = AppServices.Localization.Get("actionBar.start");
        }
    }

    private void JobProgress_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobProgressService.IsVisible)
            or nameof(JobProgressService.Value)
            or nameof(JobProgressService.IsIndeterminate)
            or nameof(JobProgressService.DisplayText))
        {
            UiDispatcher.Run(RefreshProgress);
        }
    }

    private void RefreshProgress()
    {
        var progress = AppServices.JobProgress;
        ProgressPanel.Visibility = progress.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        JobProgressBar.IsIndeterminate = progress.IsIndeterminate;
        JobProgressBar.Value = progress.Value;
        ProgressTextBlock.Text = progress.IsIndeterminate ? string.Empty : progress.DisplayText;
    }

    public void Bind(IToolShellHost? host)
    {
        if (_host is System.ComponentModel.INotifyPropertyChanged oldNpc)
        {
            oldNpc.PropertyChanged -= Host_OnPropertyChanged;
        }

        _host = host;
        if (_host is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += Host_OnPropertyChanged;
        }

        Refresh();
    }

    private void Host_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IToolShellHost.PrimaryActionLabel)
            or nameof(IToolShellHost.IsPrimaryActionEnabled)
            or nameof(IToolShellHost.IsBusy)
            or nameof(IToolShellHost.StatusText)
            or nameof(IToolShellHost.HasSettings)
            or nameof(IToolShellHost.HasOpenFullGui)
            or nameof(IStashToolHost.IsStashConnected)
            or nameof(IStashToolHost.StashConnectionTooltip))
        {
            UiDispatcher.Run(Refresh);
        }
    }

    private void Refresh()
    {
        if (_host is null)
        {
            PrimaryButton.Content = AppServices.Localization.Get("actionBar.start");
            PrimaryButton.IsEnabled = false;
            SettingsButton.Visibility = Visibility.Collapsed;
            OverflowButton.Visibility = Visibility.Collapsed;
            StatusTextBlock.Text = string.Empty;
            StashConnectionDot.Visibility = Visibility.Collapsed;
            return;
        }

        PrimaryButton.Content = _host.PrimaryActionLabel;
        PrimaryButton.IsEnabled = _host.IsPrimaryActionEnabled;
        SettingsButton.Visibility = _host.HasSettings ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Visibility = _host.HasOpenFullGui ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Text = _host.StatusText;

        if (_host is IStashToolHost stashHost)
        {
            StashConnectionDot.Visibility = Visibility.Visible;
            StashConnectionDot.Fill = stashHost.IsStashConnected ? ConnectedBrush : DisconnectedBrush;
            ToolTipService.SetToolTip(StashConnectionDot, stashHost.StashConnectionTooltip);
        }
        else
        {
            StashConnectionDot.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(StashConnectionDot, null);
        }
    }

    public async Task TryExecutePrimaryAsync()
    {
        if (_host?.IsPrimaryActionEnabled == true
            && _host.PrimaryActionCommand.CanExecute(null) == true)
        {
            await _host.PrimaryActionCommand.ExecuteAsync(null);
        }
    }

    private async void PrimaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        await TryExecutePrimaryAsync();
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_host?.OpenSettingsCommand?.CanExecute(null) != true)
        {
            return;
        }

        if (_host.SettingsContext is IStashSettingsContext stashContext)
        {
            _settingsFlyout ??= new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
            _settingsFlyout.Content = new StashConnectionSettingsFlyout(stashContext);
            _settingsFlyout.ShowAt(SettingsButton);
            return;
        }

        _host.OpenSettingsCommand.Execute(null);
    }

    private void OverflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_host?.OpenFullGuiCommand?.CanExecute(null) != true)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = _host.OpenFullGuiLabel,
            Command = _host.OpenFullGuiCommand,
        });
        flyout.ShowAt(OverflowButton);
    }
}
