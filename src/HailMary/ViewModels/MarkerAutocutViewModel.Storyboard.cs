using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>One source video in "one timeline per video" mode — moved as a whole.</summary>
public sealed partial class MarkerAutocutOrderFileCard : ObservableObject
{
    public MarkerAutocutOrderFileCard(string filePath, IReadOnlyList<MarkerAutocutRowViewModel> markers, int index, string colorHex, double minSegment)
    {
        FilePath = filePath;
        Markers = markers;
        Index = index;
        ColorHex = colorHex;
        var total = markers.Sum(m => m.EffectiveDuration(minSegment));
        DurationSeconds = total;
        Summary = Loc.F("markerautocut.cardSummary", markers.Count, MarkerAutocutViewModel.FormatDuration(total));
        var titles = markers.Take(3).Select(m => m.TitleText).ToList();
        Preview = string.Join(" · ", titles) + (markers.Count > 3 ? " …" : string.Empty);
    }

    public string FilePath { get; }

    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? Loc.T("markerautocut.noPath") : Path.GetFileName(FilePath);

    public IReadOnlyList<MarkerAutocutRowViewModel> Markers { get; }

    public int Index { get; }

    public string ColorHex { get; }

    public double DurationSeconds { get; }

    public string Summary { get; }

    public string Preview { get; }
}

/// <summary>A block of the proportional result strip above the order list.</summary>
public sealed record MarkerAutocutStripBlock(double Seconds, string ColorHex, string Tooltip);

public partial class MarkerAutocutOrderRowViewModel
{
    [ObservableProperty] private int _index;

    [ObservableProperty] private string _colorHex = "#888888";

    public string DurationText => Marker.DurationText;
}

public partial class MarkerAutocutViewModel
{
    // Distinct, readable on dark and light backgrounds.
    private static readonly string[] FilePalette =
    [
        "#4FC3F7", "#FF8A65", "#81C784", "#BA68C8", "#FFD54F", "#4DB6AC", "#F06292", "#9575CD", "#AED581", "#90A4AE",
    ];

    public ObservableCollection<MarkerAutocutOrderFileCard> OrderFiles { get; } = [];

    public IReadOnlyList<MarkerAutocutStripBlock> StripBlocks { get; private set; } = [];

    public bool IsCompilationMode => !IsPerFileMode();

    public bool HasExportOrder => ExportOrder.Count > 0;

    /// <summary>Rebuild cards, numbers, colors and the strip after any order change.</summary>
    private void RefreshStoryboard()
    {
        var fileIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ExportOrder)
        {
            var key = row.Marker.ResolvedFilePath ?? string.Empty;
            if (!fileIndex.ContainsKey(key))
            {
                fileIndex[key] = fileIndex.Count;
            }
        }

        string ColorFor(string? path) => FilePalette[fileIndex[path ?? string.Empty] % FilePalette.Length];

        for (var i = 0; i < ExportOrder.Count; i++)
        {
            ExportOrder[i].Index = i + 1;
            ExportOrder[i].ColorHex = ColorFor(ExportOrder[i].Marker.ResolvedFilePath);
        }

        OrderFiles.Clear();
        foreach (var group in ExportOrder.GroupBy(o => o.Marker.ResolvedFilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            OrderFiles.Add(new MarkerAutocutOrderFileCard(
                group.Key,
                group.Select(o => o.Marker).ToList(),
                OrderFiles.Count + 1,
                ColorFor(group.Key),
                MinSegmentSeconds));
        }

        StripBlocks = IsPerFileMode()
            ? OrderFiles.Select(c => new MarkerAutocutStripBlock(
                c.DurationSeconds,
                c.ColorHex,
                $"{c.Index}. {c.FileName} · {c.Summary}")).ToList()
            : ExportOrder.Select(o => new MarkerAutocutStripBlock(
                o.Marker.EffectiveDuration(MinSegmentSeconds),
                o.ColorHex,
                $"{o.Index}. {o.Title} · {o.Marker.DurationText} · {o.Marker.FileName}")).ToList();
        OnPropertyChanged(nameof(StripBlocks));
        OnPropertyChanged(nameof(HasExportOrder));
    }

    // --- whole videos (per-file mode)

    [RelayCommand]
    private void MoveFileCardUp(MarkerAutocutOrderFileCard? card) => MoveFileCard(card, -1);

    [RelayCommand]
    private void MoveFileCardDown(MarkerAutocutOrderFileCard? card) => MoveFileCard(card, 1);

    [RelayCommand]
    private void RemoveFileCard(MarkerAutocutOrderFileCard? card)
    {
        if (card is null)
        {
            return;
        }

        var group = FileGroups.FirstOrDefault(g => string.Equals(g.FilePath, card.FilePath, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            SetGroupSelection(group, false);
        }
    }

    private void MoveFileCard(MarkerAutocutOrderFileCard? card, int delta)
    {
        if (card is null)
        {
            return;
        }

        var order = OrderFiles.Select(c => c.FilePath).ToList();
        var idx = order.FindIndex(p => string.Equals(p, card.FilePath, StringComparison.OrdinalIgnoreCase));
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= order.Count)
        {
            return;
        }

        (order[idx], order[target]) = (order[target], order[idx]);
        ApplyFileOrder(order);
    }

    /// <summary>After drag &amp; drop of video cards: take the cards' new order.</summary>
    public void ApplyFileCardOrder() => ApplyFileOrder(OrderFiles.Select(c => c.FilePath).ToList());

    // --- single clips (compilation mode)

    [RelayCommand]
    private void MoveClipUp(MarkerAutocutOrderRowViewModel? row) => MoveClip(row, -1);

    [RelayCommand]
    private void MoveClipDown(MarkerAutocutOrderRowViewModel? row) => MoveClip(row, 1);

    [RelayCommand]
    private void RemoveClip(MarkerAutocutOrderRowViewModel? row)
    {
        if (row is not null)
        {
            row.Marker.IsSelected = false;
        }
    }

    private void MoveClip(MarkerAutocutOrderRowViewModel? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var idx = ExportOrder.IndexOf(row);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= ExportOrder.Count)
        {
            return;
        }

        ExportOrder.Move(idx, target);
        RefreshExportOrderHeaders();
    }

    /// <summary>Quick sorts for the compilation order.</summary>
    [RelayCommand]
    private void SortClips(string? mode)
    {
        var markers = ExportOrder.Select(o => o.Marker).ToList();
        IEnumerable<MarkerAutocutRowViewModel> sorted = mode switch
        {
            "tag" => markers
                .OrderBy(m => m.Source.PrimaryTag, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(m => m.ResolvedFilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.StartValue),
            "short" => markers.OrderBy(m => m.EffectiveDuration(MinSegmentSeconds)),
            "long" => markers.OrderByDescending(m => m.EffectiveDuration(MinSegmentSeconds)),
            "shuffle" => markers.OrderBy(_ => Random.Shared.Next()),
            _ => SortedMarkers(markers.Where(m => m.IsSelected)),
        };
        ReplaceExportOrder(sorted.ToList());
        Status = Loc.F("markerautocut.orderCount", ExportOrder.Count);
    }

    /// <summary>After drag &amp; drop of clip cards.</summary>
    public void OnClipsReordered() => RefreshExportOrderHeaders();
}
