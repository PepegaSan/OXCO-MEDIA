using System.Globalization;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>One text overlay as the preview should draw it.</summary>
public sealed record PreviewOverlayItem(
    TextOverlaySegment Segment,
    int SegmentIndex,
    bool IsEditor,
    TextOverlaySprite? Sprite);

public partial class TextToVideoViewModel
{
    private const int SpriteCacheLimit = 160;

    private CancellationTokenSource? _previewDebounceCts;
    private CancellationTokenSource? _spriteCts;
    private int _previewGeneration;
    private string? _lastFrameKey;
    private readonly Dictionary<string, TextOverlaySprite> _spriteCache = new(StringComparer.Ordinal);
    // Last sprite per list slot (-1 = new draft): shown while a re-render is pending, so text never blinks.
    private readonly Dictionary<int, TextOverlaySprite> _lastSpriteBySlot = [];
    private bool _centerEditorXPending;
    private bool _centerEditorYPending;

    /// <summary>Overlay set or rendered sprites changed — the preview should redraw.</summary>
    public event Action? PreviewOverlaysChanged;

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

            var sizeChanged = probe.Width != _videoWidth || probe.Height != _videoHeight;
            HasVideo = true;
            _videoWidth = probe.Width;
            _videoHeight = probe.Height;
            _probeFps = probe.Fps;
            _probeDurationSec = probe.DurationSec;
            _suggestedBitrate = string.IsNullOrWhiteSpace(probe.SuggestedBitrate) ? null : probe.SuggestedBitrate;
            _lastFrameKey = null;
            if (sizeChanged)
            {
                // Sprites are clamped to the video size — re-render for the new resolution.
                _spriteCache.Clear();
                _lastSpriteBySlot.Clear();
            }

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

