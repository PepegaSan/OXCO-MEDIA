using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class IntroCutterViewModel : IVideoBatchHost
{
    private readonly List<IntroBatchEntry> _batchEntries = [];
    private readonly HashSet<IntroBatchEntry> _selectedBatchEntries = [];

    private void InitializeBatchFromStorage()
    {
        _batchEntries.Clear();
        var paths = VideoPathDropHelper.ExpandVideoPaths(GetStoredInputPaths()).ToList();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            AddEntry(path, includeForCut: paths.Count == 1);
        }

        RefreshBatchFiles();
        EnsurePreviewLoaded();
    }

    public void AddDroppedPaths(IEnumerable<string> paths) => AddBatchPaths(paths, fromFolderImport: false);

    public void AddBatchPaths(IEnumerable<string> paths, bool fromFolderImport = false)
    {
        var added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                foreach (var file in VideoPathDropHelper.ExpandVideoPaths([full]))
                {
                    if (TryAddEntry(file, includeForCut: false))
                    {
                        added = true;
                    }
                }

                continue;
            }

            if (TryAddEntry(full, includeForCut: !fromFolderImport))
            {
                added = true;
            }
        }

        if (!added)
        {
            return;
        }

        SortEntries();
        PersistBatchQueue();
        RefreshBatchFiles();
        EnsurePreviewLoaded();
    }

    private bool TryAddEntry(string path, bool includeForCut)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (_batchEntries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        AddEntry(path, includeForCut);
        return true;
    }

    private void AddEntry(string path, bool includeForCut)
    {
        var entry = new IntroBatchEntry(path) { IsIncluded = includeForCut };
        entry.PropertyChanged += BatchEntry_OnPropertyChanged;
        _batchEntries.Add(entry);
    }

    private void BatchEntry_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntroBatchEntry.IsIncluded))
        {
            NotifyBatchMetricsChanged();
        }
    }

    private void SortEntries() =>
        _batchEntries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

    public void UpdateBatchSelection(IEnumerable<IntroBatchEntry> entries)
    {
        _selectedBatchEntries.Clear();
        foreach (var entry in entries)
        {
            _selectedBatchEntries.Add(entry);
        }

        RemoveSelectedBatchCommand.NotifyCanExecuteChanged();
    }

    public void LoadPreviewForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewVideoPath = string.Empty;
            HasVideo = false;
            UpdateCutVisualization();
            return;
        }

        PreviewVideoPath = path;
        HasVideo = true;
    }

    private void EnsurePreviewLoaded()
    {
        if (!string.IsNullOrWhiteSpace(PreviewVideoPath) && File.Exists(PreviewVideoPath))
        {
            return;
        }

        var preview = _selectedBatchEntries.LastOrDefault()?.Path
            ?? _batchEntries.FirstOrDefault(e => e.IsIncluded)?.Path
            ?? _batchEntries.FirstOrDefault()?.Path;

        if (!string.IsNullOrWhiteSpace(preview))
        {
            LoadPreviewForPath(preview);
        }
    }

    private void PersistBatchQueue()
    {
        AppServices.Session.UpdateToolIo(_tool.Id, state =>
        {
            state.InputPaths = _batchEntries.Select(e => e.Path).ToList();
            state.InputPath = _batchEntries.FirstOrDefault()?.Path;
        });
    }

    private IReadOnlyList<string> GetIncludedPaths() =>
        _batchEntries.Where(e => e.IsIncluded).Select(e => e.Path).ToList();

    private void NotifyBatchMetricsChanged()
    {
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        UpdateBatchSummaryText();
    }

    private void UpdateBatchSummaryText()
    {
        var included = _batchEntries.Count(e => e.IsIncluded);
        BatchSummary = _batchEntries.Count switch
        {
            0 => "Keine Videos",
            1 => included > 0 ? "1 Video — zum Schnitt markiert" : "1 Video — nicht markiert",
            _ => $"{_batchEntries.Count} Videos — {included} zum Schnitt markiert",
        };
    }

    [RelayCommand]
    private async Task AddVideosAsync()
    {
        var paths = await FilePickerHelper.PickVideosAsync();
        if (paths.Count > 0)
        {
            AddBatchPaths(paths, fromFolderImport: false);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await FolderPickerHelper.PickFolderAsync(
            _batchEntries.FirstOrDefault() is { } first
                ? Path.GetDirectoryName(first.Path)
                : PreviewVideoPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            AddBatchPaths([folder], fromFolderImport: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBatchItems))]
    private void RemoveSelectedBatch()
    {
        if (_selectedBatchEntries.Count == 0)
        {
            return;
        }

        foreach (var entry in _selectedBatchEntries.ToList())
        {
            entry.PropertyChanged -= BatchEntry_OnPropertyChanged;
            _batchEntries.Remove(entry);
        }

        _selectedBatchEntries.Clear();
        PersistBatchQueue();
        RefreshBatchFiles();
        EnsurePreviewLoaded();
        RemoveSelectedBatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearBatch()
    {
        foreach (var entry in _batchEntries)
        {
            entry.PropertyChanged -= BatchEntry_OnPropertyChanged;
        }

        _batchEntries.Clear();
        _selectedBatchEntries.Clear();
        PersistBatchQueue();
        RefreshBatchFiles();
        LoadPreviewForPath(string.Empty);
        RemoveSelectedBatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void MarkAllForCut()
    {
        foreach (var entry in _batchEntries)
        {
            entry.IsIncluded = true;
        }
    }

    [RelayCommand]
    private void UnmarkAllForCut()
    {
        foreach (var entry in _batchEntries)
        {
            entry.IsIncluded = false;
        }
    }

    private bool HasSelectedBatchItems => _selectedBatchEntries.Count > 0;
}
