using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public enum MarkerPathSortMode
{
    Stash,
    Asc,
    Desc,
}

public sealed partial class MarkerAutocutOrderRowViewModel : ObservableObject
{
    public MarkerAutocutRowViewModel Marker { get; init; } = null!;

    public string Display =>
        $"{Marker.Source.MarkerTitle} @ {TimecodeHelper.FormatDisplayFromText(Marker.Source.StartSeconds)} | {Marker.ResolvedFilePath}";

    [ObservableProperty] private bool _showGroupHeader;

    [ObservableProperty] private string _groupHeader = string.Empty;
}

public partial class MarkerAutocutRowViewModel
{
    public string ResolvedFilePath { get; init; } = string.Empty;

    public string Display =>
        Loc.F(
            "markerautocut.markerRowDisplay",
            Source.MarkerTitle,
            TimecodeHelper.FormatRangeFromText(Source.StartSeconds, Source.EndSeconds),
            Source.PrimaryTag,
            Source.SceneId);

    public string GroupedDisplay =>
        $"{Source.MarkerTitle} @ {TimecodeHelper.FormatRangeFromText(Source.StartSeconds, Source.EndSeconds)} | {Source.PrimaryTag}";

    internal void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(GroupedDisplay));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        _owner?.OnMarkerSelectionChanged(this, value);
    }

    internal MarkerAutocutViewModel? _owner;

    internal void AttachOwner(MarkerAutocutViewModel owner) => _owner = owner;
}

public partial class MarkerAutocutViewModel
{
    public ObservableCollection<MarkerAutocutOrderRowViewModel> ExportOrder { get; } = [];

    [ObservableProperty] private MarkerPathSortMode _pathSortMode = MarkerPathSortMode.Stash;

    [ObservableProperty] private string _nameExclude = string.Empty;

    [ObservableProperty] private string _pathExclude = string.Empty;

    [ObservableProperty] private string _mediaRoot = string.Empty;

    [ObservableProperty] private MarkerAutocutOrderRowViewModel? _selectedOrderRow;

    private IReadOnlyList<MarkerAutocutOrderRowViewModel> _selectedOrderRows = [];

    private bool _batchOrderUpdate;

    public string PathSortLabel => PathSortMode switch
    {
        MarkerPathSortMode.Asc => Loc.T("markerautocut.pathSortAsc"),
        MarkerPathSortMode.Desc => Loc.T("markerautocut.pathSortDesc"),
        _ => Loc.T("markerautocut.pathSortStash"),
    };

    private string MapFilePath(string remotePath)
    {
        var mapped = StashPathMapper.Apply(remotePath, CurrentPathMap(), UseBackup);
        if (!string.IsNullOrWhiteSpace(MediaRoot)
            && !string.IsNullOrWhiteSpace(remotePath)
            && !Path.IsPathRooted(mapped)
            && !remotePath.StartsWith(CurrentPathMap().PathPrefixRemote, StringComparison.Ordinal))
        {
            var combined = Path.Combine(MediaRoot.TrimEnd('\\', '/'), remotePath.TrimStart('/', '\\'));
            if (File.Exists(combined))
            {
                return Path.GetFullPath(combined);
            }
        }

        return mapped;
    }

