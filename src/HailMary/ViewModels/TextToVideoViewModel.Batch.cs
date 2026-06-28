using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    public string BatchExportLabel =>
        BatchRows.Count > 0 ? $"Batch exportieren ({SelectedBatchCount})" : "Batch exportieren";

    public int SelectedBatchCount => BatchRows.Count(r => r.IsSelected);

    [RelayCommand]
    private async Task PickBatchVideosAsync()
    {
        var paths = await FilePickerHelper.PickVideosAsync();
        AddBatchPaths(paths);
    }

    [RelayCommand]
    private async Task ScanBatchFolderAsync()
    {
        var folder = await FolderPickerHelper.PickFolderAsync(
            string.IsNullOrWhiteSpace(BatchOutputDir) ? VideoPath : BatchOutputDir);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".mts", ".m2ts", ".flv",
        };

        var paths = Directory.EnumerateFiles(folder)
            .Where(p => exts.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddBatchPaths(paths);
        Status = paths.Count > 0 ? $"{paths.Count} Videos aus Ordner hinzugefügt." : "Keine Videos im Ordner.";
    }

    [RelayCommand]
    private void ClearBatch()
    {
        BatchRows.Clear();
        NotifyBatchChanged();
        Status = "Batch-Liste geleert.";
    }

    [RelayCommand]
    private void RemoveSelectedBatchRows()
    {
        for (var i = BatchRows.Count - 1; i >= 0; i--)
        {
            if (BatchRows[i].IsSelected)
            {
                BatchRows.RemoveAt(i);
            }
        }

        NotifyBatchChanged();
    }

    [RelayCommand]
    private async Task PickBatchOutputDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(BatchOutputDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            BatchOutputDir = path;
        }
    }

    [RelayCommand]
    private async Task ExportBatchAsync()
    {
        var rows = BatchRows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            Status = "Keine Videos in der Batch-Liste.";
            return;
        }

        if (Segments.Count == 0 && string.IsNullOrWhiteSpace(SrtPath))
        {
            Status = Loc.T("texttovideo.status.needSegmentOrSrtBatch");
            return;
        }

        var outDir = BatchOutputDir;
        if (string.IsNullOrWhiteSpace(outDir))
        {
            outDir = await FolderPickerHelper.PickFolderAsync(
                string.IsNullOrWhiteSpace(VideoPath) ? null : Path.GetDirectoryName(VideoPath));
            if (string.IsNullOrWhiteSpace(outDir))
            {
                return;
            }

            BatchOutputDir = outDir;
        }

        Directory.CreateDirectory(outDir);
        PersistSettings();

        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        IsBusy = true;
        var ok = 0;
        var fail = 0;

        try
        {
            var settings = CollectSettings();
            var isGif = IsGifExport;
            var ext = isGif ? ".gif" : ".mp4";

            for (var i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                if (!File.Exists(row.Path))
                {
                    row.Status = "Datei fehlt";
                    fail++;
                    continue;
                }

                row.Status = "Export…";
                Status = $"Batch {i + 1}/{rows.Count}: {row.FileName}";

                var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(row.Path) + "_text" + ext);
                var config = TextToVideoBridge.BuildExportConfig(
                    row.Path,
                    output,
                    Segments,
                    null,
                    null,
                    settings);

                var result = await TextToVideoBridge.ExportAsync(config, token);
                if (result.Success)
                {
                    row.Status = Loc.T("common.done");
                    ok++;
                    if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        AppServices.Session.SetLastOutput(result.OutputPath);
                    }
                }
                else
                {
                    row.Status = Loc.T("common.error");
                    fail++;
                }
            }

            OnPropertyChanged(nameof(LastFfmpegOutput));
            OnPropertyChanged(nameof(HasLastFfmpegOutput));
            Status = fail == 0
                ? $"Batch abgeschlossen: {ok} exportiert nach {outDir}."
                : $"Batch: {ok} OK, {fail} fehlgeschlagen.";
        }
        catch (OperationCanceledException)
        {
            Status = "Batch-Export abgebrochen.";
        }
        finally
        {
            IsBusy = false;
            NotifyBatchChanged();
        }
    }

    private void AddBatchPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(BatchRows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || existing.Contains(path))
            {
                continue;
            }

            BatchRows.Add(new TextToVideoBatchRow { Path = path });
            existing.Add(path);
            added++;
        }

        NotifyBatchChanged();
        if (added > 0)
        {
            Status = $"{added} Video(s) zur Batch-Liste hinzugefügt.";
        }
    }

    private void NotifyBatchChanged()
    {
        OnPropertyChanged(nameof(BatchExportLabel));
        OnPropertyChanged(nameof(SelectedBatchCount));
    }
}
