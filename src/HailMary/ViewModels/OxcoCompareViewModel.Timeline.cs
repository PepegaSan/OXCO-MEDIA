using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>
/// Result timeline (cached per-frame metrics from compare.py, re-filtered live in C#) and per-deepfake result badges.
/// </summary>
public partial class OxcoCompareViewModel
{
    private enum CompareOutcome
    {
        Analysed,
        Exported,
        DavinciPending,
        Failed,
    }

    private sealed record CompareResult(double KeptSeconds, double CutSeconds, int Cuts, CompareOutcome Outcome);

    private readonly Dictionary<string, CompareResult> _compareResults = new(StringComparer.OrdinalIgnoreCase);
    private OxcoMetricsData? _metrics;
    private bool _metricsExact;
    private CancellationTokenSource? _metricsCts;

    [ObservableProperty] private int[]? _timelineMetric;

    [ObservableProperty] private IReadOnlyList<OxcoSegment>? _timelineSegments;

    [ObservableProperty] private int _timelineMetricOffset;

    [ObservableProperty] private string _timelineInfoText = string.Empty;

    [ObservableProperty] private string _lastResultText = string.Empty;

    public JobProgressService JobProgress => AppServices.JobProgress;

    public string TimelineEmptyText => Loc.T("oxco.timeline.empty");

    public double TimelineThreshold => ParseFilterInt(FilterPixel, 0);

    public double TimelinePixelMax => ParseFilterInt(FilterPixelMax, 0);

    public bool HasTimeline => _metrics is not null;

    /// <summary>Deepfake frame = source frame + offset (from the sync offset of the loaded analysis).</summary>
    private int PreviewBOffset => _metrics is null ? 0 : _metrics.DfOffset - _metrics.SrcOffset;

    partial void OnFilterPixelChanged(string value) => OnPostFilterChanged(rerenderPreview: true);

    partial void OnFilterPixelMaxChanged(string value) => OnPostFilterChanged(rerenderPreview: true);

    partial void OnFilterBufferChanged(string value) => OnPostFilterChanged(rerenderPreview: false);

    partial void OnFilterMinDiffRunChanged(string value) => OnPostFilterChanged(rerenderPreview: false);

    partial void OnFilterMinKeepSecondsChanged(string value) => OnPostFilterChanged(rerenderPreview: false);

    partial void OnFilterCutPaddingChanged(string value) => OnPostFilterChanged(rerenderPreview: false);

    partial void OnFilterNoiseChanged(string value) => OnAnalysisInputChanged();

    partial void OnFilterAnalysisWidthChanged(string value) => OnAnalysisInputChanged();

    private void OnPostFilterChanged(bool rerenderPreview)
    {
        OnPropertyChanged(nameof(TimelineThreshold));
        OnPropertyChanged(nameof(TimelinePixelMax));
        RecomputeTimeline();
        if (rerenderPreview)
        {
            _ = RenderPreviewFrameDebouncedAsync();
        }
    }

    /// <summary>Noise / mode / width change the measurement itself: look for a matching analysis again.</summary>
    private void OnAnalysisInputChanged()
    {
        _ = RenderPreviewFrameDebouncedAsync();
        ScheduleMetricsLoad();
    }

