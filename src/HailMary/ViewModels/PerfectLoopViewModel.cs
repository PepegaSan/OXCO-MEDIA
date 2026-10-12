using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed class SeamMethodChoice
{
    public SeamMethodChoice(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }

    public string Label { get; }
}

public partial class PerfectLoopViewModel : ToolIoViewModel, IToolShellHost, ISplitPaneToolHost, ILocalizable
{
    private static readonly string[] SeamMethodIds = ["auto", "cut", "crossfade", "morph", "pingpong"];

    private readonly ToolDefinition _tool;
    private int _analyzeEpoch;
    // Increments per analysis run; only the newest run may apply its result.
    private int _analyzeRunId;
    private int _previewEpoch;
    private bool _suppressAutoAnalyze;
    private bool _userChoseSeamSettings;
    private string? _seamPreviewTempPath;

    public PerfectLoopViewModel(ToolDefinition tool)
        : base(tool.Id)
    {
        _tool = tool;
        RebuildSeamMethodChoices(selectId: "auto");
        RefreshBatchFiles(preserveQualityPreview: false);
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<string> BatchFiles { get; } = [];

    public ObservableCollection<SeamMethodChoice> SeamMethodChoices { get; } = [];

    public IReadOnlyList<string> EncoderOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.SoftHardware);

