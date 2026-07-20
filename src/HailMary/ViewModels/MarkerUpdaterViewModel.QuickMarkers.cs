using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class MarkerUpdaterViewModel
{
    public ObservableCollection<MarkerQuickPreset> QuickPresets { get; } = [];

    [ObservableProperty] private int _activeQuickPresetIndex;

    [ObservableProperty] private string _activeQuickPresetLabel = string.Empty;

    [ObservableProperty] private string _quickMarkerHint = string.Empty;

    [ObservableProperty] private string _newQuickPresetLabel = string.Empty;

    [ObservableProperty] private string _newQuickPresetTag = string.Empty;

    [ObservableProperty] private string _newQuickPresetTitle = string.Empty;

    [ObservableProperty] private int _newQuickPresetSlot = 3;

    public IReadOnlyList<int> QuickPresetSlotChoices { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    private double? _quickRangeInSeconds;

    private void InitQuickPresets()
    {
        QuickPresets.Clear();
        var list = _settings.QuickPresets.Count > 0
            ? _settings.QuickPresets
            : MarkerUpdaterSettings.DefaultQuickPresets();
        foreach (var p in list)
        {
            QuickPresets.Add(ClonePreset(p));
        }

        if (QuickPresets.Count == 0)
        {
            foreach (var p in MarkerUpdaterSettings.DefaultQuickPresets())
            {
                QuickPresets.Add(ClonePreset(p));
            }
        }

        ActiveQuickPresetIndex = Math.Clamp(_settings.DefaultQuickPresetIndex, 0, Math.Max(0, QuickPresets.Count - 1));
        NewQuickPresetSlot = NextFreeInstantSlot();
        RefreshQuickPresetUi();
    }

    private static MarkerQuickPreset ClonePreset(MarkerQuickPreset p) => new()
    {
        Id = p.Id,
        Label = p.Label,
        PrimaryTag = string.IsNullOrWhiteSpace(p.PrimaryTag) ? p.Label : p.PrimaryTag,
        Title = string.IsNullOrWhiteSpace(p.Title) ? p.Label : p.Title,
        InstantSlot = p.InstantSlot,
    };

    private MarkerQuickPreset? ActiveQuickPreset =>
        ActiveQuickPresetIndex >= 0 && ActiveQuickPresetIndex < QuickPresets.Count
            ? QuickPresets[ActiveQuickPresetIndex]
            : null;

    partial void OnActiveQuickPresetIndexChanged(int value) => RefreshQuickPresetUi();

    private void RefreshQuickPresetUi()
    {
        var p = ActiveQuickPreset;
        ActiveQuickPresetLabel = p is null
            ? Loc.T("markerupdater.quickNoPreset")
            : Loc.F("markerupdater.quickActivePreset", p.Label);
        QuickMarkerHint = Loc.T("markerupdater.quickKeyboardHint");
        OnPropertyChanged(nameof(QuickPresets));
        RemoveActiveQuickPresetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectQuickPreset(MarkerQuickPreset? preset)
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

        ActiveQuickPresetIndex = idx;
        _settings.DefaultQuickPresetIndex = idx;
        PersistQuickPresetSettings();
        Status = Loc.F("markerupdater.quickPresetSelected", preset.Label);
    }

    [RelayCommand]
    private void AddQuickPreset()
    {
        var label = NewQuickPresetLabel.Trim();
        if (string.IsNullOrEmpty(label))
        {
            Status = Loc.T("markerupdater.quickPresetLabelRequired");
            return;
        }

        var tag = string.IsNullOrWhiteSpace(NewQuickPresetTag) ? label : NewQuickPresetTag.Trim();
        var title = string.IsNullOrWhiteSpace(NewQuickPresetTitle) ? label : NewQuickPresetTitle.Trim();
        var slot = Math.Clamp(NewQuickPresetSlot, 0, 9);

        if (slot > 0 && QuickPresets.Any(p => p.InstantSlot == slot))
        {
            Status = Loc.F("markerupdater.quickSlotTaken", slot);
            return;
        }

        var idBase = SanitizePresetId(label);
        var id = idBase;
        var n = 2;
        while (QuickPresets.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            id = $"{idBase}-{n++}";
        }

        QuickPresets.Add(new MarkerQuickPreset
        {
            Id = id,
            Label = label,
            PrimaryTag = tag,
            Title = title,
            InstantSlot = slot,
        });

        ActiveQuickPresetIndex = QuickPresets.Count - 1;
        PersistQuickPresetSettings();
        NewQuickPresetLabel = string.Empty;
        NewQuickPresetTag = string.Empty;
        NewQuickPresetTitle = string.Empty;
        NewQuickPresetSlot = NextFreeInstantSlot();
        Status = Loc.F("markerupdater.quickPresetAdded", label);
        RefreshQuickPresetUi();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveActiveQuickPreset))]
    private void RemoveActiveQuickPreset()
    {
        if (ActiveQuickPreset is null || QuickPresets.Count == 0)
        {
            return;
        }

        var label = ActiveQuickPreset.Label;
        var idx = ActiveQuickPresetIndex;
        QuickPresets.RemoveAt(idx);
        if (QuickPresets.Count == 0)
        {
            ActiveQuickPresetIndex = 0;
        }
        else
        {
            ActiveQuickPresetIndex = Math.Clamp(idx, 0, QuickPresets.Count - 1);
        }

        PersistQuickPresetSettings();
        Status = Loc.F("markerupdater.quickPresetRemoved", label);
        RefreshQuickPresetUi();
    }

    private bool CanRemoveActiveQuickPreset() => QuickPresets.Count > 0 && ActiveQuickPreset is not null;

    private static string SanitizePresetId(string label)
    {
        var chars = label.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(s) ? "preset" : s;
    }

    private int NextFreeInstantSlot()
    {
        for (var i = 1; i <= 9; i++)
        {
            if (QuickPresets.All(p => p.InstantSlot != i))
            {
                return i;
            }
        }

        return 0;
    }

    [RelayCommand]
    private void CycleQuickPreset(int direction)
    {
        if (QuickPresets.Count == 0)
        {
            return;
        }

        var next = (ActiveQuickPresetIndex + direction) % QuickPresets.Count;
        if (next < 0)
        {
            next += QuickPresets.Count;
        }

        ActiveQuickPresetIndex = next;
        _settings.DefaultQuickPresetIndex = next;
        PersistQuickPresetSettings();
        Status = Loc.F("markerupdater.quickPresetSelected", ActiveQuickPreset?.Label ?? "?");
    }

    public void QuickMarkInAt(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, Math.Max(SliderMaximum, seconds));
        _quickRangeInSeconds = clamped;
        MarkInAt(clamped);
        var label = ActiveQuickPreset?.Label ?? "?";
        Status = Loc.F("markerupdater.quickInSet", TimecodeHelper.FormatDisplay(clamped), label);
    }

    public async Task QuickMarkOutAndCreateAsync(double seconds)
    {
        if (_quickRangeInSeconds is null)
        {
            Status = Loc.T("markerupdater.quickInRequired");
            return;
        }

        var preset = ActiveQuickPreset;
        if (preset is null)
        {
            Status = Loc.T("markerupdater.quickNoPreset");
            return;
        }

        var end = Math.Clamp(seconds, 0, Math.Max(SliderMaximum, seconds));
        var start = _quickRangeInSeconds.Value;
        var from = Math.Min(start, end);
        var to = Math.Max(start, end);
        MarkOutAt(end);
        double? endArg = to > from + 0.05 ? to : null;
        await CreateQuickMarkerAsync(preset, from, endArg);
        _quickRangeInSeconds = null;
    }

    public Task QuickInstantCreateAsync(double seconds, int slot) =>
        QuickInstantCreateAsync(seconds, FindPresetBySlot(slot));

    public async Task QuickInstantCreateAsync(double seconds, MarkerQuickPreset? preset)
    {
        if (preset is null)
        {
            Status = Loc.T("markerupdater.quickNoPresetForSlot");
            return;
        }

        var t = Math.Clamp(seconds, 0, Math.Max(SliderMaximum, seconds));
        await CreateQuickMarkerAsync(preset, t, endSeconds: null);
    }

    private MarkerQuickPreset? FindPresetBySlot(int slot) =>
        QuickPresets.FirstOrDefault(p => p.InstantSlot == slot);

    private async Task CreateQuickMarkerAsync(MarkerQuickPreset preset, double startSeconds, double? endSeconds)
    {
        if (_loadedScene is null)
        {
            Status = Loc.T("markerupdater.noSceneLoaded");
            return;
        }

        if (!HasVideo && SliderMaximum <= 0)
        {
            Status = Loc.T("markerupdater.quickNeedVideo");
            return;
        }

        var tagName = preset.PrimaryTag.Trim();
        if (string.IsNullOrEmpty(tagName))
        {
            Status = Loc.T("markerupdater.primaryTagRequired");
            return;
        }

        NewMarkerTitle = preset.Title;
        NewMarkerTagName = tagName;
        NewMarkerSeconds = TimecodeHelper.FormatForEditor(startSeconds);
        NewMarkerEndSeconds = endSeconds is null
            ? string.Empty
            : TimecodeHelper.FormatForEditor(endSeconds.Value);

        if (!_tagNameToId.TryGetValue(tagName, out var tagId))
        {
            try
            {
                tagId = await _client.CreateTagAsync(tagName);
                _tagNameToId[tagName] = tagId;
                if (!AllTagNames.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                {
                    AllTagNames.Add(tagName);
                }
            }
            catch (Exception ex)
            {
                Status = ex.Message;
                return;
            }
        }

        IsBusy = true;
        try
        {
            await _client.CreateMarkerAsync(
                _loadedScene.SceneId,
                preset.Title.Trim(),
                startSeconds,
                endSeconds,
                tagId);
            await RefreshMarkersAsync();
            var range = endSeconds is null
                ? TimecodeHelper.FormatDisplay(startSeconds)
                : TimecodeHelper.FormatDisplayRange(startSeconds, endSeconds.Value);
            Status = Loc.F("markerupdater.quickCreated", preset.Label, range);
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

    private void PersistQuickPresetSettings()
    {
        _settings.QuickPresets = QuickPresets.Select(ClonePreset).ToList();
        _settings.DefaultQuickPresetIndex = ActiveQuickPresetIndex;
        try
        {
            MarkerUpdaterConfigReader.Save(_settings);
        }
        catch
        {
            // ignore persist errors
        }
    }
}
