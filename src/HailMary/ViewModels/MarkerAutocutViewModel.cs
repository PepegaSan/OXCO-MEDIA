using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class MarkerAutocutRowViewModel : ObservableObject
{
    public StashExportMarkerRow Source { get; init; } = null!;

    [ObservableProperty] private bool _isSelected = true;
}

public partial class MarkerAutocutViewModel : ObservableObject, IToolShellHost, IStashSettingsContext, IStashToolHost, ISplitPaneToolHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly StashGraphQlClient _client = new();
    private MarkerAutocutSettings _settings;

    public static IReadOnlyList<string> ExportModeOptions { get; } = ["per_file", "compilation"];

    public IReadOnlyList<string> ModeChoices => ExportModeOptions;

    public MarkerAutocutViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = MarkerAutocutConfigReader.Load();
        NameFilter = _settings.LastNameFilter;
        PathFilter = _settings.LastPathFilter;
        NameExclude = _settings.LastNameExclude;
        PathExclude = _settings.LastPathExclude;
        MediaRoot = _settings.MediaRoot;
        PathSortMode = ParsePathSortMode(_settings.PathSortMode);
        ExportMode = _settings.ExportMode;
        OutputDir = _settings.OutputDir;
        RenderPreset = _settings.RenderPreset;
        MinSegmentSeconds = _settings.MinSegmentSeconds;
        InclusiveEnd = _settings.InclusiveEnd;
        DoRender = _settings.DoRender;
        DefaultFps = _settings.DefaultFps;
        ApplyCentralStashSettings();
        StashConnectionSync.SubscribeToCentralChanges(ApplyCentralStashSettings);
        StashConnectionSync.SubscribeToGlobalConnect(OnGlobalStashConnected);
        _client.Configure(Endpoint, ApiKey);
    }

    private void OnGlobalStashConnected(string version) =>
        ConnectionInfo = StashConnectionStatus.FormatConnected(version);

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

    public ObservableCollection<MarkerAutocutRowViewModel> Markers { get; } = [];

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _pathPrefixRemote = "/data/";
    [ObservableProperty] private string _pathPrefixLocal = string.Empty;
    [ObservableProperty] private string _pathPrefixBackup = string.Empty;
    [ObservableProperty] private bool _useBackup;
    [ObservableProperty] private string _connectionInfo = Loc.T("stash.notConnected");
    [ObservableProperty] private string _nameFilter = string.Empty;
    [ObservableProperty] private string _pathFilter = string.Empty;
    [ObservableProperty] private string _exportMode = "per_file";
    [ObservableProperty] private string _outputDir = string.Empty;
    [ObservableProperty] private string _renderPreset = string.Empty;
    [ObservableProperty] private double _minSegmentSeconds = 1;
    [ObservableProperty] private bool _inclusiveEnd = true;
    [ObservableProperty] private bool _doRender;
    [ObservableProperty] private double _defaultFps = 25;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    public bool BackupToggleEnabled =>
        StashPathMapper.BackupAvailable(CurrentPathMap());

    partial void OnPathPrefixRemoteChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnPathPrefixBackupChanged(string value) => OnPropertyChanged(nameof(BackupToggleEnabled));
    partial void OnUseBackupChanged(bool value) => OnPropertyChanged(nameof(BackupToggleEnabled));

    private StashPathMapSettings CurrentPathMap() => new()
    {
        PathPrefixRemote = PathPrefixRemote,
        PathPrefixLocal = PathPrefixLocal,
        PathPrefixBackup = PathPrefixBackup,
        UseBackup = UseBackup,
    };

    private void PersistSettings()
    {
        _settings = new MarkerAutocutSettings
        {
            LastNameFilter = NameFilter,
            LastPathFilter = PathFilter,
            LastNameExclude = NameExclude,
            LastPathExclude = PathExclude,
            MediaRoot = MediaRoot,
            PathSortMode = PathSortMode.ToString().ToLowerInvariant(),
            ExportMode = ExportMode,
            OutputDir = OutputDir,
            RenderPreset = RenderPreset,
            MinSegmentSeconds = MinSegmentSeconds,
            InclusiveEnd = InclusiveEnd,
            DoRender = DoRender,
            DefaultFps = DefaultFps,
        };
        MarkerAutocutConfigReader.Save(_settings);
        StashConnectionSync.PushToolToCentral(Endpoint, ApiKey, CurrentPathMap());
        _client.Configure(Endpoint, ApiKey);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        PersistSettings();
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
        PersistSettings();
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private async Task FetchMarkersAsync()
    {
        PersistSettings();
        IsBusy = true;
        Status = Loc.T("markerautocut.loadingMarkers");
        Markers.Clear();
        try
        {
            await StashConnectionHelper.EnsureReachableAsync(_client, ConnectionInfo, v => ConnectionInfo = v);
            var rows = await _client.FindSceneMarkersAsync(NameFilter, PathFilter);
            foreach (var row in rows)
            {
                if (!PassesClientFilters(row))
                {
                    continue;
                }

                var vm = new MarkerAutocutRowViewModel
                {
                    Source = row,
                    ResolvedFilePath = MapFilePath(row.FilePath),
                };
                vm.AttachOwner(this);
                Markers.Add(vm);
            }

            ApplyMarkerSort();
            RebuildExportOrderFromSelection();
            RebuildDisplayItems();
            Status = Markers.Count == 0
                ? Loc.T("markerautocut.noMarkers")
                : Loc.F("markerautocut.markersLoaded", Markers.Count);
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
    private void SelectAllMarkers()
    {
        foreach (var row in Markers)
        {
            row.IsSelected = true;
        }

        RebuildExportOrderFromSelection();
    }

    [RelayCommand]
    private void ClearMarkerSelection()
    {
        foreach (var row in Markers)
        {
            row.IsSelected = false;
        }

        ExportOrder.Clear();
    }

    private static MarkerPathSortMode ParsePathSortMode(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "asc" => MarkerPathSortMode.Asc,
            "desc" => MarkerPathSortMode.Desc,
            _ => MarkerPathSortMode.Stash,
        };

    [RelayCommand]
    private async Task PickOutputDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(OutputDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputDir = path;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var markerRows = ExportOrder.Select(o => o.Marker).ToList();
        if (markerRows.Count == 0)
        {
            Status = Loc.T("markerautocut.emptyExportOrder");
            return;
        }

        if (IsPerFileMode())
        {
            markerRows = ChronologicalWithinFilesPreserveOrder(markerRows);
        }

        var missingEnd = markerRows.Count(m => string.IsNullOrWhiteSpace(m.Source.EndSeconds));
        if (missingEnd > 0)
        {
            Status = Loc.F("markerautocut.missingEndWarning", missingEnd, MinSegmentSeconds);
        }

        var ordered = markerRows.Select(m => m.Source).ToList();

        PersistSettings();
        IsBusy = true;
        Status = Loc.T("markerautocut.exportRunning");
        try
        {
            var map = CurrentPathMap();
            var configPath = Path.Combine(Path.GetTempPath(), $"hailmary_marker_autocut_{Guid.NewGuid():N}.json");
            var payload = new
            {
                mode = ExportMode,
                rows = ordered.Select(r => new Dictionary<string, string>
                {
                    ["marker_id"] = r.MarkerId,
                    ["marker_title"] = r.MarkerTitle,
                    ["start_seconds"] = r.StartSeconds,
                    ["end_seconds"] = r.EndSeconds,
                    ["primary_tag"] = r.PrimaryTag,
                    ["primary_tag_id"] = r.PrimaryTagId,
                    ["secondary_tags"] = r.SecondaryTags,
                    ["scene_id"] = r.SceneId,
                    ["scene_title"] = r.SceneTitle,
                    ["file_path"] = MapFilePath(r.FilePath),
                    ["file_frame_rate"] = r.FileFrameRate,
                }).ToList(),
                options = new Dictionary<string, object?>
                {
                    ["min_segment_seconds"] = MinSegmentSeconds,
                    ["inclusive_end"] = InclusiveEnd,
                    ["default_fps"] = DefaultFps,
                    ["do_render"] = DoRender,
                    ["output_dir"] = OutputDir,
                    ["output_name_prefix"] = "StashMarker",
                    ["render_preset"] = string.IsNullOrWhiteSpace(RenderPreset) ? null : RenderPreset,
                    ["path_docker_prefix"] = map.PathPrefixRemote,
                    ["path_windows_root"] = map.PathPrefixLocal,
                    ["backup_path_prefix"] = map.PathPrefixBackup,
                    ["use_backup"] = map.UseBackup,
                    ["media_root"] = string.IsNullOrWhiteSpace(MediaRoot) ? null : MediaRoot,
                },
            };

            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "marker_autocut_export_job.py",
                ["--config-json", configPath]);

            try
            {
                File.Delete(configPath);
            }
            catch
            {
                // ignore
            }

            Status = result.Success ? result.Message : result.Message;
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

    partial void OnConnectionInfoChanged(string value)
    {
        OnPropertyChanged(nameof(IStashToolHost.IsStashConnected));
        OnPropertyChanged(nameof(IStashToolHost.StashConnectionTooltip));
    }
}