            OnPropertyChanged(nameof(PreviewVideoWidth));
            OnPropertyChanged(nameof(PreviewVideoHeight));
            UpdateTimeDisplay();
            PersistSettings();
            RebuildTimeline();
            _ = LoadWaveformAsync(VideoPath);
            await Task.WhenAll(RefreshFrameAsync(), RefreshSpritesAsync());
            Status = VideoInfoLine;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Redraw overlays right away and (debounced) fetch whatever is missing: the video frame when
    /// time/SRT changed, sprites for segments whose look changed. Cheap to call often.
    /// </summary>
    public void SchedulePreviewRefresh(bool immediate = false)
    {
        PreviewOverlaysChanged?.Invoke();
        if (!HasVideo)
        {
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;
        _ = DebouncedPreviewAsync(token, immediate ? 0 : 180);
    }

    private async Task DebouncedPreviewAsync(CancellationToken token, int delayMs)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, token);
            }

            var sprites = RefreshSpritesAsync(token);
            if (!IsPreviewPlaying)
            {
                await RefreshFrameAsync(token);
            }

            await sprites;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task RefreshFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!HasVideo || string.IsNullOrWhiteSpace(VideoPath))
        {
            return;
        }

        var time = Math.Round(SliderValue, 3);
        var srt = !string.IsNullOrWhiteSpace(SrtPath) && File.Exists(SrtPath) ? SrtPath : string.Empty;
        var frameKey = FormattableString.Invariant($"{VideoPath}|{time}|{srt}");
        if (frameKey == _lastFrameKey && PreviewImageBytes is not null)
        {
            return;
        }

        _previewCts?.Cancel();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _previewCts.Token;
        var generation = Interlocked.Increment(ref _previewGeneration);

        try
        {
            var result = await TextToVideoBridge.RenderPreviewAsync(
                TextToVideoBridge.BuildPreviewConfig(VideoPath, time, srt),
                token);
            if (token.IsCancellationRequested || generation != _previewGeneration || result is null)
            {
                // Keep the last good frame.
                return;
            }

            _lastFrameKey = frameKey;
            PreviewImageBytes = result.ImageBytes;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task RefreshSpritesAsync(CancellationToken cancellationToken = default)
    {
        if (!HasVideo || _videoWidth <= 0)
        {
            return;
        }

        var missing = new List<(string Key, TextOverlaySegment Segment)>();
        foreach (var (seg, _) in CurrentEffectiveSegments())
        {
            if (string.IsNullOrWhiteSpace(seg.Text))
            {
                continue;
            }

            var key = seg.RasterKey;
            if (!_spriteCache.ContainsKey(key) && missing.All(m => m.Key != key))
            {
                // Render a snapshot: the user may keep typing while Python works.
                missing.Add((key, seg.Clone()));
            }
        }

        if (missing.Count == 0)
        {
            ApplyPendingCentering();
            return;
        }

        _spriteCts?.Cancel();
        _spriteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _spriteCts.Token;
        var sprites = await TextToVideoBridge.RenderSpritesAsync(missing, _videoWidth, _videoHeight, token);
        if (sprites is null || token.IsCancellationRequested)
        {
            return;
        }

        if (_spriteCache.Count + sprites.Count > SpriteCacheLimit)
        {
            var keep = CurrentEffectiveSegments().Select(e => e.Segment.RasterKey).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _spriteCache.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _spriteCache.Remove(key);
            }
        }

        var problems = new List<string>();
        foreach (var sprite in sprites)
        {
            if (sprite.IsValid)
            {
                _spriteCache[sprite.Key] = sprite;
            }

            var problem = !string.IsNullOrWhiteSpace(sprite.Error) ? sprite.Error : sprite.Warning;
            if (!string.IsNullOrWhiteSpace(problem) && !problems.Contains(problem))
            {
                problems.Add(problem);
            }
        }

        if (problems.Count > 0)
        {
            Status = Loc.F("texttovideo.status.renderWarning", string.Join(" · ", problems));
        }

        ApplyPendingCentering();
        PreviewOverlaysChanged?.Invoke();
    }

    private List<(TextOverlaySegment Segment, int SegmentIndex)> CurrentEffectiveSegments() =>
        TextToVideoBridge.EffectiveSegments(Segments, EditorSegment, SelectedSegmentIndex);

    /// <summary>All overlays in export order with their best available sprite.</summary>
    public IReadOnlyList<PreviewOverlayItem> GetPreviewOverlays()
    {
        var result = new List<PreviewOverlayItem>();
        foreach (var (seg, index) in CurrentEffectiveSegments())
        {
            if (string.IsNullOrWhiteSpace(seg.Text))
            {
                continue;
            }

            var isEditor = ReferenceEquals(seg, EditorSegment);
            if (_spriteCache.TryGetValue(seg.RasterKey, out var sprite))
            {
                _lastSpriteBySlot[index] = sprite;
            }
            else
            {
                _lastSpriteBySlot.TryGetValue(index, out sprite);
            }

            result.Add(new PreviewOverlayItem(seg, index, isEditor, sprite));
        }

        return result;
    }

    /// <summary>Real size of the editor overlay box in video pixels (falls back to an estimate).</summary>
    public (int Width, int Height) EditorOverlayBox()
    {
        var seg = EditorSegment;
        if (seg.LayoutWidth > 0 && seg.LayoutHeight > 0)
        {
            return (seg.LayoutWidth, seg.LayoutHeight);
        }

        if (_spriteCache.TryGetValue(seg.RasterKey, out var sprite))
        {
            return (sprite.ContentWidth, sprite.ContentHeight);
        }

        return EstimateEditorOverlayBox();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CenterEditorHorizontally()
    {
        _centerEditorXPending = true;
        ApplyPendingCentering();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CenterEditorVertically()
    {
        _centerEditorYPending = true;
        ApplyPendingCentering();
    }

    /// <summary>Centering needs the real box size, so it waits for the editor sprite when necessary.</summary>
    private void ApplyPendingCentering()
    {
        if ((!_centerEditorXPending && !_centerEditorYPending) || _videoWidth <= 0)
        {
            return;
        }

        var seg = EditorSegment;
        var hasRealSize = (seg.LayoutWidth > 0 && seg.LayoutHeight > 0)
                          || string.IsNullOrWhiteSpace(seg.Text)
                          || _spriteCache.ContainsKey(seg.RasterKey);
        if (!hasRealSize)
        {
            SchedulePreviewRefresh(immediate: true);
            return;
        }

        var (w, h) = EditorOverlayBox();
        if (_centerEditorXPending)
        {
            seg.PosX = Math.Max(0, (_videoWidth - w) / 2);
        }

        if (_centerEditorYPending)
        {
            seg.PosY = Math.Max(0, (_videoHeight - h) / 2);
        }

        _centerEditorXPending = false;
        _centerEditorYPending = false;
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
