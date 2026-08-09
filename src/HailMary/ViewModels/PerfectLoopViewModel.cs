using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class PerfectLoopViewModel : ToolIoViewModel, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private int _analyzeEpoch;
    private bool _suppressAutoAnalyze;
    private bool _userChoseSeamSettings;

    public PerfectLoopViewModel(ToolDefinition tool)
        : base(tool.Id)
    {
        _tool = tool;
        RefreshBatchFiles(preserveQualityPreview: false);
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<string> BatchFiles { get; } = [];

    public IReadOnlyList<string> EncoderOptions { get; } =
        ["nvidia_h264", "nvidia_hevc", "cpu", "cpu_hevc"];

    public IReadOnlyList<string> SeamMethodOptions { get; } =
        ["auto", "cut", "crossfade", "morph"];

    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private double _seamSeconds = 0.15;
    [ObservableProperty] private bool _smartTrim;
    [ObservableProperty] private string _selectedEncoder = "nvidia_h264";
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private string _selectedSeamMethod = "auto";
    [ObservableProperty] private string _qualityHint = string.Empty;
    [ObservableProperty] private string _batchSummary = Loc.T("intro.batchSummaryEmpty");
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasQualityPreview;

    public string ExportButtonLabel => BatchFiles.Count > 1
        ? Loc.F("perfectloop.exportBatch", BatchFiles.Count)
        : Loc.T("common.startExport");

    public string SeamSecondsLabel => Loc.F("perfectloop.seamSecondsValue", SeamSeconds.ToString("0.00"));

    public string AnalyzeButtonLabel => Loc.T("perfectloop.analyze");

    public bool SeamSliderEnabled =>
        !string.Equals(SelectedSeamMethod, "cut", StringComparison.OrdinalIgnoreCase);

    protected override void OnOutputDirUpdated(string value) => RefreshOutputFromInput();

    protected override void OnInputPathUpdated(string value)
    {
        _userChoseSeamSettings = false;
        RefreshBatchFiles(preserveQualityPreview: false);
        _ = QueueAutoAnalyzeAsync();
    }

    partial void OnSeamSecondsChanged(double value)
    {
        if (!_suppressAutoAnalyze)
        {
            _userChoseSeamSettings = true;
        }

        OnPropertyChanged(nameof(SeamSecondsLabel));
    }

    partial void OnSelectedSeamMethodChanged(string value)
    {
        if (!_suppressAutoAnalyze)
        {
            _userChoseSeamSettings = true;
        }

        OnPropertyChanged(nameof(SeamSliderEnabled));
    }

    partial void OnSmartTrimChanged(bool value) => _ = QueueAutoAnalyzeAsync();

    private void RefreshBatchFiles(bool preserveQualityPreview)
    {
        BatchFiles.Clear();
        foreach (var path in GetBatchVideoPaths())
        {
            BatchFiles.Add(path);
        }

        if (BatchFiles.Count == 0 && !string.IsNullOrWhiteSpace(InputPath) && File.Exists(InputPath))
        {
            BatchFiles.Add(InputPath);
        }

        BatchSummary = BatchFiles.Count switch
        {
            0 => Loc.T("intro.batchSummaryNoVideos"),
            1 => Path.GetFileName(BatchFiles[0]),
            _ => Loc.F("perfectloop.batchSummaryMany", BatchFiles.Count),
        };

        OnPropertyChanged(nameof(ExportButtonLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        RefreshOutputFromInput();

        if (!preserveQualityPreview && !_suppressAutoAnalyze)
        {
            HasQualityPreview = false;
            QualityHint = BatchFiles.Count > 0 ? Loc.T("perfectloop.qualityPending") : string.Empty;
        }
    }

    private void RefreshOutputFromInput()
    {
        var source = BatchFiles.FirstOrDefault() ?? InputPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return;
        }

        OutputPath = BuildOutputPath(source, OptionalOutputDir());
    }

    private static string BuildOutputPath(string input, string? outputDir)
    {
        var ip = new FileInfo(input);
        var dir = string.IsNullOrWhiteSpace(outputDir) ? ip.DirectoryName ?? "" : outputDir;
        return Path.Combine(dir, $"{ip.Name[..^ip.Extension.Length]}_loop{ip.Extension}");
    }

    private async Task QueueAutoAnalyzeAsync()
    {
        if (_suppressAutoAnalyze)
        {
            return;
        }

        var epoch = ++_analyzeEpoch;
        await Task.Delay(350);
        if (epoch != _analyzeEpoch || IsBusy)
        {
            return;
        }

        await AnalyzeCoreAsync(applyRecommendations: !_userChoseSeamSettings);
    }

    [RelayCommand]
    private async Task AnalyzeAsync() =>
        await AnalyzeCoreAsync(applyRecommendations: !_userChoseSeamSettings);

    private async Task AnalyzeCoreAsync(bool applyRecommendations)
    {
        _suppressAutoAnalyze = true;
        try
        {
            RefreshBatchFiles(preserveQualityPreview: true);
        }
        finally
        {
            _suppressAutoAnalyze = false;
        }

        var files = BatchFiles.ToList();
        if (files.Count == 0)
        {
            Status = Loc.T("perfectloop.noInput");
            QualityHint = string.Empty;
            HasQualityPreview = false;
            return;
        }

        var input = files[0];
        IsBusy = true;
        Status = Loc.T("perfectloop.analyzeRunning");
        QualityHint = Loc.T("perfectloop.analyzeRunning");

        var configPath = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_an_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_an_out_{Guid.NewGuid():N}.json");
        var payload = new
        {
            mode = "analyze",
            input,
            files = new[] { input },
            seam_seconds = SeamSeconds,
            seam_method = SelectedSeamMethod,
            smart_trim = SmartTrim,
            min_loop_seconds = 1.5,
            ffmpeg_encoder = SelectedEncoder,
            target_fps = TargetFps,
            mute_audio = true,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "perfect_loop_job.py",
                ["--config-json", configPath, "--output-json", outputJson]);

            if (!result.Success)
            {
                Status = result.Message;
                QualityHint = Loc.T("perfectloop.analyzeFailed");
                HasQualityPreview = false;
                return;
            }

            ApplyAnalyzeResult(outputJson, applyRecommendations);
            Status = Loc.T("perfectloop.analyzeDone");
        }
        finally
        {
            IsBusy = false;
            try
            {
                File.Delete(configPath);
                File.Delete(outputJson);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void ApplyAnalyzeResult(string outputJson, bool applyRecommendations)
    {
        if (!File.Exists(outputJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(outputJson));
            if (!doc.RootElement.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array
                || details.GetArrayLength() == 0)
            {
                return;
            }

            var first = details[0];
            var score = first.TryGetProperty("quality_score", out var qs) ? qs.GetDouble() : 0;
            var warn = first.TryGetProperty("quality_warning", out var qw) && qw.GetBoolean();
            var recommended = first.TryGetProperty("recommended_seam_seconds", out var rs)
                ? rs.GetDouble()
                : SeamSeconds;
            var recommendedMethod = first.TryGetProperty("recommended_seam_method", out var rm)
                ? rm.GetString() ?? "cut"
                : "cut";

            var methodLabel = Loc.T($"perfectloop.method.{recommendedMethod}");
            var hint = warn
                ? Loc.F("perfectloop.qualityWarn", score.ToString("0.00"))
                : Loc.F("perfectloop.qualityOk", score.ToString("0.00"));
            hint += " " + Loc.F("perfectloop.methodRecommend", methodLabel);
            hint += " " + Loc.F("perfectloop.seamRecommend", recommended.ToString("0.00"));
            if (_userChoseSeamSettings)
            {
                hint += " " + Loc.T("perfectloop.userSettingsKept");
            }
            else
            {
                hint += " " + Loc.T("perfectloop.scoreSeamNote");
            }

            QualityHint = hint;
            HasQualityPreview = true;

            // Only seed defaults once — never overwrite after the user touched method/seam.
            if (!applyRecommendations || _userChoseSeamSettings)
            {
                return;
            }

            _suppressAutoAnalyze = true;
            try
            {
                if (warn && Math.Abs(SeamSeconds - recommended) > 0.049)
                {
                    SeamSeconds = recommended;
                }
            }
            finally
            {
                _suppressAutoAnalyze = false;
            }
        }
        catch
        {
            QualityHint = Loc.T("perfectloop.analyzeFailed");
            HasQualityPreview = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        // Keep score + user method/seam — do not re-run analyze that overwrites settings.
        RefreshBatchFiles(preserveQualityPreview: true);
        var files = BatchFiles.ToList();
        if (files.Count == 0)
        {
            Status = Loc.T("perfectloop.noInput");
            return;
        }

        if (!HasQualityPreview)
        {
            await AnalyzeCoreAsync(applyRecommendations: !_userChoseSeamSettings);
            if (!HasQualityPreview)
            {
                return;
            }
        }

        IsBusy = true;
        var outDir = OptionalOutputDir();
        var failed = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var input = files[i];
                var output = BuildOutputPath(input, outDir);
                Status = files.Count > 1
                    ? Loc.F("perfectloop.batchProgress", i + 1, files.Count, Path.GetFileName(input))
                    : Loc.T("perfectloop.exportRunning");

                var configPath = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_{Guid.NewGuid():N}.json");
                var outputJson = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_out_{Guid.NewGuid():N}.json");
                var payload = new
                {
                    mode = "export",
                    input,
                    output,
                    output_dir = outDir ?? "",
                    files = new[] { input },
                    seam_seconds = SeamSeconds,
                    seam_method = SelectedSeamMethod,
                    smart_trim = SmartTrim,
                    min_loop_seconds = 1.5,
                    ffmpeg_encoder = SelectedEncoder,
                    target_fps = TargetFps,
                    mute_audio = true,
                };

                try
                {
                    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
                    var result = await AppServices.JobRunner.RunBridgeAsync(
                        "perfect_loop_job.py",
                        ["--config-json", configPath, "--output-json", outputJson]);

                    if (!result.Success)
                    {
                        failed++;
                        Status = result.Message;
                        break;
                    }

                    OutputPath = output;
                }
                finally
                {
                    try
                    {
                        File.Delete(configPath);
                        File.Delete(outputJson);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            if (failed == 0)
            {
                Status = files.Count > 1
                    ? Loc.F("perfectloop.batchDone", files.Count)
                    : Loc.T("perfectloop.exportDone");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
