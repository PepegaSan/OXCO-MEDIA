using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>All markers of one source video, shown as a collapsible group.</summary>
public sealed partial class MarkerAutocutFileGroup : ObservableObject
{
    private readonly MarkerAutocutViewModel _owner;

    public MarkerAutocutFileGroup(MarkerAutocutViewModel owner, string filePath, IReadOnlyList<MarkerAutocutRowViewModel> markers)
    {
        _owner = owner;
        FilePath = filePath;
        AllMarkers = markers;
        foreach (var m in markers)
        {
            VisibleMarkers.Add(m);
        }
    }

    public string FilePath { get; }

    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? Loc.T("markerautocut.noPath") : Path.GetFileName(FilePath);

    public IReadOnlyList<MarkerAutocutRowViewModel> AllMarkers { get; }

    /// <summary>Markers matching the quick search.</summary>
    public ObservableCollection<MarkerAutocutRowViewModel> VisibleMarkers { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    public string ChevronGlyph => IsExpanded ? "" : "";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));

    /// <summary>Tri-state: all / none / some markers of the file selected.</summary>
    public bool? AllSelected
    {
        get
        {
            var selected = AllMarkers.Count(m => m.IsSelected);
            return selected == 0 ? false : selected == AllMarkers.Count ? true : null;
        }
        set
        {
            // null comes back from the TwoWay binding when only some markers are selected —
            // that is a display state, not a click; treating it as "all" re-checked the marker just unchecked.
            if (value is bool all)
            {
                _owner.SetGroupSelection(this, all);
            }
        }
    }

    public string Summary
    {
        get
        {
            var selected = AllMarkers.Where(m => m.IsSelected).ToList();
            return Loc.F(
                "markerautocut.groupSummary",
                selected.Count,
                AllMarkers.Count,
                MarkerAutocutViewModel.FormatDuration(selected.Sum(m => m.EffectiveDuration(_owner.MinSegmentSeconds))));
        }
    }

    public void NotifySelection()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public partial class MarkerAutocutRowViewModel
{
    private double? _start;
    private double? _end;

    public double StartValue => _start ??= Parse(Source.StartSeconds) ?? 0;

    public double? EndValue => _end ??= Parse(Source.EndSeconds);

    /// <summary>Stash markers often have no end — the export then cuts the minimum segment.</summary>
    public bool HasEnd => EndValue is { } e && e > StartValue;

    public double EffectiveDuration(double minSegment) =>
        HasEnd ? EndValue!.Value - StartValue : Math.Max(0.1, minSegment);

    public string TimeRangeText => HasEnd
        ? $"{MarkerAutocutViewModel.FormatClock(StartValue)} – {MarkerAutocutViewModel.FormatClock(EndValue!.Value)}"
        : MarkerAutocutViewModel.FormatClock(StartValue);

    public string DurationText => HasEnd
        ? MarkerAutocutViewModel.FormatDuration(EndValue!.Value - StartValue)
        : Loc.T("markerautocut.noEnd");

    public string TitleText => string.IsNullOrWhiteSpace(Source.MarkerTitle) ? Source.PrimaryTag : Source.MarkerTitle;

    public string TagText => string.IsNullOrWhiteSpace(Source.MarkerTitle) ? string.Empty : Source.PrimaryTag;

    public string FileName => string.IsNullOrWhiteSpace(ResolvedFilePath) ? string.Empty : Path.GetFileName(ResolvedFilePath);

    private static double? Parse(string raw) =>
        !string.IsNullOrWhiteSpace(raw) && TimecodeHelper.TryParseFlexible(raw, out var sec, out _) ? sec : null;
}

public partial class MarkerAutocutViewModel
{
    private static readonly string[] ModeIds = ["per_file", "compilation"];

