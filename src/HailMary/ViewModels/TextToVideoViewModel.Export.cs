using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!HasVideo || string.IsNullOrWhiteSpace(VideoPath))
        {
            Status = "Bitte zuerst ein Video laden.";
            return;
        }

        if (Segments.Count == 0 && string.IsNullOrWhiteSpace(EditorSegment.Text.Trim()) && string.IsNullOrWhiteSpace(SrtPath))
        {
            Status = Loc.T("texttovideo.status.needSegmentOrSrt");
            return;
        }

        var isGif = IsGifExport;
        var output = await FilePickerHelper.PickExportVideoAsync(VideoPath, isGif);
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        PersistSettings();
        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        IsBusy = true;
        Status = Loc.T("cutter.exportRunning");
        try
        {
            var config = TextToVideoBridge.BuildExportConfig(
                VideoPath,
                output,
                Segments,
                EditorSegment,
                SelectedSegmentIndex,
                CollectSettings());

            var result = await TextToVideoBridge.ExportAsync(config, token);
            Status = result.Success ? result.Message : result.Message;
            if (result.Success && !string.IsNullOrWhiteSpace(result.OutputPath))
            {
                AppServices.Session.SetLastOutput(result.OutputPath);
                OnPropertyChanged(nameof(LastFfmpegOutput));
                OnPropertyChanged(nameof(HasLastFfmpegOutput));
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Export abgebrochen.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
