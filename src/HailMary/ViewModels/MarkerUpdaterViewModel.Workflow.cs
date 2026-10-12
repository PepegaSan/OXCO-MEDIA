using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class StashMarkerRowViewModel
{
    public string RangeText => string.IsNullOrWhiteSpace(EndSecondsText)
        ? TimecodeHelper.FormatDisplayFromText(SecondsText)
        : TimecodeHelper.FormatRangeFromText(SecondsText, EndSecondsText);

    public string TitleText => string.IsNullOrWhiteSpace(Title) ? PrimaryTagName : Title;

    public string TagText => PrimaryTagName;

    partial void OnPrimaryTagNameChanged(string value)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(TagText));
    }
}

/// <summary>Clearer workflow: one find box, explicit new/edit marker modes, unsaved scene changes.</summary>
public partial class MarkerUpdaterViewModel
{
    private static readonly Regex PlainSceneId = new(@"^\s*(\d+|[0-9a-f]{8}-[0-9a-f-]{27})\s*$", RegexOptions.IgnoreCase);

    [ObservableProperty] private bool _resultsExpanded = true;

    public bool HasResults => Results.Count > 0;

    public bool ShowResults => HasResults && ResultsExpanded;

    public string ResultsHeader => Loc.F("markerupdater.resultsHeader", Results.Count);

    public string MarkerCountText => Loc.F("markerupdater.markerCount", Markers.Count);

    public bool HasMarkers => Markers.Count > 0;

    public bool IsEditingMarker => SelectedMarker is not null;

    public bool IsCreatingMarker => SelectedMarker is null;

    public string MarkerEditorHeader => SelectedMarker is null
        ? Loc.T("markerupdater.newMarkerHeader")
        : Loc.F("markerupdater.editMarkerHeader", SelectedMarker.TitleText);

    public string LoadedSceneCaption => HasLoadedScene
        ? $"#{LoadedSceneId} · {ResolvedLocalPath}"
        : Loc.T("markerupdater.noSceneHint");

