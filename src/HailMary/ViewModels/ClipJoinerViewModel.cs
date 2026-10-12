using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class ClipJoinerClipRow : ObservableObject
{
    [ObservableProperty] private int _index;

    /// <summary>"0:12 · 1920×1080" once probed.</summary>
    [ObservableProperty] private string _info = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public double DurationSeconds { get; set; }

    public (int Width, int Height)? Size { get; set; }
}

public sealed partial class ClipJoinerBatchRow : ObservableObject
{
    public string OutputName { get; init; } = "joined";

    public IReadOnlyList<string> Files { get; init; } = [];

    public string Summary => Loc.F("clipjoiner.batchSummary", OutputName, Files.Count);
}

public partial class ClipJoinerViewModel : ObservableObject, IToolShellHost, ISplitPaneToolHost, ILocalizable
{
    private static readonly string[] ModeIds = ["ffmpeg", "davinci", "both"];

    private readonly ToolDefinition _tool;
    private readonly List<ClipJoinerBatchRow> _batchJobs = [];
    private int _selectedBatchIndex = -1;
    private bool _rebuildingRows;

    public ClipJoinerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var settings = ClipJoinerConfigReader.Load();
        OutputDir = settings.OutputDir;
        OutputName = settings.OutputName;
        SelectedMode = settings.Mode;
        SelectedEncoder = FfmpegEncoderCatalog.LabelFor(
            FfmpegEncoderCatalog.SoftHardwareWithCopy,
            settings.FfmpegEncoder);
        DavinciPreset = settings.DavinciPreset;
        DavinciTimeoutSeconds = settings.DavinciTimeoutSeconds;
        // Drag & drop reordering in the list changes this collection directly.
        ClipRows.CollectionChanged += ClipRows_OnCollectionChanged;
        RefreshBatchRows();
    }

    public string Description => ToolText.Description(_tool);

    /// <summary>The clips in join order (source of truth; the list reorders it in place).</summary>
    public ObservableCollection<ClipJoinerClipRow> ClipRows { get; } = [];

    public ObservableCollection<ClipJoinerBatchRow> BatchRows { get; } = [];

    public IReadOnlyList<string> ModeOptions { get; } = ModeIds;

    public IReadOnlyList<string> EncoderOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.SoftHardwareWithCopy);

    [ObservableProperty] private string _outputDir = string.Empty;
    [ObservableProperty] private string _outputName = "joined";
    [ObservableProperty] private string _selectedMode = "ffmpeg";
    [ObservableProperty] private string _selectedEncoder = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.SoftHardwareWithCopy,
        FfmpegEncoderCatalog.DefaultSoftHardwareId);
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private string _davinciPreset = "YouTube - 1080p";
    [ObservableProperty] private double _davinciTimeoutSeconds = 3600;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _clipsSummary = string.Empty;
    [ObservableProperty] private bool _hasMixedSizes;

    public bool HasClips => ClipRows.Count > 0;

    /// <summary>ComboBox index for the localized mode list.</summary>
    public int ModeIndex
    {
        get => Math.Max(0, Array.IndexOf(ModeIds, SelectedMode));
        set
        {
            if (value >= 0 && value < ModeIds.Length)
            {
                SelectedMode = ModeIds[value];
            }
        }
    }

    public string RunCurrentLabel => Loc.T("clipjoiner.runCurrent");

    public string RunBatchLabel => BatchRows.Count > 0
        ? Loc.F("clipjoiner.runBatch", BatchRows.Count)
        : Loc.T("common.startBatch");

    partial void OnSelectedModeChanged(string value) => OnPropertyChanged(nameof(ModeIndex));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void SetSelectedBatchIndex(int index) => _selectedBatchIndex = index;

    public void AddDroppedPaths(IEnumerable<string> paths) => AddClips(ExpandVideoPaths(paths));

    private static IEnumerable<string> ExpandVideoPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                result.AddRange(
                    Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
                        .Where(VideoPathDropHelper.IsVideoFile)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            else if (VideoPathDropHelper.IsVideoFile(full))
            {
                result.Add(full);
            }
        }

        return result;
    }

    private void AddClips(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(ClipRows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (existing.Add(full))
            {
                var row = new ClipJoinerClipRow { Path = full };
                ClipRows.Add(row);
                _ = ProbeClipAsync(row);
            }
        }
    }

    private async Task ProbeClipAsync(ClipJoinerClipRow row)
    {
        var duration = await FfprobeHelper.ProbeDurationSecondsAsync(row.Path) ?? 0;
        var size = await FfprobeHelper.ProbeVideoSizeAsync(row.Path);
        row.DurationSeconds = duration;
        row.Size = size;
        var parts = new List<string>();
        if (duration > 0)
        {
            parts.Add(FormatDuration(duration));
        }

        if (size is { } s)
        {
            parts.Add($"{s.Width}×{s.Height}");
        }

        row.Info = string.Join(" · ", parts);
        UpdateSummary();
    }

    private void ClipRows_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildingRows)
        {
            return;
        }

        for (var i = 0; i < ClipRows.Count; i++)
        {
            ClipRows[i].Index = i + 1;
        }

        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (ClipRows.Count == 0)
        {
            ClipsSummary = string.Empty;
            HasMixedSizes = false;
            return;
        }

        var total = ClipRows.Sum(r => r.DurationSeconds);
        ClipsSummary = Loc.F("clipjoiner.clipsSummary", ClipRows.Count, FormatDuration(total));
        var sizes = ClipRows.Where(r => r.Size is not null).Select(r => r.Size!.Value).Distinct().Count();
        HasMixedSizes = sizes > 1;
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void MoveRow(ClipJoinerClipRow row, int delta)
    {
        var idx = ClipRows.IndexOf(row);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= ClipRows.Count)
        {
            return;
        }

        ClipRows.Move(idx, target);
    }

    private void RefreshBatchRows()
    {
        BatchRows.Clear();
        foreach (var job in _batchJobs)
        {
            BatchRows.Add(job);
        }

        OnPropertyChanged(nameof(RunBatchLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    [RelayCommand]
    private async Task PickClipsAsync()
    {
        var paths = await FilePickerHelper.PickVideosAsync();
        if (paths.Count > 0)
        {
            AddClips(paths);
        }
    }

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
    private void RemoveClip(ClipJoinerClipRow? row)
    {
        if (row is not null)
        {
            ClipRows.Remove(row);
        }
    }

    [RelayCommand]
    private void MoveClipUp(ClipJoinerClipRow? row)
    {
        if (row is not null)
        {
            MoveRow(row, -1);
        }
    }

    [RelayCommand]
    private void MoveClipDown(ClipJoinerClipRow? row)
    {
        if (row is not null)
        {
            MoveRow(row, 1);
        }
    }

    [RelayCommand]
    private void SortClipsByName()
    {
        var sorted = ClipRows
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _rebuildingRows = true;
        try
        {
            ClipRows.Clear();
            foreach (var row in sorted)
            {
                ClipRows.Add(row);
            }
        }
        finally
        {
            _rebuildingRows = false;
        }

        ClipRows_OnCollectionChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    [RelayCommand]
    private void ClearClips() => ClipRows.Clear();

    [RelayCommand]
    private void AddCurrentToBatch()
    {
        if (ClipRows.Count == 0)
        {
            Status = Loc.T("clipjoiner.noClips");
            return;
        }

        var name = string.IsNullOrWhiteSpace(OutputName) ? "joined" : OutputName.Trim();
        _batchJobs.Add(new ClipJoinerBatchRow
        {
            OutputName = name,
            Files = ClipRows.Select(r => r.Path).ToList(),
        });
        RefreshBatchRows();
        Status = Loc.F("clipjoiner.batchAdded", name, ClipRows.Count);
    }

    [RelayCommand]
    private void RemoveSelectedBatch()
    {
        if (_selectedBatchIndex < 0 || _selectedBatchIndex >= _batchJobs.Count)
        {
            return;
        }

        _batchJobs.RemoveAt(_selectedBatchIndex);
        _selectedBatchIndex = -1;
        RefreshBatchRows();
    }

    [RelayCommand]
    private void ClearBatch()
    {
        _batchJobs.Clear();
        _selectedBatchIndex = -1;
        RefreshBatchRows();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        ClipJoinerConfigReader.Save(new ClipJoinerSettings
        {
            OutputDir = OutputDir,
            OutputName = OutputName,
            Mode = SelectedMode,
            FfmpegEncoder = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardwareWithCopy, SelectedEncoder),
            DavinciPreset = DavinciPreset,
            DavinciTimeoutSeconds = DavinciTimeoutSeconds,
        });
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private async Task RunCurrentAsync() => await RunBridgeAsync(single: true);

    [RelayCommand]
    private async Task RunBatchAsync() => await RunBridgeAsync(single: false);

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    private async Task RunBridgeAsync(bool single)
    {
        if (single && ClipRows.Count == 0)
        {
            Status = Loc.T("clipjoiner.noClips");
            return;
        }

        if (!single && _batchJobs.Count == 0)
        {
            Status = Loc.T("clipjoiner.batchEmpty");
            return;
        }

        SaveSettings();
        IsBusy = true;
        Status = single ? Loc.T("clipjoiner.running") : Loc.T("clipjoiner.batchRunning");

        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_clip_joiner_{Guid.NewGuid():N}.json");
        var outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_clip_joiner_out_{Guid.NewGuid():N}.json");
        var common = new Dictionary<string, object?>
        {
            ["output_dir"] = OutputDir,
            ["output_name"] = OutputName,
            ["mode"] = SelectedMode,
            ["ffmpeg_encoder"] = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardwareWithCopy, SelectedEncoder),
            ["target_fps"] = TargetFps,
            ["davinci_preset"] = DavinciPreset,
            ["davinci_timeout_s"] = DavinciTimeoutSeconds,
            ["davinci_api_path"] = DavinciResolvePaths.GetApiModulesPath(),
        };
        if (single)
        {
            common["files"] = ClipRows.Select(r => r.Path).ToList();
        }
        else
        {
            common["batch_jobs"] = _batchJobs.Select(j => new { output_name = j.OutputName, files = j.Files }).ToList();
        }

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(common));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "clip_joiner_job.py",
                ["--config-json", configPath, "--output-json", outputPath]);
            Status = result.Message;
        }
        finally
        {
            IsBusy = false;
            try
            {
                File.Delete(configPath);
                File.Delete(outputPath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
