using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    [RelayCommand]
    private void NewSegment()
    {
        _selectedSegmentIndex = -1;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        RequestSegmentListSelection?.Invoke(-1);

        var styleSource = Segments.Count > 0 ? Segments[^1] : EditorSegment;
        var fresh = new TextOverlaySegment();
        if (styleSource is not null)
        {
            fresh.Fontsize = styleSource.Fontsize;
            fresh.Color = styleSource.Color;
            fresh.PosX = styleSource.PosX;
            fresh.PosY = styleSource.PosY;
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

        NormalizeSegmentTimes(EditorSegment);

        var isExisting = IsEditingExistingSegment;
        if (!isExisting)
        {
            Segments.Add(EditorSegment);
            _selectedSegmentIndex = Segments.Count - 1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
        }

        UpdateSegmentEditorChrome();
        PersistSettings();
        SchedulePreviewRefresh();
        Status = isExisting
            ? Loc.F("texttovideo.status.segmentSaved", _selectedSegmentIndex + 1)
            : Loc.T("texttovideo.status.segmentAdded");
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
            RequestSegmentListSelection?.Invoke(-1);
            NewSegment();
        }
        else
        {
            _selectedSegmentIndex = Math.Min(_selectedSegmentIndex, Segments.Count - 1);
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
            EditorSegment = Segments[_selectedSegmentIndex];
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