    /// <summary>Scene title / details / tags differ from what Stash has (markers save immediately).</summary>
    public bool IsSceneDirty
    {
        get
        {
            if (_loadedScene is null)
            {
                return false;
            }

            if (!string.Equals(SceneTitle.Trim(), (_loadedScene.Title ?? string.Empty).Trim(), StringComparison.Ordinal)
                || !string.Equals(SceneDetails ?? string.Empty, _loadedScene.Details ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }

            var loaded = _loadedScene.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return loaded.Count != SceneTags.Count || SceneTags.Any(t => !loaded.Contains(t));
        }
    }

    private void InitWorkflow()
    {
        Results.CollectionChanged += (_, _) => NotifyResults();
        Markers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MarkerCountText));
            OnPropertyChanged(nameof(HasMarkers));
        };
        SceneTags.CollectionChanged += (_, _) => NotifySceneDirty();
    }

    private void NotifyResults()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ResultsHeader));
    }

    private void NotifySceneDirty()
    {
        OnPropertyChanged(nameof(IsSceneDirty));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    private void NotifyMarkerEditor()
    {
        OnPropertyChanged(nameof(IsEditingMarker));
        OnPropertyChanged(nameof(IsCreatingMarker));
        OnPropertyChanged(nameof(MarkerEditorHeader));
    }

    partial void OnResultsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowResults));

    partial void OnSceneTitleChanged(string value) => NotifySceneDirty();

    partial void OnSceneDetailsChanged(string value) => NotifySceneDirty();

    partial void OnLoadedSceneIdChanged(string value) => OnPropertyChanged(nameof(LoadedSceneCaption));

    partial void OnResolvedLocalPathChanged(string value) => OnPropertyChanged(nameof(LoadedSceneCaption));

    [RelayCommand]
    private void ToggleResults() => ResultsExpanded = !ResultsExpanded;

    /// <summary>One box for everything: scene ID, Stash link or search text.</summary>
    [RelayCommand]
    private async Task FindOrLoadAsync()
    {
        var text = (SearchText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            Status = Loc.T("markerupdater.findEmpty");
            return;
        }

        var id = StashSceneIdParser.FromText(text) ?? PlainIdOrNull(text);
        if (id is not null)
        {
            SceneId = id;
            if (await LoadSceneByIdAsync(id, navigateBySearchResults: false, reportErrors: PlainIdOrNull(text) is null))
            {
                return;
            }
        }

        await SearchAsync();
        ResultsExpanded = true;
        if (Results.Count == 1)
        {
            SelectedResult = Results[0];
            await UseSelectedResultAsync();
        }
    }

    private static string? PlainIdOrNull(string text)
    {
        var m = PlainSceneId.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    [RelayCommand]
    private async Task OpenResultAsync(StashSceneRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedResult = row;
        await UseSelectedResultAsync();
    }

    private async Task<bool> ConfirmDiscardSceneChangesAsync(string targetSceneId)
    {
        if (!IsSceneDirty
            || string.Equals(_loadedScene?.SceneId, targetSceneId, StringComparison.Ordinal)
            || ConfirmAsync is null)
        {
            return true;
        }

        return await ConfirmAsync(
            Loc.T("markerupdater.discardTitle"),
            Loc.F("markerupdater.discardMessage", LoadedSceneTitle));
    }

    [RelayCommand]
    private void RevertSceneChanges()
    {
        if (_loadedScene is null)
        {
            return;
        }

        SceneTitle = _loadedScene.Title;
        SceneDetails = _loadedScene.Details;
        SceneTags.Clear();
        foreach (var tag in _loadedScene.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            SceneTags.Add(tag.Name);
        }

        Status = Loc.T("markerupdater.changesDiscarded");
    }

    [RelayCommand]
    private void RemoveSceneTag(string? name)
    {
        var existing = SceneTags.FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        SceneTags.Remove(existing);
        Status = Loc.T("markerupdater.tagRemovedPending");
    }

    /// <summary>Point marker at the playhead with this tag as primary tag.</summary>
    [RelayCommand]
    private async Task MarkerWithTagHereAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SelectedMarker = null;
        NewMarkerTitle = string.Empty;
        NewMarkerEndSeconds = string.Empty;
        NewMarkerTagName = name;
        NewMarkerSeconds = TimecodeHelper.FormatForEditor(SliderValue);
        await AddMarkerAsync();
    }

    [RelayCommand]
    private void NewMarker()
    {
        SelectedMarker = null;
        ClearMarkerEditorFields();
        if (HasVideo)
        {
            NewMarkerSeconds = TimecodeHelper.FormatForEditor(SliderValue);
        }

        Status = Loc.T("markerupdater.newMarkerReady");
    }

    [RelayCommand]
    private void SetStartToPlayhead() => MarkInAt(SliderValue);

    [RelayCommand]
    private void SetEndToPlayhead() => MarkOutAt(SliderValue);

    [RelayCommand]
    private void ClearEnd() => NewMarkerEndSeconds = string.Empty;

    [RelayCommand]
    private void RemoveQuickPreset(MarkerQuickPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        var idx = QuickPresets.IndexOf(preset);
        if (idx < 0)
        {
            return;
        }

        QuickPresets.RemoveAt(idx);
        ActiveQuickPresetIndex = Math.Clamp(ActiveQuickPresetIndex >= idx ? ActiveQuickPresetIndex - 1 : ActiveQuickPresetIndex, 0, Math.Max(0, QuickPresets.Count - 1));
        PersistQuickPresetSettings();
        NewQuickPresetSlot = NextFreeInstantSlot();
        Status = Loc.F("markerupdater.quickPresetRemoved", preset.Label);
        RefreshQuickPresetUi();
    }

    /// <summary>Point marker at the playhead with the active quick preset.</summary>
    public Task QuickInstantActiveAsync(double seconds) => QuickInstantCreateAsync(seconds, ActiveQuickPreset);

    public bool HasQuickRangeStart => _quickRangeInSeconds is not null;
}
