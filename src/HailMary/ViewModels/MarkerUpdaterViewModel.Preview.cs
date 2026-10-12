using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class MarkerUpdaterViewModel
{
    [ObservableProperty] private string _videoPath = string.Empty;

    [ObservableProperty] private bool _hasVideo;

    [ObservableProperty] private string _videoPathHint = string.Empty;

    [ObservableProperty] private string _positionDisplay = "0s";

    [ObservableProperty] private string _durationDisplay = "0s";

    [ObservableProperty] private double _sliderValue;

    [ObservableProperty] private double _sliderMaximum = 1;

    [ObservableProperty] private string _markerMarks = Loc.T("markerupdater.markerMarksEmpty");

    [ObservableProperty] private double _fineStepSeconds = 10;

    [ObservableProperty] private double _coarseStepSeconds = 100;

    [ObservableProperty] private double _playbackSpeed = 1;

    [ObservableProperty] private float[]? _waveformPeaks;

    public IReadOnlyList<TimelineSpan> TimelineRanges { get; private set; } = [];

    private static IReadOnlyList<double> FineStepOptions { get; } = [1, 5, 10];

    private static IReadOnlyList<double> CoarseStepOptions { get; } = [30, 100, 300];

    private static IReadOnlyList<double> PlaybackSpeedOptions { get; } = [0.5, 1, 2];

    public IReadOnlyList<double> FineStepChoices => FineStepOptions;

    public IReadOnlyList<double> CoarseStepChoices => CoarseStepOptions;

    public IReadOnlyList<double> SpeedChoices => PlaybackSpeedOptions;

    private double _videoDuration;
    private double? _pendingMarkStart;

    public void SetVideoPath(ResolvedMediaPath resolved)
    {
        VideoPathHint = resolved.StashPath;
        VideoPath = resolved.FileExists ? resolved.ResolvedPath : string.Empty;
        HasVideo = resolved.FileExists && !string.IsNullOrWhiteSpace(resolved.ResolvedPath);
        if (!HasVideo)
        {
            SetDuration(0);
            SetPosition(0);
        }
    }

    public void MarkVideoLoaded(bool loaded) => HasVideo = loaded;

    public void SetDuration(double seconds)
    {
        _videoDuration = Math.Max(0, seconds);
        SliderMaximum = Math.Max(0.001, _videoDuration);
        DurationDisplay = TimecodeHelper.FormatDisplay(_videoDuration);
        RebuildTimeline();
    }

    public async Task LoadWaveformAsync(string? path)
    {
        WaveformPeaks = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var peaks = await WaveformPeakExtractor.ExtractAsync(path);
        if (string.Equals(path, VideoPath, StringComparison.OrdinalIgnoreCase))
        {
            WaveformPeaks = peaks;
        }
    }

    public void RebuildTimeline()
    {
        var spans = new List<TimelineSpan>();
        foreach (var marker in Markers)
        {
            if (!TimecodeHelper.TryParseFlexible(marker.SecondsText, out var start, out _))
            {
                continue;
            }

            var end = start + Math.Max(0.35, _videoDuration * 0.012);
            if (TimecodeHelper.TryParseFlexible(marker.EndSecondsText, out var parsedEnd, out _) && parsedEnd > start)
            {
                end = parsedEnd;
            }

            spans.Add(new TimelineSpan(start, end, TimelineSpanKind.Scene, marker.Title));
        }

        TimelineRanges = spans;
        OnPropertyChanged(nameof(TimelineRanges));
    }

    public void SetPosition(double seconds)
    {
        SliderValue = Math.Clamp(seconds, 0, SliderMaximum);
        PositionDisplay = TimecodeHelper.FormatDisplay(seconds);
    }

    public void MarkInAt(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, SliderMaximum);
        NewMarkerSeconds = TimecodeHelper.FormatForEditor(clamped);

        _pendingMarkStart = clamped;
        MarkerMarks = Loc.F("markerupdater.markerMarksStart", TimecodeHelper.FormatDisplay(clamped));
        Status = Loc.F("markerupdater.markerStartSet", TimecodeHelper.FormatDisplay(clamped));
    }

    public void MarkOutAt(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, SliderMaximum);
        NewMarkerEndSeconds = TimecodeHelper.FormatForEditor(clamped);

        var start = _pendingMarkStart
                    ?? (TryParseSeconds(NewMarkerSeconds, out var existing) ? existing : 0);
        MarkerMarks = Loc.F(
            "markerupdater.markerMarksStartEnd",
            TimecodeHelper.FormatDisplay(start),
            TimecodeHelper.FormatDisplay(clamped));
        _pendingMarkStart = null;
        Status = Loc.F("markerupdater.markerEndSet", TimecodeHelper.FormatDisplay(clamped));
    }

    public void NudgePosition(double deltaSeconds)
    {
        SetPosition(SliderValue + deltaSeconds);
    }

    public void SeekToMarkerStart()
    {
        if (SelectedMarker is null || !TryParseSeconds(SelectedMarker.SecondsText, out var sec))
        {
            return;
        }

        SetPosition(sec);
    }

    partial void OnSelectedMarkerChanged(StashMarkerRowViewModel? value)
    {
        NotifyMarkerEditor();
        if (value is null)
        {
            ClearMarkerEditorFields();
            return;
        }

        NewMarkerTitle = value.Title;
        NewMarkerSeconds = value.SecondsText;
        NewMarkerEndSeconds = value.EndSecondsText;
        NewMarkerTagName = value.PrimaryTagName ?? string.Empty;

        if (!HasVideo)
        {
            return;
        }

        if (TryParseSeconds(value.SecondsText, out var sec))
        {
            SetPosition(sec);
        }
    }

    private void ClearMarkerEditorFields()
    {
        NewMarkerTitle = string.Empty;
        NewMarkerSeconds = "0";
        NewMarkerEndSeconds = string.Empty;
        NewMarkerTagName = string.Empty;
    }
}
