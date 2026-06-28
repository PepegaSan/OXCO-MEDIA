using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class StashCutterViewModel
{
    public bool IsStashConnected => StashConnectionStatus.IsConnected(ConnectionInfo);

    public string StashConnectionTooltip => StashConnectionStatus.Tooltip(ConnectionInfo);

    // Wie Scene Cutter: gesamtes Tab scrollt über ToolContentScroll, nicht einzelne Spalten.
    public bool UsesSplitPaneLayout => false;

    bool IToolShellHost.HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public new object? SettingsContext => this;

    public bool ShowBackupOptions => true;

    IAsyncRelayCommand IStashSettingsContext.ConnectCommand => ConnectCommand;

    IRelayCommand IStashSettingsContext.SaveSettingsCommand => SaveSettingsCommand;

    partial void OnConnectionInfoChanged(string value)
    {
        OnPropertyChanged(nameof(IStashToolHost.IsStashConnected));
        OnPropertyChanged(nameof(IStashToolHost.StashConnectionTooltip));
    }

    public new void RefreshLocalization() => base.RefreshLocalization();

}
