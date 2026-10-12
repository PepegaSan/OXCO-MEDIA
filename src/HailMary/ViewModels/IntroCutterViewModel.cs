using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class IntroCutterViewModel : ToolIoViewModel, IToolShellHost, ISplitPaneToolHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private double _durationSeconds;

    public IntroCutterViewModel(ToolDefinition tool)
        : base(tool.Id)
    {
        _tool = tool;
        var settings = IntroCutterSettingsReader.Load();
        IntroSec = settings.IntroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        OutroSec = settings.OutroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        UseResolve = settings.UseResolve;
        SelectedVideoCodec = FfmpegEncoderCatalog.LabelFor(
            FfmpegEncoderCatalog.IntroVideoCodecs,
            settings.VideoCodec);
        VideoBitrate = settings.VideoBitrate;
        VideoBitrateAuto = settings.VideoBitrateAuto;
        AudioCodec = settings.AudioCodec;
        AudioBitrate = settings.AudioBitrate;
        RenderPreset = settings.RenderPreset;
        SelectedLengthPreset = settings.SelectedPreset;
        PresetNameToSave = settings.SelectedPreset;
        OutputBesideSource = settings.OutputBesideSource;
        if (string.IsNullOrWhiteSpace(OutputDir) && !string.IsNullOrWhiteSpace(settings.OutputDir))
        {
            OutputDir = settings.OutputDir;
        }
        ReloadPresets(settings.Presets);
        InitializeBatchFromStorage();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<IntroBatchEntry> BatchFiles { get; } = [];

    [ObservableProperty]
    private string _previewVideoPath = string.Empty;

    public IReadOnlyList<string> VideoCodecOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.IntroVideoCodecs);

    public ObservableCollection<string> LengthPresetNames { get; } = [];

    [ObservableProperty]
    private string _introSec = "3.0";

    [ObservableProperty]
    private string _outroSec = "2.0";

    [ObservableProperty]
    private string _selectedLengthPreset = Loc.T("bitrate.presetStandard");

    [ObservableProperty]
    private string _presetNameToSave = Loc.T("bitrate.presetStandard");

    [ObservableProperty]
    private string _batchSummary = Loc.T("intro.batchSummaryEmpty");

    public string RunButtonLabel
    {
        get
        {
            var included = BatchFiles.Count(e => e.IsIncluded);
            return included > 1 ? Loc.F("intro.runBatch", included) : Loc.T("intro.run");
        }
    }

    [ObservableProperty]
    private bool _useResolve;

    [ObservableProperty]
    private string _selectedVideoCodec = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.IntroVideoCodecs,
        "libx264");

    [ObservableProperty]
    private double _targetFps;

    [ObservableProperty]
    private string _videoBitrate = "8M";

    [ObservableProperty]
    private bool _videoBitrateAuto;

    [ObservableProperty]
    private string _audioCodec = "aac";

    [ObservableProperty]
    private string _audioBitrate = "192k";

    [ObservableProperty]
    private string _renderPreset = "YouTube - 1080p";

    [ObservableProperty]
    private string _status = Loc.T("common.ready");

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _outputBesideSource = true;

    [ObservableProperty]
    private bool _hasVideo;

    [ObservableProperty]
    private double _sliderValue;

    [ObservableProperty]
    private double _sliderMaximum = 1;

    [ObservableProperty]
    private string _positionDisplay = "00:00:00";

    [ObservableProperty]
    private string _durationDisplay = "00:00:00";

    [ObservableProperty]
    private string _cutSummary = Loc.T("intro.cutSummaryDefault");

    [ObservableProperty]
    private double _introBarWeight = 1;

    [ObservableProperty]
    private double _mainBarWeight = 8;

    [ObservableProperty]
    private double _outroBarWeight = 1;

    [ObservableProperty]
    private string _introBarLabel = Loc.T("common.intro");

    [ObservableProperty]
    private string _mainBarLabel = Loc.T("intro.mainBarLabel");

    [ObservableProperty]
    private string _outroBarLabel = Loc.T("common.outro");

    [ObservableProperty]
    private float[]? _waveformPeaks;

    public IReadOnlyList<TimelineSpan> TimelineRanges { get; private set; } = [];

    public bool ShowFfmpegOptions => !UseResolve;

    public bool ShowVideoBitrateField => ShowFfmpegOptions && !VideoBitrateAuto;

    public bool ShowOutputDirField => !OutputBesideSource;

    partial void OnOutputBesideSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOutputDirField));
        IntroCutterSettingsReader.SaveOutputSettings(OutputDir, value);
    }

    protected override void OnOutputDirUpdated(string value) =>
        IntroCutterSettingsReader.SaveOutputSettings(value, OutputBesideSource);

    private string? GetEffectiveOutputDir() => OutputBesideSource ? null : OptionalOutputDir();

    partial void OnUseResolveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFfmpegOptions));
        OnPropertyChanged(nameof(ShowVideoBitrateField));
    }

    partial void OnVideoBitrateAutoChanged(bool value) => OnPropertyChanged(nameof(ShowVideoBitrateField));

    partial void OnIntroSecChanged(string value) => UpdateCutVisualization();

    partial void OnOutroSecChanged(string value) => UpdateCutVisualization();

    partial void OnSelectedLengthPresetChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        PresetNameToSave = value;

        var settings = IntroCutterSettingsReader.Load();
        var preset = settings.Presets.FirstOrDefault(p => p.Name == value);
        if (preset is null)
        {
            return;
        }

        IntroSec = preset.IntroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        OutroSec = preset.OutroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    protected override void OnInputPathUpdated(string value)
    {
        HasVideo = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
        UpdateCutVisualization();
    }

    public async Task LoadWaveformAsync(string? path)
    {
        WaveformPeaks = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var peaks = await WaveformPeakExtractor.ExtractAsync(path);
        if (string.Equals(path, PreviewVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            WaveformPeaks = peaks;
        }
    }

    private void RefreshBatchFiles()
    {
        BatchFiles.Clear();
        foreach (var entry in _batchEntries)
        {
            BatchFiles.Add(entry);
        }

        UpdateBatchSummaryText();
        OnPropertyChanged(nameof(RunButtonLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void SetDuration(double seconds)
    {
        _durationSeconds = Math.Max(0, seconds);
        SliderMaximum = Math.Max(0.001, _durationSeconds);
        DurationDisplay = FormatTime(_durationSeconds);
        UpdateCutVisualization();
    }

    public void SetPosition(double seconds)
    {
        SliderValue = Math.Clamp(seconds, 0, SliderMaximum);
        PositionDisplay = FormatTime(seconds);
    }

    public double ParsedIntroSec => ParseSec(IntroSec);

    public double ParsedOutroSec => ParseSec(OutroSec);

    /// <summary>Intro ends at the current player position.</summary>
    [RelayCommand]
    private void SetIntroFromPosition()
    {
        var pos = Math.Round(SliderValue, 3);
        if (_durationSeconds > 0 && pos + ParsedOutroSec >= _durationSeconds)
        {
            Status = Loc.T("intro.lengthsTooLong");
            return;
        }

        IntroSec = pos.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Status = Loc.F("intro.introSetStatus", IntroSec);
    }

    /// <summary>Outro starts at the current player position (length = rest of the video).</summary>
    [RelayCommand]
    private void SetOutroFromPosition()
    {
        if (_durationSeconds <= 0)
        {
            return;
        }

        var outro = Math.Round(Math.Max(0, _durationSeconds - SliderValue), 3);
        if (ParsedIntroSec + outro >= _durationSeconds)
        {
            Status = Loc.T("intro.lengthsTooLong");
            return;
        }

        OutroSec = outro.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Status = Loc.F("intro.outroSetStatus", OutroSec);
    }

    [RelayCommand]
    private void SeekToIntroEnd()
    {
        SetPosition(ParsedIntroSec);
    }

    [RelayCommand]
    private void SeekToOutroStart()
    {
        var pos = Math.Max(0, _durationSeconds - ParsedOutroSec);
        SetPosition(pos);
    }

    [RelayCommand]
    private void SaveLengthPreset()
    {
        if (!TryParseLengths(out var intro, out var outro, out var error))
        {
            Status = error;
            return;
        }

        var name = PresetNameToSave.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = Loc.T("intro.presetNameRequired");
            return;
        }

        IntroCutterSettingsReader.SavePreset(name, intro, outro, name);
        ReloadPresets(IntroCutterSettingsReader.Load().Presets);
        SelectedLengthPreset = name;
        PresetNameToSave = name;
        Status = Loc.F("intro.presetSaved", name, intro, outro);
    }

    [RelayCommand]
    private void DeleteLengthPreset()
    {
        var name = SelectedLengthPreset.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IntroCutterSettingsReader.DeletePreset(name);
        var settings = IntroCutterSettingsReader.Load();
        ReloadPresets(settings.Presets);
        SelectedLengthPreset = settings.SelectedPreset;
        IntroSec = settings.IntroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        OutroSec = settings.OutroSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Status = Loc.F("intro.presetDeleted", name);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        RefreshBatchFiles();
        var files = GetIncludedPaths();
        if (files.Count == 0)
        {
            Status = _batchEntries.Count > 0
                ? Loc.T("intro.noVideosMarked")
                : Loc.T("intro.noVideosImport");
            AppServices.Log.Error("Intro Cutter: keine markierte Eingabe");
            return;
        }

        if (!TryParseLengths(out var intro, out var outro, out var error))
        {
            Status = error;
            return;
        }

        IsRunning = true;
        Status = files.Count > 1 ? Loc.F("intro.batchStarting", files.Count) : Loc.T("common.running");

        var outDir = GetEffectiveOutputDir();
            IntroCutterSettingsReader.SaveRunSettings(
            intro, outro, SelectedLengthPreset, UseResolve,
            FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.IntroVideoCodecs, SelectedVideoCodec),
            VideoBitrate, VideoBitrateAuto,
            AudioCodec, AudioBitrate, RenderPreset, outDir, OutputBesideSource);

        var failed = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var input = files[i];
                var label = Path.GetFileName(input);
                var number = i + 1;
                UiDispatcher.Run(() => Status = files.Count > 1
                    ? Loc.F("intro.runningBatch", number, files.Count, label)
                    : Loc.T("common.running"));
                AppServices.Log.Info($"--- {label} ---");

                var args = BuildJobArgs(input, intro, outro, outDir);
                var result = await AppServices.JobRunner.RunBridgeAsync(_tool.Bridge ?? "intro_cutter_job.py", args);
                if (!result.Success)
                {
                    // One broken file (too short, unreadable…) must not stop the rest of the batch.
                    failed++;
                    AppServices.Log.Error($"Fehler bei {label}");
                }
            }

            UiDispatcher.Run(() => Status = failed == 0
                ? files.Count > 1
                    ? Loc.F("intro.batchDone", files.Count)
                    : Loc.T("common.done")
                : files.Count > 1
                    ? Loc.F("intro.batchPartialFailed", failed, files.Count)
                    : Loc.T("intro.abortedSeeLog"));
        }
        finally
        {
            UiDispatcher.Run(() => IsRunning = false);
        }
    }

    private List<string> BuildJobArgs(string input, double intro, double outro, string? outDir)
    {
        var args = new List<string>
        {
            "--input", input,
            "--intro", intro.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--outro", outro.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--mode", UseResolve ? "resolve" : "ffmpeg",
            "--vcodec", FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.IntroVideoCodecs, SelectedVideoCodec),
            "--vbitrate", VideoBitrate,
            "--acodec", AudioCodec,
            "--abitrate", AudioBitrate,
            "--preset", RenderPreset,
        };

        if (TargetFps > 0)
        {
            args.Add("--target-fps");
            args.Add(TargetFps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (VideoBitrateAuto)
        {
            args.Add("--vbitrate-auto");
        }

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            args.Add("--output-dir");
            args.Add(outDir);
        }

        return args;
    }

    [RelayCommand]
    private void OpenFullGui()
    {
        var result = AppServices.Launcher.Launch(_tool);
        Status = result.Success ? Loc.T("intro.fullGuiStarted") : Loc.T("intro.fullGuiFailed");
    }

    private void ReloadPresets(IReadOnlyList<IntroCutterSettingsReader.IntroOutroPreset> presets)
    {
        LengthPresetNames.Clear();
        foreach (var preset in presets)
        {
            LengthPresetNames.Add(preset.Name);
        }
    }

    private void UpdateCutVisualization()
    {
        var intro = ParsedIntroSec;
        var outro = ParsedOutroSec;
        var duration = _durationSeconds > 0 ? _durationSeconds : intro + outro + 10;
        var main = Math.Max(0, duration - intro - outro);

        IntroBarWeight = Math.Max(0.001, intro);
        MainBarWeight = Math.Max(0.001, main);
        OutroBarWeight = Math.Max(0.001, outro);

        IntroBarLabel = Loc.F("intro.introBarWithTime", intro);
        MainBarLabel = main > 0 ? Loc.F("intro.mainBarWithTime", main) : Loc.T("intro.mainBarEmpty");
        OutroBarLabel = Loc.F("intro.outroBarWithTime", outro);

        var spans = new List<TimelineSpan>();
        if (duration > 0.05)
        {
            if (intro > 0.02)
            {
                spans.Add(new TimelineSpan(0, Math.Min(intro, duration), TimelineSpanKind.Pending, IntroBarLabel));
            }

            var keepStart = Math.Min(intro, duration);
            var keepEnd = Math.Max(keepStart, duration - outro);
            if (keepEnd > keepStart + 0.02)
            {
                spans.Add(new TimelineSpan(keepStart, keepEnd, TimelineSpanKind.Scene, MainBarLabel));
            }

            if (outro > 0.02 && duration - outro < duration)
            {
                spans.Add(new TimelineSpan(Math.Max(0, duration - outro), duration, TimelineSpanKind.Pending, OutroBarLabel));
            }
        }

        TimelineRanges = spans;
        OnPropertyChanged(nameof(TimelineRanges));

        if (_durationSeconds > 0)
        {
            CutSummary = Loc.F("intro.cutSummaryTimes", FormatTime(_durationSeconds), FormatTime(main));
        }
        else
        {
            CutSummary = Loc.T("intro.loadVideoForPreview");
        }
    }

    private bool TryParseLengths(out double intro, out double outro, out string error)
    {
        intro = ParseSec(IntroSec);
        outro = ParseSec(OutroSec);
        if (!IsValidSeconds(IntroSec) || !IsValidSeconds(OutroSec))
        {
            error = Loc.T("intro.invalidLengths");
            return false;
        }

        if (intro < 0 || outro < 0)
        {
            error = Loc.T("intro.negativeLengths");
            return false;
        }

        if (_durationSeconds > 0 && intro + outro >= _durationSeconds)
        {
            error = Loc.T("intro.lengthsTooLong");
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidSeconds(string value) =>
        string.IsNullOrWhiteSpace(value)
        || double.TryParse(value.Trim().Replace(",", "."), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static double ParseSec(string value)
    {
        if (double.TryParse(value.Replace(",", "."), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
        {
            return sec;
        }

        return 0;
    }

    private static string FormatTime(double sec)
    {
        if (sec < 0)
        {
            sec = 0;
        }

        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
