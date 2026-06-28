using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class StashPathfinderViewModel
{
    public bool IsStashConnected => StashConnectionStatus.IsConnected(ConnectionInfo);

    public string StashConnectionTooltip => StashConnectionStatus.Tooltip(ConnectionInfo);

    public bool UsesSplitPaneLayout => false;

    public string PrimaryActionLabel => Loc.T("stashpathfinder.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand => SearchCommand;

    public bool IsPrimaryActionEnabled => !IsBusy;

    public string StatusText => string.IsNullOrWhiteSpace(Status) ? ConnectionInfo : Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => this;

    public bool ShowBackupOptions => true;

    IAsyncRelayCommand IStashSettingsContext.ConnectCommand => ConnectCommand;

    IRelayCommand IStashSettingsContext.SaveSettingsCommand => SaveSettingsCommand;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnConnectionInfoChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IStashToolHost.IsStashConnected));
        OnPropertyChanged(nameof(IStashToolHost.StashConnectionTooltip));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
    }

}