    public ObservableCollection<MarkerAutocutFileGroup> FileGroups { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private string _selectionSummary = string.Empty;

    [ObservableProperty] private string _exportSummary = string.Empty;

    [ObservableProperty] private string _missingEndSummary = string.Empty;

    public bool HasMarkers => Markers.Count > 0;

    public bool HasMissingEnd => !string.IsNullOrEmpty(MissingEndSummary);

    /// <summary>ComboBox index for the export mode.</summary>
    public int ModeIndex
    {
        get => Math.Max(0, Array.IndexOf(ModeIds, ExportMode));
        set
        {
            if (value >= 0 && value < ModeIds.Length)
            {
                ExportMode = ModeIds[value];
            }
        }
    }

    /// <summary>ComboBox index for the file sort order.</summary>
    public int SortIndex
    {
        get => (int)PathSortMode;
        set
        {
            if (value is >= 0 and <= 2)
            {
                PathSortMode = (MarkerPathSortMode)value;
            }
        }
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnMissingEndSummaryChanged(string value) => OnPropertyChanged(nameof(HasMissingEnd));

    partial void OnMinSegmentSecondsChanged(double value)
    {
        UpdateSummaries();
        RefreshStoryboard();
    }

    private void RebuildDisplayItems()
    {
        FileGroups.Clear();
        var groups = SortedMarkers(Markers)
            .GroupBy(r => r.ResolvedFilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            FileGroups.Add(new MarkerAutocutFileGroup(this, g.Key, g.ToList()));
        }

        // Many files: start collapsed so the list stays scannable.
        if (FileGroups.Count > 8)
        {
            foreach (var group in FileGroups)
            {
                group.IsExpanded = false;
            }
        }

        OnPropertyChanged(nameof(HasMarkers));
        ApplySearch();
        UpdateSummaries();
    }

    private void ApplySearch()
    {
        var term = SearchText.Trim();
        foreach (var group in FileGroups)
        {
            group.VisibleMarkers.Clear();
            foreach (var m in group.AllMarkers)
            {
                if (term.Length == 0
                    || m.Source.MarkerTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || m.Source.PrimaryTag.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || m.Source.SecondaryTags.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || group.FileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    group.VisibleMarkers.Add(m);
                }
            }

            if (term.Length > 0 && group.VisibleMarkers.Count > 0)
            {
                group.IsExpanded = true;
            }
        }
    }

    internal void SetGroupSelection(MarkerAutocutFileGroup group, bool selected)
    {
        _batchOrderUpdate = true;
        try
        {
            foreach (var m in group.AllMarkers)
            {
                m.IsSelected = selected;
            }
        }
        finally
        {
            _batchOrderUpdate = false;
        }

        RefreshExportOrderHeaders();
        group.NotifySelection();
        UpdateSummaries();
    }

    private void NotifyGroupOf(MarkerAutocutRowViewModel row)
    {
        var group = FileGroups.FirstOrDefault(g => g.AllMarkers.Contains(row));
        group?.NotifySelection();
    }

    internal void UpdateSummaries()
    {
        if (Markers.Count == 0)
        {
            SelectionSummary = string.Empty;
            ExportSummary = Loc.T("markerautocut.exportSummaryEmpty");
            MissingEndSummary = string.Empty;
            return;
        }

        var selected = ExportOrder.Select(o => o.Marker).ToList();
        var files = selected.Select(m => m.ResolvedFilePath ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var total = selected.Sum(m => m.EffectiveDuration(MinSegmentSeconds));
        SelectionSummary = Loc.F("markerautocut.selectionSummary", selected.Count, Markers.Count, files, FormatDuration(total));

        if (selected.Count == 0)
        {
            ExportSummary = Loc.T("markerautocut.exportSummaryEmpty");
        }
        else if (IsPerFileMode())
        {
            ExportSummary = Loc.F(IsFfmpegEngine ? "markerautocut.exportSummaryPerFileFfmpeg" : "markerautocut.exportSummaryPerFile", files, selected.Count);
        }
        else
        {
            ExportSummary = Loc.F(IsFfmpegEngine ? "markerautocut.exportSummaryCompilationFfmpeg" : "markerautocut.exportSummaryCompilation", selected.Count, FormatDuration(total));
        }

        var missing = selected.Count(m => !m.HasEnd);
        MissingEndSummary = missing == 0
            ? string.Empty
            : Loc.F("markerautocut.missingEndInfo", missing, MinSegmentSeconds.ToString("0.#", CultureInfo.CurrentCulture));
    }

    internal static string FormatClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    internal static string FormatDuration(double seconds) =>
        seconds < 60
            ? seconds.ToString("0.#", CultureInfo.CurrentCulture) + " s"
            : FormatClock(seconds);
}
