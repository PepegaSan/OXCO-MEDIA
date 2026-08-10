using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class MediaOverlayViewModel : ToolIoViewModel, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;

    public MediaOverlayViewModel(ToolDefinition tool)
        : base(tool.Id)
    {
        _tool = tool;
        RefreshOutputFromInput();
        if (!string.IsNullOrWhiteSpace(InputPath) && File.Exists(InputPath))
        {
            _ = ProbeAndRefreshPreviewAsync();
        }
    }

    public string Description => ToolText.Description(_tool);

    public IReadOnlyList<string> EncoderOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.SoftHardware);

    [ObservableProperty] private string _overlayPath = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private double _posX = 24;
    [ObservableProperty] private double _posY = 24;
    [ObservableProperty] private double _widthPercent = 25;
    [ObservableProperty] private string _startSecondsText = "0";
    [ObservableProperty] private string _endSecondsText = "";
    [ObservableProperty] private bool _mainAudio = true;
    [ObservableProperty] private bool _overlayAudio;
    [ObservableProperty] private string _selectedEncoder = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.SoftHardware,
        "nvidia_h264");
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    public bool OverlayAudioEnabled => IsOverlayVideoPath(OverlayPath);

    public string OverlayAudioHint => OverlayAudioEnabled
        ? Loc.T("mediaoverlay.overlayAudioHintVideo")
        : Loc.T("mediaoverlay.overlayAudioHintImage");

    protected override void OnOutputDirUpdated(string value) => RefreshOutputFromInput();

    protected override void OnInputPathUpdated(string value)
    {
        RefreshOutputFromInput();
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        _ = ProbeAndRefreshPreviewAsync();
    }

    partial void OnOverlayPathChanged(string value)
    {
        OnPropertyChanged(nameof(OverlayAudioEnabled));
        OnPropertyChanged(nameof(OverlayAudioHint));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        if (!IsOverlayVideoPath(value) && OverlayAudio)
        {
            OverlayAudio = false;
        }

        _ = ProbeAndRefreshPreviewAsync();
    }

    private void RefreshOutputFromInput()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            return;
        }

        OutputPath = BuildOutputPath(InputPath, OptionalOutputDir());
    }

    private static string BuildOutputPath(string input, string? outputDir)
    {
        var ip = new FileInfo(input);
        var dir = string.IsNullOrWhiteSpace(outputDir) ? ip.DirectoryName ?? "" : outputDir;
        return Path.Combine(dir, $"{ip.Name[..^ip.Extension.Length]}_overlay{ip.Extension}");
    }

    private static bool IsOverlayVideoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return TimeFieldHelper.TryParseSeconds(text.Trim(), out seconds);
    }

    [RelayCommand]
    private async Task PickOverlayFileAsync()
    {
        var path = await FilePickerHelper.PickImageOrVideoAsync(OverlayPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OverlayPath = path;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var main = RequireInputPath();
        if (main is null)
        {
            Status = Loc.T("mediaoverlay.noMain");
            return;
        }

        if (string.IsNullOrWhiteSpace(OverlayPath) || !File.Exists(OverlayPath))
        {
            Status = Loc.T("mediaoverlay.noOverlay");
            return;
        }

        if (!TryParseSeconds(StartSecondsText, out var start) || start < 0)
        {
            Status = Loc.T("mediaoverlay.invalidStart");
            return;
        }

        if (!TryParseSeconds(EndSecondsText, out var end) || end < 0)
        {
            Status = Loc.T("mediaoverlay.invalidEnd");
            return;
        }

        if (end > 0 && end <= start)
        {
            Status = Loc.T("mediaoverlay.endBeforeStart");
            return;
        }

        var outDir = OptionalOutputDir();
        var output = BuildOutputPath(main, outDir);
        OutputPath = output;

        IsBusy = true;
        Status = Loc.T("mediaoverlay.exportRunning");

        var configPath = Path.Combine(Path.GetTempPath(), $"hm_media_overlay_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_media_overlay_out_{Guid.NewGuid():N}.json");
        var payload = new
        {
            main,
            overlay = OverlayPath,
            output,
            output_dir = outDir ?? "",
            pos_x = (int)Math.Max(0, Math.Round(PosX)),
            pos_y = (int)Math.Max(0, Math.Round(PosY)),
            width_percent = WidthPercent,
            start_seconds = start,
            end_seconds = end,
            main_audio = MainAudio,
            overlay_audio = OverlayAudio && OverlayAudioEnabled,
            ffmpeg_encoder = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardware, SelectedEncoder),
            target_fps = TargetFps,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, System.Text.Json.JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "media_overlay_job.py",
                ["--config-json", configPath, "--output-json", outputJson]);

            if (!result.Success)
            {
                Status = result.Message;
                return;
            }

            Status = Loc.T("mediaoverlay.exportDone");
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
}