    private void ScheduleMetricsLoad(string? knownPath = null)
    {
        _metricsCts?.Cancel();
        _metricsCts = new CancellationTokenSource();
        var token = _metricsCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(knownPath is null ? 450 : 0, token);
                UiDispatcher.Run(() => _ = LoadMetricsAsync(knownPath, token));
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
        }, token);
    }

    private async Task LoadMetricsAsync(string? knownPath, CancellationToken token)
    {
        var src = SourcePath.Trim();
        var df = DeepfakePath.Trim();
        if (!File.Exists(src) || !File.Exists(df))
        {
            ClearTimeline();
            return;
        }

        var path = knownPath;
        var exact = true;
        if (path is null)
        {
            (path, exact) = await _previewWorker.FindMetricsAsync(new JsonObject
            {
                ["source"] = src,
                ["deepfake"] = df,
                ["analysis_mode"] = _analysisMode,
                ["noise"] = ParseFilterInt(FilterNoise, 15),
                ["analysis_width"] = ParseFilterInt(FilterAnalysisWidth, 640),
                ["auto_sync_offset"] = FilterAutoSync,
            });
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var data = path is null ? null : await Task.Run(() => OxcoMetricsData.Load(path), token);
        if (token.IsCancellationRequested
            || !string.Equals(src, SourcePath.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(df, DeepfakePath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (data is null)
        {
            ClearTimeline();
            return;
        }

        var offsetChanged = PreviewBOffset != data.DfOffset - data.SrcOffset;
        _metrics = data;
        _metricsExact = exact;
        TimelineMetric = data.Metric;
        TimelineMetricOffset = data.SrcOffset;
        OnPropertyChanged(nameof(HasTimeline));
        RecomputeTimeline();
        if (offsetChanged)
        {
            _ = RenderPreviewFrameDebouncedAsync();
        }
    }

    private void ClearTimeline()
    {
        _metrics = null;
        TimelineMetric = null;
        TimelineSegments = null;
        TimelineMetricOffset = 0;
        TimelineInfoText = string.Empty;
        OnPropertyChanged(nameof(HasTimeline));
    }

    private void RecomputeTimeline()
    {
        if (_metrics is null)
        {
            return;
        }

        var fps = _metrics.Fps > 0 ? _metrics.Fps : _previewFps;
        var p = new OxcoFilterParams(
            ParseFilterInt(FilterPixel, 200),
            ParseFilterInt(FilterPixelMax, 0),
            (int)(fps * ParseFilterDouble(FilterBuffer, 2.0)),
            ParseFilterInt(FilterMinDiffRun, 2),
            (int)Math.Round(ParseFilterDouble(FilterMinKeepSeconds, 0.25) * fps),
            ParseFilterInt(FilterCutPadding, 0));
        var (segments, _) = OxcoSegmentFilter.Compute(_metrics.Metric, p);
        TimelineSegments = segments;

        var (kept, cut, count) = OxcoSegmentFilter.Summary(segments);
        var info = Loc.F("oxco.timeline.summary", kept / fps, cut / fps, count);
        if (!_metricsExact)
        {
            info += "  ·  " + Loc.F("oxco.timeline.stale", LocalizeAnalysisMode(_metrics.Mode), _metrics.Noise);
        }

        TimelineInfoText = info;
    }

    private void JumpToCut(bool forward)
    {
        if (TimelineSegments is null || !PreviewIsLoaded)
        {
            return;
        }

        var offset = TimelineMetricOffset;
        var current = (int)PreviewFrameIndex - offset;
        var cuts = TimelineSegments.Where(s => !s.Keep).ToList();
        var target = forward
            ? cuts.FirstOrDefault(s => s.Start > current)
            : cuts.LastOrDefault(s => s.Start < current);
        if (target.Length > 0)
        {
            SeekPreviewToFrame(target.Start + offset);
        }
    }

    /// <summary>Handles "HM_RESULT:{json}" from compare.py (see compare._print_cut_summary).</summary>
    /// <returns>The metrics cache path reported by compare.py, or null.</returns>
    private string? HandleCompareOutputLine(string line, string deepfakePath)
    {
        const string prefix = "HM_RESULT:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line[prefix.Length..]);
            var root = doc.RootElement;
            var result = new CompareResult(
                root.GetProperty("kept_seconds").GetDouble(),
                root.GetProperty("cut_seconds").GetDouble(),
                root.GetProperty("cuts").GetInt32(),
                CompareOutcome.Analysed);
            var path = root.TryGetProperty("metrics", out var m) ? m.GetString() : null;
            UiDispatcher.Run(() => SetCompareResult(deepfakePath, result));
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null; // not fatal — badge just stays empty
        }
    }

    private void FinishCompareResult(string deepfakePath, string? pipelinePhase, JobResult result, string? metricsPath)
    {
        var outcome = result.ExitCode switch
        {
            0 when pipelinePhase == "analysis" && FilterDavinci => CompareOutcome.DavinciPending,
            0 => CompareOutcome.Exported,
            _ => CompareOutcome.Failed,
        };

        // Runs that find nothing to cut end without a result line → 0 / 0.
        var updated = _compareResults.TryGetValue(deepfakePath, out var previous)
            ? previous with { Outcome = outcome }
            : new CompareResult(0, 0, 0, outcome);

        SetCompareResult(deepfakePath, updated);
        LastResultText = FormatResultLine(Path.GetFileName(deepfakePath), updated);

        if (string.Equals(deepfakePath, DeepfakePath, StringComparison.OrdinalIgnoreCase))
        {
            ScheduleMetricsLoad(metricsPath);
        }
    }

    private void SetCompareResult(string deepfakePath, CompareResult result)
    {
        _compareResults[deepfakePath] = result;
        ApplyResultBadges();
    }

    private void ApplyResultBadges()
    {
        foreach (var item in DeepfakeDisplayItems)
        {
            if (item.IsGroupHeader || item.Entry is null)
            {
                continue;
            }

            if (_compareResults.TryGetValue(item.Entry.Path, out var r))
            {
                item.ResultText = FormatBadge(r);
                item.ResultColor = r.Outcome switch
                {
                    CompareOutcome.Failed => "#E5484D",
                    CompareOutcome.DavinciPending or CompareOutcome.Analysed => "#E6A100",
                    _ => "#2ECC71",
                };
                item.ResultToolTip = FormatResultLine(item.Name, r);
            }
            else
            {
                item.ResultText = string.Empty;
                item.ResultToolTip = null;
            }
        }
    }

    private static string FormatBadge(CompareResult r) => r.Outcome switch
    {
        CompareOutcome.Failed => "✗",
        CompareOutcome.DavinciPending or CompareOutcome.Analysed => $"… −{r.CutSeconds:0.0}s",
        _ => $"✓ −{r.CutSeconds:0.0}s",
    };

    private static string FormatResultLine(string name, CompareResult r)
    {
        var state = r.Outcome switch
        {
            CompareOutcome.Failed => Loc.T("oxco.result.failed"),
            CompareOutcome.DavinciPending => Loc.T("oxco.result.davinciPending"),
            CompareOutcome.Analysed => Loc.T("oxco.result.analysed"),
            _ => Loc.T("oxco.result.exported"),
        };
        return Loc.F("oxco.result.line", name, state, r.KeptSeconds, r.CutSeconds, r.Cuts);
    }
}
