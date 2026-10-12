using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HailMary.ViewModels;

/// <summary>
/// Compare preview of the active pair (SourcePath / DeepfakePath) through a long-running preview worker.
/// Measurement uses the current filter values (mode, noise, pixel threshold), exactly like the analysis.
/// </summary>
public partial class OxcoCompareViewModel
{
    private readonly DispatcherQueue _previewDispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly OxcoPreviewWorker _previewWorker = new();
    private CancellationTokenSource? _previewRenderCts;
    private CancellationTokenSource? _previewLoadCts;
    private DispatcherTimer? _previewPlayTimer;
    private bool _previewRenderInFlight;
    private int? _pendingPreviewFrame;
    private double _previewFps = 25;

    [ObservableProperty] private bool _previewSideBySide = true;

    [ObservableProperty] private bool _previewOverlay = true;

    [ObservableProperty] private double _previewMaxFps = 24;

    [ObservableProperty] private double _previewFrameIndex;

    [ObservableProperty] private int _previewFrameTotal;

    [ObservableProperty] private string _previewDiffText = "—";

    [ObservableProperty] private string _previewSwapText = string.Empty;

    [ObservableProperty] private string _previewInfoText = Loc.T("oxco.noVideoLoaded");

    [ObservableProperty] private string _previewTimeText = string.Empty;

    [ObservableProperty] private bool _previewIsLoaded;

    [ObservableProperty] private bool _previewIsPlaying;

    [ObservableProperty] private bool _previewIsBusy;

    [ObservableProperty] private BitmapImage? _previewImage;

    public string PreviewPlayButtonText => PreviewIsPlaying ? Loc.T("common.pause") : Loc.T("common.play");

