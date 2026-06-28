using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class MarkerUpdaterViewModel
{
    public bool IsStashConnected => StashConnectionStatus.IsConnected(ConnectionInfo);

    public string StashConnectionTooltip => StashConnectionStatus.Tooltip(ConnectionInfo);

    public string PrimaryActionLabel => Loc.T("markerupdater.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand => SaveTagsCommand;

    public bool IsPrimaryActionEnabled => !IsBusy;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => true;

    public bool UsesSplitPaneLayout => true;

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        CreateMarkerAtSelectedTagCommand.NotifyCanExecuteChanged();
        PreviousSceneCommand.NotifyCanExecuteChanged();
        NextSceneCommand.NotifyCanExecuteChanged();
        CleanupScanCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
    }

}
