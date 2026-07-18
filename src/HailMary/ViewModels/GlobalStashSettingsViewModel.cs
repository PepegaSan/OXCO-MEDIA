using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class GlobalStashSettingsViewModel : ObservableObject, IStashSettingsContext
{
    private readonly StashGraphQlClient _client = new();

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _pathPrefixRemote = "/data/";
    [ObservableProperty] private string _pathPrefixLocal = string.Empty;
    [ObservableProperty] private string _pathPrefixBackup = string.Empty;
    [ObservableProperty] private bool _useBackup;
    [ObservableProperty] private string _status = string.Empty;

    public bool ShowBackupOptions => true;

    public bool BackupToggleEnabled =>
        StashPathMapper.BackupAvailable(CurrentPathMap());

    public GlobalStashSettingsViewModel()
    {
        ReloadFromService();
        AppServices.StashSettings.Changed += (_, _) => ReloadFromService();
    }

    partial void OnPathPrefixRemoteChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    partial void OnPathPrefixBackupChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    partial void OnUseBackupChanged(bool value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    public void ReloadFromService()
    {
        AppServices.StashSettings.ApplyToTool(
            v => Endpoint = v,
            v => ApiKey = v,
            v => PathPrefixRemote = v,
            v => PathPrefixLocal = v,
            v => PathPrefixBackup = v,
            v => UseBackup = v);
        _client.Configure(Endpoint, ApiKey);
    }

    private StashPathMapSettings CurrentPathMap() => new()
    {
        PathPrefixRemote = PathPrefixRemote,
        PathPrefixLocal = PathPrefixLocal,
        PathPrefixBackup = PathPrefixBackup,
        UseBackup = UseBackup,
    };

    private void Persist()
    {
        AppServices.StashSettings.Update(Endpoint, ApiKey, CurrentPathMap(), AppServices.Settings);
        _client.Configure(Endpoint, ApiKey);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Persist();
        Status = Loc.T("stash.connecting");
        try
        {
            var version = await _client.PingAsync();
            StashConnectionSync.BroadcastConnected(version);
            Status = Loc.F("stash.connectedWithVersion", version);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        Persist();
        Status = Loc.T("stash.settingsSaved");
    }
}
