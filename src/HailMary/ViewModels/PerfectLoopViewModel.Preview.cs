using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>Alternative loop window found by the search.</summary>
public sealed class LoopCandidateItem
{
    public LoopCandidateItem(double start, double end, double score)
    {
        Start = start;
        End = end;
        Score = score;
    }

    public double Start { get; }

    public double End { get; }

    public double Score { get; }

    public string Label => Loc.F(
        "perfectloop.candidateLabel",
        (End - Start).ToString("0.00", CultureInfo.CurrentCulture),
        Math.Round(Score * 100).ToString("0", CultureInfo.CurrentCulture));

    public string Tooltip => Loc.F(
        "perfectloop.candidateTooltip",
        Start.ToString("0.00", CultureInfo.CurrentCulture),
        End.ToString("0.00", CultureInfo.CurrentCulture));
}

public partial class PerfectLoopViewModel
{
    private string? _loopPreviewKey;

    public ObservableCollection<LoopCandidateItem> Candidates { get; } = [];

    /// <summary>JPEG bytes of the frames around the seam: [end-2, end-1, start, start+1].</summary>
    [ObservableProperty] private IReadOnlyList<byte[]> _seamThumbs = [];

    /// <summary>0..1, or -1 while nothing has been analysed.</summary>
    [ObservableProperty] private double _qualityScore = -1;

    [ObservableProperty] private double _minLoopSeconds = 1.5;

    [ObservableProperty] private bool _isLoopPreviewMode;

    /// <summary>Encoded low-res loop of the current settings (played with native looping).</summary>
    [ObservableProperty] private string _loopPreviewPath = string.Empty;

    public bool HasCandidates => Candidates.Count > 0;

    public bool HasSeamThumbs => SeamThumbs.Count == 4 && SeamThumbs.Any(t => t.Length > 0);

    public string QualityBadgeText => QualityScore < 0
        ? "—"
        : Math.Round(QualityScore * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    public string QualityVerdict => QualityScore switch
    {
        < 0 => Loc.T("perfectloop.verdict.none"),
        >= 0.7 => Loc.T("perfectloop.verdict.good"),
        >= 0.4 => Loc.T("perfectloop.verdict.ok"),
        _ => Loc.T("perfectloop.verdict.bad"),
    };

    public bool IsOriginalMode => !IsLoopPreviewMode;

    public string MinLoopLabel => Loc.F("perfectloop.minLoopValue", MinLoopSeconds.ToString("0.0", CultureInfo.CurrentCulture));

    partial void OnSeamThumbsChanged(IReadOnlyList<byte[]> value) => OnPropertyChanged(nameof(HasSeamThumbs));

    partial void OnQualityScoreChanged(double value)
    {
        OnPropertyChanged(nameof(QualityBadgeText));
        OnPropertyChanged(nameof(QualityVerdict));
    }

    partial void OnMinLoopSecondsChanged(double value) => OnPropertyChanged(nameof(MinLoopLabel));

    partial void OnIsLoopPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOriginalMode));
        if (value)
        {
            EnsureLoopPreview();
        }
    }

    [RelayCommand]
    private void ShowOriginalMode() => IsLoopPreviewMode = false;

    [RelayCommand]
    private void ShowLoopMode() => IsLoopPreviewMode = true;

    [RelayCommand]
    private void ApplyCandidate(LoopCandidateItem? item)
    {
        if (item is null)
        {
            return;
        }

        _suppressAutoAnalyze = true;
        try
        {
            LoopStartSeconds = item.Start;
            LoopEndSeconds = item.End;
            _userSetRange = true;
        }
        finally
        {
            _suppressAutoAnalyze = false;
        }

        NotifyRangeUi();
        _ = AnalyzeCoreAsync(applyRecommendations: !_userChoseSeamSettings);
    }

    /// <summary>Key of everything that changes the rendered loop.</summary>
    private string CurrentLoopKey() => string.Join(
        '|',
        SourceVideoPath ?? string.Empty,
        LoopStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        EffectiveLoopEnd.ToString("0.###", CultureInfo.InvariantCulture),
        SelectedSeamMethod,
        SeamSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        TargetFps.ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>Render the loop preview when it is shown and out of date.</summary>
    private void EnsureLoopPreview()
    {
        if (!IsLoopPreviewMode || !HasVideo)
        {
            return;
        }

        if (HasLoopPreviewFor(CurrentLoopKey()))
        {
            return;
        }

        _ = QueueSeamPreviewAsync();
    }

    private bool HasLoopPreviewFor(string key) =>
        _loopPreviewKey == key && !string.IsNullOrWhiteSpace(LoopPreviewPath) && File.Exists(LoopPreviewPath);

    private void ApplyAnalyzeExtras(JsonElement first)
    {
        QualityScore = first.TryGetProperty("quality_score", out var qs) ? qs.GetDouble() : -1;

        var thumbs = new List<byte[]>();
        if (first.TryGetProperty("seam_thumbs", out var st) && st.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in st.EnumerateArray())
            {
                var b64 = t.GetString();
                thumbs.Add(string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64));
            }
        }

        SeamThumbs = thumbs;

        // Keep the previous alternatives when only re-scoring a picked candidate.
        if (first.TryGetProperty("candidates", out var cands)
            && cands.ValueKind == JsonValueKind.Array
            && cands.GetArrayLength() > 0)
        {
            Candidates.Clear();
            foreach (var c in cands.EnumerateArray())
            {
                Candidates.Add(new LoopCandidateItem(
                    c.GetProperty("start_seconds").GetDouble(),
                    c.GetProperty("end_seconds").GetDouble(),
                    c.GetProperty("score").GetDouble()));
            }

            OnPropertyChanged(nameof(HasCandidates));
        }

        EnsureLoopPreview();
    }

    private void ClearAnalysisVisuals()
    {
        QualityScore = -1;
        SeamThumbs = [];
        Candidates.Clear();
        OnPropertyChanged(nameof(HasCandidates));
    }
}
