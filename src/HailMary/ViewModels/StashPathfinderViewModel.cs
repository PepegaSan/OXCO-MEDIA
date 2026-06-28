using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.ViewModels;

public sealed partial class StashSceneRowViewModel : ObservableObject
{
    public string SceneId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Date { get; init; } = string.Empty;

    public string RemotePath { get; init; } = string.Empty;

    public string MappedPath { get; init; } = string.Empty;

    public string Display => $"{SceneId} | {Title} | {MappedPath}";
}

public partial class StashPathfinderViewModel : SessionAwareViewModel, IToolShellHost, IStashSettingsContext, IStashToolHost, ISplitPaneToolHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly StashGraphQlClient _client = new();
    private StashPathfinderSettings _settings;
    private StashSceneItem? _loadedScene;

    public StashPathfinderViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = StashPathfinderConfigReader.Load();
        SearchText = _settings.LastSceneSearch;
        ApplyCentralStashSettings();
        StashConnectionSync.SubscribeToCentralChanges(ApplyCentralStashSettings);
        StashConnectionSync.SubscribeToGlobalConnect(OnGlobalStashConnected);
        RefreshFromSessionStashId();
        _client.Configure(Endpoint, ApiKey);
    }

    private void OnGlobalStashConnected(string version)
    {
        ConnectionInfo = StashConnectionStatus.FormatConnected(version);
    }

    private void ApplyCentralStashSettings()
    {
        StashConnectionSync.ApplyCentralToTool(
            v => Endpoint = v,
            v => ApiKey = v,
            v => PathPrefixRemote = v,
            v => PathPrefixLocal = v,
            v => PathPrefixBackup = v,
            v => UseBackup = v);
        _client.Configure(Endpoint, ApiKey);
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<StashSceneRowViewModel> Results { get; } = [];

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _sceneId = string.Empty;
    [ObservableProperty] private string _pathPrefixRemote = "/data/";
    [ObservableProperty] private string _pathPrefixLocal = string.Empty;
    [ObservableProperty] private string _pathPrefixBackup = string.Empty;
    [ObservableProperty] private bool _useBackup;
    [ObservableProperty] private string _connectionInfo = Loc.T("stash.notConnected");
    [ObservableProperty] private string _loadedSummary = Loc.T("stash.noSceneLoaded");
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private StashSceneRowViewModel? _selectedResult;

    public bool BackupToggleEnabled =>
        StashPathMapper.BackupAvailable(CurrentPathMap());

    partial void OnPathPrefixRemoteChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnPathPrefixBackupChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnUseBackupChanged(bool value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    protected override void RefreshFromSession()
    {
        base.RefreshFromSession();
        RefreshFromSessionStashId();
    }

    private void RefreshFromSessionStashId()
    {
        var sid = AppServices.Session.Current.StashSceneId;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            SceneId = sid.Trim();
        }
    }

    private StashPathMapSettings CurrentPathMap() => new()
    {
        PathPrefixRemote = PathPrefixRemote,
        PathPrefixLocal = PathPrefixLocal,
        PathPrefixBackup = PathPrefixBackup,
        UseBackup = UseBackup,
    };

    private string MapPath(string? path) =>
        StashPathResolver.Resolve(path, CurrentPathMap()).ResolvedPath;

    private async Task EnsureStashReachableAsync()
    {
        await StashConnectionHelper.EnsureReachableAsync(_client, ConnectionInfo, v => ConnectionInfo = v);
    }

    private void SyncSettings()
    {
        _settings.LastSceneSearch = SearchText;
        var pathMap = CurrentPathMap();
        _settings.Endpoint = Endpoint;
        _settings.ApiKey = ApiKey;
        _settings.PathMap = pathMap;
        StashConnectionSync.PushToolToCentral(Endpoint, ApiKey, pathMap);
        _client.Configure(Endpoint, ApiKey);
    }

    private StashSceneRowViewModel ToRow(StashSceneItem item) => new()
    {
        SceneId = item.SceneId,
        Title = item.Title,
        Date = item.Date,
        RemotePath = item.Path,
        MappedPath = MapPath(item.Path),
    };

    [RelayCommand]
    private async Task ConnectAsync()
    {
        SyncSettings();
        IsBusy = true;
        Status = Loc.T("stash.connecting");
        try
        {
            var version = await _client.PingAsync();
            ConnectionInfo = StashConnectionStatus.FormatConnected(version);
            Status = Loc.T("stash.connected");
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SyncSettings();
        StashPathfinderConfigReader.Save(_settings);
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            Status = Loc.T("stash.searchTermRequired");
            return;
        }

        SyncSettings();
        StashPathfinderConfigReader.Save(_settings);
        IsBusy = true;
        Status = Loc.T("stash.searchRunning");
        Results.Clear();
        _loadedScene = null;
        LoadedSummary = Loc.T("stash.noSceneLoaded");

        try
        {
            await EnsureStashReachableAsync();
            var scenes = await _client.FindScenesAsync(SearchText);
            foreach (var scene in scenes)
            {
                Results.Add(ToRow(scene));
            }

            Status = scenes.Count == 0
                ? "Keine Treffer — Suchbegriff ändern oder Stash prüfen."
                : $"{scenes.Count} Treffer.";
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadSceneAsync()
    {
        if (string.IsNullOrWhiteSpace(SceneId))
        {
            Status = Loc.T("stash.sceneIdRequired");
            return;
        }

        var sid = StashSceneIdParser.FromText(SceneId.Trim()) ?? SceneId.Trim();
        SceneId = sid;
        await LoadSceneByIdAsync(sid);
    }

    [RelayCommand]
    private async Task LoadFromSessionAsync()
    {
        RefreshFromSessionStashId();
        if (string.IsNullOrWhiteSpace(SceneId))
        {
            Status = Loc.T("stash.noLastScene");
            return;
        }

        await LoadSceneByIdAsync(SceneId);
    }

    [RelayCommand]
    private async Task LoadFromClipboardAsync()
    {
        var read = await StashClipboardHelper.TryReadSceneIdAsync();
        var sid = read.SceneId
                  ?? StashSceneIdParser.ResolveForLoad(
                      null,
                      SceneId,
                      _loadedScene?.SceneId,
                      SelectedResult?.SceneId);
        if (sid is null)
        {
            Status = read.Error ?? "Keine Szenen-ID erkannt.";
            return;
        }

        SceneId = sid;
        await LoadSceneByIdAsync(sid);
    }

    [RelayCommand]
    private async Task ReloadLoadedAsync()
    {
        if (_loadedScene is null)
        {
            Status = Loc.T("stash.noLoadedScene");
            return;
        }

        await LoadSceneByIdAsync(_loadedScene.SceneId);
    }

    private async Task LoadSceneByIdAsync(string sceneId)
    {
        SyncSettings();
        IsBusy = true;
        Status = Loc.T("stash.loadingScene");
        try
        {
            await EnsureStashReachableAsync();
            var scene = await _client.GetSceneAsync(sceneId);
            _loadedScene = scene;
            var mapped = MapPath(scene.Path);
            LoadedSummary = $"Geladen: {scene.SceneId} | {scene.Title} | {mapped}";
            AppServices.Session.SetStashSceneId(scene.SceneId);

            var existing = Results.FirstOrDefault(r => r.SceneId == scene.SceneId);
            if (existing is null)
            {
                Results.Insert(0, ToRow(scene));
                SelectedResult = Results[0];
            }
            else
            {
                SelectedResult = existing;
            }

            Status = Loc.T("stash.sceneLoaded");
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private StashSceneItem? ActiveScene()
    {
        if (_loadedScene is not null)
        {
            return _loadedScene;
        }

        if (SelectedResult is not null)
        {
            return new StashSceneItem(
                SelectedResult.SceneId,
                SelectedResult.Title,
                SelectedResult.Date,
                SelectedResult.RemotePath);
        }

        return null;
    }

    private string? ActiveMappedPath()
    {
        var scene = ActiveScene();
        if (scene is null || string.IsNullOrWhiteSpace(scene.Path))
        {
            return null;
        }

        return MapPath(scene.Path);
    }

    [RelayCommand]
    private void CopyFolderPath()
    {
        var mapped = ActiveMappedPath();
        if (string.IsNullOrWhiteSpace(mapped))
        {
            Status = Loc.T("stash.noFilePath");
            return;
        }

        var folder = Directory.Exists(mapped) ? mapped : Path.GetDirectoryName(mapped);
        if (string.IsNullOrWhiteSpace(folder))
        {
            Status = Loc.T("stash.folderPathEmpty");
            return;
        }

        CopyText(folder);
        Status = Loc.T("stash.folderCopied");
    }

    [RelayCommand]
    private void CopyFilename()
    {
        var mapped = ActiveMappedPath();
        if (string.IsNullOrWhiteSpace(mapped))
        {
            Status = Loc.T("stash.noFilePath");
            return;
        }

        CopyText(Path.GetFileName(mapped));
        Status = Loc.T("stash.filenameCopied");
    }

    [RelayCommand]
    private void CopyFullPath()
    {
        var mapped = ActiveMappedPath();
        if (string.IsNullOrWhiteSpace(mapped))
        {
            Status = Loc.T("stash.noFilePath");
            return;
        }

        CopyText(mapped);
        Status = Loc.T("stash.fullPathCopied");
    }

    [RelayCommand]
    private void OpenExplorer()
    {
        var mapped = ActiveMappedPath();
        if (string.IsNullOrWhiteSpace(mapped))
        {
            Status = Loc.T("stash.noFilePath");
            return;
        }

        var folder = Directory.Exists(mapped) ? mapped : Path.GetDirectoryName(mapped);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Status = Loc.T("stash.folderUnreachable");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            Status = Loc.T("stash.explorerOpened");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenInStash()
    {
        var scene = ActiveScene();
        if (scene is null)
        {
            Status = Loc.T("stash.noSceneSelected");
            return;
        }

        var url = StashSceneIdParser.StashBrowserUrl(Endpoint, scene.SceneId);
        if (url is null)
        {
            Status = Loc.T("stash.stashUrlMissing");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Status = Loc.T("stash.stashOpened");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void UseSelectedResult()
    {
        if (SelectedResult is null)
        {
            Status = Loc.T("stash.selectResult");
            return;
        }

        SceneId = SelectedResult.SceneId;
        _loadedScene = new StashSceneItem(
            SelectedResult.SceneId,
            SelectedResult.Title,
            SelectedResult.Date,
            SelectedResult.RemotePath);
        LoadedSummary = $"Auswahl: {SelectedResult.SceneId} | {SelectedResult.Title} | {SelectedResult.MappedPath}";
        AppServices.Session.SetStashSceneId(SelectedResult.SceneId);
        Status = Loc.T("stash.resultAdopted");
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
