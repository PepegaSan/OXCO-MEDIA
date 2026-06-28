using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class ClipJoinerClipRow : ObservableObject
{
    public int Index { get; set; }

    public string Path { get; init; } = string.Empty;

    public string DisplayName => System.IO.Path.GetFileName(Path);
}

public sealed partial class ClipJoinerBatchRow : ObservableObject
{
    public string OutputName { get; init; } = "joined";

    public IReadOnlyList<string> Files { get; init; } = [];

    public string Summary => $"{OutputName} ({Files.Count} Clips)";
}

public partial class ClipJoinerViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly List<string> _clips = [];
    private readonly List<ClipJoinerBatchRow> _batchJobs = [];
    private int _selectedClipIndex = -1;
    private int _selectedBatchIndex = -1;

    public ClipJoinerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var settings = ClipJoinerConfigReader.Load();
        OutputDir = settings.OutputDir;
        OutputName = settings.OutputName;
        SelectedMode = settings.Mode;
        SelectedEncoder = settings.FfmpegEncoder;
        DavinciPreset = settings.DavinciPreset;
        DavinciTimeoutSeconds = settings.DavinciTimeoutSeconds;
        RefreshClipRows();
        RefreshBatchRows();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<ClipJoinerClipRow> ClipRows { get; } = [];

    public ObservableCollection<ClipJoinerBatchRow> BatchRows { get; } = [];

    public IReadOnlyList<string> ModeOptions { get; } = ["ffmpeg", "davinci", "both"];

    public IReadOnlyList<string> EncoderOptions { get; } =
        ["copy", "nvidia_h264", "nvidia_hevc", "cpu", "cpu_hevc"];

    [ObservableProperty] private string _outputDir = string.Empty;
    [ObservableProperty] private string _outputName = "joined";
    [ObservableProperty] private string _selectedMode = "ffmpeg";
    [ObservableProperty] private string _selectedEncoder = "nvidia_h264";
    [ObservableProperty] private string _davinciPreset = "YouTube - 1080p";
    [ObservableProperty] private double _davinciTimeoutSeconds = 3600;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    public string RunCurrentLabel => "Jetzt zusammenfügen";

    public string RunBatchLabel => BatchRows.Count > 0 ? $"Batch starten ({BatchRows.Count})" : "Batch starten";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void SetSelectedClipIndex(int index) => _selectedClipIndex = index;

    public void SetSelectedBatchIndex(int index) => _selectedBatchIndex = index;

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        AddClips(ExpandVideoPaths(paths));
    }

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
                        .Where(VideoPathDropHelper.IsVideoFile));
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
        var existing = new HashSet<string>(_clips, StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (existing.Add(full))
            {
                _clips.Add(full);
                added = true;
            }
        }

        if (added)
        {
            RefreshClipRows();
        }
    }

    private void RefreshClipRows()
    {
        ClipRows.Clear();
        for (var i = 0; i < _clips.Count; i++)
        {
            ClipRows.Add(new ClipJoinerClipRow { Index = i + 1, Path = _clips[i] });
        }

        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
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
    private void RemoveSelectedClips()
    {
        if (_selectedClipIndex < 0 || _selectedClipIndex >= _clips.Count)
        {
            return;
        }

        _clips.RemoveAt(_selectedClipIndex);
        _selectedClipIndex = -1;
        RefreshClipRows();
    }

    [RelayCommand]
    private void MoveClipUp()
    {
        var idx = _selectedClipIndex;
        if (idx <= 0 || idx >= _clips.Count)
        {
            return;
        }

        (_clips[idx - 1], _clips[idx]) = (_clips[idx], _clips[idx - 1]);
        _selectedClipIndex = idx - 1;
        RefreshClipRows();
    }

    [RelayCommand]
    private void MoveClipDown()
    {
        var idx = _selectedClipIndex;
        if (idx < 0 || idx >= _clips.Count - 1)
        {
            return;
        }

        (_clips[idx + 1], _clips[idx]) = (_clips[idx], _clips[idx + 1]);
        _selectedClipIndex = idx + 1;
        RefreshClipRows();
    }

    [RelayCommand]
    private void SortClipsByName()
    {
        var selectedPath = _selectedClipIndex >= 0 && _selectedClipIndex < _clips.Count
            ? _clips[_selectedClipIndex]
            : null;
        _clips.Sort((a, b) => string.Compare(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        _selectedClipIndex = selectedPath is not null ? _clips.IndexOf(selectedPath) : -1;
        RefreshClipRows();
    }

    [RelayCommand]
    private void ClearClips()
    {
        _clips.Clear();
        _selectedClipIndex = -1;
        RefreshClipRows();
    }

    [RelayCommand]
    private void AddCurrentToBatch()
    {
        if (_clips.Count == 0)
        {
            Status = Loc.T("clipjoiner.noClips");
            return;
        }

        var name = string.IsNullOrWhiteSpace(OutputName) ? "joined" : OutputName.Trim();
        _batchJobs.Add(new ClipJoinerBatchRow
        {
            OutputName = name,
            Files = _clips.ToList(),
        });
        RefreshBatchRows();
        Status = $"Batch: {name} mit {_clips.Count} Clips hinzugefügt.";
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
            FfmpegEncoder = SelectedEncoder,
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
        if (single && _clips.Count == 0)
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
        Status = single ? "Zusammenfügen…" : "Batch läuft…";

        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_clip_joiner_{Guid.NewGuid():N}.json");
        var outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_clip_joiner_out_{Guid.NewGuid():N}.json");

        object payload = single
            ? new
            {
                output_dir = OutputDir,
                output_name = OutputName,
                mode = SelectedMode,
                ffmpeg_encoder = SelectedEncoder,
                davinci_preset = DavinciPreset,
                davinci_timeout_s = DavinciTimeoutSeconds,
                davinci_api_path = DavinciResolvePaths.GetApiModulesPath(),
                files = _clips,
            }
            : new
            {
                output_dir = OutputDir,
                output_name = OutputName,
                mode = SelectedMode,
                ffmpeg_encoder = SelectedEncoder,
                davinci_preset = DavinciPreset,
                davinci_timeout_s = DavinciTimeoutSeconds,
                davinci_api_path = DavinciResolvePaths.GetApiModulesPath(),
                batch_jobs = _batchJobs.Select(j => new { output_name = j.OutputName, files = j.Files }).ToList(),
            };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
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