    partial void OnPreviewIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PreviewPlayButtonText));

    partial void OnSourcePathChanged(string value)
    {
        RefreshListRowHighlights();
        OnActivePairChanged();
    }

    partial void OnDeepfakePathChanged(string value)
    {
        RefreshListRowHighlights();
        OnActivePairChanged();
    }

    partial void OnPreviewSideBySideChanged(bool value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewOverlayChanged(bool value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewMaxFpsChanged(double value)
    {
        if (_previewPlayTimer is not null)
        {
            _previewPlayTimer.Interval = PlaybackInterval();
        }
    }

    partial void OnPreviewFrameIndexChanged(double value)
    {
        UpdatePreviewTimeText();
        if (!PreviewIsPlaying)
        {
            _ = RenderPreviewFrameDebouncedAsync();
        }
    }

    private void OnActivePairChanged()
    {
        SchedulePreviewLoad();
        ScheduleMetricsLoad();
    }

    /// <summary>Called after list selection changed the pair.</summary>
    internal void RefreshPreviewFromWorkflow()
    {
        if (!string.IsNullOrWhiteSpace(DeepfakePath))
        {
            PreviewSideBySide = true;
        }

        SchedulePreviewLoad();
    }

    internal void InitializePreviewFromFilters()
    {
        if (PreviewIsLoaded)
        {
            _ = RenderPreviewFrameDebouncedAsync();
        }
        else
        {
            SchedulePreviewLoad();
        }
    }

    /// <summary>Frees the open video files (call before moving / recycling / converting them).</summary>
    internal async Task ReleasePreviewFilesAsync()
    {
        StopPreviewPlayback();
        PreviewIsLoaded = false;
        PreviewImage = null;
        await _previewWorker.ReleaseFilesAsync();
    }

    public void SeekPreviewToFrame(int frame)
    {
        if (!PreviewIsLoaded || PreviewFrameTotal <= 0)
        {
            return;
        }

        StopPreviewPlayback();
        PreviewFrameIndex = Math.Clamp(frame, 0, PreviewFrameTotal - 1);
    }

    private void SchedulePreviewLoad()
    {
        _previewLoadCts?.Cancel();
        _previewLoadCts = new CancellationTokenSource();
        var token = _previewLoadCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                _previewDispatcher.TryEnqueue(() => _ = LoadPreviewCoreAsync(silent: true, token));
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
        }, token);
    }

    [RelayCommand]
    private async Task LoadPreviewAsync() => await LoadPreviewCoreAsync(silent: false, CancellationToken.None);

    private async Task LoadPreviewCoreAsync(bool silent, CancellationToken token)
    {
        var pathA = SourcePath.Trim();
        var pathB = DeepfakePath.Trim();
        if (string.IsNullOrWhiteSpace(pathA) || !File.Exists(pathA))
        {
            if (!silent)
            {
                Status = Loc.T("oxco.preview.sourceMissing");
            }

            StopPreviewPlayback();
            PreviewIsLoaded = false;
            PreviewImage = null;
            PreviewInfoText = Loc.T("oxco.noVideoLoaded");
            return;
        }

        if (!File.Exists(pathB))
        {
            pathB = string.Empty;
        }

        if (PreviewIsLoaded && _previewWorker.IsOpen(pathA, pathB))
        {
            await RenderPreviewFrameAsync((int)PreviewFrameIndex);
            return;
        }

        StopPreviewPlayback();
        PreviewIsBusy = true;
        try
        {
            var open = await _previewWorker.OpenAsync(pathA, pathB);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (open is null || open.FrameCount <= 0)
            {
                if (!silent)
                {
                    Status = Loc.T("oxco.preview.metadataUnreadable");
                }

                PreviewIsLoaded = false;
                PreviewImage = null;
                return;
            }

            _previewFps = open.Fps > 0 ? open.Fps : 25;
            PreviewFrameTotal = open.FrameCount;
            PreviewIsLoaded = true;
            PreviewInfoText = Loc.F("oxco.preview.fileInfoDetailed", open.NameA, open.FrameCount, open.Width, open.Height);
            if (PreviewFrameIndex >= open.FrameCount)
            {
                PreviewFrameIndex = 0;
            }

            UpdatePreviewTimeText();
            await RenderPreviewFrameAsync((int)PreviewFrameIndex);
        }
        finally
        {
            PreviewIsBusy = false;
        }
    }

    [RelayCommand]
    private void TogglePreviewPlay()
    {
        if (!PreviewIsLoaded)
        {
            return;
        }

        if (PreviewIsPlaying)
        {
            StopPreviewPlayback();
        }
        else
        {
            StartPreviewPlayback();
        }
    }

    [RelayCommand]
    private void PreviewStepBack() => StepPreviewFrame(-1);

    [RelayCommand]
    private void PreviewStepForward() => StepPreviewFrame(1);

    [RelayCommand]
    private void PreviewJumpPrevCut() => JumpToCut(forward: false);

    [RelayCommand]
    private void PreviewJumpNextCut() => JumpToCut(forward: true);

    private TimeSpan PlaybackInterval()
    {
        var fps = Math.Clamp(Math.Min(PreviewMaxFps, _previewFps), 1, 60);
        return TimeSpan.FromMilliseconds(1000.0 / fps);
    }

    private void StartPreviewPlayback()
    {
        if (!PreviewIsLoaded)
        {
            return;
        }

        PreviewIsPlaying = true;
        _previewPlayTimer ??= new DispatcherTimer();
        _previewPlayTimer.Interval = PlaybackInterval();
        _previewPlayTimer.Tick -= PreviewPlayTimer_Tick;
        _previewPlayTimer.Tick += PreviewPlayTimer_Tick;
        _previewPlayTimer.Start();
    }

    private void StopPreviewPlayback()
    {
        PreviewIsPlaying = false;
        _previewPlayTimer?.Stop();
    }

    private void PreviewPlayTimer_Tick(object? sender, object e)
    {
        if (!PreviewIsLoaded || PreviewFrameTotal <= 0)
        {
            StopPreviewPlayback();
            return;
        }

        // Advance only when the previous frame is on screen, so the worker keeps reading sequentially.
        if (_previewRenderInFlight)
        {
            return;
        }

        var next = ((int)PreviewFrameIndex + 1) % PreviewFrameTotal;
        PreviewFrameIndex = next;
        _ = RenderPreviewFrameAsync(next);
    }

    private void StepPreviewFrame(int delta)
    {
        if (!PreviewIsLoaded || PreviewFrameTotal <= 0)
        {
            return;
        }

        StopPreviewPlayback();
        PreviewFrameIndex = Math.Clamp(PreviewFrameIndex + delta, 0, PreviewFrameTotal - 1);
    }

    private async Task RenderPreviewFrameDebouncedAsync()
    {
        _previewRenderCts?.Cancel();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;
        try
        {
            await Task.Delay(90, token);
            await RenderPreviewFrameAsync((int)PreviewFrameIndex, token);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
    }

    private async Task RenderPreviewFrameAsync(int frameIndex, CancellationToken cancellationToken = default)
    {
        if (!PreviewIsLoaded)
        {
            return;
        }

        if (_previewRenderInFlight)
        {
            _pendingPreviewFrame = frameIndex;
            return;
        }

        _previewRenderInFlight = true;
        try
        {
            var request = new JsonObject
            {
                ["index"] = frameIndex,
                ["b_offset"] = PreviewBOffset,
                ["noise"] = ParseFilterInt(FilterNoise, 15),
                ["pixel_threshold"] = ParseFilterInt(FilterPixel, 200),
                ["pixel_max"] = ParseFilterInt(FilterPixelMax, 0),
                ["side_by_side"] = PreviewSideBySide,
                ["overlay"] = PreviewOverlay,
                ["max_width"] = 1280,
                ["analysis_mode"] = _analysisMode,
                ["analysis_width"] = ParseFilterInt(FilterAnalysisWidth, 640),
            };
            var frame = await _previewWorker.RenderAsync(request, cancellationToken);
            if (frame is null || frame.ImageBytes.Length == 0)
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(frame.ImageBytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            var hasB = !string.IsNullOrWhiteSpace(DeepfakePath) && File.Exists(DeepfakePath);
            PreviewDiffText = hasB
                ? Loc.F(IsBlobMode ? "oxco.preview.blobArea" : "oxco.preview.diffPixels", frame.DiffCount)
                : Loc.T("oxco.noDeepfake");
            PreviewSwapText = !hasB
                ? string.Empty
                : frame.DiffOverThreshold
                    ? Loc.T("oxco.aboveThreshold")
                    : Loc.T("oxco.belowThreshold");
            PreviewImage = bitmap;
        }
        finally
        {
            _previewRenderInFlight = false;
            if (_pendingPreviewFrame is int pending)
            {
                _pendingPreviewFrame = null;
                if (pending != frameIndex)
                {
                    _ = RenderPreviewFrameAsync(pending, cancellationToken);
                }
            }
        }
    }

    private void UpdatePreviewTimeText()
    {
        if (PreviewFrameTotal <= 0)
        {
            PreviewTimeText = string.Empty;
            return;
        }

        var fps = _previewFps > 0 ? _previewFps : 25;
        var now = TimeSpan.FromSeconds(PreviewFrameIndex / fps);
        var total = TimeSpan.FromSeconds(PreviewFrameTotal / fps);
        PreviewTimeText = $"{now:mm\\:ss\\.ff} / {total:mm\\:ss}  ·  {Loc.F("oxco.preview.frameShort", (int)PreviewFrameIndex, PreviewFrameTotal - 1)}";
    }
}
