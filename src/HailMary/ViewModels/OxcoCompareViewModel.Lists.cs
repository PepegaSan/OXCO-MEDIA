using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    private readonly List<OxcoCompareFileEntry> _origEntries = [];
    private readonly List<OxcoCompareFileEntry> _dfEntries = [];
    private string? _selectedOrigSignature;
    private bool _applyingDeepfakeSelection;
    private bool _applyingOriginalSelection;
    private CancellationTokenSource? _probeCts;
    private int _loadListGeneration;

    public ObservableCollection<OxcoCompareDisplayItem> OriginalDisplayItems { get; } = [];

    public ObservableCollection<OxcoCompareDisplayItem> DeepfakeDisplayItems { get; } = [];

    public IReadOnlyList<string> CompareSortChoices =>
        OxcoCompareListService.SortModeKeys.Select(OxcoCompareListService.LocalizeSortMode).ToList();

    public IReadOnlyList<string> CompareGroupChoices =>
        OxcoCompareListService.GroupModeKeys.Select(OxcoCompareListService.LocalizeGroupMode).ToList();

    [ObservableProperty] private string _compareSourceDir = string.Empty;

    [ObservableProperty] private string _compareDeepfakeDir = string.Empty;

    [ObservableProperty] private bool _compareRecursive = true;

    [ObservableProperty] private string _compareSortMode = "date_desc";

    [ObservableProperty] private string _compareGroupMode = "folder";

    [ObservableProperty] private string _compareSortLabel = OxcoCompareListService.LocalizeSortMode("date_desc");

    [ObservableProperty] private string _compareGroupLabel = OxcoCompareListService.LocalizeGroupMode("folder");

    [ObservableProperty] private string _taggerPattern = "YYMMDDHHmmSS";

    [ObservableProperty] private string _probeStatus = string.Empty;

    [ObservableProperty] private OxcoCompareDisplayItem? _selectedOriginalItem;

    [ObservableProperty] private OxcoCompareDisplayItem? _selectedDeepfakeItem;

    partial void OnCompareSortModeChanged(string value) => RebuildDisplayLists();

    partial void OnCompareGroupModeChanged(string value) => RebuildDisplayLists();

    partial void OnCompareSortLabelChanged(string value)
    {
        var key = OxcoCompareListService.SortKeyFromLabel(value);
        if (!string.IsNullOrEmpty(key) && key != CompareSortMode)
        {
            CompareSortMode = key;
        }
    }

    partial void OnCompareGroupLabelChanged(string value)
    {
        var key = OxcoCompareListService.GroupKeyFromLabel(value);
        if (!string.IsNullOrEmpty(key) && key != CompareGroupMode)
        {
            CompareGroupMode = key;
        }
    }

    public event Action<string>? ScrollToOriginalRequested;

    public event Action<string>? ScrollToDeepfakeRequested;

    public event Action<IReadOnlyList<string>>? RestoreDeepfakeMultiSelectionRequested;

    public event Action? ClearDeepfakeListSelectionRequested;

    partial void OnSelectedOriginalItemChanged(OxcoCompareDisplayItem? value)
    {
        if (_applyingDeepfakeSelection
            || _applyingOriginalSelection
            || value?.Entry is null
            || value.IsGroupHeader)
        {
            return;
        }

        ApplyOriginalSelection(value.Entry);
    }

    internal void RefreshListRowHighlights()
    {
        if (OriginalDisplayItems.Count == 0 && DeepfakeDisplayItems.Count == 0)
        {
            return;
        }

        var orig = _origEntries.FirstOrDefault(e =>
            string.Equals(e.Path, SourcePath, StringComparison.OrdinalIgnoreCase));
        RebuildDisplayLists(orig);
    }

    [RelayCommand]
    private async Task PickCompareSourceDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(CompareSourceDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CompareSourceDir = path;
        }
    }

    [RelayCommand]
    private async Task PickCompareDeepfakeDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(CompareDeepfakeDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CompareDeepfakeDir = path;
        }
    }

    [RelayCommand]
    private async Task LoadCompareListsAsync()
    {
        if (string.IsNullOrWhiteSpace(CompareSourceDir) || !Directory.Exists(CompareSourceDir))
        {
            Status = "Original-Ordner fehlt oder nicht gefunden.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CompareDeepfakeDir) || !Directory.Exists(CompareDeepfakeDir))
        {
            Status = "Deepfake-Ordner fehlt oder nicht gefunden.";
            return;
        }

        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;
        var generation = Interlocked.Increment(ref _loadListGeneration);

        IsBusy = true;
        Status = Loc.T("oxco.scanningFolders");
        ProbeStatus = string.Empty;
        try
        {
            _origEntries.Clear();
            _dfEntries.Clear();
            _origEntries.AddRange(OxcoCompareListService.ScanCompareFolder(CompareSourceDir, CompareRecursive));
            _dfEntries.AddRange(OxcoCompareListService.ScanCompareFolder(CompareDeepfakeDir, CompareRecursive));
            _selectedOrigSignature = null;

            RebuildDisplayLists();
            if (generation == _loadListGeneration)
            {
                Status = $"Gescannt: {_origEntries.Count} Original, {_dfEntries.Count} Deepfake — ffprobe läuft…";
            }

            PersistSettings();

            var all = _origEntries.Concat(_dfEntries).ToList();
            var progress = new Progress<(int done, int total)>(p =>
            {
                UiDispatcher.Run(() =>
                {
                    if (generation == _loadListGeneration)
                    {
                        ProbeStatus = $"Metadaten: {p.done}/{p.total}";
                    }
                });
            });

            await OxcoCompareListService.ProbeEntriesAsync(all, progress, token);
            if (token.IsCancellationRequested || generation != _loadListGeneration)
            {
                return;
            }

            UiDispatcher.Run(() => RebuildDisplayLists());
            var probed = all.Count(e => e.ProbeOk);
            UiDispatcher.Run(() =>
            {
                if (generation != _loadListGeneration)
                {
                    return;
                }

                Status = $"Listen geladen: {_origEntries.Count} Original, {_dfEntries.Count} Deepfake ({probed} mit Metadaten).";
                ProbeStatus = string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
            if (generation == _loadListGeneration)
            {
                Status = "Scan abgebrochen.";
            }
        }
        catch (Exception ex)
        {
            if (generation == _loadListGeneration)
            {
                Status = ex.Message;
            }
        }
        finally
        {
            if (generation == _loadListGeneration)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task RunCompareBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
        {
            Status = Loc.T("oxco.status.pickOriginal");
            return;
        }

        if (!File.Exists(OxcoCompareConfigReader.SettingsIniPath))
        {
            Status = $"settings.ini fehlt: {OxcoCompareConfigReader.SettingsIniPath}";
            return;
        }

        var selectedDfs = SelectedDeepfakeEntries.Count > 0
            ? SelectedDeepfakeEntries.ToList()
            : [];

        if (selectedDfs.Count == 0)
        {
            var orig = _origEntries.FirstOrDefault(e =>
                string.Equals(e.Path, SourcePath, StringComparison.OrdinalIgnoreCase));
            if (orig is not null)
            {
                selectedDfs.AddRange(OxcoCompareListService.RankDeepfakesForOriginal(orig, _dfEntries, TaggerPattern));
            }
        }

        if (selectedDfs.Count == 0)
        {
            Status = Loc.T("oxco.status.noDeepfake");
            return;
        }

        IsBusy = true;
        CanRetryCompare = false;
        IsCompareRunning = true;
        var ok = 0;
        var fail = 0;
        var usePipeline = FilterDavinci && CompareBatchPipeline && selectedDfs.Count > 1;
        _batchCompareCts?.Cancel();
        _batchCompareCts = new CancellationTokenSource();
        var batchToken = _batchCompareCts.Token;
        AppServices.JobProgress.BeginBatch(selectedDfs.Count, "Compare");
        Task<JobResult>? pendingDavinci = null;
        string? pendingCheckpoint = null;
        try
        {
            for (var i = 0; i < selectedDfs.Count; i++)
            {
                if (batchToken.IsCancellationRequested)
                {
                    break;
                }

                AppServices.JobProgress.SetBatchItem(i);
                DeepfakePath = selectedDfs[i].Path;
                Status = usePipeline
                    ? $"Compare {i + 1}/{selectedDfs.Count}: Analyse — {selectedDfs[i].FileName}"
                    : $"Compare {i + 1}/{selectedDfs.Count}: {selectedDfs[i].FileName}";
                PersistSettings();

                if (usePipeline)
                {
                    var checkpointPath = Path.Combine(
                        Path.GetTempPath(),
                        $"hm_oxco_ck_{Guid.NewGuid():N}.json");

                    var analysisResult = await RunCompareCoreAsync(
                        cancellationToken: batchToken,
                        resetCompareCts: false,
                        pipelinePhase: "analysis",
                        checkpointPath: checkpointPath);

                    if (pendingDavinci is not null)
                    {
                        Status = $"Compare {i}/{selectedDfs.Count}: warte auf DaVinci-Render…";
                        var davinciResult = await pendingDavinci;
                        RecordBatchCompareOutcome(davinciResult, ref ok, ref fail);
                        TryDeleteFile(pendingCheckpoint);
                        pendingCheckpoint = null;
                        pendingDavinci = null;
                    }

                    if (FilterDavinci && ShouldQueueDavinciExport(analysisResult, checkpointPath))
                    {
                        pendingCheckpoint = checkpointPath;
                        pendingDavinci = RunCompareCoreAsync(
                            cancellationToken: batchToken,
                            resetCompareCts: false,
                            pipelinePhase: "davinci_export",
                            checkpointPath: checkpointPath);
                        Status = $"Compare {i + 1}/{selectedDfs.Count}: DaVinci-Render — {selectedDfs[i].FileName}";
                    }
                    else
                    {
                        pendingDavinci = null;
                        RecordBatchCompareOutcome(analysisResult, ref ok, ref fail);
                        TryDeleteFile(checkpointPath);
                    }
                }
                else
                {
                    var result = await RunCompareCoreAsync(cancellationToken: batchToken, resetCompareCts: false);
                    RecordBatchCompareOutcome(result, ref ok, ref fail);
                }
            }

            if (pendingDavinci is not null && !batchToken.IsCancellationRequested)
            {
                Status = Loc.T("oxco.status.batchDavinciRunning");
                var lastDavinci = await pendingDavinci;
                RecordBatchCompareOutcome(lastDavinci, ref ok, ref fail);
                TryDeleteFile(pendingCheckpoint);
            }
            else if (batchToken.IsCancellationRequested)
            {
                Status = "Compare-Batch abgebrochen.";
            }
            else
            {
                Status = usePipeline
                    ? $"Batch fertig (Pipeline): {ok} OK, {fail} Fehler."
                    : $"Batch fertig: {ok} OK, {fail} Fehler.";
            }
        }
        finally
        {
            AppServices.JobProgress.EndBatch();
            _batchCompareCts?.Cancel();
            _batchCompareCts = null;
            IsCompareRunning = false;
            IsBusy = false;
        }
    }

    private CancellationTokenSource? _batchCompareCts;

    private void RecordBatchCompareOutcome(JobResult result, ref int ok, ref int fail)
    {
        if (result.Success)
        {
            ok++;
        }
        else
        {
            fail++;
        }

        if (result.ExitCode == 3)
        {
            CanRetryCompare = true;
        }
    }

    private static bool ShouldQueueDavinciExport(JobResult analysisResult, string checkpointPath)
    {
        if (!File.Exists(checkpointPath))
        {
            return false;
        }

        return analysisResult.ExitCode is 0 or 3;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    internal void ApplyOriginalSelection(OxcoCompareFileEntry entry)
    {
        _applyingOriginalSelection = true;
        try
        {
            SourcePath = entry.Path;
            _selectedOrigSignature = OxcoCompareListService.CompareMatchKey(entry);

            var ranked = OxcoCompareListService.RankDeepfakesForOriginal(entry, _dfEntries, TaggerPattern);
            RebuildDisplayLists(
                prioritizeOrig: entry,
                pinGroupForEntry: ranked.FirstOrDefault(),
                alsoPinGroupForEntry: entry);

            SelectedOriginalItem = OriginalDisplayItems.FirstOrDefault(i =>
                !i.IsGroupHeader && string.Equals(i.Entry?.Path, entry.Path, StringComparison.OrdinalIgnoreCase));

            if (ranked.Count > 0)
            {
                DeepfakePath = ranked[0].Path;
                SelectedDeepfakeItem = DeepfakeDisplayItems.FirstOrDefault(i =>
                    !i.IsGroupHeader && string.Equals(i.Entry?.Path, ranked[0].Path, StringComparison.OrdinalIgnoreCase));
                Status = ranked.Count == 1
                    ? $"Passendes Deepfake: {ranked[0].FileName}"
                    : $"{ranked.Count} passende Deepfakes — bestes: {ranked[0].FileName}";
                ScrollToDeepfakeRequested?.Invoke(ranked[0].Path);
            }
            else
            {
                Status = "Kein passendes Deepfake (ffprobe) gefunden.";
            }

            RefreshPreviewFromWorkflow();

            if (SelectedDeepfakeEntries.Count > 0)
            {
                RestoreDeepfakeMultiSelectionRequested?.Invoke(
                    SelectedDeepfakeEntries.Select(e => e.Path).ToList());
            }
        }
        finally
        {
            _applyingOriginalSelection = false;
        }
    }

    internal void ApplyDeepfakeSelection(OxcoCompareDisplayItem item)
    {
        if (item.Entry is null || item.IsGroupHeader)
        {
            return;
        }

        var entry = item.Entry;
        DeepfakePath = entry.Path;
        PreviewSideBySide = true;

        _applyingDeepfakeSelection = true;
        try
        {
            _selectedOrigSignature = OxcoCompareListService.CompareMatchKey(entry);

            var ranked = OxcoCompareListService.RankOriginalsForDeepfake(entry, _origEntries, TaggerPattern);
            RebuildDisplayLists(
                prioritizeDf: entry,
                pinGroupForEntry: ranked.FirstOrDefault(),
                alsoPinGroupForEntry: entry);

            ScrollToDeepfakeRequested?.Invoke(entry.Path);

            if (ranked.Count > 0)
            {
                SourcePath = ranked[0].Path;
                SelectedOriginalItem = OriginalDisplayItems.FirstOrDefault(i =>
                    !i.IsGroupHeader && string.Equals(i.Entry?.Path, ranked[0].Path, StringComparison.OrdinalIgnoreCase));
                Status = ranked.Count == 1
                    ? $"Passendes Original: {ranked[0].FileName}"
                    : $"{ranked.Count} passende Originale — bestes: {ranked[0].FileName}";
                ScrollToOriginalRequested?.Invoke(ranked[0].Path);
            }
            else
            {
                Status = "Kein passendes Original (ffprobe) gefunden.";
            }

            RefreshPreviewFromWorkflow();

            RestoreDeepfakeMultiSelectionRequested?.Invoke(
                SelectedDeepfakeEntries.Select(e => e.Path).ToList());
        }
        finally
        {
            _applyingDeepfakeSelection = false;
        }
    }

    private void RebuildDisplayLists(
        OxcoCompareFileEntry? prioritizeOrig = null,
        OxcoCompareFileEntry? prioritizeDf = null,
        OxcoCompareFileEntry? pinGroupForEntry = null,
        OxcoCompareFileEntry? alsoPinGroupForEntry = null)
    {
        string? dfSig = _selectedOrigSignature;
        string? dfToken = null;
        string? origSig = null;
        string? origToken = null;

        if (prioritizeOrig is not null)
        {
            dfSig = OxcoCompareListService.CompareMatchKey(prioritizeOrig);
            dfToken = OxcoCompareListService.ExtractPatternMatch(prioritizeOrig.Stem, TaggerPattern);
            _selectedOrigSignature = dfSig;
        }
        else if (prioritizeDf is not null)
        {
            origSig = OxcoCompareListService.CompareMatchKey(prioritizeDf);
            origToken = OxcoCompareListService.ExtractPatternMatch(prioritizeDf.Stem, TaggerPattern);
            _selectedOrigSignature = origSig;
        }

        var sigMap = OxcoCompareListService.BuildSignatureColorMap(_origEntries, _dfEntries);
        var (origGroups, dfGroups) = OxcoCompareListService.BuildAlignedGroups(
            _origEntries,
            _dfEntries,
            CompareGroupMode,
            CompareSortMode,
            TaggerPattern,
            dfSig,
            dfToken,
            origSig,
            origToken,
            pinGroupForEntry,
            alsoPinGroupForEntry);

        var rankedDfs = prioritizeOrig is not null
            ? OxcoCompareListService.RankDeepfakesForOriginal(prioritizeOrig, _dfEntries, TaggerPattern)
            : [];
        var bestDfPath = rankedDfs.FirstOrDefault()?.Path;

        var rankedOrigs = prioritizeDf is not null
            ? OxcoCompareListService.RankOriginalsForDeepfake(prioritizeDf, _origEntries, TaggerPattern)
            : [];
        var bestOrigPath = rankedOrigs.FirstOrDefault()?.Path;

        OriginalDisplayItems.Clear();
        foreach (var (label, items) in origGroups)
        {
            if (!string.IsNullOrEmpty(label) && CompareGroupMode != "none")
            {
                OriginalDisplayItems.Add(new OxcoCompareDisplayItem
                {
                    IsGroupHeader = true,
                    GroupLabel = OxcoCompareListService.FormatGroupLabel(CompareGroupMode, label),
                });
            }

            foreach (var e in items)
            {
                var isBest = bestOrigPath is not null
                    && string.Equals(e.Path, bestOrigPath, StringComparison.OrdinalIgnoreCase);
                OriginalDisplayItems.Add(CreateFileItem(
                    e,
                    sigMap,
                    _selectedOrigSignature,
                    TaggerPattern,
                    workflowActivePath: SourcePath,
                    isBestMatch: isBest));
            }
        }

        DeepfakeDisplayItems.Clear();
        foreach (var (label, items) in dfGroups)
        {
            if (!string.IsNullOrEmpty(label) && CompareGroupMode != "none")
            {
                DeepfakeDisplayItems.Add(new OxcoCompareDisplayItem
                {
                    IsGroupHeader = true,
                    GroupLabel = OxcoCompareListService.FormatGroupLabel(CompareGroupMode, label),
                });
            }

            foreach (var e in items)
            {
                var isBest = bestDfPath is not null
                    && string.Equals(e.Path, bestDfPath, StringComparison.OrdinalIgnoreCase);
                var item = CreateFileItem(
                    e,
                    sigMap,
                    _selectedOrigSignature,
                    TaggerPattern,
                    workflowActivePath: DeepfakePath,
                    isBestMatch: isBest);
                DeepfakeDisplayItems.Add(item);
            }
        }

        RefreshDeepfakeSelectionBorders();

        if (SelectedDeepfakeEntries.Count > 0)
        {
            RestoreDeepfakeMultiSelectionRequested?.Invoke(
                SelectedDeepfakeEntries.Select(e => e.Path).ToList());
        }
    }

    private static OxcoCompareDisplayItem CreateFileItem(
        OxcoCompareFileEntry entry,
        IReadOnlyDictionary<string, string> sigMap,
        string? matchSignature,
        string patternText,
        string? workflowActivePath = null,
        bool isBestMatch = false)
    {
        var sig = OxcoCompareListService.CompareMatchKey(entry);
        string? bg = OxcoCompareListService.DefaultRowBackground;
        if (!string.IsNullOrEmpty(sig) && sigMap.TryGetValue(sig, out var color))
        {
            bg = color;
        }
        else if (!entry.ProbeOk)
        {
            bg = OxcoCompareListService.ProbeUnknownColor;
        }

        var isMatch = !string.IsNullOrEmpty(matchSignature)
            && !string.IsNullOrEmpty(sig)
            && sig == matchSignature;

        if (isMatch)
        {
            bg = OxcoCompareListService.MatchHighlightColor;
        }

        var isActive = !string.IsNullOrWhiteSpace(workflowActivePath)
            && string.Equals(entry.Path, workflowActivePath, StringComparison.OrdinalIgnoreCase);

        string? rowBorder = null;
        var rowBorderThickness = 2.0;
        if (isActive)
        {
            rowBorder = "#0078D4";
            rowBorderThickness = 4;
        }
        else if (isBestMatch)
        {
            rowBorder = "#2ECC71";
            rowBorderThickness = 3;
        }
        else if (isMatch)
        {
            rowBorder = "#E6A100";
        }

        return new OxcoCompareDisplayItem
        {
            Entry = entry,
            Name = entry.FileName,
            Rel = entry.Rel,
            Duration = OxcoCompareListService.FormatDuration(entry.DurationSec),
            Resolution = OxcoCompareListService.FormatResolution(entry.Width, entry.Height),
            Size = OxcoCompareListService.FormatFileSize(entry.Size),
            SortTime = OxcoCompareListService.FormatSortTime(entry, patternText),
            BackgroundColor = bg,
            ForegroundColor = OxcoCompareListService.ForegroundForBackground(bg),
            IsMatchHighlight = isMatch,
            IsBestMatch = isBestMatch,
            IsWorkflowActive = isActive,
            ShowBestBadge = isBestMatch && !isActive,
            BaseRowBorderColor = rowBorder,
            BaseRowBorderThickness = rowBorderThickness,
            RowBorderColor = rowBorder,
            RowBorderThickness = rowBorderThickness,
        };
    }

    internal void RefreshDeepfakeSelectionBorders()
    {
        var selected = SelectedDeepfakeEntries
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in DeepfakeDisplayItems)
        {
            if (item.IsGroupHeader || item.Entry is null)
            {
                continue;
            }

            item.ApplyListSelection(selected.Contains(item.Entry.Path));
        }
    }

    private async Task<JobResult> RunCompareCoreAsync(
        bool retryExportOnly = false,
        CancellationToken cancellationToken = default,
        bool resetCompareCts = true,
        string? pipelinePhase = null,
        string? checkpointPath = null)
    {
        if (!File.Exists(OxcoCompareConfigReader.SettingsIniPath))
        {
            Status = $"settings.ini fehlt: {OxcoCompareConfigReader.SettingsIniPath}";
            return new JobResult { Success = false, Message = Status };
        }

        if (resetCompareCts)
        {
            _compareCts?.Cancel();
            _compareCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
        }

        var jobToken = resetCompareCts
            ? _compareCts!.Token
            : cancellationToken != default
                ? cancellationToken
                : _compareCts?.Token ?? CancellationToken.None;
        IsCompareRunning = true;
        CanRetryCompare = false;

        if (!FilterFfmpeg && !FilterDavinci)
        {
            Status = "Mindestens FFmpeg- oder DaVinci-Export aktivieren.";
            IsCompareRunning = false;
            return new JobResult { Success = false, Message = Status };
        }

        PersistSettings();
        var phaseLabel = pipelinePhase switch
        {
            "analysis" => "Analyse",
            "davinci_export" => "DaVinci",
            _ => "Compare",
        };
        Status = $"{phaseLabel}: FFmpeg={(FilterFfmpeg ? "an" : "aus")}, DaVinci={(FilterDavinci ? "an" : "aus")}…";

        Dictionary<string, object?> filterPayload;
        try
        {
            filterPayload = BuildCompareFilterPayload();
        }
        catch (Exception ex)
        {
            IsCompareRunning = false;
            Status = $"Ungültige Filter-Einstellungen: {ex.Message}";
            return new JobResult { Success = false, Message = Status };
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"hailmary_oxco_{Guid.NewGuid():N}.json");
        var payload = new Dictionary<string, object?>
        {
            ["source"] = SourcePath,
            ["deepfake"] = DeepfakePath,
            ["retry_export_only"] = retryExportOnly,
            ["pipeline_phase"] = pipelinePhase ?? "full",
            ["checkpoint_path"] = checkpointPath ?? string.Empty,
            ["filters"] = filterPayload,
        };

        JobResult result;
        try
        {
            await File.WriteAllTextAsync(configPath, System.Text.Json.JsonSerializer.Serialize(payload), jobToken);
            result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_compare_job.py",
                ["--config-json", configPath],
                jobToken);
        }
        catch (Exception ex)
        {
            result = new JobResult { Success = false, Message = ex.Message };
        }
        finally
        {
            if (resetCompareCts)
            {
                IsCompareRunning = false;
            }

            TryDeleteFile(configPath);
        }

        if (resetCompareCts)
        {
            Status = result.Message;
            if (result.ExitCode == 3)
            {
                CanRetryCompare = true;
                Status = "Export teilweise — „Export wiederholen“ nutzen.";
            }
        }

        return result;
    }

    private Dictionary<string, object?> BuildCompareFilterPayload() =>
        new()
        {
            ["export_dir"] = CompareExportDir,
            ["language"] = "de",
            ["buffer_seconds"] = double.Parse(FilterBuffer.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture),
            ["pixel_noise"] = int.Parse(FilterNoise),
            ["changed_pixels"] = int.Parse(FilterPixel),
            ["changed_pixels_max"] = int.Parse(string.IsNullOrWhiteSpace(FilterPixelMax) ? "0" : FilterPixelMax),
            ["enable_ffmpeg"] = FilterFfmpeg ? 1 : 0,
            ["enable_davinci"] = FilterDavinci ? 1 : 0,
            ["ffmpeg_target"] = FilterFfmpegTarget,
            ["davinci_timeout"] = int.Parse(FilterDavinciTimeout),
            ["export_unique"] = FilterExportUnique ? 1 : 0,
            ["davinci_api_path"] = DavinciResolvePaths.GetApiModulesPath(),
            ["davinci_render_preset"] = DavinciRenderPreset,
            ["davinci_exe_path"] = DavinciResolvePaths.GetExePath(),
            ["davinci_startup_wait"] = int.Parse(DavinciStartupWaitSeconds),
        };
}
