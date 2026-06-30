using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    private CancellationTokenSource? _compareCts;

    [ObservableProperty] private bool _canRetryCompare;

    [ObservableProperty] private bool _isCompareRunning;

    public IReadOnlyList<OxcoCompareFileEntry> SelectedDeepfakeEntries { get; private set; } = [];

    [ObservableProperty] private int _selectedDeepfakeCount;

    public IReadOnlyList<string> SelectedDeepfakePaths =>
        SelectedDeepfakeEntries.Select(e => e.Path).ToList();

    public string MoveSelectedDeepfakesToBitrateLabel =>
        SelectedDeepfakeCount <= 1
            ? Loc.T("oxco.moveToBitrateShort")
            : Loc.F("oxco.moveToBitrate", SelectedDeepfakeCount);

    public string MoveSelectedDeepfakesToTaggerLabel =>
        SelectedDeepfakeCount <= 1
            ? Loc.T("oxco.moveToTaggerShort")
            : Loc.F("oxco.moveToTagger", SelectedDeepfakeCount);

    internal void SetSelectedDeepfakeEntries(IReadOnlyList<OxcoCompareFileEntry> entries)
    {
        SelectedDeepfakeEntries = entries;
        SelectedDeepfakeCount = entries.Count;
        OnPropertyChanged(nameof(SelectedDeepfakePaths));
        OnPropertyChanged(nameof(MoveSelectedDeepfakesToBitrateLabel));
        OnPropertyChanged(nameof(MoveSelectedDeepfakesToTaggerLabel));
        MoveSelectedDeepfakesToBitrateInCommand.NotifyCanExecuteChanged();
        MoveSelectedDeepfakesToTaggerInCommand.NotifyCanExecuteChanged();
        RefreshDeepfakeSelectionBorders();
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedDeepfakes))]
    private async Task MoveSelectedDeepfakesToBitrateInAsync() =>
        await MoveDeepfakesToBitrateInAsync(SelectedDeepfakePaths);

    [RelayCommand(CanExecute = nameof(CanMoveSelectedDeepfakes))]
    private async Task MoveSelectedDeepfakesToTaggerInAsync() =>
        await MoveDeepfakesToTaggerInAsync(SelectedDeepfakePaths);

    private bool CanMoveSelectedDeepfakes() => SelectedDeepfakeCount > 0;

    [RelayCommand(CanExecute = nameof(CanStopCompare))]
    private void StopCompare()
    {
        _compareCts?.Cancel();
        _batchCompareCts?.Cancel();
        Status = Loc.T("oxco.status.compareAborted");
    }

    private bool CanStopCompare() => IsCompareRunning;

    [RelayCommand(CanExecute = nameof(CanRetryCompare))]
    private async Task RetryCompareAsync()
    {
        if (!CanRetryCompare)
        {
            return;
        }

        CanRetryCompare = false;
        IsBusy = true;
        Status = Loc.T("oxco.status.exportRetrying");
        try
        {
            await RunCompareCoreAsync(retryExportOnly: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    internal async Task MoveDeepfakesToBitrateInAsync(IReadOnlyList<string>? paths)
    {
        await MoveFilesAsync(paths, BitrateInDir, "oxco.bitrateInput", removeFromDeepfakeList: true);
    }

    [RelayCommand]
    internal async Task MoveDeepfakesToTaggerInAsync(IReadOnlyList<string>? paths)
    {
        await MoveFilesAsync(paths, TaggerInDir, "oxco.taggerInput", removeFromDeepfakeList: true);
    }

    [RelayCommand]
    private void RemoveDeepfakesFromList(IReadOnlyList<string>? paths) =>
        RemoveDeepfakesFromListInternal(paths);

    private void RemoveDeepfakesFromListInternal(IReadOnlyList<string>? paths)
    {
        var set = NormalizePaths(paths);
        if (set.Count == 0)
        {
            return;
        }

        _dfEntries.RemoveAll(e => set.Contains(NormalizePath(e.Path)));
        if (set.Contains(NormalizePath(DeepfakePath)))
        {
            DeepfakePath = string.Empty;
        }

        RebuildDisplayLists();
        SetSelectedDeepfakeEntries([]);
        ClearDeepfakeListSelectionRequested?.Invoke();
        Status = Loc.F("oxco.status.removedFromDeepfakeList", set.Count);
    }

    [RelayCommand]
    private async Task RecycleDeepfakesAsync(IReadOnlyList<string>? paths)
    {
        var list = paths?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (list.Count == 0)
        {
            Status = Loc.T("oxco.status.noFilesSelected");
            return;
        }

        IsBusy = true;
        try
        {
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_recycle_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(
                configPath,
                System.Text.Json.JsonSerializer.Serialize(new { paths = list }));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_recycle_job.py",
                ["--config-json", configPath]);
            try
            {
                File.Delete(configPath);
            }
            catch
            {
                // ignore
            }

            if (result.Success)
            {
                RemoveDeepfakesFromListInternal(list);
                Status = Loc.F("oxco.status.recycledToTrash", list.Count);
            }
            else
            {
                Status = result.Message;
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
    private async Task MoveBitrateRowsToTaggerAsync()
    {
        if (string.IsNullOrWhiteSpace(TaggerInDir) || !Directory.Exists(TaggerInDir))
        {
            Status = Loc.T("oxco.status.taggerInputMissing");
            return;
        }

        var taggerRoot = Path.GetFullPath(TaggerInDir.Trim());
        var bitrateInRoot = string.IsNullOrWhiteSpace(BitrateInDir) ? string.Empty : Path.GetFullPath(BitrateInDir.Trim());
        var bitrateOutRoot = string.IsNullOrWhiteSpace(BitrateOutDir) ? string.Empty : Path.GetFullPath(BitrateOutDir.Trim());

        if (!string.IsNullOrEmpty(bitrateInRoot) &&
            string.Equals(taggerRoot, bitrateInRoot, StringComparison.OrdinalIgnoreCase))
        {
            Status = Loc.T("oxco.status.taggerSameAsBitrate");
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(BrSuffix) ? "_bitrate" : BrSuffix.Trim();
        var pathsToMove = new List<string>();
        var sourcesToDelete = new List<string>();

        foreach (var row in BitrateRows.Where(r => r.Action == "convert"))
        {
            var outputPath = OxcoBitratePathHelper.ResolveConvertedOutputPath(
                row.Path, BitrateInDir, BitrateOutDir, suffix, BrOutputMp4);

            if (outputPath is not null && File.Exists(outputPath))
            {
                if (!string.Equals(taggerRoot, bitrateOutRoot, StringComparison.OrdinalIgnoreCase))
                {
                    pathsToMove.Add(outputPath);
                }

                if (File.Exists(row.Path))
                {
                    sourcesToDelete.Add(row.Path);
                }

                continue;
            }

            if (File.Exists(row.Path))
            {
                pathsToMove.Add(row.Path);
            }
        }

        foreach (var row in BitrateRows.Where(r => r.Action != "convert" && File.Exists(r.Path)))
        {
            pathsToMove.Add(row.Path);
        }

        if (pathsToMove.Count == 0 && sourcesToDelete.Count == 0)
        {
            if (!string.IsNullOrEmpty(bitrateOutRoot) &&
                string.Equals(taggerRoot, bitrateOutRoot, StringComparison.OrdinalIgnoreCase))
            {
                Status = Loc.T("oxco.status.alreadyInTaggerFolder");
            }
            else
            {
                Status = Loc.T("oxco.status.nothingToMove");
            }

            return;
        }

        if (pathsToMove.Count > 0)
        {
            await MoveFilesAsync(pathsToMove, TaggerInDir, "oxco.taggerInput", removeFromDeepfakeList: false);
        }

        var deleted = 0;
        foreach (var src in sourcesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(src))
            {
                TryDeleteBitrateSource(src);
                if (!File.Exists(src))
                {
                    deleted++;
                }
            }
        }

        PruneBitrateRowsAfterConvert();

        if (pathsToMove.Count == 0 && deleted > 0)
        {
            Status = Loc.F("oxco.status.originalsRemovedFromBitrate", deleted);
            await RefreshTaggerListCoreAsync(logCount: false);
        }
    }

    private async Task MoveFilesAsync(
        IReadOnlyList<string>? paths,
        string destDir,
        string destLabel,
        bool removeFromDeepfakeList)
    {
        if (string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(destDir))
        {
            Status = Loc.F("oxco.status.destFolderMissing", Loc.T(destLabel));
            return;
        }

        var list = paths?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (list.Count == 0)
        {
            Status = Loc.T("oxco.status.noFilesSelected");
            return;
        }

        Directory.CreateDirectory(destDir);
        var moved = new List<string>();
        var errors = new List<string>();
        foreach (var src in list)
        {
            try
            {
                var target = Path.Combine(destDir, Path.GetFileName(src));
                if (File.Exists(target))
                {
                    target = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(src)}_{Guid.NewGuid():N}{Path.GetExtension(src)}");
                }

                File.Move(src, target);
                moved.Add(src);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
            }
        }

        if (moved.Count == 0)
        {
            Status = errors.Count > 0
                ? Loc.F("oxco.status.moveFailed", errors[0])
                : Loc.T("oxco.status.noFilesSelected");
            return;
        }

        if (removeFromDeepfakeList)
        {
            RemoveDeepfakesFromListInternal(moved);
        }

        Status = errors.Count > 0
            ? Loc.F("oxco.status.filesMovedWithErrors", moved.Count, Loc.T(destLabel), errors.Count, errors[0])
            : Loc.F("oxco.status.filesMoved", moved.Count, Loc.T(destLabel));
        if (string.Equals(destLabel, "oxco.taggerInput", StringComparison.Ordinal))
        {
            await RefreshTaggerListCoreAsync(logCount: false);
        }
    }

    private static HashSet<string> NormalizePaths(IReadOnlyList<string>? paths) =>
        paths is null ? [] : paths.Select(NormalizePath).Where(p => !string.IsNullOrEmpty(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    partial void OnIsCompareRunningChanged(bool value)
    {
        StopCompareCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
    }

    partial void OnCanRetryCompareChanged(bool value) =>
        RetryCompareCommand.NotifyCanExecuteChanged();
}
