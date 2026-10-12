using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class AudioCleanerViewModel : ToolIoViewModel, IToolShellHost, ISplitPaneToolHost, ILocalizable
{
    private static readonly string SettingsPath = Path.Combine(AppPaths.SettingsDirectory, "audio_cleaner.json");

    private readonly ToolDefinition _tool;
    private readonly string _previewDir = Path.Combine(Path.GetTempPath(), "hm_audio_preview");
    private bool _loadingSettings;
    private string? _previewKey;

    public AudioCleanerViewModel(ToolDefinition tool)
        : base(tool.Id)
    {
        _tool = tool;
        LoadSettings();
        RefreshBatchFiles();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<string> BatchFiles { get; } = [];

    public IReadOnlyList<string> PresetOptions { get; } = ["low", "mid", "high"];

    public IReadOnlyList<string> VideoCodecOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.LibAndNvencWithCopy);

    [ObservableProperty] private string _outputPath = string.Empty;

    [ObservableProperty] private string _selectedPreset = "mid";

    [ObservableProperty] private bool _focusEcho;

    [ObservableProperty] private bool _focusNoise;

    /// <summary>Volume in percent (100 = unchanged).</summary>
    [ObservableProperty] private double _boostPercent = 116.67;

    [ObservableProperty] private string _selectedVideoCodec = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.LibAndNvencWithCopy,
        "copy");

    [ObservableProperty] private double _targetFps;

    [ObservableProperty] private double _videoBitrateKbps = 4000;

    [ObservableProperty] private string _batchSummary = Loc.T("intro.batchSummaryEmpty");

    [ObservableProperty] private string _status = Loc.T("common.ready");

    [ObservableProperty] private bool _isBusy;

    // --- A/B preview
    [ObservableProperty] private double _previewStartSeconds;

    [ObservableProperty] private double _previewMaxStart = 1;

    [ObservableProperty] private string _previewOriginalPath = string.Empty;

    [ObservableProperty] private string _previewCleanedPath = string.Empty;

    [ObservableProperty] private bool _isPreviewRendering;

    public bool HasPreview => File.Exists(PreviewOriginalPath) && File.Exists(PreviewCleanedPath);

    /// <summary>Settings changed since the snippet was rendered.</summary>
    public bool IsPreviewStale => HasPreview && _previewKey != CurrentPreviewKey();

    public string PreviewStartLabel => Loc.F(
        "audiocleaner.previewStartValue",
        TimeSpan.FromSeconds(PreviewStartSeconds).ToString(@"m\:ss", CultureInfo.InvariantCulture));

    public string BoostLabel => Loc.F(
        "audiocleaner.boostValue",
        Math.Round(BoostPercent).ToString("0", CultureInfo.CurrentCulture),
        ((BoostPercent - 100.0) * 0.18).ToString("+0.0;-0.0;0", CultureInfo.CurrentCulture));

    /// <summary>ComboBox index for the localized preset list.</summary>
    public int PresetIndex
    {
        get => Math.Max(0, PresetOptions.ToList().IndexOf(SelectedPreset));
        set
        {
            if (value >= 0 && value < PresetOptions.Count)
            {
                SelectedPreset = PresetOptions[value];
            }
        }
    }

    public string ExportButtonLabel => BatchFiles.Count > 1
        ? Loc.F("audiocleaner.exportBatch", BatchFiles.Count)
        : Loc.T("common.startExport");

    protected override void OnOutputDirUpdated(string value) => RefreshOutputFromInput();

    protected override void OnInputPathUpdated(string value) => RefreshBatchFiles();

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(PresetIndex));
        OnCleanSettingChanged();
    }

    partial void OnFocusEchoChanged(bool value) => OnCleanSettingChanged();

    partial void OnFocusNoiseChanged(bool value) => OnCleanSettingChanged();

    partial void OnBoostPercentChanged(double value)
    {
        OnPropertyChanged(nameof(BoostLabel));
        OnCleanSettingChanged();
    }

    partial void OnSelectedVideoCodecChanged(string value) => SaveSettings();

    partial void OnTargetFpsChanged(double value) => SaveSettings();

    partial void OnVideoBitrateKbpsChanged(double value) => SaveSettings();

    partial void OnPreviewStartSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewStartLabel));
        OnPropertyChanged(nameof(IsPreviewStale));
    }

    partial void OnPreviewOriginalPathChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnPreviewCleanedPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(IsPreviewStale));
    }

    private void OnCleanSettingChanged()
    {
        OnPropertyChanged(nameof(IsPreviewStale));
        SaveSettings();
    }

    private void RefreshBatchFiles()
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
            _ => Loc.F("audiocleaner.batchSummaryMany", BatchFiles.Count),
        };

        OnPropertyChanged(nameof(ExportButtonLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        RefreshOutputFromInput();
        _ = ProbePreviewRangeAsync();
    }

    /// <summary>Default the snippet to a third into the first file (intros are often silent).</summary>
    private async Task ProbePreviewRangeAsync()
    {
        var first = BatchFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            PreviewMaxStart = 1;
            return;
        }

        var duration = await FfprobeHelper.ProbeDurationSecondsAsync(first) ?? 0;
        if (!string.Equals(first, BatchFiles.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PreviewMaxStart = Math.Max(1, duration - 10);
        PreviewStartSeconds = Math.Round(Math.Clamp(duration / 3, 0, PreviewMaxStart));
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
        return Path.Combine(dir, $"{ip.Name[..^ip.Extension.Length]}_audio_clean{ip.Extension}");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        RefreshBatchFiles();
        var files = BatchFiles.ToList();
        if (files.Count == 0)
        {
            Status = Loc.T("audiocleaner.noInput");
            return;
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
                    ? Loc.F("audiocleaner.batchProgress", i + 1, files.Count, Path.GetFileName(input))
                    : Loc.T("audiocleaner.exportRunning");

                var result = await RunJobAsync(BuildConfig(input, output));
                if (!result.Success)
                {
                    // Keep going: one damaged file must not stop the whole batch.
                    failed++;
                    continue;
                }

                OutputPath = output;
            }

            Status = failed == 0
                ? files.Count > 1 ? Loc.F("audiocleaner.batchDone", files.Count) : Loc.T("audiocleaner.exportDone")
                : files.Count > 1 ? Loc.F("intro.batchPartialFailed", failed, files.Count) : Loc.T("intro.abortedSeeLog");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Render ~10 s once untouched and once cleaned for an A/B comparison.</summary>
    [RelayCommand]
    private async Task RenderPreviewAsync()
    {
        var input = BatchFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(input) || IsPreviewRendering)
        {
            Status = Loc.T("audiocleaner.noInput");
            return;
        }

        IsPreviewRendering = true;
        Status = Loc.T("audiocleaner.previewRendering");
        try
        {
            // Fresh folder per render: the player may still hold the previous files open.
            var dir = Path.Combine(_previewDir, Guid.NewGuid().ToString("N"));
            var config = BuildConfig(input, Path.Combine(dir, "unused.mp4"), new
            {
                mode = "preview",
                preview_dir = dir,
                preview_start = PreviewStartSeconds,
                preview_seconds = 10,
            });
            var key = CurrentPreviewKey();
            var result = await RunJobAsync(config);
            var original = Path.Combine(dir, "preview_original.wav");
            var cleaned = Path.Combine(dir, "preview_cleaned.wav");
            if (!result.Success || !File.Exists(original) || !File.Exists(cleaned))
            {
                Status = Loc.T("audiocleaner.previewFailed");
                return;
            }

            CleanupOldPreviews(dir);
            _previewKey = key;
            PreviewOriginalPath = original;
            PreviewCleanedPath = cleaned;
            Status = Loc.T("audiocleaner.previewReady");
        }
        finally
        {
            IsPreviewRendering = false;
        }
    }

    private void CleanupOldPreviews(string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_previewDir))
            {
                if (!string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // still in use by the player — removed next time
                    }
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private string CurrentPreviewKey() => string.Join(
        '|',
        BatchFiles.FirstOrDefault() ?? string.Empty,
        SelectedPreset,
        FocusEcho,
        FocusNoise,
        BoostPercent.ToString("0.##", CultureInfo.InvariantCulture),
        PreviewStartSeconds.ToString("0.#", CultureInfo.InvariantCulture));

    private static async Task<JobResult> RunJobAsync(Dictionary<string, object?> config)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"hm_audio_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
        try
        {
            return await AppServices.JobRunner.RunBridgeAsync("audio_clean_export_job.py", ["--config-json", configPath]);
        }
        finally
        {
            try
            {
                File.Delete(configPath);
            }
            catch
            {
                // best effort
            }
        }
    }

    private Dictionary<string, object?> BuildConfig(string input, string output, object? extra = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["output"] = output,
            ["preset"] = SelectedPreset,
            ["focus_echo"] = FocusEcho,
            ["focus_noise"] = FocusNoise,
            ["boost_pct"] = Math.Clamp(BoostPercent, 0, 200),
            ["video_codec"] = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.LibAndNvencWithCopy, SelectedVideoCodec),
            ["video_bitrate_kbps"] = Math.Clamp((int)Math.Round(VideoBitrateKbps), 100, 200_000),
            ["video_bitrate_set"] = true,
            ["target_fps"] = TargetFps,
        };
        if (extra is not null)
        {
            foreach (var prop in extra.GetType().GetProperties())
            {
                config[prop.Name] = prop.GetValue(extra);
            }
        }

        return config;
    }

    private sealed class StoredSettings
    {
        public string Preset { get; set; } = "mid";

        public bool FocusEcho { get; set; }

        public bool FocusNoise { get; set; }

        public double BoostPercent { get; set; } = 116.67;

        public string VideoCodec { get; set; } = "copy";

        public double TargetFps { get; set; }

        public double VideoBitrateKbps { get; set; } = 4000;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var s = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
            if (s is null)
            {
                return;
            }

            SelectedPreset = PresetOptions.Contains(s.Preset) ? s.Preset : "mid";
            FocusEcho = s.FocusEcho;
            FocusNoise = s.FocusNoise;
            BoostPercent = Math.Clamp(s.BoostPercent, 0, 200);
            SelectedVideoCodec = FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.LibAndNvencWithCopy, s.VideoCodec);
            TargetFps = s.TargetFps;
            VideoBitrateKbps = s.VideoBitrateKbps > 0 ? s.VideoBitrateKbps : 4000;
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Audio Cleaner: Einstellungen nicht lesbar ({ex.Message})");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new StoredSettings
            {
                Preset = SelectedPreset,
                FocusEcho = FocusEcho,
                FocusNoise = FocusNoise,
                BoostPercent = BoostPercent,
                VideoCodec = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.LibAndNvencWithCopy, SelectedVideoCodec),
                TargetFps = TargetFps,
                VideoBitrateKbps = VideoBitrateKbps,
            }));
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Audio Cleaner: Einstellungen nicht gespeichert ({ex.Message})");
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
