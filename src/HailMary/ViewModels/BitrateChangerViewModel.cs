using System.Collections.ObjectModel;
using System.ComponentModel;
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

    public string VideoCodec { get; init; } = "-";

    public string SourceKbps { get; init; } = "-";

    public string TargetKbps { get; init; } = "-";

    public string EstSaveMb { get; init; } = "-";

    public double EstSaveMbValue { get; init; }

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

public partial class BitrateChangerViewModel : ObservableObject, IToolShellHost, ISplitPaneToolHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private BitrateChangerSettings _settings;
    private string? _lastScanJsonPath;
    private CancellationTokenSource? _jobCts;

    public BitrateChangerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = BitrateConfigReader.Load();
        InputFolder = _settings.InputFolder;
        OutputFolder = _settings.OutputFolder;
        Recursive = _settings.Recursive;
        OnlyLower = _settings.OnlyLower;
        SelectedScanMode = NormalizeScanMode(_settings.ScanMode);
        OutputBesideSource = _settings.OutputBesideSource;
        OutputMp4 = _settings.OutputMp4;
        StripAutobitrateSuffix = _settings.StripAutobitrateSuffix;
        SelectedCodec = FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.LibNvencVp9, _settings.Codec);
        TargetFps = _settings.TargetFps;
        SelectedAudioMode = _settings.AudioMode;
        SelectedRateControl = NormalizeRateControl(_settings.RateControl);
        SelectedCrfPreset = NormalizeCrfPreset(_settings.CrfPreset);
        CrfValue = ClampCrf(_settings.Crf > 0 ? _settings.Crf : CrfFromPreset(SelectedCrfPreset));
        ParallelJobs = Math.Clamp(_settings.ParallelJobs <= 0 ? 1 : _settings.ParallelJobs, 1, 8);
        FfmpegThreads = Math.Clamp(_settings.FfmpegThreads, 0, 64);
        LowPriority = _settings.LowPriority;
        SelectedX264Preset = NormalizeX264Preset(_settings.X264Preset);
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
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.LibNvencVp9);

    public IReadOnlyList<string> AudioModeOptions { get; } = ["copy", "aac_128k"];

    public IReadOnlyList<string> PostActionOptions { get; } =
        ["keep", "move_to_backup", "delete_original"];

    /// <summary>Asks the user (set by the panel); used before originals get deleted.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>ComboBox index for the localized post-action list.</summary>
    public int PostActionIndex
    {
        get => Math.Max(0, PostActionOptions.ToList().IndexOf(SelectedPostAction));
        set
        {
            if (value >= 0 && value < PostActionOptions.Count)
            {
                SelectedPostAction = PostActionOptions[value];
            }
        }
    }

    /// <summary>ComboBox index for the localized audio-mode list.</summary>
    public int AudioModeIndex
    {
        get => Math.Max(0, AudioModeOptions.ToList().IndexOf(SelectedAudioMode));
        set
        {
            if (value >= 0 && value < AudioModeOptions.Count)
            {
                SelectedAudioMode = AudioModeOptions[value];
            }
        }
    }

    partial void OnSelectedPostActionChanged(string value) => OnPropertyChanged(nameof(PostActionIndex));

    partial void OnSelectedAudioModeChanged(string value) => OnPropertyChanged(nameof(AudioModeIndex));

    public IReadOnlyList<string> PresetOptions { get; } =
        ["Standard", "Leicht reduziert", "Reduziert"];

    public IReadOnlyList<string> X264PresetOptions { get; } =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium"];

    [ObservableProperty] private string _inputFolder = string.Empty;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _onlyLower = true;
    [ObservableProperty] private string _selectedScanMode = "bitrate";
    [ObservableProperty] private bool _outputBesideSource;

    public bool ScanModeIsBitrate
    {
        get => SelectedScanMode == "bitrate";
        set
        {
            if (value)
            {
                SelectedScanMode = "bitrate";
            }
        }
    }

    public bool ScanModeIsProblemCodecs
    {
        get => SelectedScanMode == "problem_codecs";
        set
        {
            if (value)
            {
                SelectedScanMode = "problem_codecs";
            }
        }
    }

    public bool ScanModeIsBitrateOrCodecs
    {
        get => SelectedScanMode == "bitrate_or_codecs";
        set
        {
            if (value)
            {
                SelectedScanMode = "bitrate_or_codecs";
            }
        }
    }

    private static readonly string[] ScanModeIds = ["bitrate", "problem_codecs", "bitrate_or_codecs"];

    private static readonly string[] RateControlIds = ["auto", "bitrate", "crf"];

    private static readonly string[] CrfPresetIds = ["quality", "balanced", "compact", "tiny"];

    /// <summary>ComboBox index for the scan target.</summary>
    public int ScanModeIndex
    {
        get => Math.Max(0, Array.IndexOf(ScanModeIds, SelectedScanMode));
        set
        {
            if (value >= 0 && value < ScanModeIds.Length)
            {
                SelectedScanMode = ScanModeIds[value];
            }
        }
    }

    /// <summary>ComboBox index for the encode control mode.</summary>
    public int RateControlIndex
    {
        get => Math.Max(0, Array.IndexOf(RateControlIds, SelectedRateControl));
        set
        {
            if (value >= 0 && value < RateControlIds.Length)
            {
                SelectedRateControl = RateControlIds[value];
            }
        }
    }

    /// <summary>ComboBox index for the CRF quality preset.</summary>
    public int CrfPresetIndex
    {
        get => Math.Max(0, Array.IndexOf(CrfPresetIds, SelectedCrfPreset));
        set
        {
            if (value >= 0 && value < CrfPresetIds.Length)
            {
                SelectedCrfPreset = CrfPresetIds[value];
            }
        }
    }

    partial void OnSelectedScanModeChanged(string value)
    {
        OnPropertyChanged(nameof(ScanModeIndex));
        OnPropertyChanged(nameof(ScanModeIsBitrate));
        OnPropertyChanged(nameof(ScanModeIsProblemCodecs));
        OnPropertyChanged(nameof(ScanModeIsBitrateOrCodecs));
    }

    [ObservableProperty] private bool _outputMp4;
    [ObservableProperty] private bool _stripAutobitrateSuffix;
    [ObservableProperty] private string _selectedCodec = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.LibNvencVp9,
        "libx264");
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private string _selectedAudioMode = "copy";
    [ObservableProperty] private string _selectedRateControl = "auto";
    [ObservableProperty] private string _selectedCrfPreset = "balanced";
    [ObservableProperty] private double _crfValue = 20;
    [ObservableProperty] private double _parallelJobs = 1;
    [ObservableProperty] private double _ffmpegThreads = 6;
    [ObservableProperty] private bool _lowPriority = true;
    [ObservableProperty] private string _selectedX264Preset = "veryfast";
    [ObservableProperty] private string _suffix = "_bitrate";

    public bool ShowCrfControls =>
        SelectedRateControl is "crf" or "auto";

    public bool RateControlIsAuto
    {
        get => SelectedRateControl == "auto";
        set
        {
            if (value)
            {
                SelectedRateControl = "auto";
            }
        }
    }

    public bool RateControlIsBitrate
    {
        get => SelectedRateControl == "bitrate";
        set
        {
            if (value)
            {
                SelectedRateControl = "bitrate";
            }
        }
    }

    public bool RateControlIsCrf
    {
        get => SelectedRateControl == "crf";
        set
        {
            if (value)
            {
                SelectedRateControl = "crf";
            }
        }
    }

    public bool CrfPresetIsQuality
    {
        get => SelectedCrfPreset == "quality";
        set
        {
            if (value)
            {
                SelectedCrfPreset = "quality";
            }
        }
    }

    public bool CrfPresetIsBalanced
    {
        get => SelectedCrfPreset == "balanced";
        set
        {
            if (value)
            {
                SelectedCrfPreset = "balanced";
            }
        }
    }

    public bool CrfPresetIsCompact
    {
        get => SelectedCrfPreset == "compact";
        set
        {
            if (value)
            {
                SelectedCrfPreset = "compact";
            }
        }
    }

    public bool CrfPresetIsTiny
    {
        get => SelectedCrfPreset == "tiny";
        set
        {
            if (value)
            {
                SelectedCrfPreset = "tiny";
            }
        }
    }

    partial void OnSelectedRateControlChanged(string value)
    {
        OnPropertyChanged(nameof(RateControlIndex));
        OnPropertyChanged(nameof(RateControlIsAuto));
        OnPropertyChanged(nameof(RateControlIsBitrate));
        OnPropertyChanged(nameof(RateControlIsCrf));
        OnPropertyChanged(nameof(ShowCrfControls));
    }

    partial void OnSelectedCrfPresetChanged(string value)
    {
        var code = NormalizeCrfPreset(value);
        if (code != value)
        {
            SelectedCrfPreset = code;
            return;
        }

        OnPropertyChanged(nameof(CrfPresetIsQuality));
        OnPropertyChanged(nameof(CrfPresetIsBalanced));
        OnPropertyChanged(nameof(CrfPresetIsCompact));
        OnPropertyChanged(nameof(CrfPresetIsTiny));
        OnPropertyChanged(nameof(CrfPresetIndex));
        CrfValue = CrfFromPreset(code);
    }

    partial void OnCrfValueChanged(double value)
    {
        var clamped = ClampCrf((int)Math.Round(value));
        if (Math.Abs(clamped - value) > 0.01)
        {
            CrfValue = clamped;
            return;
        }

        foreach (var preset in new[] { "quality", "balanced", "compact", "tiny" })
        {
            if (CrfFromPreset(preset) != clamped)
            {
                continue;
            }

            if (!string.Equals(SelectedCrfPreset, preset, StringComparison.OrdinalIgnoreCase))
            {
                _selectedCrfPreset = preset;
                OnPropertyChanged(nameof(SelectedCrfPreset));
                OnPropertyChanged(nameof(CrfPresetIsQuality));
                OnPropertyChanged(nameof(CrfPresetIsBalanced));
                OnPropertyChanged(nameof(CrfPresetIsCompact));
                OnPropertyChanged(nameof(CrfPresetIsTiny));
                OnPropertyChanged(nameof(CrfPresetIndex));
            }

            break;
        }
    }
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
        _settings.ScanMode = NormalizeScanMode(SelectedScanMode);
        _settings.OutputBesideSource = OutputBesideSource;
        _settings.OutputMp4 = OutputMp4;
        _settings.StripAutobitrateSuffix = StripAutobitrateSuffix;
        _settings.Codec = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.LibNvencVp9, SelectedCodec);
        _settings.TargetFps = TargetFps;
        _settings.AudioMode = SelectedAudioMode;
        _settings.RateControl = NormalizeRateControl(SelectedRateControl);
        _settings.CrfPreset = NormalizeCrfPreset(SelectedCrfPreset);
        _settings.Crf = ClampCrf((int)Math.Round(CrfValue));
        _settings.ParallelJobs = Math.Clamp((int)Math.Round(ParallelJobs), 1, 8);
        _settings.FfmpegThreads = Math.Clamp((int)Math.Round(FfmpegThreads), 0, 64);
        _settings.LowPriority = LowPriority;
        _settings.X264Preset = NormalizeX264Preset(SelectedX264Preset);
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
        if (IsBusy)
        {
            return;
        }

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
        BeginJobToken();
        IsBusy = true;
        Status = Loc.T("bitrate.scanRunning");
        Rows.Clear();

        try
        {
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_cfg_{Guid.NewGuid():N}.json");
            var outPath = Path.Combine(Path.GetTempPath(), $"hm_bitrate_scan_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, BitrateConfigReader.ToJobJson(_settings));

            var result = await AppServices.JobRunner.RunBridgeAsync("bitrate_scan_job.py",
                ["--config-json", configPath, "--output-json", outPath],
                _jobCts!.Token);

            if (result.ExitCode == -2)
            {
                Status = Loc.T("bitrate.convertCancelled");
                return;
            }

            TryDelete(configPath);
            if (!result.Success || !File.Exists(outPath))
            {
                Status = result.Message;
                return;
            }

            TryDelete(_lastScanJsonPath);
            _lastScanJsonPath = outPath;
            var json = await File.ReadAllTextAsync(outPath);
            using var doc = JsonDocument.Parse(json);
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                var path = row.GetProperty("path").GetString() ?? "";
                var w = row.GetProperty("width").GetInt32();
                var h = row.GetProperty("height").GetInt32();
                var codec = row.TryGetProperty("video_codec", out var vc) && vc.ValueKind == JsonValueKind.String
                    ? (vc.GetString() ?? "-")
                    : "-";
                var srcKbps = row.TryGetProperty("source_kbps", out var sk) && sk.ValueKind != JsonValueKind.Null ? sk.GetInt32().ToString() : "-";
                var tgtKbps = row.TryGetProperty("effective_target_kbps", out var tk) && tk.ValueKind != JsonValueKind.Null ? tk.GetInt32().ToString() : "-";
                var saveBytes = row.TryGetProperty("estimated_saved_bytes", out var sb) && sb.ValueKind != JsonValueKind.Null
                    ? sb.GetInt64() : 0L;
                var saveMb = saveBytes > 0
                    ? $"{saveBytes / (1024.0 * 1024.0):F1}" : "-";
                var actionCode = row.GetProperty("action").GetString() ?? "";
                var reasonRaw = row.GetProperty("reason").GetString() ?? "";
                var scanRow = new BitrateRowViewModel
                {
                    Path = path,
                    FileName = System.IO.Path.GetFileName(path),
                    Resolution = $"{w}x{h}",
                    VideoCodec = string.IsNullOrWhiteSpace(codec) ? "-" : codec,
                    SourceKbps = srcKbps,
                    TargetKbps = tgtKbps,
                    EstSaveMb = saveMb,
                    EstSaveMbValue = saveBytes / (1024.0 * 1024.0),
                    ActionCode = actionCode,
                    ReasonRaw = reasonRaw,
                    IsIncluded = string.Equals(actionCode, "convert", StringComparison.OrdinalIgnoreCase),
                };
                scanRow.ApplyLocalization();
                AddScanRow(scanRow);
            }

        }
        finally
        {
            EndJobToken();
            IsBusy = false;
        }

        // Only after IsBusy is cleared — the summary is skipped while a job runs.
        UpdateSelectionStatus();
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsBusy)
        {
            return;
        }

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
        BeginJobToken();
        IsBusy = true;
        Status = Loc.T("bitrate.convertRunning");

        try
        {
            var includedPaths = Rows
                .Where(r => r.IsIncluded && string.Equals(r.ActionCode, "convert", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (includedPaths.Count == 0)
            {
                Status = Loc.T("bitrate.noRowsSelected");
                return;
            }

            if (SelectedPostAction == "delete_original" && ConfirmAsync is not null
                && !await ConfirmAsync(
                    Loc.T("bitrate.confirmDeleteTitle"),
                    Loc.F("bitrate.confirmDeleteBody", includedPaths.Count)))
            {
                Status = Loc.T("bitrate.convertCancelled");
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
                ["--config-json", configPath, "--rows-json", rowsPath],
                _jobCts!.Token);
            TryDelete(configPath);
            TryDelete(rowsPath);
            Status = result.ExitCode == -2
                ? Loc.T("bitrate.convertCancelled")
                : result.Message;
        }
        finally
        {
            EndJobToken();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopConversion))]
    private void StopConversion()
    {
        if (_jobCts is null || _jobCts.IsCancellationRequested)
        {
            return;
        }

        Status = Loc.T("bitrate.convertStopping");
        try
        {
            _jobCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        StopConversionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStopActionEnabled));
    }

    private bool CanStopConversion() => IsBusy && _jobCts is { IsCancellationRequested: false };

    private void BeginJobToken()
    {
        _jobCts?.Cancel();
        _jobCts?.Dispose();
        _jobCts = new CancellationTokenSource();
        StopConversionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStopActionEnabled));
    }

    private void EndJobToken()
    {
        _jobCts?.Dispose();
        _jobCts = null;
        StopConversionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStopActionEnabled));
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
                    ? Loc.T("bitrate.suffixRemoved")
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
            if (string.Equals(row.ActionCode, "convert", StringComparison.OrdinalIgnoreCase))
            {
                row.IsIncluded = included;
            }
            else if (!included)
            {
                row.IsIncluded = false;
            }
        }

        UpdateSelectionStatus();
    }

    [RelayCommand]
    private void SortRowsBySaveMb()
    {
        if (Rows.Count <= 1)
        {
            return;
        }

        var sorted = Rows.OrderByDescending(r => r.EstSaveMbValue).ToList();
        Rows.Clear();
        foreach (var row in sorted)
        {
            Rows.Add(row);
        }
    }

    private void AddScanRow(BitrateRowViewModel row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        Rows.Add(row);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BitrateRowViewModel.IsIncluded))
        {
            UpdateSelectionStatus();
        }
    }

    private void UpdateSelectionStatus()
    {
        if (Rows.Count == 0 || IsBusy)
        {
            return;
        }

        var included = Rows.Where(r => r.IsIncluded).ToList();
        var savedMb = included.Sum(r => r.EstSaveMbValue);
        Status = savedMb >= 1
            ? Loc.F("bitrate.rowSelectionSavings", Rows.Count, included.Count, FormatSize(savedMb))
            : Loc.F("bitrate.rowSelection", Rows.Count, included.Count);
    }

    private static string FormatSize(double mb) =>
        mb >= 1024
            ? (mb / 1024).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB"
            : mb.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " MB";

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    private static string NormalizeScanMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "problem_codecs" => "problem_codecs",
            "bitrate_or_codecs" => "bitrate_or_codecs",
            _ => "bitrate",
        };

    private static string NormalizeRateControl(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "crf" => "crf",
            "bitrate" => "bitrate",
            _ => "auto",
        };

    private static string NormalizeCrfPreset(string? preset) =>
        preset?.Trim().ToLowerInvariant() switch
        {
            "quality" or "qualitaet" or "qualität" => "quality",
            "compact" or "kompakt" => "compact",
            "tiny" or "sehr kompakt" => "tiny",
            _ => "balanced",
        };

    private static int CrfFromPreset(string preset) =>
        NormalizeCrfPreset(preset) switch
        {
            "quality" => 18,
            "compact" => 23,
            "tiny" => 26,
            _ => 20,
        };

    private static int ClampCrf(int value) => Math.Clamp(value, 0, 51);

    private static string NormalizeX264Preset(string? preset)
    {
        var normalized = preset?.Trim().ToLowerInvariant();
        return normalized is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow"
            ? normalized
            : "veryfast";
    }
}
