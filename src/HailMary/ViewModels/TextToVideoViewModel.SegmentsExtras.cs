using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    [RelayCommand]
    private async Task PasteTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                Status = Loc.T("texttovideo.status.clipboardEmpty");
                return;
            }

            var text = await content.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                Status = "Zwischenablage ist leer.";
                return;
            }

            EditorSegment.Text = text.TrimEnd();
            SchedulePreviewRefresh();
            Status = Loc.T("texttovideo.status.clipboardPasted");
        }
        catch (Exception ex)
        {
            Status = $"Einfügen fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplyStylePreset(string presetId)
    {
        var preset = TextOverlayStylePresets.Find(presetId);
        if (preset is null)
        {
            return;
        }

        var w = _videoWidth > 0 ? _videoWidth : 1920;
        var h = _videoHeight > 0 ? _videoHeight : 1080;
        preset.Apply(EditorSegment, w, h);
        SchedulePreviewRefresh();
        Status = $"Stil „{preset.Label}“ angewendet.";
    }

    [RelayCommand]
    private void ImportSrtToSegments()
    {
        if (string.IsNullOrWhiteSpace(SrtPath) || !File.Exists(SrtPath))
        {
            Status = Loc.T("texttovideo.status.selectSrt");
            return;
        }

        var cues = SrtParser.ParseFile(SrtPath);
        if (cues.Count == 0)
        {
            Status = "Keine Untertitel in der SRT-Datei gefunden.";
            return;
        }

        var w = _videoWidth > 0 ? _videoWidth : 1920;
        var h = _videoHeight > 0 ? _videoHeight : 1080;
        var subtitlePreset = TextOverlayStylePresets.Find("subtitle_bottom");

        Segments.Clear();
        foreach (var cue in cues)
        {
            var seg = cue.Clone();
            subtitlePreset?.Apply(seg, w, h);
            Segments.Add(seg);
        }

        _selectedSegmentIndex = Segments.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        if (_selectedSegmentIndex >= 0)
        {
            EditorSegment = Segments[_selectedSegmentIndex];
            RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
        }

        UpdateSegmentEditorChrome();

        PersistSettings();
        SchedulePreviewRefresh();
        Status = $"{cues.Count} Untertitel aus SRT importiert.";
    }
}
