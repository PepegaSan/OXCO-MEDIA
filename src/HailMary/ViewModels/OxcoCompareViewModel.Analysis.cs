using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>Compare analysis mode, post filters, sync and FFmpeg encoder (sent to compare.py via the bridge).</summary>
public partial class OxcoCompareViewModel
{
    private sealed record Thresholds(string Noise, string Pixel, string PixelMax);

    public static IReadOnlyList<string> AnalysisModeKeys { get; } = ["classic", "blob"];

    private string _analysisMode = "classic";

    // Noise/Pixel/PixelMax of each mode; the active mode's values live in FilterNoise/FilterPixel/FilterPixelMax.
    private Thresholds _classicThresholds = new("15", "200", "0");
    private Thresholds _blobThresholds = new("12", "100", "0");
    private bool _initializingAnalysis;

    [ObservableProperty] private string _analysisModeLabel = string.Empty;
    [ObservableProperty] private string _filterAnalysisWidth = "640";
    [ObservableProperty] private bool _filterAnalysisHwaccel;
    [ObservableProperty] private bool _filterAutoSync = true;
    [ObservableProperty] private string _filterMinDiffRun = "2";
    [ObservableProperty] private string _filterMinKeepSeconds = "0.25";
    [ObservableProperty] private string _filterCutPadding = "0";
    [ObservableProperty] private string _filterFfmpegEncoder =
        FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.LibAndNvenc, FfmpegEncoderCatalog.DefaultLibx264Id);

    public IReadOnlyList<string> AnalysisModeChoices => AnalysisModeKeys.Select(LocalizeAnalysisMode).ToList();

    public IReadOnlyList<string> CompareEncoderOptions => FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.LibAndNvenc);

    public bool IsBlobMode => _analysisMode == "blob";

    private static string LocalizeAnalysisMode(string key) => Loc.T($"oxco.analysisMode.{key}");

    private static string? AnalysisModeKeyFromLabel(string? label) =>
        AnalysisModeKeys.FirstOrDefault(k =>
            string.Equals(LocalizeAnalysisMode(k), label, StringComparison.Ordinal)
            || string.Equals(k, label, StringComparison.OrdinalIgnoreCase));

    private void InitializeAnalysisFromSettings(OxcoCompareSettings settings)
    {
        _initializingAnalysis = true;
        try
        {
            _classicThresholds = new Thresholds(settings.FilterNoise, settings.FilterPixel, settings.FilterPixelMax);
            _blobThresholds = new Thresholds(settings.BlobNoise, settings.BlobPixel, settings.BlobPixelMax);
            _analysisMode = AnalysisModeKeyFromLabel(settings.AnalysisMode) ?? "classic";
            ApplyThresholds(_analysisMode == "blob" ? _blobThresholds : _classicThresholds);
            AnalysisModeLabel = LocalizeAnalysisMode(_analysisMode);
            FilterAnalysisWidth = settings.AnalysisWidth;
            FilterAnalysisHwaccel = settings.AnalysisHwaccel;
            FilterAutoSync = settings.AutoSyncOffset;
            FilterMinDiffRun = settings.MinDiffRunFrames;
            FilterMinKeepSeconds = settings.MinKeepSeconds;
            FilterCutPadding = settings.CutPaddingFrames;
            FilterFfmpegEncoder = FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.LibAndNvenc, settings.CompareFfmpegEncoder);
        }
        finally
        {
            _initializingAnalysis = false;
        }
    }

    partial void OnAnalysisModeLabelChanged(string value)
    {
        if (_initializingAnalysis)
        {
            return;
        }

        var key = AnalysisModeKeyFromLabel(value);
        if (key is null || key == _analysisMode)
        {
            return;
        }

        StoreActiveThresholds();
        _analysisMode = key;
        ApplyThresholds(key == "blob" ? _blobThresholds : _classicThresholds);
        InitializePreviewFromFilters();
        OnPropertyChanged(nameof(IsBlobMode));
        _ = RenderPreviewFrameDebouncedAsync();
        ScheduleMetricsLoad();
    }

    partial void OnFilterAutoSyncChanged(bool value) => ScheduleMetricsLoad();

    private void StoreActiveThresholds()
    {
        var current = new Thresholds(FilterNoise, FilterPixel, FilterPixelMax);
        if (_analysisMode == "blob")
        {
            _blobThresholds = current;
        }
        else
        {
            _classicThresholds = current;
        }
    }

    private void ApplyThresholds(Thresholds t)
    {
        FilterNoise = t.Noise;
        FilterPixel = t.Pixel;
        FilterPixelMax = t.PixelMax;
    }

    /// <summary>Writes both modes' thresholds and the analysis options into the settings object.</summary>
    private void FillAnalysisSettings(OxcoCompareSettings settings)
    {
        StoreActiveThresholds();
        settings.FilterNoise = _classicThresholds.Noise;
        settings.FilterPixel = _classicThresholds.Pixel;
        settings.FilterPixelMax = _classicThresholds.PixelMax;
        settings.BlobNoise = _blobThresholds.Noise;
        settings.BlobPixel = _blobThresholds.Pixel;
        settings.BlobPixelMax = _blobThresholds.PixelMax;
        settings.AnalysisMode = _analysisMode;
        settings.AnalysisWidth = FilterAnalysisWidth;
        settings.AnalysisHwaccel = FilterAnalysisHwaccel;
        settings.AutoSyncOffset = FilterAutoSync;
        settings.MinDiffRunFrames = FilterMinDiffRun;
        settings.MinKeepSeconds = FilterMinKeepSeconds;
        settings.CutPaddingFrames = FilterCutPadding;
        settings.CompareFfmpegEncoder = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.LibAndNvenc, FilterFfmpegEncoder);
    }

    private void AppendAnalysisFilters(Dictionary<string, object?> payload)
    {
        payload["analysis_mode"] = _analysisMode;
        payload["analysis_width"] = ParseFilterInt(FilterAnalysisWidth, 640);
        payload["analysis_hwaccel"] = FilterAnalysisHwaccel;
        payload["auto_sync_offset"] = FilterAutoSync;
        payload["min_diff_run_frames"] = ParseFilterInt(FilterMinDiffRun, 2);
        payload["min_keep_seconds"] = ParseFilterDouble(FilterMinKeepSeconds, 0.25);
        payload["cut_padding_frames"] = ParseFilterInt(FilterCutPadding, 0);
        payload["ffmpeg_encoder"] = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.LibAndNvenc, FilterFfmpegEncoder);
    }

    private void RefreshAnalysisLocalization()
    {
        OnPropertyChanged(nameof(AnalysisModeChoices));
        _initializingAnalysis = true;
        try
        {
            AnalysisModeLabel = LocalizeAnalysisMode(_analysisMode);
        }
        finally
        {
            _initializingAnalysis = false;
        }
    }
}
