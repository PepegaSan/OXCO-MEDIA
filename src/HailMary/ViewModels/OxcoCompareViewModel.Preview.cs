using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Windows.Storage.Streams;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    private readonly DispatcherQueue _previewDispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _previewRenderCts;
    private CancellationTokenSource? _previewAutoLoadCts;
    private DispatcherTimer? _previewPlayTimer;
    private int _previewFrameCount;
    private bool _previewSyncingFromWorkflow;
    private bool _previewRenderInFlight;
    private int? _pendingPreviewFrame;

    [ObservableProperty] private string _previewPathA = string.Empty;

    [ObservableProperty] private string _previewPathB = string.Empty;

    [ObservableProperty] private bool _previewLinkPaths = true;

    [ObservableProperty] private bool _previewAutoLoad = true;

    [ObservableProperty] private bool _previewSideBySide = true;

    [ObservableProperty] private bool _previewOverlay = true;

    [ObservableProperty] private double _previewNoise = 15;

    [ObservableProperty] private double _previewPixel = 200;

    [ObservableProperty] private double _previewMaxFps = 24;

    [ObservableProperty] private double _previewFrameIndex;

    [ObservableProperty] private double _previewFrameMax = 1;

    [ObservableProperty] private string _previewDiffText = "—";

    [ObservableProperty] private string _previewSwapText = string.Empty;

    [ObservableProperty] private string _previewInfoText = Loc.T("oxco.noVideoLoaded");

    [ObservableProperty] private bool _previewIsLoaded;

    [ObservableProperty] private bool _previewIsPlaying;

    public string PreviewPlayButtonText => PreviewIsPlaying ? Loc.T("common.pause") : Loc.T("common.play");

    [ObservableProperty] private bool _previewIsBusy;

    [ObservableProperty] private BitmapImage? _previewImage;

    partial void OnPreviewIsPlayingChanged(bool value) =>
        OnPropertyChanged(nameof(PreviewPlayButtonText));

    partial void OnSourcePathChanged(string value)
    {
        OnWorkflowPathsChanged();
        RefreshListRowHighlights();
    }

    partial void OnDeepfakePathChanged(string value)
    {
        OnWorkflowPathsChanged();
        RefreshListRowHighlights();
    }

    partial void OnPreviewLinkPathsChanged(bool value)
    {
        if (value)
        {
            SyncPreviewPathsFromWorkflow();
            SchedulePreviewAutoLoad();
        }
    }

    partial void OnPreviewPathAChanged(string value) => UnlinkPreviewIfDiverged(isA: true);

    partial void OnPreviewPathBChanged(string value) => UnlinkPreviewIfDiverged(isA: false);

    partial void OnPreviewNoiseChanged(double value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewPixelChanged(double value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewSideBySideChanged(bool value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewOverlayChanged(bool value) => _ = RenderPreviewFrameDebouncedAsync();

    partial void OnPreviewMaxFpsChanged(double value)
    {
        if (_previewPlayTimer is not null && PreviewIsPlaying)
        {
            _previewPlayTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(16, 1000.0 / Math.Max(1, (int)value)));
        }
    }

    partial void OnPreviewFrameIndexChanged(double value)
    {
        if (!PreviewIsPlaying)
        {
            _ = RenderPreviewFrameDebouncedAsync();
        }
    }

    internal void RefreshPreviewFromWorkflow()
    {
        if (!PreviewLinkPaths)
        {
            PreviewLinkPaths = true;
        }

        SyncPreviewPathsFromWorkflow();
        if (!string.IsNullOrWhiteSpace(DeepfakePath))
        {
            PreviewSideBySide = true;
        }

        SchedulePreviewAutoLoad();
    }

    partial void OnPreviewAutoLoadChanged(bool value)
    {
        if (value)
        {
            SchedulePreviewAutoLoad();
        }
    }

    private void OnWorkflowPathsChanged()
    {
        if (!PreviewLinkPaths)
        {
            return;
        }

        SyncPreviewPathsFromWorkflow();
        if (!string.IsNullOrWhiteSpace(DeepfakePath))
        {
            PreviewSideBySide = true;
        }

        SchedulePreviewAutoLoad();
    }

    private void SyncPreviewPathsFromWorkflow()
    {
        _previewSyncingFromWorkflow = true;
        try
        {
            PreviewPathA = SourcePath;
            PreviewPathB = DeepfakePath;
        }
        finally
        {
            _previewSyncingFromWorkflow = false;
        }
    }

    private void UnlinkPreviewIfDiverged(bool isA)
    {
        if (_previewSyncingFromWorkflow || !PreviewLinkPaths)
        {
            return;
        }

        if (isA && !string.Equals(PreviewPathA, SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            PreviewLinkPaths = false;
        }
        else if (!isA && !string.Equals(PreviewPathB, DeepfakePath, StringComparison.OrdinalIgnoreCase))
        {
            PreviewLinkPaths = false;
        }
    }

    private void SchedulePreviewAutoLoad()
    {
        if (!PreviewAutoLoad)
        {
            return;
        }

        _previewAutoLoadCts?.Cancel();
        _previewAutoLoadCts = new CancellationTokenSource();
        var token = _previewAutoLoadCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _previewDispatcher.TryEnqueue(() => _ = LoadPreviewInternalAsync(silent: true));
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }, token);
    }

    [RelayCommand]
    private async Task PickPreviewPathAAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(PreviewPathA);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PreviewPathA = path;
            if (PreviewAutoLoad)
            {
                await LoadPreviewInternalAsync(silent: true);
            }
        }
    }

    [RelayCommand]
    private async Task PickPreviewPathBAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(PreviewPathB);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PreviewPathB = path;
            PreviewSideBySide = true;
            if (PreviewAutoLoad)
            {
                await LoadPreviewInternalAsync(silent: true);
            }
        }
    }

    [RelayCommand]
    private async Task LoadPreviewAsync() => await LoadPreviewInternalAsync(silent: false);

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
    private void ApplyPreviewToFilters()
    {
        FilterNoise = ((int)Math.Round(PreviewNoise)).ToString();
        FilterPixel = ((int)Math.Round(PreviewPixel)).ToString();
        Status = Loc.T("oxco.thresholdsApplied");
    }

    [RelayCommand]
    private async Task SavePreviewThresholdsAsync()
    {
        FilterNoise = ((int)Math.Round(PreviewNoise)).ToString();
        FilterPixel = ((int)Math.Round(PreviewPixel)).ToString();
        PersistSettings();
        Status = Loc.T("oxco.status.thresholdsSaved");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void SyncPreviewFromWorkflowNow()
    {
        PreviewLinkPaths = true;
        SyncPreviewPathsFromWorkflow();
        SchedulePreviewAutoLoad();
    }

    internal void InitializePreviewFromFilters()
    {
        if (int.TryParse(FilterNoise, out var n))
        {
            PreviewNoise = n;
        }

        if (int.TryParse(FilterPixel, out var p))
        {
            PreviewPixel = p;
        }

        SyncPreviewPathsFromWorkflow();
        if (PreviewAutoLoad && HasPreviewableWorkflowPaths())
        {
            SchedulePreviewAutoLoad();
        }
    }

    private bool HasPreviewableWorkflowPaths()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
        {
            return false;
        }

        if (!PreviewSideBySide)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(DeepfakePath) && File.Exists(DeepfakePath);
    }

    private async Task LoadPreviewInternalAsync(bool silent)
    {
        var pathA = PreviewPathA.Trim();
        if (string.IsNullOrWhiteSpace(pathA) || !File.Exists(pathA))
        {
            if (!silent)
            {
                Status = "Vorschau: Original-Video fehlt.";
            }

            PreviewIsLoaded = false;
            PreviewImage = null;
            return;
        }

        if (PreviewSideBySide)
        {
            var pathB = PreviewPathB.Trim();
            if (string.IsNullOrWhiteSpace(pathB) || !File.Exists(pathB))
            {
                if (!silent)
                {
                    Status = "Vorschau: Deepfake-Video fehlt (Side-by-Side).";
                }

                return;
            }
        }

        StopPreviewPlayback();
        PreviewIsBusy = true;
        try
        {
            var probe = await OxcoPreviewBridge.ProbeAsync(pathA, PreviewPathB.Trim());
            if (probe is null || probe.FrameCount <= 0)
            {
                if (!silent)
                {
                    Status = "Vorschau: Metadaten konnten nicht gelesen werden (ffprobe/OpenCV).";
                }

                PreviewIsLoaded = false;
                return;
            }

            _previewFrameCount = probe.FrameCount;
            PreviewFrameMax = Math.Max(1, probe.FrameCount - 1);
            PreviewFrameIndex = 0;
            PreviewIsLoaded = true;
            PreviewInfoText = $"{probe.NameA} — {probe.FrameCount} Frames, {probe.Width}×{probe.Height}";
            await RenderPreviewFrameAsync((int)PreviewFrameIndex);
        }
        finally
        {
            PreviewIsBusy = false;
        }
    }

    private void StartPreviewPlayback()
    {
        if (!PreviewIsLoaded)
        {
            return;
        }

        PreviewIsPlaying = true;
        _previewPlayTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(16, 1000 / Math.Max(1, (int)PreviewMaxFps))) };
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
        if (!PreviewIsLoaded || _previewFrameCount <= 0)
        {
            StopPreviewPlayback();
            return;
        }

        var next = ((int)PreviewFrameIndex + 1) % _previewFrameCount;
        PreviewFrameIndex = next;
        _ = RenderPreviewFrameAsync(next);
    }

    private void StepPreviewFrame(int delta)
    {
        if (!PreviewIsLoaded || _previewFrameCount <= 0)
        {
            return;
        }

        var idx = (int)Math.Clamp(PreviewFrameIndex + delta, 0, _previewFrameCount - 1);
        PreviewFrameIndex = idx;
    }

    private async Task RenderPreviewFrameDebouncedAsync()
    {
        _previewRenderCts?.Cancel();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;
        try
        {
            await Task.Delay(120, token);
            await RenderPreviewFrameAsync((int)PreviewFrameIndex, token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task RenderPreviewFrameAsync(int frameIndex, CancellationToken cancellationToken = default)
    {
        if (!PreviewIsLoaded)
        {
            return;
        }

        var pathA = PreviewPathA.Trim();
        if (string.IsNullOrWhiteSpace(pathA) || !File.Exists(pathA))
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
            var frame = await OxcoPreviewBridge.RenderFrameAsync(
                pathA,
                PreviewPathB.Trim(),
                frameIndex,
                (int)Math.Round(PreviewNoise),
                (int)Math.Round(PreviewPixel),
                PreviewSideBySide,
                PreviewOverlay,
                cancellationToken: cancellationToken);
            if (frame is null)
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(frame.ImageBytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            UiDispatcher.Run(() =>
            {
                PreviewDiffText = string.IsNullOrWhiteSpace(PreviewPathB)
                    ? Loc.T("oxco.noDeepfake")
                    : $"{frame.DiffCount:N0} abweichende Pixel";
                PreviewSwapText = string.IsNullOrWhiteSpace(PreviewPathB)
                    ? string.Empty
                    : frame.DiffOverThreshold
                        ? Loc.T("oxco.aboveThreshold")
                        : Loc.T("oxco.belowThreshold");
                PreviewInfoText = $"Frame {frame.FrameIndex} / {Math.Max(0, frame.FrameCount - 1)}";
                PreviewImage = bitmap;
            });
        }
        finally
        {
            _previewRenderInFlight = false;
            if (_pendingPreviewFrame is int pending && pending != frameIndex)
            {
                _pendingPreviewFrame = null;
                _ = RenderPreviewFrameAsync(pending, cancellationToken);
            }
        }
    }
}
