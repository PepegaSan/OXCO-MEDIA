using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class SceneCutterViewModel : ToolIoViewModel, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly CutterWorkspacePaths _workspace;
    private CutterConfigReader.CutterConfig _cutterConfig;
    private double? _pendingIn;
    private double? _pendingOut;
    private double _videoDuration;
    private bool _suppressAutosave;

    public SceneCutterViewModel(ToolDefinition tool, CutterWorkspacePaths? workspace = null)
        : base(tool.Id)
    {
        _tool = tool;
        _workspace = workspace ?? CutterWorkspacePaths.VideoCutter;
        _cutterConfig = CutterConfigReader.Load(AppServices.Settings.ProjectsRoot, _workspace);
        DavinciPreset = _cutterConfig.DavinciPreset;
        DavinciOutputDir = _cutterConfig.DavinciOutputDir;
        if (string.IsNullOrWhiteSpace(FfmpegOutputDir) && !string.IsNullOrWhiteSpace(OutputDir))
        {
            FfmpegOutputDir = OutputDir;
        }
        Scenes.CollectionChanged += Scenes_OnCollectionChanged;
        UpdatePendingMarks();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<SceneEntry> Scenes { get; } = [];

    public IReadOnlyList<string> CodecOptions { get; } =
        ["libx264", "libx265", "h264_nvenc", "hevc_nvenc"];

    [ObservableProperty] private string _selectedCodec = "libx264";
    [ObservableProperty] private string _positionDisplay = "00:00:00.000";
    [ObservableProperty] private string _durationDisplay = "00:00:00.000";
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private double _sliderMaximum = 1;
    [ObservableProperty] private string _pendingMarks = "In: —   Out: —";
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasVideo;
    [ObservableProperty] private bool _partialSelectMode;
    [ObservableProperty] private bool _chronologicalSort;
    [ObservableProperty] private bool _safeOutput = true;
    [ObservableProperty] private bool _autoRemoveExported;
    [ObservableProperty] private bool _useDavinciExport;
    [ObservableProperty] private string _davinciPreset = "YouTube - 1080p";
    [ObservableProperty] private string _davinciOutputDir = string.Empty;
    [ObservableProperty] private string _ffmpegOutputDir = string.Empty;

    protected virtual string CutterLocPrefix => "scenecutter";

    public bool ShowSceneCheckboxes => PartialSelectMode;

    public string PartialSelectButtonText =>
        PartialSelectMode
            ? Loc.T($"{CutterLocPrefix}.partialSelectAll")
            : Loc.T($"{CutterLocPrefix}.partialSelectPartial");

    partial void OnPartialSelectModeChanged(bool value)
    {
        if (value)
        {
            foreach (var scene in Scenes)
            {
                scene.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(ShowSceneCheckboxes));
        OnPropertyChanged(nameof(PartialSelectButtonText));
        ScheduleAutosave();
    }

    partial void OnChronologicalSortChanged(bool value) => ScheduleAutosave();

    protected override void OnInputPathUpdated(string value)
    {
        HasVideo = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
        if (!HasVideo)
        {
            Scenes.Clear();
            _pendingIn = null;
            _pendingOut = null;
            UpdatePendingMarks();
            return;
        }

        TryRestoreAutosave(value);
    }

    public void SetDuration(double seconds)
    {
        _videoDuration = Math.Max(0, seconds);
        SliderMaximum = Math.Max(0.001, _videoDuration);
        DurationDisplay = FormatTime(_videoDuration);
    }

    public void SetPosition(double seconds)
    {
        SliderValue = Math.Clamp(seconds, 0, SliderMaximum);
        PositionDisplay = FormatTime(seconds);
    }

    public void MarkInAt(double seconds)
    {
        _pendingIn = Math.Clamp(seconds, 0, SliderMaximum);
        UpdatePendingMarks();
    }

    public void MarkOutAt(double seconds)
    {
        _pendingOut = Math.Clamp(seconds, 0, SliderMaximum);
        UpdatePendingMarks();
    }

    public void SeekToScene(SceneEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SetPosition(entry.StartSeconds);
    }

    [RelayCommand]
    private void TogglePartialSelectMode()
    {
        PartialSelectMode = !PartialSelectMode;
        Status = PartialSelectMode
            ? Loc.T($"{CutterLocPrefix}.partialSelectOnHint")
            : Loc.T($"{CutterLocPrefix}.partialSelectOffHint");
    }

    [RelayCommand]
    private void AddScene()
    {
        if (_pendingIn is null || _pendingOut is null)
        {
            Status = Loc.T("scenecutter.markInOutRequired");
            return;
        }

        if (_pendingOut <= _pendingIn)
        {
            Status = Loc.T("scenecutter.outAfterIn");
            return;
        }

        Scenes.Add(SceneEntry.FromSeconds(_pendingIn.Value, _pendingOut.Value, Scenes.Count + 1));
        RenumberScenes();
        Status = Loc.F($"{CutterLocPrefix}.sceneCount", Scenes.Count);
        _pendingIn = null;
        _pendingOut = null;
        UpdatePendingMarks();
        ScheduleAutosave();
    }

    [RelayCommand]
    private void AddFullVideoScene()
    {
        if (!HasVideo || _videoDuration <= 0)
        {
            Status = Loc.T("scenecutter.loadVideoForFullScene");
            return;
        }

        if (PartialSelectMode)
        {
            PartialSelectMode = false;
        }

        Scenes.Clear();
        Scenes.Add(SceneEntry.FromSeconds(0, _videoDuration, 1));
        RenumberScenes();
        Status = Loc.T("scenecutter.fullVideoSceneAdded");
        ScheduleAutosave();
    }

    [RelayCommand]
    private void RemoveScene(SceneEntry? entry)
    {
        if (entry is not null && Scenes.Contains(entry))
        {
            Scenes.Remove(entry);
            RenumberScenes();
            Status = Scenes.Count == 0 ? Loc.T("common.ready") : Loc.F($"{CutterLocPrefix}.sceneCount", Scenes.Count);
            ScheduleAutosave();
        }
    }

    [RelayCommand]
    private void ClearScenes()
    {
        Scenes.Clear();
        CutterSceneAutosave.Clear(AppServices.Settings.ProjectsRoot, _workspace);
        Status = Loc.T("scenecutter.scenesCleared");
    }

    public void ApplyScenePosition(SceneEntry entry)
    {
        var raw = entry.PositionInput.Trim();
        entry.PositionInput = string.Empty;
        if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var target))
        {
            return;
        }

        var fromIdx = Scenes.IndexOf(entry);
        if (fromIdx < 0)
        {
            return;
        }

        MoveSceneToIndex(fromIdx, target - 1);
        RenumberScenes();
        ScheduleAutosave();
    }

    public void CommitSceneTimes(SceneEntry entry)
    {
        if (entry.TryApplyTimes(out var error))
        {
            ScheduleAutosave();
            return;
        }

        Status = error;
    }

    [RelayCommand]
    private void MoveSceneUp(SceneEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var idx = Scenes.IndexOf(entry);
        if (idx <= 0)
        {
            return;
        }

        MoveSceneToIndex(idx, idx - 1);
        RenumberScenes();
        ScheduleAutosave();
    }

    [RelayCommand]
    private void MoveSceneDown(SceneEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var idx = Scenes.IndexOf(entry);
        if (idx < 0 || idx >= Scenes.Count - 1)
        {
            return;
        }

        MoveSceneToIndex(idx, idx + 1);
        RenumberScenes();
        ScheduleAutosave();
    }

    [RelayCommand]
    protected virtual void SaveSettings()
    {
        _cutterConfig = new CutterConfigReader.CutterConfig(
            DavinciPreset.Trim(),
            DavinciOutputDir.Trim(),
            string.Empty,
            string.Empty,
            string.Empty);
        CutterConfigReader.Save(_cutterConfig, AppServices.Settings.ProjectsRoot, _workspace);
        Status = Loc.T("scenecutter.settingsSaved");
    }

    [RelayCommand]
    private async Task ExportFfmpegAsync() => await ExportAsync("scene_cutter_export_job.py", BuildFfmpegArgs);

    [RelayCommand]
    private async Task ExportDavinciAsync() => await ExportAsync("scene_cutter_davinci_job.py", BuildDavinciArgs);

    [RelayCommand]
    private void OpenFullGui()
    {
        var input = RequireInputPath();
        if (input is not null)
        {
            PersistAutosave();
            CutterAutosaveSeeder.TrySeed(AppServices.Settings.ProjectsRoot, input, AppServices.Log, _workspace);
        }

        AppServices.Launcher.Launch(_tool);
    }

    private async Task ExportAsync(string bridge, Func<List<(double Start, double End)>, List<string>> buildArgs)
    {
        if (IsRunning)
        {
            return;
        }

        var input = RequireInputPath();
        if (input is null)
        {
            Status = Loc.T("scenecutter.noVideoFile");
            return;
        }

        List<(double Start, double End)> pairs;
        List<SceneEntry> exportedEntries;
        try
        {
            (pairs, exportedEntries) = GetScenesForExport();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }

        IsRunning = true;
        Status = Loc.T("cutter.exportRunning");
        PersistAutosave();

        try
        {
            var args = buildArgs(pairs);
            var result = await AppServices.JobRunner.RunBridgeAsync(bridge, args);
            if (result.Success && AutoRemoveExported && exportedEntries.Count > 0)
            {
                UiDispatcher.Run(() =>
                {
                    foreach (var entry in exportedEntries)
                    {
                        Scenes.Remove(entry);
                    }

                    RenumberScenes();
                    ScheduleAutosave();
                });
            }

            UiDispatcher.Run(() => Status = result.Success ? Loc.T("audiocleaner.exportDone") : result.Message);
        }
        finally
        {
            UiDispatcher.Run(() => IsRunning = false);
        }
    }

    private List<string> BuildFfmpegArgs(List<(double Start, double End)> pairs)
    {
        var input = RequireInputPath()!;
        var scenesFile = SceneExportArgs.WriteScenesTempFile(pairs);
        var args = new List<string>
        {
            "--input", input,
            "--codec", SelectedCodec,
            "--scenes-file", scenesFile,
        };

        if (!SafeOutput)
        {
            args.Add("--no-safe-output");
        }

        var outDir = FfmpegOutputDir.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            outDir = OptionalOutputDir() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            args.Add("--output-dir");
            args.Add(outDir);
        }

        return args;
    }

    private List<string> BuildDavinciArgs(List<(double Start, double End)> pairs)
    {
        var input = RequireInputPath()!;
        var scenesFile = SceneExportArgs.WriteScenesTempFile(pairs);
        var outDir = DavinciOutputDir.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            outDir = _cutterConfig.DavinciOutputDir;
        }

        var args = new List<string>
        {
            "--input", input,
            "--preset", DavinciPreset.Trim(),
            "--scenes-file", scenesFile,
            "--output-dir", outDir,
        };

        if (!string.IsNullOrWhiteSpace(DavinciResolvePaths.GetExePath()))
        {
            args.Add("--resolve-exe");
            args.Add(DavinciResolvePaths.GetExePath());
        }

        if (!string.IsNullOrWhiteSpace(DavinciResolvePaths.GetApiModulesPath()))
        {
            args.Add("--resolve-modules");
            args.Add(DavinciResolvePaths.GetApiModulesPath());
        }

        if (!string.IsNullOrWhiteSpace(DavinciResolvePaths.GetFusionScriptDll()))
        {
            args.Add("--resolve-dll");
            args.Add(DavinciResolvePaths.GetFusionScriptDll());
        }

        return args;
    }

    private (List<(double Start, double End)> Pairs, List<SceneEntry> Entries) GetScenesForExport()
    {
        if (Scenes.Count == 0)
        {
            throw new InvalidOperationException(Loc.T($"{CutterLocPrefix}.minOneScene"));
        }

        foreach (var scene in Scenes)
        {
            if (!scene.TryApplyTimes(out var error))
            {
                throw new InvalidOperationException(Loc.F("scenecutter.sceneError", scene.Number, error));
            }
        }

        IEnumerable<SceneEntry> source = PartialSelectMode
            ? Scenes.Where(s => s.IsSelected)
            : Scenes;

        var list = source.ToList();
        if (PartialSelectMode && list.Count == 0)
        {
            throw new InvalidOperationException(Loc.T($"{CutterLocPrefix}.partialNeedChecked"));
        }

        var pairs = list.Select(s => (s.StartSeconds, s.EndSeconds)).ToList();
        if (ChronologicalSort)
        {
            pairs = pairs.OrderBy(p => p.StartSeconds).ThenBy(p => p.EndSeconds).ToList();
        }

        return (pairs, list);
    }

    private void TryRestoreAutosave(string inputPath)
    {
        var data = CutterSceneAutosave.TryLoad(AppServices.Settings.ProjectsRoot, inputPath, _workspace);
        if (data is null)
        {
            if (!_suppressAutosave)
            {
                Scenes.Clear();
            }

            _pendingIn = null;
            _pendingOut = null;
            UpdatePendingMarks();
            return;
        }

        _suppressAutosave = true;
        Scenes.Clear();
        foreach (var scene in data.Scenes)
        {
            Scenes.Add(scene);
        }

        ChronologicalSort = data.ChronoSort;
        PartialSelectMode = data.SceneSelectMode;
        RenumberScenes();
        _suppressAutosave = false;
        Status = Loc.F($"{CutterLocPrefix}.autosaveRestored", Scenes.Count);
    }

    private void ScheduleAutosave()
    {
        if (_suppressAutosave)
        {
            return;
        }

        var input = InputPath;
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input) || Scenes.Count == 0)
        {
            return;
        }

        foreach (var scene in Scenes)
        {
            scene.TryApplyTimes(out _);
        }

        CutterSceneAutosave.Save(
            AppServices.Settings.ProjectsRoot,
            input,
            Scenes,
            ChronologicalSort,
            PartialSelectMode,
            _workspace);
    }

    private void PersistAutosave() => ScheduleAutosave();

    private void MoveSceneToIndex(int fromIdx, int toIdx)
    {
        if (fromIdx == toIdx || fromIdx < 0 || fromIdx >= Scenes.Count)
        {
            return;
        }

        toIdx = Math.Clamp(toIdx, 0, Scenes.Count - 1);
        var item = Scenes[fromIdx];
        Scenes.RemoveAt(fromIdx);
        Scenes.Insert(toIdx, item);
    }

    private void RenumberScenes()
    {
        for (var i = 0; i < Scenes.Count; i++)
        {
            Scenes[i].Number = i + 1;
        }
    }

    private void UpdatePendingMarks()
    {
        var ins = _pendingIn.HasValue ? FormatTime(_pendingIn.Value) : "—";
        var outs = _pendingOut.HasValue ? FormatTime(_pendingOut.Value) : "—";
        PendingMarks = Loc.F($"{CutterLocPrefix}.pendingMarks", ins, outs);
    }

    private static string FormatTime(double sec) => TimecodeHelper.FormatDisplay(sec);
}
