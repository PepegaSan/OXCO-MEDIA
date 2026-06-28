using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;

using HailMary.Models;

using HailMary.Services;



namespace HailMary.ViewModels;



public partial class AudioCleanerViewModel : ToolIoViewModel, IToolShellHost, ILocalizable

{

    private readonly ToolDefinition _tool;



    public AudioCleanerViewModel(ToolDefinition tool)

        : base(tool.Id)

    {

        _tool = tool;

        RefreshBatchFiles();

    }



    public string Description => ToolText.Description(_tool);



    public ObservableCollection<string> BatchFiles { get; } = [];



    public IReadOnlyList<string> PresetOptions { get; } = ["low", "mid", "high"];



    public IReadOnlyList<string> VideoCodecOptions { get; } =

        ["copy", "libx264", "libx265", "h264_nvenc", "hevc_nvenc"];



    [ObservableProperty] private string _outputPath = string.Empty;

    [ObservableProperty] private string _selectedPreset = "mid";

    [ObservableProperty] private bool _focusEcho;

    [ObservableProperty] private bool _focusNoise;

    [ObservableProperty] private string _boostPercent = "116.67";

    [ObservableProperty] private string _selectedVideoCodec = "copy";

    [ObservableProperty] private string _videoBitrateKbps = "4000";

    [ObservableProperty] private string _batchSummary = Loc.T("intro.batchSummaryEmpty");

    [ObservableProperty] private string _status = Loc.T("common.ready");

    [ObservableProperty] private bool _isBusy;



    public string ExportButtonLabel => BatchFiles.Count > 1
        ? Loc.F("audiocleaner.exportBatch", BatchFiles.Count)
        : Loc.T("common.startExport");



    protected override void OnOutputDirUpdated(string value) => RefreshOutputFromInput();

    protected override void OnInputPathUpdated(string value) => RefreshBatchFiles();



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

                    ? $"Batch {i + 1}/{files.Count}: {Path.GetFileName(input)}"

                    : Loc.T("audiocleaner.exportRunning");



                var configPath = Path.Combine(Path.GetTempPath(), $"hm_audio_{Guid.NewGuid():N}.json");

                var json = System.Text.Json.JsonSerializer.Serialize(BuildConfig(input, output));

                await File.WriteAllTextAsync(configPath, json);



                var result = await AppServices.JobRunner.RunBridgeAsync("audio_clean_export_job.py",

                    ["--config-json", configPath]);

                if (!result.Success)

                {

                    failed++;

                    break;

                }



                OutputPath = output;

            }



            Status = failed == 0
                ? files.Count > 1 ? Loc.F("audiocleaner.batchDone", files.Count) : Loc.T("audiocleaner.exportDone")
                : Loc.T("intro.abortedSeeLog");

        }

        finally

        {

            IsBusy = false;

        }

    }



    private object BuildConfig(string input, string output) => new

    {

        input,

        output,

        preset = SelectedPreset,

        focus_echo = FocusEcho,

        focus_noise = FocusNoise,

        boost_pct = double.TryParse(BoostPercent, System.Globalization.NumberStyles.Float,

            System.Globalization.CultureInfo.InvariantCulture, out var bp) ? bp : 116.67,

        video_codec = SelectedVideoCodec,

        video_bitrate_kbps = int.TryParse(VideoBitrateKbps, out var vb) ? vb : 4000,

        video_bitrate_set = true,

    };



    [RelayCommand]

    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

}


