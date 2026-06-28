using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class BitrateRowViewModel : ObservableObject
{
    public string Path { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public string SourceKbps { get; init; } = "-";

    public string TargetKbps { get; init; } = "-";

    public string EstSaveMb { get; init; } = "-";

    public string ActionCode { get; init; } = string.Empty;

    public string ReasonRaw { get; init; } = string.Empty;

    [ObservableProperty]
    private string _action = string.Empty;

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private bool _isIncluded;

    public void ApplyLocalization()
    {
        Action = BitrateScanText.LocalizeAction(ActionCode);
        Reason = BitrateScanText.LocalizeReason(ActionCode, ReasonRaw);
    }
}

public partial class BitrateChangerViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private BitrateChangerSettings _settings;
    private string? _lastScanJsonPath;

    public BitrateChangerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = BitrateConfigReader.Load();
        InputFolder = _settings.InputFolder;
        OutputFolder = _settings.OutputFolder;
        Recursive = _settings.Recursive;
        OnlyLower = _settings.OnlyLower;
        OutputBesideSource = _settings.OutputBesideSource;
        OutputMp4 = _settings.OutputMp4;
        StripAutobitrateSuffix = _settings.StripAutobitrateSuffix;
        SelectedCodec = _settings.Codec;
        SelectedAudioMode = _settings.AudioMode;
        Suffix = _settings.Suffix;
        SelectedPostAction = _settings.PostSuccessAction;
        SelectedPreset = _settings.PresetName;
        Rule2160 = _settings.RuleValues.GetValueOrDefault("2160", "12000");
        Rule1440 = _settings.RuleValues.GetValueOrDefault("1440", "8000");
        Rule1080 = _settings.RuleValues.GetValueOrDefault("1080", "5000");
        Rule720 = _settings.RuleValues.GetValueOrDefault("720", "2800");
        Rule480 = _settings.RuleValues.GetValueOrDefault("480", "1500");
        Rule360 = _settings.RuleValues.GetValueOrDefault("360", "900");
        Rule0 = _settings.RuleValues.GetValueOrDefault("0", "700");
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<BitrateRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> CodecOptions { get; } =
        ["libx264", "libx265", "libvpx-vp9", "h264_nvenc", "hevc_nvenc"];

    public IReadOnlyList<string> AudioModeOptions { get; } = ["copy", "aac_128k"];

    public IReadOnlyList<string> PostActionOptions { get; } =
        ["keep", "move_to_backup", "delete_original"];

    public IReadOnlyList<string> PresetOptions { get; } =
        ["Standard", "Leicht reduziert", "Reduziert"];

    [ObservableProperty] private string _inputFolder = string.Empty;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _onlyLower = true;
    [ObservableProperty] private bool _outputBesideSource;
    [ObservableProperty] private bool _outputMp4;
    [ObservableProperty] private bool _stripAutobitrateSuffix;
    [ObservableProperty] private string _selectedCodec = "libx264";
    [ObservableProperty] private string _selectedAudioMode = "copy";
    [ObservableProperty] private string _suffix = "_bitrate";
    [ObservableProperty] private string _selectedPostAction = "keep";
    [ObservableProperty] private string _selectedPreset = Loc.T("bitrate.presetStandard");
    [ObservableProperty] private string _rule2160 = "12000";
    [ObservableProperty] private string _rule1440 = "8000";
    [ObservableProperty] private string _rule1080 = "5000";
    [ObservableProperty] private string _rule720 = "2800";
    [ObservableProperty] private string _rule480 = "1500";
    [ObservableProperty] private string _rule360 = "900";
    [ObservableProperty] private string _rule0 = "700";
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task PickInputFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(InputFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            InputFolder = path;
            SyncSettings();
        }
    }

    [RelayCommand]
    private async Task PickOutputFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(OutputFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputFolder = path;
            SyncSettings();
        }
    }

    [RelayCommand]
    private void AutoOutput()
    {
        if (!string.IsNullOrWhiteSpace(InputFolder))
        {
            OutputFolder = Path.Combine(InputFolder, "_bitrate_output");
        }
    }

    private void SyncSettings()
    {
        _settings.InputFolder = InputFolder;
        _settings.OutputFolder = OutputFolder;
        _settings.Recursive = Recursive;
        _settings.OnlyLower = OnlyLower;
        _settings.OutputBesideSource = OutputBesideSource;
        _settings.OutputMp4 = OutputMp4;
        _settings.StripAutobitrateSuffix = StripAutobitrateSuffix;
        _settings.Codec = SelectedCodec;
        _settings.AudioMode = SelectedAudioMode;
        _settings.Suffix = Suffix;
        _settings.PostSuccessAction = SelectedPostAction;
        _settings.PresetName = SelectedPreset;
        _settings.RuleValues = new Dictionary<string, string>
        {
            ["2160"] = Rule2160, ["1440"] = Rule1440, ["1080"] = Rule1080,
            ["720"] = Rule720, ["480"] = Rule480, ["360"] = Rule360, ["0"] = Rule0,
        };
        BitrateConfigReader.Save(_settings);
    }

    [RelayCommand]
    private void LoadPreset()
    {
        var presets = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard"] = new() { ["2160"] = "12000", ["1440"] = "8000", ["1080"] = "5000", ["720"] = "2800", ["480"] = "1500", ["360"] = "900", ["0"] = "700" },
            ["Leicht reduziert"] = new() { ["2160"] = "8000", ["1440"] = "6000", ["1080"] = "4000", ["720"] = "2000", ["480"] = "1000", ["360"] = "800", ["0"] = "700" },
            ["Reduziert"] = new() { ["2160"] = "6000", ["1440"] = "4000", ["1080"] = "3000", ["720"] = "1500", ["480"] = "800", ["360"] = "600", ["0"] = "500" },
        };
        if (!presets.TryGetValue(SelectedPreset, out var rules))
        {
            return;
        }

        Rule2160 = rules["2160"]; Rule1440 = rules["1440"]; Rule1080 = rules["1080"];
        Rule720 = rules["720"]; Rule480 = rules["480"]; Rule360 = rules["360"]; Rule0 = rules["0"];
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(InputFolder) || !Directory.Exists(InputFolder))
        {
            Status = Loc.T("bitrate.inputMissing");
            return;
        }

        if (OutputBesideSource)
        {
            OutputFolder = InputFolder;
        }
        else if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            Status = Loc.T("bitrate.outputMissing");
            return;
        }

        SyncSettings();
        IsBusy = true;
        Status = Loc.T("bitrate.scanRunning");
        Rows.Clear();

        try
        {
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_cfg_{Guid.NewGuid():N}.json");
            var outPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_scan_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, BitrateConfigReader.ToJobJson(_settings));

            var result = await AppServices.JobRunner.RunBridgeAsync("bitrate_scan_job.py",
                ["--config-json", configPath, "--output-json", outPath]);

            if (!result.Success || !File.Exists(outPath))
            {
                Status = result.Message;
                return;
            }

            _lastScanJsonPath = outPath;
            var json = await File.ReadAllTextAsync(outPath);
            using var doc = JsonDocument.Parse(json);
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                var path = row.GetProperty("path").GetString() ?? "";
                var w = row.GetProperty("width").GetInt32();
                var h = row.GetProperty("height").GetInt32();
                var srcKbps = row.TryGetProperty("source_kbps", out var sk) && sk.ValueKind != JsonValueKind.Null ? sk.GetInt32().ToString() : "-";
                var tgtKbps = row.TryGetProperty("effective_target_kbps", out var tk) && tk.ValueKind != JsonValueKind.Null ? tk.GetInt32().ToString() : "-";
                var saveMb = row.TryGetProperty("estimated_saved_bytes", out var sb) && sb.ValueKind != JsonValueKind.Null
                    ? $"{sb.GetInt64() / (1024.0 * 1024.0):F1}" : "-";
                var actionCode = row.GetProperty("action").GetString() ?? "";
                var reasonRaw = row.GetProperty("reason").GetString() ?? "";
                var scanRow = new BitrateRowViewModel
                {
                    Path = path,
                    FileName = System.IO.Path.GetFileName(path),
                    Resolution = $"{w}x{h}",
                    SourceKbps = srcKbps,
                    TargetKbps = tgtKbps,
                    EstSaveMb = saveMb,
                    ActionCode = actionCode,
                    ReasonRaw = reasonRaw,
                    IsIncluded = string.Equals(actionCode, "convert", StringComparison.OrdinalIgnoreCase),
                };
                scanRow.ApplyLocalization();
                Rows.Add(scanRow);
            }

            var convertCount = Rows.Count(r => r.IsIncluded && r.ActionCode == "convert");
            Status = Loc.F("bitrate.scanDone", Rows.Count, convertCount);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastScanJsonPath) || !File.Exists(_lastScanJsonPath))
        {
            Status = Loc.T("bitrate.scanFirst");
            return;
        }

        if (OutputBesideSource)
        {
            OutputFolder = InputFolder;
        }

        SyncSettings();
        IsBusy = true;
        Status = Loc.T("bitrate.convertRunning");

        try
        {
            var includedPaths = Rows
                .Where(r => r.IsIncluded && string.Equals(r.Action, "convert", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (includedPaths.Count == 0)
            {
                Status = Loc.T("bitrate.noRowsSelected");
                return;
            }

            var scanJson = await File.ReadAllTextAsync(_lastScanJsonPath);
            using var scanDoc = JsonDocument.Parse(scanJson);
            var filteredRowJson = scanDoc.RootElement.GetProperty("rows")
                .EnumerateArray()
                .Where(row =>
                {
                    var path = row.GetProperty("path").GetString();
                    return path is not null && includedPaths.Contains(path);
                })
                .Select(row => row.GetRawText())
                .ToList();

            var rowsPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_rows_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(rowsPath, $"{{\"rows\":[{string.Join(',', filteredRowJson)}]}}");

            var configPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_cfg_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, BitrateConfigReader.ToJobJson(_settings));
            var result = await AppServices.JobRunner.RunBridgeAsync("bitrate_convert_job.py",
                ["--config-json", configPath, "--rows-json", rowsPath]);
            Status = result.Success ? result.Message : result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PreviewRenamesAsync() => await RunRenameJobAsync(previewOnly: true);

    [RelayCommand]
    private async Task ApplyRenamesAsync() => await RunRenameJobAsync(previewOnly: false);

    [RelayCommand]
    private async Task StripSuffixNowAsync() => await RunRenameJobAsync(previewOnly: false);

    private async Task RunRenameJobAsync(bool previewOnly)
    {
        if (OutputBesideSource && !string.IsNullOrWhiteSpace(InputFolder))
        {
            OutputFolder = InputFolder;
        }

        var hasInput = !string.IsNullOrWhiteSpace(InputFolder) && Directory.Exists(InputFolder);
        var hasOutput = !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder);
        if (!hasInput && !hasOutput)
        {
            Status = Loc.T("bitrate.foldersMissing");
            return;
        }

        SyncSettings();
        IsBusy = true;
        try
        {
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_rename_{Guid.NewGuid():N}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                input_folder = InputFolder,
                output_folder = OutputFolder,
                recursive = Recursive,
                rename_only_video = true,
            });
            await File.WriteAllTextAsync(configPath, json);
            var args = new List<string> { "--config-json", configPath };
            if (previewOnly)
            {
                args.Add("--preview-only");
            }

            var result = await AppServices.JobRunner.RunBridgeAsync("bitrate_rename_job.py", args);
            Status = previewOnly
                ? result.Message
                : string.IsNullOrWhiteSpace(result.Message)
                    ? "Suffix entfernt."
                    : result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void MarkAllRows() => SetAllRowsIncluded(true);

    [RelayCommand]
    private void UnmarkAllRows() => SetAllRowsIncluded(false);

    private void SetAllRowsIncluded(bool included)
    {
        foreach (var row in Rows)
        {
            if (string.Equals(row.Action, "convert", StringComparison.OrdinalIgnoreCase))
            {
                row.IsIncluded = included;
            }
            else if (!included)
            {
                row.IsIncluded = false;
            }
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
