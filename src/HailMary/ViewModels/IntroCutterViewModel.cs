using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class IntroCutterViewModel : ToolIoViewModel, IToolShellHost, ILocalizable
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
        SelectedVideoCodec = settings.VideoCodec;
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
    [
        "libx264",
        "libx265",
        "libvpx-vp9",
        "libsvtav1",
        "libaom-av1",
        "h264_nvenc",
        "hevc_nvenc",
        "av1_nvenc",
        "mpeg4",
        "copy",
    ];

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
            return included > 1 ? $"Batch starten ({included})" : "Ausführen";
        }
    }

    [ObservableProperty]
    private bool _useResolve;

    [ObservableProperty]
    private string _selectedVideoCodec = "libx264";

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
        Status = $"Preset '{name}' gespeichert ({intro:0.##}s / {outro:0.##}s)";
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
        Status = $"Preset '{name}' gelöscht";
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
                ? "Keine Videos zum Schnitt markiert — bitte anhaken"
                : "Keine Videos — Datei(en) oder Ordner importieren";
            AppServices.Log.Error("Intro Cutter: keine markierte Eingabe");
            return;
        }

        if (!TryParseLengths(out var intro, out var outro, out var error))
        {
            Status = error;
            return;
        }

        IsRunning = true;
        Status = files.Count > 1 ? $"Batch 0/{files.Count}…" : Loc.T("common.running");

        var outDir = GetEffectiveOutputDir();
        IntroCutterSettingsReader.SaveRunSettings(
            intro, outro, SelectedLengthPreset, UseResolve,
            SelectedVideoCodec, VideoBitrate, VideoBitrateAuto,
            AudioCodec, AudioBitrate, RenderPreset, outDir, OutputBesideSource);

        var failed = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var input = files[i];
                var label = Path.GetFileName(input);
                UiDispatcher.Run(() => Status = files.Count > 1
                    ? $"Batch {i + 1}/{files.Count}: {label}"
                    : Loc.T("common.running"));
                AppServices.Log.Info($"--- {label} ---");

                var args = BuildJobArgs(input, intro, outro, outDir);
                var result = await AppServices.JobRunner.RunBridgeAsync(_tool.Bridge ?? "intro_cutter_job.py", args);
                if (!result.Success)
                {
                    failed++;
                    AppServices.Log.Error($"Fehler bei {label}");
                    break;
                }
            }

            var ok = failed == 0;
            UiDispatcher.Run(() => Status = ok
                ? files.Count > 1
                    ? $"Batch fertig — {files.Count} Videos"
                    : Loc.T("common.done")
                : "Abgebrochen — siehe Log");
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
            "--vcodec", SelectedVideoCodec,
            "--vbitrate", VideoBitrate,
            "--acodec", AudioCodec,
            "--abitrate", AudioBitrate,
            "--preset", RenderPreset,
        };

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
        Status = result.Success ? "Volles GUI gestartet" : "Start fehlgeschlagen";
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

        IntroBarLabel = $"Intro {intro:0.##}s";
        MainBarLabel = main > 0 ? $"Inhalt {main:0.#}s" : Loc.T("intro.mainBarEmpty");
        OutroBarLabel = $"Outro {outro:0.##}s";

        if (_durationSeconds > 0)
        {
            CutSummary = $"Gesamt {FormatTime(_durationSeconds)} | behalten {FormatTime(main)}";
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
