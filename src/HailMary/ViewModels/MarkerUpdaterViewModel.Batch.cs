using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class MarkerBatchSceneRowViewModel : ObservableObject
{
    public string SceneId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<StashTagItem> Tags { get; init; } = [];

    public string Display => $"{SceneId} | {Title} | {Path}";
}

public partial class MarkerUpdaterViewModel
{
    public ObservableCollection<MarkerBatchSceneRowViewModel> BatchMatches { get; } = [];

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private string _batchSearchText = string.Empty;

    [ObservableProperty] private string _batchSearchTagName = string.Empty;

    [ObservableProperty] private bool _batchSearchByTag;

    public bool BatchSearchByPath => !BatchSearchByTag;

    partial void OnBatchSearchByTagChanged(bool value) => OnPropertyChanged(nameof(BatchSearchByPath));

    [ObservableProperty] private string _batchAddTagName = string.Empty;

    [ObservableProperty] private string _batchRemoveTagName = string.Empty;

    [ObservableProperty] private string _batchDeleteGlobalTagName = string.Empty;

    [ObservableProperty] private bool _batchScopeSelectedOnly = true;

    [ObservableProperty] private bool _batchRemoveAlsoDeleteFromStash;

    [ObservableProperty] private string _batchResultsSummary = Loc.F("markerupdater.batchResults", 0);

    [ObservableProperty] private MarkerBatchSceneRowViewModel? _selectedBatchMatch;

    [RelayCommand]
    private void SetBatchSearchModePath()
    {
        BatchSearchByTag = false;
    }

    [RelayCommand]
    private void SetBatchSearchModeTag()
    {
        BatchSearchByTag = true;
    }

