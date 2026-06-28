using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public interface IToolShellHost
{
    string PrimaryActionLabel { get; }

    IAsyncRelayCommand PrimaryActionCommand { get; }

    bool IsPrimaryActionEnabled { get; }

    string StatusText { get; }

    bool IsBusy { get; }

    bool HasVideoPreview { get; }

    bool HasSettings { get; }

    IRelayCommand? OpenSettingsCommand { get; }

    bool HasOpenFullGui { get; }

    string OpenFullGuiLabel { get; }

    IRelayCommand? OpenFullGuiCommand { get; }

    object? SettingsContext { get; }
}

public interface ISplitPaneToolHost
{
    bool UsesSplitPaneLayout { get; }
}

public interface IStashSettingsContext
{
    string Endpoint { get; set; }

    string ApiKey { get; set; }

    string PathPrefixRemote { get; set; }

    string PathPrefixLocal { get; set; }

    string PathPrefixBackup { get; set; }

    bool UseBackup { get; set; }

    bool ShowBackupOptions { get; }

    bool BackupToggleEnabled { get; }

    IAsyncRelayCommand ConnectCommand { get; }

    IRelayCommand SaveSettingsCommand { get; }
}