    private static bool MatchesAny(string text, string commaTerms)
    {
        if (string.IsNullOrWhiteSpace(commaTerms))
        {
            return false;
        }

        return commaTerms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool PassesClientFilters(StashExportMarkerRow row)
    {
        var haystack = $"{row.MarkerTitle} {row.PrimaryTag} {row.SecondaryTags} {row.SceneTitle}";
        if (MatchesAny(haystack, NameExclude))
        {
            return false;
        }

        if (MatchesAny(row.FilePath, PathExclude))
        {
            return false;
        }

        return true;
    }

    private IEnumerable<MarkerAutocutRowViewModel> SortedMarkers(IEnumerable<MarkerAutocutRowViewModel> rows) =>
        PathSortMode switch
        {
            MarkerPathSortMode.Asc => rows.OrderBy(r => r.ResolvedFilePath, StringComparer.OrdinalIgnoreCase),
            MarkerPathSortMode.Desc => rows.OrderByDescending(r => r.ResolvedFilePath, StringComparer.OrdinalIgnoreCase),
            _ => rows,
        };

    internal void OnMarkerSelectionChanged(MarkerAutocutRowViewModel row, bool selected)
    {
        if (selected)
        {
            if (ExportOrder.All(o => o.Marker != row))
            {
                ExportOrder.Insert(ExportOrderInsertIndex(row), new MarkerAutocutOrderRowViewModel { Marker = row });
            }
        }
        else
        {
            var existing = ExportOrder.FirstOrDefault(o => o.Marker == row);
            if (existing is not null)
            {
                ExportOrder.Remove(existing);
            }
        }

        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        if (!_batchOrderUpdate)
        {
            RefreshExportOrderHeaders();
        }
    }

    public void SetSelectedOrderRows(IReadOnlyList<MarkerAutocutOrderRowViewModel> rows)
    {
        _selectedOrderRows = rows;
        SelectedOrderRow = rows.FirstOrDefault();
    }

    internal void OnExportOrderReordered() => RefreshExportOrderHeaders();

    // Re-checking a marker should restore it to its position in the marker list
    // (same order as the left pane) instead of always appending at the bottom.
    private int ExportOrderInsertIndex(MarkerAutocutRowViewModel row)
    {
        var ordered = SortedMarkers(Markers).ToList();
        var markerIndex = ordered.IndexOf(row);
        if (markerIndex < 0)
        {
            return ExportOrder.Count;
        }

        for (var i = 0; i < ExportOrder.Count; i++)
        {
            if (ordered.IndexOf(ExportOrder[i].Marker) > markerIndex)
            {
                return i;
            }
        }

        return ExportOrder.Count;
    }

    private void RebuildExportOrderFromSelection()
    {
        ExportOrder.Clear();
        foreach (var row in SortedMarkers(Markers.Where(m => m.IsSelected)))
        {
            ExportOrder.Add(new MarkerAutocutOrderRowViewModel { Marker = row });
        }

        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        RefreshExportOrderHeaders();
    }

    // In per_file mode each source video becomes its own export, so the order pane
    // groups rows by file (like the marker list). In compilation mode everything is
    // a single output, so no per-video headers are shown.
    internal void RefreshExportOrderHeaders()
    {
        var perFile = string.Equals(ExportMode, "per_file", StringComparison.OrdinalIgnoreCase);
        string? lastPath = null;
        foreach (var entry in ExportOrder)
        {
            if (!perFile)
            {
                entry.ShowGroupHeader = false;
                entry.GroupHeader = string.Empty;
                continue;
            }

            var pathKey = entry.Marker.ResolvedFilePath;
            if (pathKey != lastPath)
            {
                lastPath = pathKey;
                var shortName = string.IsNullOrWhiteSpace(pathKey)
                    ? Loc.T("markerautocut.noPath")
                    : Path.GetFileName(pathKey);
                entry.GroupHeader = $"▶ {shortName}";
                entry.ShowGroupHeader = true;
            }
            else
            {
                entry.ShowGroupHeader = false;
                entry.GroupHeader = string.Empty;
            }
        }
    }

    partial void OnExportModeChanged(string value) => RefreshExportOrderHeaders();

    partial void OnPathSortModeChanged(MarkerPathSortMode value)
    {
        OnPropertyChanged(nameof(PathSortLabel));
        ApplyMarkerSort();
        RebuildDisplayItems();
        RebuildExportOrderFromSelection();
    }

    private void ApplyMarkerSort()
    {
        if (PathSortMode == MarkerPathSortMode.Stash || Markers.Count <= 1)
        {
            return;
        }

        var snapshot = Markers.ToList();
        Markers.Clear();
        foreach (var row in SortedMarkers(snapshot))
        {
            Markers.Add(row);
        }
    }

    [RelayCommand]
    private void CyclePathSort()
    {
        PathSortMode = PathSortMode switch
        {
            MarkerPathSortMode.Stash => MarkerPathSortMode.Asc,
            MarkerPathSortMode.Asc => MarkerPathSortMode.Desc,
            _ => MarkerPathSortMode.Stash,
        };
    }

    [RelayCommand]
    private void SyncExportOrder()
    {
        RebuildExportOrderFromSelection();
        Status = Loc.F("markerautocut.orderCount", ExportOrder.Count);
    }

    [RelayCommand]
    private void MoveOrderUp() => MoveSelectedOrderBlock(-1);

    [RelayCommand]
    private void MoveOrderDown() => MoveSelectedOrderBlock(+1);

    [RelayCommand]
    private void MoveOrderTop() => MoveSelectedOrderBlockTo(0);

    [RelayCommand]
    private void MoveOrderBottom() => MoveSelectedOrderBlockTo(ExportOrder.Count);

    [RelayCommand]
    private void RemoveFromExportOrder()
    {
        if (_selectedOrderRows.Count == 0)
        {
            return;
        }

        var toRemove = _selectedOrderRows.ToList();
        _batchOrderUpdate = true;
        try
        {
            foreach (var row in toRemove)
            {
                row.Marker.IsSelected = false;
            }
        }
        finally
        {
            _batchOrderUpdate = false;
        }

        SetSelectedOrderRows([]);
        RefreshExportOrderHeaders();
    }

    private void MoveSelectedOrderBlock(int direction)
    {
        if (_selectedOrderRows.Count == 0)
        {
            return;
        }

        var ordered = _selectedOrderRows
            .OrderBy(r => ExportOrder.IndexOf(r))
            .ToList();
        var indices = ordered.Select(r => ExportOrder.IndexOf(r)).ToList();
        var first = indices[0];
        var last = indices[^1];

        if (direction < 0 && first == 0)
        {
            return;
        }

        if (direction > 0 && last >= ExportOrder.Count - 1)
        {
            return;
        }

        MoveSelectedOrderBlockTo(direction < 0 ? first - 1 : first + 1);
    }

    private void MoveSelectedOrderBlockTo(int insertAt)
    {
        if (_selectedOrderRows.Count == 0)
        {
            return;
        }

        var ordered = _selectedOrderRows
            .OrderBy(r => ExportOrder.IndexOf(r))
            .ToList();
        var indices = ordered.Select(r => ExportOrder.IndexOf(r)).ToList();

        foreach (var idx in indices.OrderByDescending(i => i))
        {
            ExportOrder.RemoveAt(idx);
        }

        insertAt = Math.Clamp(insertAt, 0, ExportOrder.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            ExportOrder.Insert(insertAt + i, ordered[i]);
        }

        RefreshExportOrderHeaders();
    }
}