    [RelayCommand]
    private async Task BatchSearchAsync()
    {
        SyncSettings();
        IsBusy = true;
        Status = Loc.T("markerupdater.batchSearchRunning");
        BatchMatches.Clear();
        BatchResultsSummary = Loc.F("markerupdater.batchResults", 0);
        try
        {
            await EnsureStashReachableAsync();
            await RefreshAllTagsAsync();

            IReadOnlyList<StashBatchSceneItem> scenes;
            if (BatchSearchByTag)
            {
                var tagName = ResolveExistingTagName(BatchSearchTagName);
                if (tagName is null)
                {
                    Status = Loc.T("markerupdater.batchTagNotFound");
                    return;
                }

                scenes = await _client.FindScenesByTagNameAsync(tagName);
                Status = scenes.Count == 0
                    ? Loc.T("markerupdater.batchNoResultsTag")
                    : Loc.F("markerupdater.batchResultsTagFilter", scenes.Count, tagName);
            }
            else
            {
                scenes = await _client.FindScenesWithTagsAsync(BatchSearchText);
                var terms = string.IsNullOrWhiteSpace(BatchSearchText)
                    ? Loc.T("markerupdater.batchFilterAllFiles")
                    : BatchSearchText.Trim();
                Status = scenes.Count == 0
                    ? Loc.T("markerupdater.batchNoResultsPath")
                    : Loc.F("markerupdater.batchResultsPathFilter", scenes.Count, terms);
            }

            foreach (var scene in scenes)
            {
                BatchMatches.Add(ToBatchRow(scene));
            }

            BatchResultsSummary = Loc.F("markerupdater.batchResults", scenes.Count);
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
    private async Task BatchAddTagAsync()
    {
        var tagName = ResolveBatchTagInput(BatchAddTagName);
        if (tagName is null)
        {
            Status = Loc.T("markerupdater.tagNameRequired");
            return;
        }

        var targets = GetBatchTargetScenes();
        if (targets.Count == 0)
        {
            Status = BatchScopeSelectedOnly
                ? Loc.T("markerupdater.batchNoRowsSelected")
                : Loc.T("stash.noResults");
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureStashReachableAsync();
            await RefreshAllTagsAsync();
            if (!_tagNameToId.TryGetValue(tagName, out var tagId))
            {
                tagId = await _client.CreateTagAsync(tagName);
                _tagNameToId[tagName] = tagId;
                if (!AllTagNames.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                {
                    AllTagNames.Add(tagName);
                }
            }

            var updated = 0;
            foreach (var scene in targets)
            {
                if (scene.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var tagIds = scene.Tags.Select(t => t.Id).ToList();
                tagIds.Add(tagId);
                await _client.UpdateSceneAsync(scene.SceneId, tagIds: tagIds);
                updated++;
            }

            Status = Loc.F("markerupdater.batchTagAdded", tagName, updated);
            await BatchSearchAsync();
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
    private async Task BatchRemoveTagAsync()
    {
        var tagName = ResolveExistingTagName(BatchRemoveTagName);
        if (tagName is null)
        {
            Status = Loc.T("markerupdater.batchTagNotFound");
            return;
        }

        var targets = GetBatchTargetScenes();
        if (targets.Count == 0)
        {
            Status = BatchScopeSelectedOnly
                ? Loc.T("markerupdater.batchNoRowsSelected")
                : Loc.T("stash.noResults");
            return;
        }

        var affected = targets.Count(scene =>
            scene.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)));
        if (affected == 0)
        {
            Status = Loc.T("markerupdater.status.tagNotOnScene");
            return;
        }

        var confirmMessage = BatchRemoveAlsoDeleteFromStash
            ? Loc.F("markerupdater.batchRemoveAndDeleteConfirm", tagName, affected)
            : Loc.F("markerupdater.batchRemoveOnlyConfirm", tagName, affected);
        if (ConfirmAsync is not null && !await ConfirmAsync(Loc.T("markerupdater.batchConfirmTitle"), confirmMessage))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureStashReachableAsync();
            await RefreshAllTagsAsync();
            var updated = 0;
            foreach (var scene in targets)
            {
                if (!scene.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var tagIds = scene.Tags
                    .Where(t => !t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Id)
                    .ToList();
                await _client.UpdateSceneAsync(scene.SceneId, tagIds: tagIds);
                updated++;
            }

            Status = Loc.F("markerupdater.batchTagRemovedFromScenes", updated);

            if (BatchRemoveAlsoDeleteFromStash
                && _tagNameToId.TryGetValue(tagName, out var tagId))
            {
                await _client.DeleteTagAsync(tagId);
                _tagNameToId.Remove(tagName);
                var existing = AllTagNames.FirstOrDefault(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    AllTagNames.Remove(existing);
                }

                Status = Loc.F("markerupdater.batchTagDeletedFromStash", tagName);
            }

            await BatchSearchAsync();
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
    private async Task BatchDeleteGlobalTagAsync()
    {
        var tagName = ResolveExistingTagName(BatchDeleteGlobalTagName);
        if (tagName is null)
        {
            Status = Loc.T("markerupdater.status.pickTagToDelete");
            return;
        }

        if (!_tagNameToId.TryGetValue(tagName, out var tagId))
        {
            Status = Loc.T("markerupdater.tagIdUnknown");
            return;
        }

        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("markerupdater.batchDeleteGlobalTitle"), Loc.F("markerupdater.batchDeleteGlobalConfirm", tagName)))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureStashReachableAsync();
            await _client.DeleteTagAsync(tagId);
            _tagNameToId.Remove(tagName);
            var existing = AllTagNames.FirstOrDefault(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                AllTagNames.Remove(existing);
            }

            BatchDeleteGlobalTagName = string.Empty;
            Status = Loc.F("markerupdater.batchGlobalTagDeleted", tagName);
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

    internal List<MarkerBatchSceneRowViewModel> GetBatchTargetScenes()
    {
        if (BatchScopeSelectedOnly && _batchSelectedMatches.Count > 0)
        {
            return _batchSelectedMatches;
        }

        return BatchMatches.ToList();
    }

    private readonly List<MarkerBatchSceneRowViewModel> _batchSelectedMatches = [];

    internal void SetBatchSelection(IEnumerable<MarkerBatchSceneRowViewModel> selected)
    {
        _batchSelectedMatches.Clear();
        _batchSelectedMatches.AddRange(selected);
    }

    internal void BatchSelectAllRows()
    {
        _batchSelectedMatches.Clear();
        _batchSelectedMatches.AddRange(BatchMatches);
    }

    internal void BatchClearSelection()
    {
        _batchSelectedMatches.Clear();
    }

    private static MarkerBatchSceneRowViewModel ToBatchRow(StashBatchSceneItem scene) => new()
    {
        SceneId = scene.SceneId,
        Title = scene.Title,
        Path = scene.Path,
        Tags = scene.Tags,
    };

    private static string? ResolveBatchTagInput(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private string? ResolveExistingTagName(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (_tagNameToId.ContainsKey(value))
        {
            return value;
        }

        return _tagNameToId.Keys.FirstOrDefault(k => k.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
