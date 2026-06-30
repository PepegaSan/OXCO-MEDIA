using System.Collections.ObjectModel;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed class MarkerAutocutDisplayItem
{
    public bool IsFileHeader { get; init; }

    public string HeaderText { get; init; } = string.Empty;

    public MarkerAutocutRowViewModel? Marker { get; init; }
}

public partial class MarkerAutocutViewModel
{
    public ObservableCollection<MarkerAutocutDisplayItem> DisplayItems { get; } = [];

    private void RebuildDisplayItems()
    {
        DisplayItems.Clear();
        var ordered = SortedMarkers(Markers).ToList();
        string? lastPath = null;
        foreach (var row in ordered)
        {
            var pathKey = row.ResolvedFilePath;
            if (pathKey != lastPath)
            {
                lastPath = pathKey;
                var shortName = string.IsNullOrWhiteSpace(pathKey)
                    ? Loc.T("markerautocut.noPath")
                    : Path.GetFileName(pathKey);
                DisplayItems.Add(new MarkerAutocutDisplayItem
                {
                    IsFileHeader = true,
                    HeaderText = string.IsNullOrWhiteSpace(pathKey)
                        ? $"▶ {Loc.T("markerautocut.noPath")}"
                        : $"▶ {shortName}",
                });
            }

            DisplayItems.Add(new MarkerAutocutDisplayItem { Marker = row });
        }
    }
}
