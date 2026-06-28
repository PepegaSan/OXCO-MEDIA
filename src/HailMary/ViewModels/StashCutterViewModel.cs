using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class StashCutterViewModel : SceneCutterViewModel, IToolShellHost, IStashSettingsContext, IStashToolHost, ISplitPaneToolHost, ILocalizable
{
    protected override string CutterLocPrefix => "cutter";

    private readonly StashGraphQlClient _client = new();
    private StashCutterSettings _settings;
    private StashSceneItem? _loadedScene;

    public StashCutterViewModel(ToolDefinition tool) : base(tool, CutterWorkspacePaths.StashCutter)
    {
        _settings = StashCutterConfigReader.Load(AppServices.Settings.ProjectsRoot);
        SearchText = _settings.LastSceneSearch;
        ApplyCentralStashSettings();
        StashConnectionSync.SubscribeToCentralChanges(ApplyCentralStashSettings);
        StashConnectionSync.SubscribeToGlobalConnect(OnGlobalStashConnected);
        RefreshFromSessionStashId();
        _client.Configure(Endpoint, ApiKey);
        AppServices.Session.SessionChanged += OnGlobalSessionChanged;
    }

    private void OnGlobalSessionChanged() => UiDispatcher.Run(RefreshFromSessionStashId);

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
    [ObservableProperty] private bool _isStashBusy;
    [ObservableProperty] private StashSceneRowViewModel? _selectedResult;

    public bool BackupToggleEnabled =>
        StashPathMapper.BackupAvailable(CurrentPathMap());

    partial void OnPathPrefixRemoteChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnPathPrefixBackupChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnUseBackupChanged(bool value) => OnPropertyChanged(nameof(BackupToggleEnabled));

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
        var pathMap = CurrentPathMap();
        _settings.Endpoint = Endpoint;
        _settings.ApiKey = ApiKey;
        _settings.LastSceneSearch = SearchText;
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
        IsStashBusy = true;
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
            IsStashBusy = false;
        }
    }

    protected override void SaveSettings()
    {
        SyncSettings();
        StashCutterConfigReader.Save(_settings, LoadCurrentCutterConfig(), AppServices.Settings.ProjectsRoot);
        base.SaveSettings();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        SyncSettings();
        StashCutterConfigReader.Save(_settings, LoadCurrentCutterConfig(), AppServices.Settings.ProjectsRoot);
        IsStashBusy = true;
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
                ? Loc.T("stash.noResults")
                : $"{scenes.Count} Treffer.";
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsStashBusy = false;
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
    private void OpenInStash()
    {
        var sceneId = _loadedScene?.SceneId
                      ?? SelectedResult?.SceneId
                      ?? StashSceneIdParser.FromText(SceneId.Trim())
                      ?? (string.IsNullOrWhiteSpace(SceneId) ? null : SceneId.Trim());
        if (sceneId is null)
        {
            Status = Loc.T("markerupdater.noSceneLoadedOrSelected");
            return;
        }

        var url = StashSceneIdParser.StashBrowserUrl(Endpoint, sceneId);
        if (url is null)
        {
            Status = Loc.T("stash.stashUrlMissingEndpoint");
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
        _ = LoadSceneByIdAsync(SelectedResult.SceneId);
    }

    private async Task LoadSceneByIdAsync(string sceneId)
    {
        SyncSettings();
        IsStashBusy = true;
        Status = Loc.T("stash.loadingScene");
        try
        {
            await EnsureStashReachableAsync();
            var scene = await _client.GetSceneAsync(sceneId);
            _loadedScene = scene;
            var resolved = StashPathResolver.Resolve(scene.Path, CurrentPathMap());
            var mapped = resolved.ResolvedPath;
            LoadedSummary = $"Geladen: {scene.SceneId} | {scene.Title} | {mapped}";
            AppServices.Session.SetStashSceneId(scene.SceneId);

            if (resolved.FileExists && !string.IsNullOrWhiteSpace(mapped))
            {
                InputPath = mapped;
                Status = Loc.T("stashcutter.sceneLoadedVideoReady");
            }
            else if (!string.IsNullOrWhiteSpace(mapped))
            {
                Status = $"Szene geladen, aber Datei nicht gefunden: {mapped}";
            }
            else
            {
                Status = Loc.T("stashcutter.noFilePathInStash");
            }

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
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsStashBusy = false;
        }
    }

    private CutterConfigReader.CutterConfig LoadCurrentCutterConfig() =>
        CutterConfigReader.Load(AppServices.Settings.ProjectsRoot, CutterWorkspacePaths.StashCutter);
}
