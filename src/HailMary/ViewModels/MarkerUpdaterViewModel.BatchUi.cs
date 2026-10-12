using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class MarkerBatchSceneRowViewModel
{
    internal MarkerUpdaterViewModel? Owner { get; init; }

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => Owner?.OnBatchRowChecked();

    public string TitleText => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileName(Path) : Title;

    public string TagsText => Tags.Count == 0
        ? Loc.T("markerupdater.batchNoTags")
        : string.Join(" · ", Tags.Select(t => t.Name).Take(8)) + (Tags.Count > 8 ? $" … (+{Tags.Count - 8})" : string.Empty);
}

public sealed partial class MarkerCleanupRowViewModel
{
    public string SceneLine
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Source.SceneTitle) ? Loc.T("markerupdater.noTitle") : Source.SceneTitle;
            return $"{title}  ·  #{Source.SceneId}";
        }
    }

    public string MarkerLine
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Source.MarkerTitle) ? Source.PrimaryTagName : Source.MarkerTitle;
            var range = Source.EndSeconds > 0
                ? TimecodeHelper.FormatDisplayRange(Source.Seconds, Source.EndSeconds)
                : TimecodeHelper.FormatDisplay(Source.Seconds);
            return Loc.F("markerupdater.cleanupRow", label, range, TimecodeHelper.FormatDisplay(Source.Duration));
        }
    }

    /// <summary>How far the marker reaches past the end of the video.</summary>
    public string OverrunText
    {
        get
        {
            var end = Math.Max(Source.Seconds, Source.EndSeconds);
            return $"+{Math.Max(0, end - Source.Duration):0.#} s";
        }
    }
}

/// <summary>Batch tab: explicit search mode / scope as indices and a live selection count.</summary>
public partial class MarkerUpdaterViewModel
{
    [ObservableProperty] private int _batchSelectionCount;

    /// <summary>Only tags the scenes in scope actually carry (most common first).</summary>
    public ObservableCollection<string> BatchRemovableTags { get; } = [];

    [ObservableProperty] private string _batchRemovableHint = string.Empty;

    private void RefreshBatchRemovableTags()
    {
        var targets = GetBatchTargetScenes();
        var names = targets
            .SelectMany(s => s.Tags.Select(t => t.Name))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();

        BatchRemovableTags.Clear();
        foreach (var (name, _) in names)
        {
            BatchRemovableTags.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(BatchRemoveTagName)
            && !BatchRemovableTags.Contains(BatchRemoveTagName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            BatchRemoveTagName = string.Empty;
        }

        BatchRemovableHint = targets.Count == 0
            ? Loc.T(BatchScopeSelectedOnly ? "markerupdater.batchRemovableNoneSelected" : "markerupdater.batchRemovableNoResults")
            : names.Count == 0
                ? Loc.T("markerupdater.batchRemovableNoTags")
                : Loc.F("markerupdater.batchRemovableHint", names.Count, targets.Count);
    }

    public int BatchSearchModeIndex
    {
        get => BatchSearchByTag ? 1 : 0;
        set => BatchSearchByTag = value == 1;
    }

    public int BatchScopeIndex
    {
        get => BatchScopeSelectedOnly ? 0 : 1;
        set => BatchScopeSelectedOnly = value != 1;
    }

    public string BatchScopeSelectedText => Loc.F("markerupdater.batchScopeSelectedCount", BatchSelectionCount);

    public string BatchScopeAllText => Loc.F("markerupdater.batchScopeAllCount", BatchMatches.Count);

    public bool HasBatchMatches => BatchMatches.Count > 0;

    public string CleanupClampHeader => Loc.F("markerupdater.cleanupClampHeader", CleanupClampItems.Count);

    public string CleanupDeleteHeader => Loc.F("markerupdater.cleanupDeleteHeader", CleanupDeleteItems.Count);

    private void InitBatchUi()
    {
        BatchMatches.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BatchScopeAllText));
            OnPropertyChanged(nameof(HasBatchMatches));
            RefreshBatchRemovableTags();
        };
        RefreshBatchRemovableTags();
        CleanupClampItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CleanupClampHeader));
        CleanupDeleteItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CleanupDeleteHeader));
    }

    partial void OnBatchSelectionCountChanged(int value) => OnPropertyChanged(nameof(BatchScopeSelectedText));

    partial void OnBatchScopeSelectedOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(BatchScopeIndex));
        RefreshBatchRemovableTags();
    }
}
