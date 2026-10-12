using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class MediaOverlayViewModel
{
    private CancellationTokenSource? _previewDebounceCts;
    private CancellationTokenSource? _previewCts;
    private int _previewGeneration;
    private bool _layoutDragging;
    private bool _suppressLayoutPreview;
    private int _videoWidth;
    private int _videoHeight;
    private int _overlaySourceWidth = 16;
    private int _overlaySourceHeight = 9;
    private double _probeDurationSec;

    [ObservableProperty] private byte[]? _previewImageBytes;
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private double _sliderMaximum = 1;
    [ObservableProperty] private string _timeDisplay = "0:00.00";
    [ObservableProperty] private bool _hasVideo;
    [ObservableProperty] private string _videoInfoLine = string.Empty;
    [ObservableProperty] private string _previewHint = string.Empty;
    [ObservableProperty] private float[]? _waveformPeaks;

    public IReadOnlyList<TimelineSpan> TimelineRanges { get; private set; } = [];

    public int VideoPixelWidth => _videoWidth;
    public int VideoPixelHeight => _videoHeight;
    public int OverlaySourceWidth => Math.Max(1, _overlaySourceWidth);
    public int OverlaySourceHeight => Math.Max(1, _overlaySourceHeight);

    public double OverlayAspect =>
        OverlaySourceWidth <= 0 ? 16.0 / 9.0 : (double)OverlaySourceHeight / OverlaySourceWidth;

    partial void OnPosXChanged(double value)
    {
        if (!_layoutDragging && !_suppressLayoutPreview)
        {
            SchedulePreviewRefresh();
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    partial void OnPosYChanged(double value)
    {
        if (!_layoutDragging && !_suppressLayoutPreview)
        {
            SchedulePreviewRefresh();
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    partial void OnOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(OpacityLabel));
        SchedulePreviewRefresh();
    }

    public string OpacityLabel => Loc.F(
        "mediaoverlay.opacityValue",
        Math.Round(Opacity).ToString("0", System.Globalization.CultureInfo.CurrentCulture));

    partial void OnWidthPercentChanged(double value)
    {
        if (!_layoutDragging && !_suppressLayoutPreview)
        {
            SchedulePreviewRefresh();
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    partial void OnSliderValueChanged(double value)
    {
        UpdateTimeDisplay();
        if (!_layoutDragging)
        {
            SchedulePreviewRefresh();
        }
    }

    partial void OnStartSecondsTextChanged(string value)
    {
        RebuildTimeline();
        if (!_layoutDragging)
        {
            SchedulePreviewRefresh();
        }
    }

    partial void OnEndSecondsTextChanged(string value)
    {
        RebuildTimeline();
        if (!_layoutDragging)
        {
            SchedulePreviewRefresh();
        }
    }

    public string LayoutSummary
    {
        get
        {
            var w = OverlayWidthPixels();
            var h = OverlayHeightPixels(w);
            return Loc.F(
                "mediaoverlay.layoutSummary",
                (int)Math.Round(PosX),
                (int)Math.Round(PosY),
                w,
                h,
                WidthPercent.ToString("0.##", CultureInfo.CurrentCulture));
        }
    }

    public int OverlayWidthPixels()
    {
        if (_videoWidth <= 0)
        {
            return Math.Max(2, (int)Math.Round(WidthPercent));
        }

        var w = (int)Math.Round(_videoWidth * (Math.Clamp(WidthPercent, 1, 100) / 100.0));
        w = Math.Max(2, w - (w % 2));
        return w;
    }

    public int OverlayHeightPixels(int widthPx)
    {
        var h = (int)Math.Round(widthPx * OverlayAspect);
        return Math.Max(2, h - (h % 2));
    }

    private void UpdateTimeDisplay()
    {
        TimeDisplay = TimecodeHelper.FormatDisplay(SliderValue);
    }

    public async Task LoadWaveformAsync(string? path)
    {
        WaveformPeaks = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var peaks = await WaveformPeakExtractor.ExtractAsync(path);
        if (string.Equals(path, InputPath, StringComparison.OrdinalIgnoreCase))
        {
            WaveformPeaks = peaks;
        }
    }

    public void RebuildTimeline()
    {
        var spans = new List<TimelineSpan>();
        var duration = Math.Max(0, _probeDurationSec);
        if (TryParseSeconds(StartSecondsText, out var start))
        {
            var end = duration;
            if (TryParseSeconds(EndSecondsText, out var parsedEnd) && parsedEnd > start)
            {
                end = parsedEnd;
            }
            else if (duration > start)
            {
                end = duration;
            }
            else
            {
                end = start + 0.4;
            }

            spans.Add(new TimelineSpan(start, end, TimelineSpanKind.Loop, Loc.T("mediaoverlay.overlayHeader")));
        }

        TimelineRanges = spans;
        OnPropertyChanged(nameof(TimelineRanges));
    }

    public void BeginLayoutDrag()
    {
        _layoutDragging = true;
        _previewDebounceCts?.Cancel();
    }

    public void EndLayoutDrag()
    {
        _layoutDragging = false;
        SnapLayoutToPixels();
        SchedulePreviewRefresh(immediate: true);
    }

    public void CancelLayoutDrag()
    {
        _layoutDragging = false;
        SchedulePreviewRefresh(immediate: true);
    }

    /// <summary>Normalized preview coords (0–1) → video pixels. Keep aspect via width %.</summary>
    public void ApplyMoveFromPreview(double nx, double ny)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        var w = OverlayWidthPixels();
        var h = OverlayHeightPixels(w);
        var x = Math.Clamp(Math.Round(nx * _videoWidth), 0, Math.Max(0, _videoWidth - w));
        var y = Math.Clamp(Math.Round(ny * _videoHeight), 0, Math.Max(0, _videoHeight - h));
        _suppressLayoutPreview = true;
        try
        {
            PosX = x;
            PosY = y;
        }
        finally
        {
            _suppressLayoutPreview = false;
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    /// <summary>Resize from SE corner; nx/ny are pointer in normalized preview space (absolute).</summary>
    public void ApplyResizeFromPreview(double nRight, double nBottom)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        var left = PosX / _videoWidth;
        var top = PosY / _videoHeight;
        var newWNorm = Math.Max(0.01, nRight - left);
        var newHFromAspect = newWNorm * (_videoWidth * OverlayAspect) / _videoHeight;
        // Prefer width-driven aspect; clamp so bottom stays on screen if needed
        if (top + newHFromAspect > 1.0 && OverlayAspect > 0)
        {
            var maxH = Math.Max(0.01, 1.0 - top);
            newWNorm = maxH * _videoHeight / (_videoWidth * OverlayAspect);
        }

        var widthPx = Math.Clamp(Math.Round(newWNorm * _videoWidth), 2, _videoWidth);
        widthPx -= widthPx % 2;
        var pct = 100.0 * widthPx / _videoWidth;
        pct = Math.Clamp(pct, 1, 100);

        _suppressLayoutPreview = true;
        try
        {
            WidthPercent = Math.Round(pct, 2);
            // Keep top-left; clamp if resized past edge
            var h = OverlayHeightPixels((int)widthPx);
            PosX = Math.Clamp(PosX, 0, Math.Max(0, _videoWidth - widthPx));
            PosY = Math.Clamp(PosY, 0, Math.Max(0, _videoHeight - h));
        }
        finally
        {
            _suppressLayoutPreview = false;
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    public void NudgeLayout(int dxPx, int dyPx, int dWidthPx)
    {
        if (_videoWidth <= 0)
        {
            return;
        }

        _suppressLayoutPreview = true;
        try
        {
            if (dWidthPx != 0)
            {
                var w = Math.Clamp(OverlayWidthPixels() + dWidthPx, 2, _videoWidth);
                w -= w % 2;
                WidthPercent = Math.Round(100.0 * w / _videoWidth, 2);
            }

            var ow = OverlayWidthPixels();
            var oh = OverlayHeightPixels(ow);
            PosX = Math.Clamp(Math.Round(PosX) + dxPx, 0, Math.Max(0, _videoWidth - ow));
            PosY = Math.Clamp(Math.Round(PosY) + dyPx, 0, Math.Max(0, _videoHeight - oh));
        }
        finally
        {
            _suppressLayoutPreview = false;
        }

        OnPropertyChanged(nameof(LayoutSummary));
        SchedulePreviewRefresh(immediate: true);
    }

    private void SnapLayoutToPixels()
    {
        if (_videoWidth <= 0)
        {
            return;
        }

        _suppressLayoutPreview = true;
        try
        {
            var w = OverlayWidthPixels();
            var h = OverlayHeightPixels(w);
            PosX = Math.Clamp(Math.Round(PosX), 0, Math.Max(0, _videoWidth - w));
            PosY = Math.Clamp(Math.Round(PosY), 0, Math.Max(0, _videoHeight - h));
            WidthPercent = Math.Round(100.0 * w / _videoWidth, 2);
        }
        finally
        {
            _suppressLayoutPreview = false;
        }

        OnPropertyChanged(nameof(LayoutSummary));
    }

    public void SchedulePreviewRefresh(bool immediate = false)
    {
        if (!HasVideo || string.IsNullOrWhiteSpace(OverlayPath) || !File.Exists(OverlayPath))
        {
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;
        _ = DebouncedPreviewAsync(token, immediate ? 0 : 280);
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
        var main = RequireInputPath();
        if (main is null || string.IsNullOrWhiteSpace(OverlayPath) || !File.Exists(OverlayPath))
        {
            return;
        }

        if (!TryParseSeconds(StartSecondsText, out var start))
        {
            start = 0;
        }

        if (!TryParseSeconds(EndSecondsText, out var end))
        {
            end = 0;
        }

        _previewCts?.Cancel();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _previewCts.Token;
        var generation = Interlocked.Increment(ref _previewGeneration);

        var configPath = Path.Combine(Path.GetTempPath(), $"hm_mo_prev_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_mo_prev_out_{Guid.NewGuid():N}.json");
        var payload = new
        {
            mode = "preview",
            main,
            overlay = OverlayPath,
            output = OutputPath,
            pos_x = (int)Math.Max(0, Math.Round(PosX)),
            pos_y = (int)Math.Max(0, Math.Round(PosY)),
            width_percent = WidthPercent,
            start_seconds = start,
            end_seconds = end,
            time_sec = SliderValue,
            force_show = true,
            opacity = Opacity,
            width_max = 1280,
            height_max = 720,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload), token);
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "media_overlay_job.py",
                ["--config-json", configPath, "--output-json", outputJson],
                cancellationToken: token);

            if (generation != _previewGeneration || token.IsCancellationRequested)
            {
                return;
            }

            if (!result.Success || !File.Exists(outputJson))
            {
                PreviewHint = Loc.T("mediaoverlay.previewFailed");
                return;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputJson, token));
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            {
                PreviewHint = Loc.T("mediaoverlay.previewFailed");
                return;
            }

            if (root.TryGetProperty("image_base64", out var b64))
            {
                PreviewImageBytes = Convert.FromBase64String(b64.GetString() ?? "");
            }

            if (root.TryGetProperty("overlay_width", out var ow) && ow.TryGetInt32(out var owi) && owi > 0)
            {
                _overlaySourceWidth = owi;
            }

            if (root.TryGetProperty("overlay_height", out var oh) && oh.TryGetInt32(out var ohi) && ohi > 0)
            {
                _overlaySourceHeight = ohi;
            }

            PreviewHint = Loc.T("mediaoverlay.previewHint");
            OnPropertyChanged(nameof(OverlayAspect));
            OnPropertyChanged(nameof(LayoutSummary));
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            PreviewHint = ex.Message;
        }
        finally
        {
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

    public async Task ProbeAndRefreshPreviewAsync()
    {
        var main = RequireInputPath();
        if (main is null)
        {
            HasVideo = false;
            PreviewImageBytes = null;
            VideoInfoLine = string.Empty;
            return;
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"hm_mo_probe_{Guid.NewGuid():N}.json");
        var outputJson = Path.Combine(Path.GetTempPath(), $"hm_mo_probe_out_{Guid.NewGuid():N}.json");
        var payload = new { mode = "probe", main, overlay = OverlayPath ?? "" };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "media_overlay_job.py",
                ["--config-json", configPath, "--output-json", outputJson]);

            if (!result.Success || !File.Exists(outputJson))
            {
                HasVideo = false;
                Status = Loc.T("mediaoverlay.probeFailed");
                return;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputJson));
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            {
                HasVideo = false;
                Status = Loc.T("mediaoverlay.probeFailed");
                return;
            }

            _videoWidth = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            _videoHeight = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            _probeDurationSec = root.TryGetProperty("duration_sec", out var d) ? d.GetDouble() : 0;
            if (root.TryGetProperty("overlay_width", out var ow) && ow.TryGetInt32(out var owi) && owi > 0)
            {
                _overlaySourceWidth = owi;
            }

            if (root.TryGetProperty("overlay_height", out var oh) && oh.TryGetInt32(out var ohi) && ohi > 0)
            {
                _overlaySourceHeight = ohi;
            }

            HasVideo = _videoWidth > 0 && _videoHeight > 0;
            SliderMaximum = Math.Max(0.001, _probeDurationSec);
            if (SliderValue > SliderMaximum)
            {
                SliderValue = 0;
            }

            VideoInfoLine = HasVideo
                ? Loc.F(
                    "mediaoverlay.videoInfoLine",
                    _videoWidth,
                    _videoHeight,
                    _probeDurationSec.ToString("0.##", CultureInfo.CurrentCulture))
                : string.Empty;

            OnPropertyChanged(nameof(VideoPixelWidth));
            OnPropertyChanged(nameof(VideoPixelHeight));
            OnPropertyChanged(nameof(OverlayAspect));
            OnPropertyChanged(nameof(LayoutSummary));
            UpdateTimeDisplay();
            RebuildTimeline();
            _ = LoadWaveformAsync(InputPath);
            SchedulePreviewRefresh(immediate: true);
        }
        finally
        {
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
