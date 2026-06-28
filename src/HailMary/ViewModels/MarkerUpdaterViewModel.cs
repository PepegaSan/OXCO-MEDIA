using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class StashMarkerRowViewModel : ObservableObject
{
    public string MarkerId { get; init; } = string.Empty;

    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty] private string _secondsText = "0";

    [ObservableProperty] private string _endSecondsText = string.Empty;

    [ObservableProperty] private string _primaryTagName = string.Empty;

    public string? PrimaryTagId { get; set; }

    public string Display
    {
        get
        {
            var start = TimecodeHelper.FormatDisplayFromText(SecondsText);
            if (string.IsNullOrWhiteSpace(EndSecondsText))
            {
                return $"{Title} @ {start}";
            }

            return $"{Title} @ {TimecodeHelper.FormatRangeFromText(SecondsText, EndSecondsText)}";
        }
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Display));

    partial void OnSecondsTextChanged(string value) => OnPropertyChanged(nameof(Display));

    partial void OnEndSecondsTextChanged(string value) => OnPropertyChanged(nameof(Display));
}

public partial class MarkerUpdaterViewModel : SessionAwareViewModel, IToolShellHost, IStashSettingsContext, IStashToolHost, ISplitPaneToolHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly StashGraphQlClient _client = new();
    private MarkerUpdaterSettings _settings;
    private StashSceneDetails? _loadedScene;
    private Dictionary<string, string> _tagNameToId = new(StringComparer.OrdinalIgnoreCase);
    private bool _navigateBySearchResults;

    public MarkerUpdaterViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = MarkerUpdaterConfigReader.Load();
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
    public ObservableCollection<string> SceneTags { get; } = [];
    public ObservableCollection<StashMarkerRowViewModel> Markers { get; } = [];
    public ObservableCollection<string> AllTagNames { get; } = [];

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _sceneId = string.Empty;
    [ObservableProperty] private string _pathPrefixRemote = "/data/";
    [ObservableProperty] private string _pathPrefixLocal = string.Empty;
    [ObservableProperty] private string _pathPrefixBackup = string.Empty;
    [ObservableProperty] private bool _useBackup;
    [ObservableProperty] private string _connectionInfo = Loc.T("stash.notConnected");

    public bool BackupToggleEnabled =>
        StashPathMapper.BackupAvailable(CurrentPathMap());

    partial void OnPathPrefixRemoteChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    partial void OnPathPrefixBackupChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    partial void OnUseBackupChanged(bool value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    [ObservableProperty] private string _loadedSummary = Loc.T("stash.noSceneLoaded");
    [ObservableProperty] private bool _hasLoadedScene;
    [ObservableProperty] private string _loadedSceneId = string.Empty;
    [ObservableProperty] private string _loadedSceneTitle = string.Empty;
    [ObservableProperty] private string _stashFilePath = string.Empty;
    [ObservableProperty] private string _resolvedLocalPath = string.Empty;
    [ObservableProperty] private bool _localFileExists;
    [ObservableProperty] private string _pathSourceLabel = string.Empty;

    public bool ShowLocalFileMissing => HasLoadedScene && !LocalFileExists;

    public string LocalFileMissingHint => ShowLocalFileMissing
        ? "Datei nicht lokal gefunden — in Einstellungen NAS-Pfad (z. B. H:\\VideoStash) prüfen."
        : string.Empty;

    partial void OnHasLoadedSceneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLocalFileMissing));
        OnPropertyChanged(nameof(LocalFileMissingHint));
        PreviousSceneCommand.NotifyCanExecuteChanged();
        NextSceneCommand.NotifyCanExecuteChanged();
    }

    partial void OnLocalFileExistsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLocalFileMissing));
        OnPropertyChanged(nameof(LocalFileMissingHint));
    }

    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private StashSceneRowViewModel? _selectedResult;
    [ObservableProperty] private StashMarkerRowViewModel? _selectedMarker;
    [ObservableProperty] private string _newTagName = string.Empty;
    [ObservableProperty] private string? _selectedSceneTag;
    [ObservableProperty] private string _newMarkerTitle = string.Empty;
    [ObservableProperty] private string _newMarkerSeconds = "0";
    [ObservableProperty] private string _newMarkerEndSeconds = string.Empty;
    [ObservableProperty] private string _newMarkerTagName = string.Empty;
    [ObservableProperty] private string _sceneTitle = string.Empty;
    [ObservableProperty] private string _sceneDetails = string.Empty;

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

    private StashPathMapSettings CurrentPathMap() => new()
    {
        PathPrefixRemote = PathPrefixRemote,
        PathPrefixLocal = PathPrefixLocal,
        PathPrefixBackup = PathPrefixBackup,
        UseBackup = UseBackup,
    };

    private string MapPath(string? path) =>
        StashPathResolver.Resolve(path, CurrentPathMap()).ResolvedPath;

    private void ApplyLoadedScenePaths(StashSceneDetails scene)
    {
        var resolved = StashPathResolver.Resolve(scene.Path, CurrentPathMap());
        HasLoadedScene = true;
        LoadedSceneId = scene.SceneId;
        LoadedSceneTitle = string.IsNullOrWhiteSpace(scene.Title) ? "(ohne Titel)" : scene.Title;
        StashFilePath = resolved.StashPath;
        ResolvedLocalPath = resolved.ResolvedPath;
        LocalFileExists = resolved.FileExists;
        PathSourceLabel = resolved.FileExists ? resolved.SourceLabel : string.Empty;
        LoadedSummary = $"{LoadedSceneId} — {LoadedSceneTitle}";
        SetVideoPath(resolved);
    }

    private StashSceneRowViewModel ToRow(StashSceneItem item) => new()
    {
        SceneId = item.SceneId,
        Title = item.Title,
        Date = item.Date,
        RemotePath = item.Path,
        MappedPath = MapPath(item.Path),
    };

    private async Task RefreshAllTagsAsync()
    {
        var tags = await _client.GetTagsAsync();
        _tagNameToId = tags.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        AllTagNames.Clear();
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            AllTagNames.Add(tag.Name);
        }
    }

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
            NotifyStashConnectionChanged();
            await RefreshAllTagsAsync();
            Status = Loc.T("stash.connected");
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            NotifyStashConnectionChanged();
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
        MarkerUpdaterConfigReader.Save(_settings);
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
        MarkerUpdaterConfigReader.Save(_settings);
        IsBusy = true;
        Status = Loc.T("stash.searchRunning");
        Results.Clear();

        try
        {
            await EnsureStashReachableAsync();
            var scenes = await _client.FindScenesAsync(SearchText);
            foreach (var scene in scenes)
            {
                Results.Add(ToRow(scene));
            }

            Status = scenes.Count == 0 ? "Keine Treffer." : $"{scenes.Count} Treffer.";
            _navigateBySearchResults = scenes.Count > 0;
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            NotifyStashConnectionChanged();
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
        await LoadSceneByIdAsync(sid, navigateBySearchResults: false);
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

        await LoadSceneByIdAsync(SceneId, navigateBySearchResults: false);
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
        await LoadSceneByIdAsync(sid, navigateBySearchResults: false);
    }

    [RelayCommand]
    private void OpenInStash()
    {
        var sceneId = ActiveSceneId();
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

    [RelayCommand(CanExecute = nameof(CanNavigateAdjacentScene))]
    private async Task PreviousSceneAsync() => await NavigateAdjacentSceneAsync(-1);

    [RelayCommand(CanExecute = nameof(CanNavigateAdjacentScene))]
    private async Task NextSceneAsync() => await NavigateAdjacentSceneAsync(1);

    private bool CanNavigateAdjacentScene() => !IsBusy && HasLoadedScene;

    private async Task NavigateAdjacentSceneAsync(int delta)
    {
        var cur = _loadedScene?.SceneId.Trim();
        if (string.IsNullOrEmpty(cur))
        {
            Status = Loc.T("markerupdater.noSceneLoaded");
            return;
        }

        if (_navigateBySearchResults)
        {
            if (!TryGetResultsIndex(cur, out var index))
            {
                Status = Loc.T("markerupdater.notInResults");
                return;
            }

            var newIndex = index + delta;
            if (newIndex >= 0 && newIndex < Results.Count)
            {
                var row = Results[newIndex];
                SceneId = row.SceneId;
                SelectedResult = row;
                await LoadSceneByIdAsync(row.SceneId);
                return;
            }

            Status = Loc.T("markerupdater.noMoreInDirection");
            return;
        }

        try
        {
            await EnsureStashReachableAsync();
            var adjacentId = await _client.FindAdjacentSceneIdAsync(cur, delta > 0);
            if (adjacentId is null)
            {
                Status = delta < 0
                    ? "Keine vorherige Szene (Stash-ID)."
                    : "Keine nächste Szene (Stash-ID).";
                return;
            }

            SceneId = adjacentId;
            await LoadSceneByIdAsync(adjacentId);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private bool TryGetResultsIndex(string sceneId, out int index)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i].SceneId == sceneId)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private string? ActiveSceneId()
    {
        if (_loadedScene is not null)
        {
            return _loadedScene.SceneId;
        }

        if (SelectedResult is not null)
        {
            return SelectedResult.SceneId;
        }

        if (string.IsNullOrWhiteSpace(SceneId))
        {
            return null;
        }

        return StashSceneIdParser.FromText(SceneId.Trim()) ?? SceneId.Trim();
    }

    [RelayCommand]
    private async Task UseSelectedResultAsync()
    {
        if (SelectedResult is null)
        {
            Status = Loc.T("stash.selectResult");
            return;
        }

        SceneId = SelectedResult.SceneId;
        await LoadSceneByIdAsync(SelectedResult.SceneId, navigateBySearchResults: true);
    }

    private async Task LoadSceneByIdAsync(string sceneId, bool? navigateBySearchResults = null)
    {
        if (navigateBySearchResults.HasValue)
        {
            _navigateBySearchResults = navigateBySearchResults.Value;
        }

        SyncSettings();
        IsBusy = true;
        Status = Loc.T("stash.loadingScene");
        try
        {
            await EnsureStashReachableAsync();
            await RefreshAllTagsAsync();
            var scene = await _client.GetSceneDetailsAsync(sceneId);
            _loadedScene = scene;
            AppServices.Session.SetStashSceneId(scene.SceneId);
            ApplyLoadedScenePaths(scene);
            SceneTitle = scene.Title;
            SceneDetails = scene.Details;

            SceneTags.Clear();
            SelectedSceneTag = null;
            foreach (var tag in scene.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                SceneTags.Add(tag.Name);
                _tagNameToId[tag.Name] = tag.Id;
            }

            Markers.Clear();
            foreach (var marker in scene.Markers.OrderBy(m => m.Seconds))
            {
                Markers.Add(new StashMarkerRowViewModel
                {
                    MarkerId = marker.Id,
                    Title = marker.Title,
                    SecondsText = TimecodeHelper.FormatForEditor(marker.Seconds),
                    EndSecondsText = marker.EndSeconds.HasValue
                        ? TimecodeHelper.FormatForEditor(marker.EndSeconds.Value)
                        : string.Empty,
                    PrimaryTagName = marker.PrimaryTag?.Name ?? "",
                    PrimaryTagId = marker.PrimaryTag?.Id,
                });
            }

            Status = LocalFileExists
                ? $"Szene geladen — {SceneTags.Count} Tag(s), {Markers.Count} Marker. Video: {PathSourceLabel}"
                : $"Szene geladen — {SceneTags.Count} Tag(s), {Markers.Count} Marker. Video nicht gefunden (Pfad-Mapping prüfen).";
            CreateMarkerAtSelectedTagCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            NotifyStashConnectionChanged();
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshMarkersAsync()
    {
        if (_loadedScene is null)
        {
            return;
        }

        var scene = await _client.GetSceneDetailsAsync(_loadedScene.SceneId);
        _loadedScene = scene;

        Markers.Clear();
        foreach (var marker in scene.Markers.OrderBy(m => m.Seconds))
        {
            Markers.Add(new StashMarkerRowViewModel
            {
                MarkerId = marker.Id,
                Title = marker.Title,
                SecondsText = TimecodeHelper.FormatForEditor(marker.Seconds),
                EndSecondsText = marker.EndSeconds.HasValue
                    ? TimecodeHelper.FormatForEditor(marker.EndSeconds.Value)
                    : string.Empty,
                PrimaryTagName = marker.PrimaryTag?.Name ?? "",
                PrimaryTagId = marker.PrimaryTag?.Id,
            });
        }
    }

    private async Task EnsureStashReachableAsync()
    {
        await StashConnectionHelper.EnsureReachableAsync(_client, ConnectionInfo, v => ConnectionInfo = v);
        NotifyStashConnectionChanged();
    }

    private void NotifyStashConnectionChanged()
    {
        OnPropertyChanged(nameof(IStashToolHost.IsStashConnected));
        OnPropertyChanged(nameof(IStashToolHost.StashConnectionTooltip));
    }

    partial void OnConnectionInfoChanged(string value) => NotifyStashConnectionChanged();

    partial void OnSelectedSceneTagChanged(string? value)
    {
        RemoveSelectedTagCommand.NotifyCanExecuteChanged();
        CreateMarkerAtSelectedTagCommand.NotifyCanExecuteChanged();
        if (!string.IsNullOrWhiteSpace(value))
        {
            NewTagName = value;
            NewMarkerTagName = value;
        }
    }

    partial void OnNewTagNameChanged(string value) => RemoveSelectedTagCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void AddTagToScene()
    {
        var name = NewTagName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Status = Loc.T("markerupdater.tagNameRequired");
            return;
        }

        if (SceneTags.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = Loc.T("markerupdater.tagAlreadyOnScene");
            return;
        }

        SceneTags.Add(name);
        if (!AllTagNames.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            AllTagNames.Add(name);
        }

        NewTagName = string.Empty;
        Status = Loc.T("markerupdater.tagAddedPending");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSceneTag))]
    private void RemoveSelectedTag()
    {
        var name = ResolveSceneTagName();
        if (name is null)
        {
            Status = Loc.T("markerupdater.tagSelectOrEnter");
            return;
        }

        var existing = SceneTags.FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Status = Loc.T("markerupdater.tagNotOnScene");
            return;
        }

        SceneTags.Remove(existing);
        SelectedSceneTag = null;
        NewTagName = string.Empty;
        Status = Loc.T("markerupdater.tagRemovedPending");
    }

    private bool CanRemoveSceneTag() =>
        !string.IsNullOrWhiteSpace(SelectedSceneTag) || !string.IsNullOrWhiteSpace(NewTagName);

    private string? ResolveSceneTagName()
    {
        if (!string.IsNullOrWhiteSpace(SelectedSceneTag))
        {
            return SelectedSceneTag.Trim();
        }

        return string.IsNullOrWhiteSpace(NewTagName) ? null : NewTagName.Trim();
    }

    [RelayCommand(CanExecute = nameof(CanCreateMarkerAtSelectedTag))]
    private async Task CreateMarkerAtSelectedTagAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSceneTag))
        {
            Status = Loc.T("markerupdater.tagSelect");
            return;
        }

        NewMarkerTagName = SelectedSceneTag;
        if (HasVideo)
        {
            NewMarkerSeconds = TimecodeHelper.FormatForEditor(SliderValue);
        }

        await AddMarkerAsync();
    }

    private bool CanCreateMarkerAtSelectedTag() =>
        _loadedScene is not null
        && !string.IsNullOrWhiteSpace(SelectedSceneTag)
        && !IsBusy;

    [RelayCommand]
    private async Task SaveTagsAsync()
    {
        if (_loadedScene is null)
        {
            Status = "Keine Szene geladen.";
            return;
        }

        IsBusy = true;
        Status = Loc.T("markerupdater.savingScene");
        try
        {
            var tagIds = new List<string>();
            foreach (var name in SceneTags)
            {
                if (!_tagNameToId.TryGetValue(name, out var id))
                {
                    id = await _client.CreateTagAsync(name);
                    _tagNameToId[name] = id;
                }

                tagIds.Add(id);
            }

            await _client.UpdateSceneAsync(
                _loadedScene.SceneId,
                title: SceneTitle.Trim(),
                details: SceneDetails,
                tagIds: tagIds);
            await LoadSceneByIdAsync(_loadedScene.SceneId);
            Status = Loc.T("markerupdater.sceneSaved");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddMarkerAsync()
    {
        if (_loadedScene is null)
        {
            Status = "Keine Szene geladen.";
            return;
        }

        if (!TryParseSeconds(NewMarkerSeconds, out var seconds))
        {
            Status = Loc.T("markerupdater.invalidStartSeconds");
            return;
        }

        double? endSeconds = null;
        if (!string.IsNullOrWhiteSpace(NewMarkerEndSeconds))
        {
            if (!TryParseSeconds(NewMarkerEndSeconds, out var end))
            {
                Status = Loc.T("markerupdater.invalidEndSeconds");
                return;
            }

            endSeconds = end;
        }

        var tagName = NewMarkerTagName.Trim();
        if (string.IsNullOrEmpty(tagName))
        {
            Status = Loc.T("markerupdater.primaryTagRequired");
            return;
        }

        if (!_tagNameToId.TryGetValue(tagName, out var tagId))
        {
            tagId = await _client.CreateTagAsync(tagName);
            _tagNameToId[tagName] = tagId;
            if (!AllTagNames.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            {
                AllTagNames.Add(tagName);
            }
        }

        IsBusy = true;
        try
        {
            await _client.CreateMarkerAsync(
                _loadedScene.SceneId,
                NewMarkerTitle.Trim(),
                seconds,
                endSeconds,
                tagId);
            await RefreshMarkersAsync();
            NewMarkerTitle = string.Empty;
            NewMarkerSeconds = "0";
            NewMarkerEndSeconds = string.Empty;
            Status = Loc.T("markerupdater.markerCreated");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedMarkerAsync()
    {
        if (SelectedMarker is null)
        {
            Status = Loc.T("markerupdater.markerSelect");
            return;
        }

        if (!TryParseSeconds(NewMarkerSeconds, out var seconds))
        {
            Status = Loc.T("markerupdater.invalidStartSeconds");
            return;
        }

        double? endSeconds = null;
        if (!string.IsNullOrWhiteSpace(NewMarkerEndSeconds))
        {
            if (!TryParseSeconds(NewMarkerEndSeconds, out var end))
            {
                Status = Loc.T("markerupdater.invalidEndSeconds");
                return;
            }

            endSeconds = end;
        }

        var tagName = NewMarkerTagName.Trim();
        if (string.IsNullOrEmpty(tagName))
        {
            Status = Loc.T("markerupdater.primaryTagRequired");
            return;
        }

        if (!_tagNameToId.TryGetValue(tagName, out var tagId))
        {
            tagId = await _client.CreateTagAsync(tagName);
            _tagNameToId[tagName] = tagId;
        }

        IsBusy = true;
        try
        {
            await _client.UpdateMarkerAsync(
                SelectedMarker.MarkerId,
                NewMarkerTitle.Trim(),
                seconds,
                endSeconds,
                tagId);
            await RefreshMarkersAsync();
            Status = Loc.T("markerupdater.markerUpdated");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedMarkerAsync()
    {
        if (SelectedMarker is null)
        {
            Status = Loc.T("markerupdater.markerSelect");
            return;
        }

        IsBusy = true;
        try
        {
            await _client.DeleteMarkerAsync(SelectedMarker.MarkerId);
            SelectedMarker = null;
            await RefreshMarkersAsync();
            Status = Loc.T("markerupdater.markerDeleted");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    private static bool TryParseSeconds(string text, out double seconds) =>
        TimecodeHelper.TryParseFlexible(text, out seconds, out _);
}
