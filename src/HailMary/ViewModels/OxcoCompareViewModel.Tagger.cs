using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Windows.Storage.Streams;

namespace HailMary.ViewModels;

public sealed partial class TaggerFileRowViewModel : ObservableObject
{
    public required string Path { get; init; }

    public required string FileName { get; init; }
}

public sealed partial class TagRouteRuleRow : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;

    [ObservableProperty] private string _folder = string.Empty;
}

public partial class OxcoCompareViewModel
{
    private readonly List<string> _taggerScanPaths = [];
    private readonly DispatcherQueue _taggerDispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _taggerPreviewCts;
    private CancellationTokenSource? _taggerRefreshCts;
    private DispatcherTimer? _taggerPlayTimer;
    private int _taggerFrameCount;
    private bool _taggerInFollowsBitrate = true;
    private bool _syncingTaggerInFromBitrate;
    private bool _taggerPreviewRenderInFlight;
    private int? _pendingTaggerPreviewFrame;

    public ObservableCollection<TaggerFileRowViewModel> TaggerFiles { get; } = [];

    public ObservableCollection<TagRouteRuleRow> TagRouteRules { get; } = [];

    public ObservableCollection<string> TaggerTagChoices { get; } = [];

    [ObservableProperty] private string _taggerTag = "[Stash]";

    [ObservableProperty] private string _taggerProfileName = "Schritt1";

    [ObservableProperty] private string _taggerKeep = "_hyb,_pro,_exp";

    [ObservableProperty] private string _taggerIgnore = "_p";

    [ObservableProperty] private string _taggerDrop = string.Empty;

    [ObservableProperty] private bool _taggerRouteAuto;

    [ObservableProperty] private string _brSuffix = "_bitrate";

    [ObservableProperty] private string _taggerPreviewPath = string.Empty;

    [ObservableProperty] private double _taggerPreviewFrameIndex;

    [ObservableProperty] private double _taggerPreviewFrameMax = 1;

    [ObservableProperty] private bool _taggerPreviewIsLoaded;

    [ObservableProperty] private bool _taggerPreviewIsPlaying;

    [ObservableProperty] private bool _taggerPreviewIsBusy;

    [ObservableProperty] private BitmapImage? _taggerPreviewImage;

    [ObservableProperty] private string _taggerPreviewInfo = Loc.T("oxco.noFileSelected");

    public string TaggerPreviewPlayText => TaggerPreviewIsPlaying ? Loc.T("common.pause") : Loc.T("common.play");

    partial void OnTaggerPreviewIsPlayingChanged(bool value) =>
        OnPropertyChanged(nameof(TaggerPreviewPlayText));

    internal void InitializeTaggerFromSettings(OxcoCompareSettings settings)
    {
        TaggerTag = settings.TaggerTag;
        TaggerProfileName = settings.TaggerProfileName;
        TaggerKeep = settings.TaggerKeep;
        TaggerIgnore = settings.TaggerIgnore;
        TaggerDrop = settings.TaggerDrop;
        TaggerRouteAuto = settings.TaggerRouteAuto;
        TagRouteRules.Clear();
        foreach (var rule in settings.TaggerRouteRules)
        {
            TagRouteRules.Add(new TagRouteRuleRow { Tag = rule.Tag, Folder = rule.Folder });
        }

        RefreshTaggerTagChoices();
        RefreshTaggerFollowsBitrate();
        _ = RefreshTaggerListCoreAsync(logCount: false);
    }

    internal void NotifyTaggerSelectionRequired() =>
        Status = Loc.T("oxco.status.noFilesMarked");

    internal void ApplyTagRouteRulesFromDialog(IReadOnlyList<TagRouteRuleRow> rows)
    {
        TagRouteRules.Clear();
        foreach (var row in rows)
        {
            var tag = row.Tag.Trim();
            var folder = row.Folder.Trim();
            if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(folder))
            {
                TagRouteRules.Add(new TagRouteRuleRow { Tag = tag, Folder = folder });
            }
        }