    [ObservableProperty] private SeamMethodChoice? _selectedSeamMethodItem;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private double _seamSeconds = 0.15;
    [ObservableProperty] private string _selectedEncoder = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.SoftHardware,
        FfmpegEncoderCatalog.DefaultSoftHardwareId);
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private double _crfValue;
    [ObservableProperty] private double _loopCount = 1;
    [ObservableProperty] private string _qualityHint = string.Empty;
    [ObservableProperty] private string _previewHint = string.Empty;
    [ObservableProperty] private string _previewVideoPath = string.Empty;
    [ObservableProperty] private string _batchSummary = Loc.T("intro.batchSummaryEmpty");
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPreviewEncoding;
    [ObservableProperty] private bool _hasQualityPreview;
    [ObservableProperty] private double _loopStartSeconds;
    [ObservableProperty] private double _loopEndSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private double _sliderMaximum = 1;
    [ObservableProperty] private string _positionDisplay = "00:00";
    [ObservableProperty] private string _durationDisplay = "00:00";
    [ObservableProperty] private string _rangeSummary = string.Empty;
    [ObservableProperty] private float[]? _waveformPeaks;

    public IReadOnlyList<TimelineSpan> TimelineRanges { get; private set; } = [];

    private bool _userSetRange;
    private bool _hasSearchWindow;
    private double _searchWindowStart;
    private double _searchWindowEnd;

    public string SelectedSeamMethod => SelectedSeamMethodItem?.Id ?? "auto";

    public bool HasPreviewVideo =>
        !string.IsNullOrWhiteSpace(PreviewVideoPath) && File.Exists(PreviewVideoPath);

    public string ExportButtonLabel => BatchFiles.Count > 1
        ? Loc.F("perfectloop.exportBatch", BatchFiles.Count)
        : Loc.T("common.startExport");

    public string SeamSecondsLabel => Loc.F("perfectloop.seamSecondsValue", SeamSeconds.ToString("0.00"));

    public string AnalyzeButtonLabel => Loc.T("perfectloop.analyze");

    public string FindBestButtonLabel => Loc.T("perfectloop.findBest");

    public bool SeamSliderEnabled =>
        SelectedSeamMethod is not ("cut" or "pingpong");

    public bool IsSourcePreview
    {
        get
        {
            var source = BatchFiles.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(source)
                && string.Equals(PreviewVideoPath, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool HasVideo =>
        BatchFiles.Count > 0 && File.Exists(BatchFiles[0]);

    public string? SourceVideoPath =>
        BatchFiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

    public bool CanMarkRange => HasVideo;

    public double EffectiveLoopEnd
    {
        get
        {
            if (LoopEndSeconds <= 0.001)
            {
                return DurationSeconds;
            }

            return DurationSeconds > 0 ? Math.Min(LoopEndSeconds, DurationSeconds) : LoopEndSeconds;
        }
    }

    public double RangeBarBefore => Math.Max(0.001, LoopStartSeconds);
    public double RangeBarMid => Math.Max(0.001, EffectiveLoopEnd - LoopStartSeconds);
    public double RangeBarAfter => Math.Max(0.001, DurationSeconds - EffectiveLoopEnd);

    protected override void OnOutputDirUpdated(string value) => RefreshOutputFromInput();

    protected override void OnInputPathUpdated(string value)
    {
        _userChoseSeamSettings = false;
        _userSetRange = false;
        _hasSearchWindow = false;
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
        EnsureLoopPreview();
    }

    partial void OnSelectedSeamMethodItemChanged(SeamMethodChoice? value)
    {
        if (!_suppressAutoAnalyze)
        {
            _userChoseSeamSettings = true;
        }

        OnPropertyChanged(nameof(SeamSliderEnabled));
        OnPropertyChanged(nameof(SelectedSeamMethod));
        EnsureLoopPreview();
    }

    partial void OnTargetFpsChanged(double value) => EnsureLoopPreview();

    partial void OnPreviewVideoPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPreviewVideo));
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(IsSourcePreview));
        OnPropertyChanged(nameof(CanMarkRange));
    }

    partial void OnLoopStartSecondsChanged(double value) => OnLoopRangeEdited();

    partial void OnLoopEndSecondsChanged(double value) => OnLoopRangeEdited();

    private void OnLoopRangeEdited()
    {
        NotifyRangeUi();
        if (_suppressAutoAnalyze)
        {
            return;
        }

        _userSetRange = true;
        _ = QueueAutoAnalyzeAsync();
    }

    private void NotifyRangeUi()
    {
        var start = Math.Max(0, LoopStartSeconds);
        var end = EffectiveLoopEnd;
        var span = Math.Max(0, end - start);
        RangeSummary = DurationSeconds <= 0
            ? string.Empty
            : Loc.F("perfectloop.rangeSummary", start.ToString("0.00"), end.ToString("0.00"), span.ToString("0.00"));
        OnPropertyChanged(nameof(EffectiveLoopEnd));
        OnPropertyChanged(nameof(RangeBarBefore));
        OnPropertyChanged(nameof(RangeBarMid));
        OnPropertyChanged(nameof(RangeBarAfter));
        OnPropertyChanged(nameof(CanMarkRange));
        RebuildTimeline();
    }

    private void RebuildSeamMethodChoices(string? selectId)
    {
        var keepId = selectId ?? SelectedSeamMethodItem?.Id ?? "auto";
        _suppressAutoAnalyze = true;
        try
        {
            SeamMethodChoices.Clear();
            foreach (var id in SeamMethodIds)
            {
                SeamMethodChoices.Add(new SeamMethodChoice(id, Loc.T($"perfectloop.method.{id}")));
            }

            SelectedSeamMethodItem = SeamMethodChoices.FirstOrDefault(c =>
                string.Equals(c.Id, keepId, StringComparison.OrdinalIgnoreCase))
                ?? SeamMethodChoices[0];
        }
        finally
        {
            _suppressAutoAnalyze = false;
        }
    }

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
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(SourceVideoPath));
        OnPropertyChanged(nameof(CanMarkRange));
        RefreshOutputFromInput();

        if (!preserveQualityPreview && !_suppressAutoAnalyze)
        {
            HasQualityPreview = false;
            ClearAnalysisVisuals();
            QualityHint = BatchFiles.Count > 0 ? Loc.T("perfectloop.qualityPending") : string.Empty;
            ShowSourcePreview();
        }
        else if (BatchFiles.Count == 0)
        {
            ShowSourcePreview();
        }
        else if (!HasPreviewVideo)
        {
            ShowSourcePreview();
        }
    }

    private void ShowSourcePreview()
    {
        _previewEpoch++;
        ClearSeamPreviewTemp();
        var source = BatchFiles.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
        {
            PreviewVideoPath = source;
            PreviewHint = Loc.T("perfectloop.previewSource");
            _ = ProbeSourceDurationAsync(source);
        }
        else
        {
            PreviewVideoPath = string.Empty;
            PreviewHint = string.Empty;
            WaveformPeaks = null;
            RebuildTimeline();
        }
    }

    private void ClearSeamPreviewTemp()
    {
        var old = _seamPreviewTempPath;
        _seamPreviewTempPath = null;
        _loopPreviewKey = null;
        LoopPreviewPath = string.Empty;
        if (string.IsNullOrWhiteSpace(old) || !File.Exists(old))
        {
            return;
        }

        try
        {
            File.Delete(old);
        }
        catch
        {
            // ignore
        }
    }

    private async Task ProbeSourceDurationAsync(string path)
    {
        var dur = await FfprobeHelper.ProbeDurationSecondsAsync(path) ?? 0;
        ApplyDuration(dur);
        _ = LoadWaveformAsync(path);
    }

    public void ApplyDuration(double seconds)
    {
        var dur = Math.Max(0, seconds);
        DurationSeconds = dur;
        SliderMaximum = Math.Max(0.001, dur);
        DurationDisplay = FormatTime(dur);
        if (!_userSetRange)
        {
            _suppressAutoAnalyze = true;
            try
            {
                LoopStartSeconds = 0;
                LoopEndSeconds = dur;
            }
            finally
            {
                _suppressAutoAnalyze = false;
            }
        }
        else if (LoopEndSeconds <= 0.001 && dur > 0)
        {
            _suppressAutoAnalyze = true;
            try
            {
                LoopEndSeconds = dur;
            }
            finally
            {
                _suppressAutoAnalyze = false;
            }
        }

        NotifyRangeUi();
    }

    public async Task LoadWaveformAsync(string? path)
    {
        WaveformPeaks = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var peaks = await WaveformPeakExtractor.ExtractAsync(path);
        if (string.Equals(path, SourceVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            WaveformPeaks = peaks;
        }
    }

    public void RebuildTimeline()
    {
        var spans = new List<TimelineSpan>();
        var loopStart = Math.Max(0, LoopStartSeconds);
        var loopEnd = EffectiveLoopEnd;
        if (_hasSearchWindow && _searchWindowEnd > _searchWindowStart + 0.05)
        {
            spans.Add(new TimelineSpan(
                _searchWindowStart,
                _searchWindowEnd,
                TimelineSpanKind.Pending,
                Loc.T("common.selection")));
        }

        if (loopEnd > loopStart + 0.02)
        {
            spans.Add(new TimelineSpan(
                loopStart,
                loopEnd,
                TimelineSpanKind.Loop,
                Loc.T("perfectloop.rangeBarLoop")));
        }

        TimelineRanges = spans;
        OnPropertyChanged(nameof(TimelineRanges));
    }

    public void SetPosition(double seconds)
    {
        SliderValue = Math.Clamp(seconds, 0, SliderMaximum);
        PositionDisplay = FormatTime(SliderValue);
    }

    public void MarkInAt(double seconds)
    {
        if (!HasVideo)
        {
            return;
        }

        if (!IsSourcePreview)
        {
            ShowSourcePreview();
        }

        var end = EffectiveLoopEnd > 0.05 ? EffectiveLoopEnd : DurationSeconds;
        LoopStartSeconds = Math.Clamp(seconds, 0, Math.Max(0, end - 0.05));
    }

    public void MarkOutAt(double seconds)
    {
        if (!HasVideo)
        {
            return;
        }

        if (!IsSourcePreview)
        {
            ShowSourcePreview();
        }

        var minEnd = LoopStartSeconds + 0.05;
        var maxEnd = DurationSeconds > 0 ? DurationSeconds : seconds;
        LoopEndSeconds = Math.Clamp(seconds, minEnd, Math.Max(minEnd, maxEnd));
    }

    [RelayCommand]
    private void MarkLoopStart() => MarkInAt(SliderValue);

    [RelayCommand]
    private void MarkLoopEnd() => MarkOutAt(SliderValue);

    [RelayCommand]
    private void ResetLoopRange()
    {
        _userSetRange = false;
        _hasSearchWindow = false;
        _suppressAutoAnalyze = true;
        try
        {
            LoopStartSeconds = 0;
            LoopEndSeconds = DurationSeconds;
        }
        finally
        {
            _suppressAutoAnalyze = false;
        }

        NotifyRangeUi();
        if (!IsSourcePreview)
        {
            ShowSourcePreview();
        }

        _ = QueueAutoAnalyzeAsync();
    }

    [RelayCommand]
    private void ShowOriginalPreview() => ShowSourcePreview();

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
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

    private int ExportLoopCount => Math.Clamp((int)Math.Round(LoopCount), 1, 20);

    private int ExportCrf => Math.Clamp((int)Math.Round(CrfValue), 0, 51);

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

    [RelayCommand]
    private async Task FindBestLoopInRangeAsync()
    {
        _searchWindowStart = Math.Max(0, LoopStartSeconds);
        _searchWindowEnd = EffectiveLoopEnd;
        _hasSearchWindow = _searchWindowEnd > _searchWindowStart + 0.05;
        RebuildTimeline();
        await AnalyzeCoreAsync(
            applyRecommendations: !_userChoseSeamSettings,
            searchBestInRange: true);
    }

    private async Task AnalyzeCoreAsync(
        bool applyRecommendations,
        bool encodeSeamPreview = false,
        bool searchBestInRange = false)
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
            ShowSourcePreview();
            return;
        }

        var input = files[0];
        var runId = ++_analyzeRunId;
        IsBusy = true;
        Status = searchBestInRange
            ? Loc.T("perfectloop.findBestRunning")
            : Loc.T("perfectloop.analyzeRunning");
        QualityHint = Status;

        var configPath = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_an_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_an_out_{Guid.NewGuid():N}.json");
        var payload = new
        {
            mode = "analyze",
            input,
            files = new[] { input },
            seam_seconds = SeamSeconds,
            seam_method = SelectedSeamMethod,
            smart_trim = searchBestInRange,
            min_loop_seconds = MinLoopSeconds,
            ffmpeg_encoder = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardware, SelectedEncoder),
            target_fps = TargetFps,
            mute_audio = true,
            range_start_seconds = Math.Max(0, LoopStartSeconds),
            range_end_seconds = EffectiveLoopEnd,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "perfect_loop_job.py",
                ["--config-json", configPath, "--output-json", outputJson]);

            // A newer analysis (or another video) took over while this one ran — drop the stale result.
            if (runId != _analyzeRunId
                || !string.Equals(input, SourceVideoPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!result.Success)
            {
                Status = result.Message;
                QualityHint = Loc.T("perfectloop.analyzeFailed");
                HasQualityPreview = false;
                return;
            }

            ApplyAnalyzeResult(outputJson, applyRecommendations, applyFoundRange: searchBestInRange);
            Status = searchBestInRange
                ? Loc.T("perfectloop.findBestDone")
                : Loc.T("perfectloop.analyzeDone");
            if (encodeSeamPreview)
            {
                _ = QueueSeamPreviewAsync();
            }
        }
        finally
        {
            if (runId == _analyzeRunId)
            {
                IsBusy = false;
            }

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

    private void ApplyAnalyzeResult(string outputJson, bool applyRecommendations, bool applyFoundRange = false)
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

            if (score < 0.25 && recommendedMethod != "pingpong")
            {
                hint += " " + Loc.T("perfectloop.pingpongHint");
            }

            QualityHint = hint;
            HasQualityPreview = true;

            var smartUsed = first.TryGetProperty("smart_trim_used", out var stu) && stu.GetBoolean();
            if ((applyFoundRange || smartUsed)
                && first.TryGetProperty("range_start_seconds", out var rss)
                && first.TryGetProperty("range_end_seconds", out var res))
            {
                _suppressAutoAnalyze = true;
                try
                {
                    LoopStartSeconds = rss.GetDouble();
                    LoopEndSeconds = res.GetDouble();
                    _userSetRange = true;
                    NotifyRangeUi();
                }
                finally
                {
                    _suppressAutoAnalyze = false;
                }
            }

            ApplyAnalyzeExtras(first);

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

    private async Task QueueSeamPreviewAsync()
    {
        var epoch = ++_previewEpoch;
        await Task.Delay(450);
        if (epoch != _previewEpoch || IsBusy || BatchFiles.Count == 0 || !HasQualityPreview)
        {
            return;
        }

        await EncodeSeamPreviewAsync(epoch);
    }

    private async Task EncodeSeamPreviewAsync(int epoch)
    {
        var input = BatchFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            return;
        }

        IsPreviewEncoding = true;
        PreviewHint = Loc.T("perfectloop.previewEncoding");
        var key = CurrentLoopKey();

        var previewPath = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_prev_{Guid.NewGuid():N}.mp4");
        var configPath = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_prev_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_perfect_loop_prev_out_{Guid.NewGuid():N}.json");
        var payload = new
        {
            mode = "preview",
            input,
            output = previewPath,
            files = new[] { input },
            seam_seconds = SeamSeconds,
            seam_method = SelectedSeamMethod,
            smart_trim = false,
            min_loop_seconds = 1.5,
            ffmpeg_encoder = "cpu",
            target_fps = TargetFps,
            mute_audio = true,
            loop_count = 1,
            preview_max_width = 640,
            range_start_seconds = Math.Max(0, LoopStartSeconds),
            range_end_seconds = EffectiveLoopEnd,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "perfect_loop_job.py",
                ["--config-json", configPath, "--output-json", outputJson]);

            if (epoch != _previewEpoch)
            {
                TryDelete(previewPath);
                return;
            }

            if (!result.Success || !File.Exists(previewPath))
            {
                PreviewHint = Loc.T("perfectloop.previewSource");
                TryDelete(previewPath);
                return;
            }

            var oldTemp = _seamPreviewTempPath;
            _seamPreviewTempPath = previewPath;
            _loopPreviewKey = key;
            LoopPreviewPath = previewPath;
            PreviewHint = Loc.T("perfectloop.previewSeam");
            if (!string.IsNullOrWhiteSpace(oldTemp)
                && !string.Equals(oldTemp, previewPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(oldTemp);
            }
        }
        finally
        {
            if (epoch == _previewEpoch)
            {
                IsPreviewEncoding = false;
            }

            TryDelete(configPath);
            TryDelete(outputJson);
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
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

        _previewEpoch++;
        IsBusy = true;
        var outDir = OptionalOutputDir();
        var failed = 0;
        var encoderId = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardware, SelectedEncoder);
        var repeats = ExportLoopCount;

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
                    smart_trim = false,
                    min_loop_seconds = 1.5,
                    ffmpeg_encoder = encoderId,
                    target_fps = TargetFps,
                    mute_audio = true,
                    loop_count = repeats,
                    crf = ExportCrf,
                    range_start_seconds = Math.Max(0, LoopStartSeconds),
                    range_end_seconds = EffectiveLoopEnd,
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
