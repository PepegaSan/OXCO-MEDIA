using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>Export engine: FFmpeg (direct files, mixed formats normalized) or DaVinci Resolve (timelines).</summary>
public partial class MarkerAutocutViewModel
{
    private static readonly string[] EngineIds = ["ffmpeg", "davinci"];

    /// <summary>Wire ids for the bridge (resolution / fps / fit), same order as the ComboBox items.</summary>
    private static readonly string[] ResolutionIds = ["auto", "2160p", "1440p", "1080p", "720p", "1080x1920", "720x1280"];

    private static readonly string[] FpsIds = ["auto", "23.976", "24", "25", "29.97", "30", "50", "59.94", "60"];

    private static readonly string[] FitIds = ["blur", "letterbox", "crop"];

    public IReadOnlyList<string> FfmpegEncoderOptions { get; } =
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.SoftHardware);

    [ObservableProperty] private string _exportEngine = "ffmpeg";
    [ObservableProperty] private string _ffmpegResolution = "auto";
    [ObservableProperty] private string _ffmpegFps = "auto";
    [ObservableProperty] private string _ffmpegFit = "blur";
    [ObservableProperty] private string _ffmpegEncoder = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.SoftHardware,
        FfmpegEncoderCatalog.DefaultSoftHardwareId);

    public bool IsFfmpegEngine => ExportEngine == "ffmpeg";

    public bool IsDavinciEngine => !IsFfmpegEngine;

    public int EngineIndex
    {
        get => Math.Max(0, Array.IndexOf(EngineIds, ExportEngine));
        set
        {
            if (value >= 0 && value < EngineIds.Length)
            {
                ExportEngine = EngineIds[value];
            }
        }
    }

    public int ResolutionIndex
    {
        get => Math.Max(0, Array.IndexOf(ResolutionIds, FfmpegResolution));
        set
        {
            if (value >= 0 && value < ResolutionIds.Length)
            {
                FfmpegResolution = ResolutionIds[value];
            }
        }
    }

    public int FpsIndex
    {
        get => Math.Max(0, Array.IndexOf(FpsIds, FfmpegFps));
        set
        {
            if (value >= 0 && value < FpsIds.Length)
            {
                FfmpegFps = FpsIds[value];
            }
        }
    }

    public int FitIndex
    {
        get => Math.Max(0, Array.IndexOf(FitIds, FfmpegFit));
        set
        {
            if (value >= 0 && value < FitIds.Length)
            {
                FfmpegFit = FitIds[value];
            }
        }
    }

    /// <summary>Fit only matters when formats are mixed into one video.</summary>
    public bool ShowFitOption => IsFfmpegEngine && (IsCompilationMode || FfmpegResolution != "auto");

    private void LoadEngineSettings(MarkerAutocutSettings s)
    {
        ExportEngine = EngineIds.Contains(s.ExportEngine) ? s.ExportEngine : "ffmpeg";
        FfmpegResolution = ResolutionIds.Contains(s.FfmpegResolution) ? s.FfmpegResolution : "auto";
        FfmpegFps = FpsIds.Contains(s.FfmpegFps) ? s.FfmpegFps : "auto";
        FfmpegFit = FitIds.Contains(s.FfmpegFit) ? s.FfmpegFit : "blur";
        FfmpegEncoder = FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.SoftHardware, s.FfmpegEncoder);
    }

    private void StoreEngineSettings(MarkerAutocutSettings s)
    {
        s.ExportEngine = ExportEngine;
        s.FfmpegResolution = FfmpegResolution;
        s.FfmpegFps = FfmpegFps;
        s.FfmpegFit = FfmpegFit;
        s.FfmpegEncoder = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardware, FfmpegEncoder);
    }

    private void AddEngineOptions(Dictionary<string, object?> options)
    {
        options["ffmpeg_resolution"] = FfmpegResolution;
        options["ffmpeg_fps"] = FfmpegFps;
        options["ffmpeg_fit"] = FfmpegFit;
        options["ffmpeg_encoder"] = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.SoftHardware, FfmpegEncoder);
    }

    partial void OnExportEngineChanged(string value)
    {

        OnPropertyChanged(nameof(EngineIndex));
        OnPropertyChanged(nameof(IsFfmpegEngine));
        OnPropertyChanged(nameof(IsDavinciEngine));
        OnPropertyChanged(nameof(ShowFitOption));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        UpdateSummaries();
    }

    partial void OnFfmpegResolutionChanged(string value)
    {
        OnPropertyChanged(nameof(ResolutionIndex));
        OnPropertyChanged(nameof(ShowFitOption));
    }

    partial void OnFfmpegFpsChanged(string value) => OnPropertyChanged(nameof(FpsIndex));

    partial void OnFfmpegFitChanged(string value) => OnPropertyChanged(nameof(FitIndex));
}
