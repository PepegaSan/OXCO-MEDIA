using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty] private string _markerMarks = "Start: —   Ende: —";

    [ObservableProperty] private double _fineStepSeconds = 10;

    [ObservableProperty] private double _coarseStepSeconds = 100;

    [ObservableProperty] private double _playbackSpeed = 1;

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
    }

    public void SetPosition(double seconds)
    {
        SliderValue = Math.Clamp(seconds, 0, SliderMaximum);
        PositionDisplay = TimecodeHelper.FormatDisplay(seconds);
    }

    public void MarkInAt(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, SliderMaximum);
        var text = TimecodeHelper.FormatForEditor(clamped);
        if (SelectedMarker is not null)
        {
            SelectedMarker.SecondsText = text;
            NewMarkerSeconds = text;
        }
        else
        {
            NewMarkerSeconds = text;
        }

        _pendingMarkStart = clamped;
        MarkerMarks = $"Start: {TimecodeHelper.FormatDisplay(clamped)}   Ende: —";
        Status = $"Marker-Start bei {TimecodeHelper.FormatDisplay(clamped)} gesetzt.";
    }

    public void MarkOutAt(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, SliderMaximum);
        var text = TimecodeHelper.FormatForEditor(clamped);
        if (SelectedMarker is not null)
        {
            SelectedMarker.EndSecondsText = text;
            NewMarkerEndSeconds = text;
        }
        else
        {
            NewMarkerEndSeconds = text;
        }

        var start = _pendingMarkStart
                    ?? (SelectedMarker is not null && TryParseSeconds(SelectedMarker.SecondsText, out var existing)
                        ? existing
                        : 0);
        MarkerMarks = $"Start: {TimecodeHelper.FormatDisplay(start)}   Ende: {TimecodeHelper.FormatDisplay(clamped)}";
        _pendingMarkStart = null;
        Status = $"Marker-Ende bei {TimecodeHelper.FormatDisplay(clamped)} gesetzt.";
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
        if (value is not null)
        {
            NewMarkerTitle = value.Title;
            NewMarkerSeconds = value.SecondsText;
            NewMarkerEndSeconds = value.EndSecondsText;
            NewMarkerTagName = value.PrimaryTagName;
        }

        if (value is null || !HasVideo)
        {
            return;
        }

        if (TryParseSeconds(value.SecondsText, out var sec))
        {
            SetPosition(sec);
        }
    }
}
