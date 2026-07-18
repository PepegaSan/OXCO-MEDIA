using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class IntroCutterViewModel
{
    [RelayCommand]
    private async Task PreviewStripSuffixAsync() => await RunStripSuffixJobAsync(previewOnly: true);

    [RelayCommand]
    private async Task ApplyStripSuffixAsync() => await RunStripSuffixJobAsync(previewOnly: false);

    private async Task RunStripSuffixJobAsync(bool previewOnly)
    {
        var folders = GetSuffixStripFolders().ToList();
        if (folders.Count == 0)
        {
            Status = OutputBesideSource
                ? Loc.T("intro.noSourceFolders")
                : Loc.T("intro.noOutputFolder");
            return;
        }

        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        Status = previewOnly ? Loc.T("intro.renamePreview") : Loc.T("intro.renameApplying");

        try
        {
            var messages = new List<string>();
            foreach (var folder in folders)
            {
                var configPath = Path.Combine(Path.GetTempPath(), $"hm_intro_rename_{Guid.NewGuid():N}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    folder,
                    recursive = false,
                });
                await File.WriteAllTextAsync(configPath, json);

                var args = new List<string> { "--config-json", configPath };
                if (previewOnly)
                {
                    args.Add("--preview-only");
                }

                var result = await AppServices.JobRunner.RunBridgeAsync("intro_cutter_rename_job.py", args);
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(folders.Count > 1 ? $"{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}: {result.Message}" : result.Message);
                }
            }

            Status = messages.Count > 0 ? string.Join(" | ", messages) : Loc.T("common.done");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private IEnumerable<string> GetSuffixStripFolders()
    {
        if (OutputBesideSource)
        {
            return _batchEntries
                .Select(e => Path.GetDirectoryName(e.Path))
                .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase)!;
        }

        if (!string.IsNullOrWhiteSpace(OutputDir) && Directory.Exists(OutputDir))
        {
            return [OutputDir];
        }

        foreach (var entry in _batchEntries)
        {
            var dir = Path.GetDirectoryName(entry.Path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                return [dir];
            }
        }

        return [];
    }
}
