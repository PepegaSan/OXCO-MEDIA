using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using System.Collections.ObjectModel;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    [ObservableProperty] private bool _useLastFfmpegForResolve;

    [ObservableProperty] private string _batchOutputDir = string.Empty;

    public ObservableCollection<TextToVideoBatchRow> BatchRows { get; } = [];

    public IReadOnlyList<TextOverlayStylePreset> StylePresets => TextOverlayStylePresets.All;

    public string LastFfmpegOutput => AppServices.Session.Current.LastOutput ?? string.Empty;

    public bool HasLastFfmpegOutput => !string.IsNullOrWhiteSpace(LastFfmpegOutput) && File.Exists(LastFfmpegOutput);

    partial void OnDavinciOutputDirChanged(string value) => PersistSettings();

    partial void OnDavinciPresetChanged(string value) => PersistSettings();

    partial void OnUseLastFfmpegForResolveChanged(bool value) => PersistSettings();

    [RelayCommand]
    private async Task PickDavinciOutputDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(DavinciOutputDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            DavinciOutputDir = path;
        }
    }

    [RelayCommand]
    private async Task ExportResolveAsync()
    {
        var src = UseLastFfmpegForResolve && HasLastFfmpegOutput ? LastFfmpegOutput : VideoPath;
        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
        {
            Status = Loc.T("texttovideo.status.noValidVideo");
            return;
        }

        var preset = DavinciPreset.Trim();
        if (string.IsNullOrWhiteSpace(preset))
        {
            Status = Loc.T("texttovideo.status.davinciPresetRequired");
            return;
        }

        var outDir = string.IsNullOrWhiteSpace(DavinciOutputDir)
            ? Path.GetDirectoryName(src) ?? string.Empty
            : DavinciOutputDir;

        PersistSettings();
        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        IsBusy = true;
        Status = Loc.T("texttovideo.status.davinciConnecting");
        try
        {
            var config = TextToVideoBridge.BuildResolveConfig(src, outDir, preset, CollectSettings());
            var result = await TextToVideoBridge.ResolveExportAsync(config, token);
            Status = result.Message;
            if (result.Success && !string.IsNullOrWhiteSpace(result.OutputPath))
            {
                AppServices.Session.SetLastOutput(result.OutputPath);
                OnPropertyChanged(nameof(LastFfmpegOutput));
                OnPropertyChanged(nameof(HasLastFfmpegOutput));
            }
        }
        catch (OperationCanceledException)
        {
            Status = Loc.T("texttovideo.status.davinciExportCancelled");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
