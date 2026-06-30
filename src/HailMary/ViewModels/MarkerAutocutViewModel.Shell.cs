using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class MarkerAutocutViewModel
{
    public bool IsStashConnected => StashConnectionStatus.IsConnected(ConnectionInfo);

    public string StashConnectionTooltip => StashConnectionStatus.Tooltip(ConnectionInfo);

    public string PrimaryActionLabel => Loc.T("markerautocut.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand => ExportCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && ExportOrder.Count > 0;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

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
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PathSortLabel));
        RebuildDisplayItems();
        foreach (var row in Markers)
        {
            row.NotifyDisplayChanged();
        }
    }

}
