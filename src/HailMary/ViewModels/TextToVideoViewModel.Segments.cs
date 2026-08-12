using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    [RelayCommand]
    private void NewSegment()
    {
        // Prefer style from the currently selected segment, else the last one.
        TextOverlaySegment? styleSource = null;
        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < Segments.Count)
        {
            styleSource = Segments[_selectedSegmentIndex];
        }
        else if (Segments.Count > 0)
        {
            styleSource = Segments[^1];
        }

        _selectedSegmentIndex = -1;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        RequestSegmentListSelection?.Invoke(-1);

        var fresh = new TextOverlaySegment();
        if (styleSource is not null)
        {
            fresh.Fontsize = styleSource.Fontsize;
            fresh.Color = styleSource.Color;
            fresh.PosX = styleSource.PosX;
            fresh.PosY = styleSource.PosY;
            fresh.LayoutWidth = styleSource.LayoutWidth;
            fresh.LayoutHeight = styleSource.LayoutHeight;
            fresh.LineSpacing = styleSource.LineSpacing;
            fresh.BoxBorder = styleSource.BoxBorder;
            fresh.BoxEnabled = styleSource.BoxEnabled;
            fresh.FontPath = styleSource.FontPath;
            fresh.ItalicFontPath = styleSource.ItalicFontPath;
            fresh.Bold = styleSource.Bold;
            fresh.Italic = styleSource.Italic;
            fresh.Strike = styleSource.Strike;
        }

        EditorSegment = fresh;
        UpdateSegmentEditorChrome();
        SchedulePreviewRefresh();
        Status = Loc.T("texttovideo.status.newSegmentHint");
    }

    [RelayCommand]
    private void CommitSegment()
    {
        if (string.IsNullOrWhiteSpace(EditorSegment.Text.Trim()))
        {
            Status = Loc.T("texttovideo.status.textRequired");
            return;
        }

        if (!TryValidateAndNormalizeEditorTimes())
        {
            return;
        }

        var isExisting = IsEditingExistingSegment;
        if (isExisting)
        {
            // Write draft back onto the list item (keeps list identity, refreshes ListLabel).
            Segments[_selectedSegmentIndex].ApplyFrom(EditorSegment);
            // Keep editing a fresh draft copy of the saved segment.
            EditorSegment = Segments[_selectedSegmentIndex].Clone();
        }
        else
        {
            var added = EditorSegment.Clone();
            Segments.Add(added);
            _selectedSegmentIndex = Segments.Count - 1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
            EditorSegment = added.Clone();
        }

        UpdateSegmentEditorChrome();
        PersistSettings();
        SchedulePreviewRefresh(immediate: true);
        Status = isExisting
            ? Loc.F("texttovideo.status.segmentSaved", _selectedSegmentIndex + 1)
            : Loc.F("texttovideo.status.segmentAddedNumbered", _selectedSegmentIndex + 1);
    }

    [RelayCommand]
    private void DuplicateSegment()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= Segments.Count)
        {
            Status = Loc.T("texttovideo.status.selectSegment");
            return;
        }

        var copy = Segments[_selectedSegmentIndex].Clone();
        Segments.Insert(_selectedSegmentIndex + 1, copy);
        _selectedSegmentIndex++;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
        EditorSegment = copy.Clone();
        UpdateSegmentEditorChrome();
        PersistSettings();
        SchedulePreviewRefresh();
        Status = Loc.F("texttovideo.status.segmentDuplicated", _selectedSegmentIndex + 1);
    }

    [RelayCommand]
    private void DeleteSegment()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= Segments.Count)
        {
            Status = Loc.T("texttovideo.status.selectSegment");
            return;
        }

        Segments.RemoveAt(_selectedSegmentIndex);
        if (Segments.Count == 0)
        {
            _selectedSegmentIndex = -1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            RequestSegmentListSelection?.Invoke(-1);
            NewSegment();
        }
        else
        {
            _selectedSegmentIndex = Math.Min(_selectedSegmentIndex, Segments.Count - 1);
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
            EditorSegment = Segments[_selectedSegmentIndex].Clone();
        }

        UpdateSegmentEditorChrome();
        PersistSettings();
        SchedulePreviewRefresh();
        Status = Loc.T("texttovideo.status.segmentDeleted");
    }

    [RelayCommand]
    private void PickPaletteColor(string hex)
    {
        EditorSegment.Color = hex;
        SchedulePreviewRefresh();
    }

    [RelayCommand]
    private async Task PickFontAsync()
    {
        var path = await FilePickerHelper.PickFontFileAsync(EditorSegment.FontPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorSegment.FontPath = path;
        }
    }

    [RelayCommand]
    private async Task PickItalicFontAsync()
    {
        var path = await FilePickerHelper.PickFontFileAsync(EditorSegment.ItalicFontPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorSegment.ItalicFontPath = path;
        }
    }

    [RelayCommand]
    private async Task PickSrtAsync()
    {
        var path = await FilePickerHelper.PickSrtAsync(SrtPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SrtPath = path;
            PersistSettings();
        }
    }

    [RelayCommand]
    private void ClearSrt()
    {
        SrtPath = string.Empty;
        PersistSettings();
        SchedulePreviewRefresh();
    }

    [RelayCommand]
    private void ApplySourceBitrate()
    {
        if (!HasVideo)
        {
            Status = Loc.T("texttovideo.status.loadVideoFirst");
            return;
        }

        if (string.IsNullOrWhiteSpace(_suggestedBitrate))
        {
            Status = Loc.T("texttovideo.status.noSourceBitrate");
            return;
        }

        Bitrate = _suggestedBitrate;
        PersistSettings();
    }
}
