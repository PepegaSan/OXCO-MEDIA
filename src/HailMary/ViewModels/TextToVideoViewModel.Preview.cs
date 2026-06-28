using System.Globalization;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    private CancellationTokenSource? _previewDebounceCts;

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
            await RefreshPreviewAsync();
            Status = VideoInfoLine;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SchedulePreviewRefresh()
    {
        if (!HasVideo)
        {
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;
        _ = DebouncedPreviewAsync(token);
    }

    private async Task DebouncedPreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
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
            if (result is null || token.IsCancellationRequested)
            {
                return;
            }

            PreviewImageBytes = result.ImageBytes;
            _videoWidth = result.Width;
            _videoHeight = result.Height;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }
}
