using System.Globalization;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    private CancellationTokenSource? _previewDebounceCts;
    private int _previewGeneration;

    private void UpdateVideoInfoLine()
    {
        if (!HasVideo || _videoWidth <= 0)
        {
            VideoInfoLine = string.Empty;
            return;
        }

        var fps = _probeFps.ToString("0.###", CultureInfo.CurrentCulture);
        var dur = _probeDurationSec.ToString("0.##", CultureInfo.CurrentCulture);
        VideoInfoLine = Loc.F("texttovideo.videoInfoLine", _videoWidth, _videoHeight, fps, dur);
        if (!string.IsNullOrWhiteSpace(_suggestedBitrate))
        {
            VideoInfoLine += ", " + Loc.F("texttovideo.suggestedBitrate", _suggestedBitrate);
        }
    }

    private async Task LoadVideoAsync()
    {
        if (string.IsNullOrWhiteSpace(VideoPath) || !File.Exists(VideoPath))
        {
            return;
        }

        IsBusy = true;
        Status = Loc.T("texttovideo.status.analyzingVideo");
        try
        {
            var probe = await TextToVideoBridge.ProbeAsync(VideoPath);
            if (probe is null)
            {
                Status = Loc.T("texttovideo.status.videoProbeFailed");
                HasVideo = false;
                return;
            }

            HasVideo = true;
            _videoWidth = probe.Width;
            _videoHeight = probe.Height;
            _probeFps = probe.Fps;
            _probeDurationSec = probe.DurationSec;
            _suggestedBitrate = string.IsNullOrWhiteSpace(probe.SuggestedBitrate) ? null : probe.SuggestedBitrate;
            SliderMaximum = Math.Max(0.001, probe.DurationSec);
            SliderValue = 0;
            if (!string.IsNullOrWhiteSpace(_suggestedBitrate))
            {
                Bitrate = _suggestedBitrate;
            }

            UpdateVideoInfoLine();

            if (EditorSegment.PosX <= 80 && EditorSegment.PosY <= 80)
            {
                EditorSegment.PosX = Math.Max(40, (int)(probe.Width * 0.05));
                EditorSegment.PosY = Math.Max(40, (int)(probe.Height * 0.85 - EditorSegment.Fontsize));
            }

            UpdateTimeDisplay();
            PersistSettings();
            RebuildTimeline();
            _ = LoadWaveformAsync(VideoPath);
            await RefreshPreviewAsync();
            Status = VideoInfoLine;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SchedulePreviewRefresh(bool immediate = false)
    {
        if (!HasVideo || IsPreviewPlaying)
        {
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;
        var delayMs = immediate ? 0 : 320;
        _ = DebouncedPreviewAsync(token, delayMs);
    }

    private async Task DebouncedPreviewAsync(CancellationToken token, int delayMs)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, token);
            }

            await RefreshPreviewAsync(token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task RefreshPreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!HasVideo || string.IsNullOrWhiteSpace(VideoPath))
        {
            return;
        }

        _previewCts?.Cancel();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _previewCts.Token;
        var generation = Interlocked.Increment(ref _previewGeneration);

        var config = TextToVideoBridge.BuildPreviewConfig(
            VideoPath,
            SliderValue,
            Segments,
            EditorSegment,
            SrtPath,
            SelectedSegmentIndex);

        try
        {
            var result = await TextToVideoBridge.RenderPreviewAsync(config, token);
            if (token.IsCancellationRequested || generation != _previewGeneration)
            {
                return;
            }

            if (result is null)
            {
                // Keep the last good frame; only hint in status when not busy/cancelled.
                return;
            }

            PreviewImageBytes = result.ImageBytes;
            // Keep probe dimensions for click mapping — preview PNG may be scaled.
            if (result.Width > 0 && result.Height > 0 && _videoWidth <= 0)
            {
                _videoWidth = result.Width;
                _videoHeight = result.Height;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
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
        foreach (var seg in Segments)
        {
            if (!TimeFieldHelper.TryParseSeconds(seg.From, out var from))
            {
                continue;
            }

            var to = from + 0.4;
            if (TimeFieldHelper.TryParseSeconds(seg.To, out var parsedTo) && parsedTo > from)
            {
                to = parsedTo;
            }

            spans.Add(new TimelineSpan(from, to, TimelineSpanKind.Scene, seg.ListLabel));
        }

        TimelineRanges = spans;
        OnPropertyChanged(nameof(TimelineRanges));
    }
}
