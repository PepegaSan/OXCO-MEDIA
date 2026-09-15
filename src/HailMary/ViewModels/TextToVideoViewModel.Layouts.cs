using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    private bool _suspendLayoutSwitch;
    private string _layoutKey = string.Empty;

    public ObservableCollection<string> LayoutNames { get; } = [];

    public string LayoutKey => _layoutKey;

    public string LayoutDisplay =>
        string.IsNullOrWhiteSpace(_layoutKey)
            ? Loc.T("texttovideo.layoutNone")
            : Loc.F("texttovideo.layoutCurrent", _layoutKey);

    public bool CanDeleteLayout => TextToVideoLayoutStore.Exists(_layoutKey);

    public bool CanApplyLayout => !string.IsNullOrWhiteSpace(SelectedLayoutName);

    [ObservableProperty] private string _selectedLayoutName = string.Empty;

    partial void OnSelectedLayoutNameChanged(string value) =>
        OnPropertyChanged(nameof(CanApplyLayout));

    private void NotifyLayoutChrome()
    {
        OnPropertyChanged(nameof(LayoutKey));
        OnPropertyChanged(nameof(LayoutDisplay));
        OnPropertyChanged(nameof(CanDeleteLayout));
        OnPropertyChanged(nameof(CanApplyLayout));
    }

    private void RefreshLayoutNames()
    {
        var previous = SelectedLayoutName;
        LayoutNames.Clear();
        foreach (var name in TextToVideoLayoutStore.ListKeys())
        {
            LayoutNames.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(previous)
            && LayoutNames.Contains(previous, StringComparer.OrdinalIgnoreCase))
        {
            SelectedLayoutName = LayoutNames.First(n => n.Equals(previous, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(_layoutKey)
                 && LayoutNames.Contains(_layoutKey, StringComparer.OrdinalIgnoreCase))
        {
            SelectedLayoutName = LayoutNames.First(n => n.Equals(_layoutKey, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SelectedLayoutName = LayoutNames.Count > 0 ? LayoutNames[0] : string.Empty;
        }

        NotifyLayoutChrome();
    }

    private void SwitchLayoutForVideo(string newPath)
    {
        var newKey = TextToVideoLayoutKey.FromPath(newPath);
        if (string.Equals(newKey, _layoutKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PersistCurrentLayoutIfNeeded();
        var hadKey = !string.IsNullOrWhiteSpace(_layoutKey);
        _layoutKey = newKey;

        if (string.IsNullOrWhiteSpace(newKey))
        {
            RefreshLayoutNames();
            return;
        }

        var layout = TextToVideoLayoutStore.TryLoad(newKey);
        if (layout is not null)
        {
            ApplySegmentList(layout.OverlaySegments);
            Status = Loc.F("texttovideo.status.layoutLoaded", newKey);
        }
        else if (hadKey || Segments.Count == 0)
        {
            ApplySegmentList([]);
            Status = Loc.F("texttovideo.status.layoutNew", newKey);
        }
        else
        {
            Status = Loc.F("texttovideo.status.layoutNew", newKey);
        }

        RefreshLayoutNames();
    }

    private void PersistCurrentLayoutIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_layoutKey))
        {
            return;
        }

        if (Segments.Count == 0 && !TextToVideoLayoutStore.Exists(_layoutKey))
        {
            return;
        }

        TextToVideoLayoutStore.Save(_layoutKey, VideoPath, Segments.Select(s => s.Clone()));
        RefreshLayoutNames();
    }

    private void ApplySegmentList(IReadOnlyList<TextOverlaySegment> source)
    {
        Segments.Clear();
        foreach (var seg in source)
        {
            Segments.Add(seg.Clone());
        }

        if (Segments.Count > 0)
        {
            _selectedSegmentIndex = 0;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            EditorSegment = Segments[0].Clone();
            RequestSegmentListSelection?.Invoke(0);
        }
        else
        {
            _selectedSegmentIndex = -1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            EditorSegment = new TextOverlaySegment();
            RequestSegmentListSelection?.Invoke(-1);
        }

        UpdateSegmentEditorChrome();
        SyncSelectedFontFromEditor();
        RebuildTimeline();
        SchedulePreviewRefresh();
    }

    [RelayCommand]
    private async Task ApplyLayoutAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedLayoutName))
        {
            Status = Loc.T("texttovideo.status.layoutPickFirst");
            return;
        }

        if (string.IsNullOrWhiteSpace(_layoutKey))
        {
            Status = Loc.T("texttovideo.status.layoutNoKey");
            return;
        }

        var source = TextToVideoLayoutStore.TryLoad(SelectedLayoutName);
        if (source is null)
        {
            Status = Loc.F("texttovideo.status.layoutMissing", SelectedLayoutName);
            RefreshLayoutNames();
            return;
        }

        var replacing = Segments.Count > 0
            && !SelectedLayoutName.Equals(_layoutKey, StringComparison.OrdinalIgnoreCase);
        if (replacing && ConfirmAsync is not null)
        {
            var confirmed = await ConfirmAsync(
                Loc.T("texttovideo.layoutApplyConfirmTitle"),
                Loc.F("texttovideo.layoutApplyConfirm", SelectedLayoutName, _layoutKey, Segments.Count));
            if (!confirmed)
            {
                return;
            }
        }

        ApplySegmentList(source.OverlaySegments);
        PersistCurrentLayoutIfNeeded();
        Status = Loc.F("texttovideo.status.layoutApplied", SelectedLayoutName, _layoutKey);
    }

    [RelayCommand]
    private async Task DeleteLayoutAsync()
    {
        if (string.IsNullOrWhiteSpace(_layoutKey) || !TextToVideoLayoutStore.Exists(_layoutKey))
        {
            Status = Loc.T("texttovideo.status.layoutPickFirst");
            return;
        }

        if (ConfirmAsync is not null)
        {
            var confirmed = await ConfirmAsync(
                Loc.T("texttovideo.layoutDeleteConfirmTitle"),
                Loc.F("texttovideo.layoutDeleteConfirm", _layoutKey));
            if (!confirmed)
            {
                return;
            }
        }

        TextToVideoLayoutStore.Delete(_layoutKey);
        ApplySegmentList([]);
        RefreshLayoutNames();
        Status = Loc.F("texttovideo.status.layoutDeleted", _layoutKey);
    }
}