        RefreshTaggerTagChoices();
        PersistSettings();
    }

    internal void RefreshTaggerTagChoices()
    {
        TaggerTagChoices.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in TagRouteRules)
        {
            var tag = rule.Tag.Trim();
            if (string.IsNullOrEmpty(tag) || !seen.Add(tag))
            {
                continue;
            }

            TaggerTagChoices.Add(tag);
        }

        var current = TaggerTag.Trim();
        if (!string.IsNullOrEmpty(current) && !seen.Contains(current))
        {
            TaggerTagChoices.Insert(0, current);
        }
    }

    internal List<TagRouteRuleRow> CloneTagRouteRules() =>
        TagRouteRules.Select(r => new TagRouteRuleRow { Tag = r.Tag, Folder = r.Folder }).ToList();

    private void RefreshTaggerFollowsBitrate()
    {
        if (_syncingTaggerInFromBitrate)
        {
            return;
        }

        var bo = BitrateOutDir.Trim();
        var ti = TaggerInDir.Trim();
        _taggerInFollowsBitrate = string.IsNullOrEmpty(ti)
            || string.Equals(ti, bo, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyTaggerInFromBitrateIfLinked()
    {
        if (!_taggerInFollowsBitrate)
        {
            return;
        }

        var bo = BitrateOutDir.Trim();
        if (string.IsNullOrEmpty(bo))
        {
            return;
        }

        if (string.Equals(TaggerInDir.Trim(), bo, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _syncingTaggerInFromBitrate = true;
        try
        {
            TaggerInDir = bo;
        }
        finally
        {
            _syncingTaggerInFromBitrate = false;
            RefreshTaggerFollowsBitrate();
        }
    }

    partial void OnTaggerInDirChanged(string value)
    {
        RefreshTaggerFollowsBitrate();
        ScheduleTaggerListRefresh();
    }

    private void ScheduleTaggerListRefresh()
    {
        _taggerRefreshCts?.Cancel();
        _taggerRefreshCts = new CancellationTokenSource();
        var token = _taggerRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _taggerDispatcher.TryEnqueue(() => _ = RefreshTaggerListCoreAsync(logCount: false));
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }, token);
    }

    [RelayCommand]
    private async Task RefreshTaggerListAsync() => await RefreshTaggerListCoreAsync(logCount: true);

    private async Task RefreshTaggerListCoreAsync(bool logCount)
    {
        _taggerScanPaths.Clear();
        TaggerFiles.Clear();
        ReleaseTaggerPreview();

        var inp = TaggerInDir.Trim();
        if (string.IsNullOrWhiteSpace(inp) || !Directory.Exists(inp))
        {
            if (logCount)
            {
                Status = Loc.T("oxco.status.taggerSourceMissing");
            }

            return;
        }

        var searchOption = BrRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(inp, "*.mp4", searchOption)
            .Where(p => !IsPartialTempVideo(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _taggerScanPaths.AddRange(files);
        foreach (var path in files)
        {
            TaggerFiles.Add(new TaggerFileRowViewModel
            {
                Path = path,
                FileName = Path.GetFileName(path),
            });
        }

        if (logCount)
        {
            var scope = BrRecursive ? Loc.T("oxco.scopeRecursive") : Loc.T("oxco.scopeMainOnly");
            Status = files.Count > 0
                ? Loc.F("oxco.status.autotaggerList", files.Count, scope)
                : Loc.F("oxco.status.autotaggerListEmpty", scope);
        }

        await Task.CompletedTask;
    }

    internal void OnTaggerFileSelected(TaggerFileRowViewModel? row)
    {
        if (row is null || !File.Exists(row.Path))
        {
            ReleaseTaggerPreview();
            return;
        }

        _ = LoadTaggerPreviewAsync(row.Path);
    }

    [RelayCommand]
    private async Task RunTaggerAsync(IReadOnlyList<string>? onlyPaths)
    {
        var inp = TaggerInDir.Trim();
        var outp = TaggerOutDir.Trim();
        if (string.IsNullOrWhiteSpace(inp) || !Directory.Exists(inp))
        {
            Status = Loc.T("oxco.status.taggerPathsMissing");
            return;
        }

        if (string.IsNullOrWhiteSpace(outp))
        {
            Status = Loc.T("oxco.status.taggerTargetMissing");
            return;
        }

        List<string>? onlyFiles = null;
        if (onlyPaths is { Count: > 0 })
        {
            onlyFiles = onlyPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (onlyFiles.Count == 0)
            {
                Status = Loc.T("oxco.status.invalidTaggerSelection");
                return;
            }
        }

        ReleaseTaggerPreview();
        PersistSettings();
        IsBusy = true;
        Status = Loc.T("oxco.taggerRunning");
        try
        {
            await WaitForTaggerPreviewIdleAsync();
            Directory.CreateDirectory(outp);
            var result = await OxcoTaggerBridge.ProcessAsync(
                inp,
                outp,
                TaggerTag.Trim(),
                string.IsNullOrWhiteSpace(TaggerProfileName) ? Loc.T("common.profile") : TaggerProfileName.Trim(),
                TaggerKeep,
                TaggerIgnore,
                TaggerDrop,
                TaggerPattern,
                onlyFiles,
                ParseFilterDouble(FilterBuffer, 2.0),
                ParseFilterInt(FilterNoise, 15),
                ParseFilterInt(FilterPixel, 200),
                ParseFilterInt(FilterPixelMax, 0),
                string.IsNullOrWhiteSpace(BrSuffix) ? "_bitrate" : BrSuffix.Trim());

            if (result is null)
            {
                Status = Loc.T("oxco.status.autotaggerBridgeFailed");
                return;
            }

            Status = Loc.F("oxco.status.autotaggerDone", result.Ok, result.Skipped);
            await RefreshTaggerListCoreAsync(logCount: false);
            if (result.Ok > 0)
            {
                await MaybeAutoTagDistributeAsync();
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DistributeByTagRouteAsync()
    {
        var outp = TaggerOutDir.Trim();
        if (string.IsNullOrWhiteSpace(outp) || !Directory.Exists(outp))
        {
            Status = Loc.T("oxco.status.taggerTargetInvalid");
            return;
        }

        var rules = TagRouteRules
            .Select(r => new TagRouteRule { Tag = r.Tag.Trim(), Folder = r.Folder.Trim() })
            .Where(r => !string.IsNullOrEmpty(r.Tag) && !string.IsNullOrEmpty(r.Folder))
            .OrderByDescending(r => r.Tag.Length)
            .ToList();
        if (rules.Count == 0)
        {
            Status = Loc.T("oxco.status.noTagRouteRules");
            return;
        }

        PersistSettings();
        IsBusy = true;
        Status = Loc.T("oxco.status.tagDistributionRunning");
        try
        {
            var result = await OxcoTaggerBridge.DistributeAsync(outp, rules);
            if (result is null)
            {
                Status = Loc.T("oxco.status.tagDistributionBridgeFailed");
                return;
            }

            Status = Loc.F("oxco.status.tagDistributionDone", result.Moved, result.NoMatch, result.Errors);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleTaggerPreviewPlay()
    {
        if (!TaggerPreviewIsLoaded)
        {
            return;
        }

        if (TaggerPreviewIsPlaying)
        {
            StopTaggerPreviewPlayback();
        }
        else
        {
            StartTaggerPreviewPlayback();
        }
    }

    [RelayCommand]
    private void TaggerPreviewStepBack() => StepTaggerPreviewFrame(-1);

    [RelayCommand]
    private void TaggerPreviewStepForward() => StepTaggerPreviewFrame(1);

    partial void OnTaggerPreviewFrameIndexChanged(double value)
    {
        if (!TaggerPreviewIsPlaying)
        {
            _ = RenderTaggerPreviewFrameDebouncedAsync();
        }
    }

    private async Task MaybeAutoTagDistributeAsync()
    {
        if (!TaggerRouteAuto || TagRouteRules.Count == 0 || string.IsNullOrWhiteSpace(TaggerOutDir))
        {
            return;
        }

        await DistributeByTagRouteAsync();
    }

    private async Task LoadTaggerPreviewAsync(string path)
    {
        StopTaggerPreviewPlayback();
        ResetTaggerPreviewCancellation();
        var token = _taggerPreviewCts!.Token;
        TaggerPreviewIsBusy = true;
        TaggerPreviewPath = path;
        try
        {
            var probe = await OxcoPreviewBridge.ProbeAsync(path, string.Empty, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (probe is null || probe.FrameCount <= 0)
            {
                TaggerPreviewIsLoaded = false;
                TaggerPreviewImage = null;
                TaggerPreviewInfo = "Vorschau: Metadaten nicht lesbar.";
                return;
            }

            _taggerFrameCount = probe.FrameCount;
            TaggerPreviewFrameMax = Math.Max(1, probe.FrameCount - 1);
            TaggerPreviewFrameIndex = 0;
            TaggerPreviewIsLoaded = true;
            TaggerPreviewInfo = $"{Path.GetFileName(path)} — {probe.FrameCount} Frames";
            await RenderTaggerPreviewFrameAsync(0, token);
        }
        catch (OperationCanceledException)
        {
            // ignore — neue Auswahl oder Autotagger-Lauf
        }
        finally
        {
            TaggerPreviewIsBusy = false;
        }
    }

    private void ReleaseTaggerPreview()
    {
        StopTaggerPreviewPlayback();
        _taggerPreviewCts?.Cancel();
        _taggerPreviewRenderInFlight = false;
        _pendingTaggerPreviewFrame = null;
        TaggerPreviewPath = string.Empty;
        TaggerPreviewIsLoaded = false;
        TaggerPreviewImage = null;
        TaggerPreviewInfo = Loc.T("oxco.noFileSelected");
        _taggerFrameCount = 0;
    }

    private void ResetTaggerPreviewCancellation()
    {
        _taggerPreviewCts?.Cancel();
        _taggerPreviewCts?.Dispose();
        _taggerPreviewCts = new CancellationTokenSource();
    }

    private async Task WaitForTaggerPreviewIdleAsync()
    {
        for (var i = 0; i < 30 && (_taggerPreviewRenderInFlight || TaggerPreviewIsBusy); i++)
        {
            await Task.Delay(50);
        }

        ResetTaggerPreviewCancellation();
    }

    private void StartTaggerPreviewPlayback()
    {
        if (!TaggerPreviewIsLoaded)
        {
            return;
        }

        TaggerPreviewIsPlaying = true;
        _taggerPlayTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 24),
        };
        _taggerPlayTimer.Tick -= TaggerPlayTimer_Tick;
        _taggerPlayTimer.Tick += TaggerPlayTimer_Tick;
        _taggerPlayTimer.Start();
    }

    private void StopTaggerPreviewPlayback()
    {
        TaggerPreviewIsPlaying = false;
        _taggerPlayTimer?.Stop();
    }

    private void TaggerPlayTimer_Tick(object? sender, object e)
    {
        if (!TaggerPreviewIsLoaded || _taggerFrameCount <= 0)
        {
            StopTaggerPreviewPlayback();
            return;
        }

        var next = ((int)TaggerPreviewFrameIndex + 1) % _taggerFrameCount;
        TaggerPreviewFrameIndex = next;
        var token = _taggerPreviewCts?.Token ?? CancellationToken.None;
        _ = RenderTaggerPreviewFrameAsync(next, token);
    }

    private void StepTaggerPreviewFrame(int delta)
    {
        if (!TaggerPreviewIsLoaded || _taggerFrameCount <= 0)
        {
            return;
        }

        var idx = (int)Math.Clamp(TaggerPreviewFrameIndex + delta, 0, _taggerFrameCount - 1);
        TaggerPreviewFrameIndex = idx;
    }

    private async Task RenderTaggerPreviewFrameDebouncedAsync()
    {
        ResetTaggerPreviewCancellation();
        var token = _taggerPreviewCts!.Token;
        try
        {
            await Task.Delay(100, token);
            await RenderTaggerPreviewFrameAsync((int)TaggerPreviewFrameIndex, token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task RenderTaggerPreviewFrameAsync(int frameIndex, CancellationToken cancellationToken = default)
    {
        if (!TaggerPreviewIsLoaded || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var path = TaggerPreviewPath.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (_taggerPreviewRenderInFlight)
        {
            _pendingTaggerPreviewFrame = frameIndex;
            return;
        }

        _taggerPreviewRenderInFlight = true;
        try
        {
            var frame = await OxcoPreviewBridge.RenderFrameAsync(
                path,
                string.Empty,
                frameIndex,
                ParseFilterInt(FilterNoise, 15),
                ParseFilterInt(FilterPixel, 200),
                sideBySide: false,
                overlay: false,
                cancellationToken: cancellationToken);
            if (frame is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(frame.ImageBytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            TaggerPreviewImage = bitmap;
            TaggerPreviewInfo = $"Frame {frame.FrameIndex} / {Math.Max(0, frame.FrameCount - 1)}";
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        finally
        {
            _taggerPreviewRenderInFlight = false;
            if (_pendingTaggerPreviewFrame is int pending && pending != frameIndex && !cancellationToken.IsCancellationRequested)
            {
                _pendingTaggerPreviewFrame = null;
                var token = _taggerPreviewCts?.Token ?? CancellationToken.None;
                _ = RenderTaggerPreviewFrameAsync(pending, token);
            }
        }
    }

    private static bool IsPartialTempVideo(string name) =>
        name.Contains(".partial", StringComparison.OrdinalIgnoreCase);

    private static double ParseFilterDouble(string raw, double fallback) =>
        double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    private static int ParseFilterInt(string raw, int fallback) =>
        int.TryParse(raw.Trim(), out var v) ? v : fallback;
}
