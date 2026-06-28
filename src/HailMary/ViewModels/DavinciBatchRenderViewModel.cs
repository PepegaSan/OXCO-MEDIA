using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class DavinciBatchRenderQueueRow : ObservableObject
{
    public string Path { get; init; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private string _status = Loc.T("davincibatch.itemPending");
}

public partial class DavinciBatchRenderViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private readonly List<string> _queue = [];
    private readonly Dictionary<string, DavinciBatchRenderQueueRow> _rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedIndex = -1;
    private CancellationTokenSource? _batchCts;
    private string? _currentRunningPath;

    public DavinciBatchRenderViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var settings = DavinciBatchRenderConfigReader.Load();
        DavinciPreset = settings.DavinciPreset;
        OutputDir = settings.DavinciOutputDir;
        SafeOutput = settings.SafeOutput;
        UiLanguage = settings.UiLanguage;
        RefreshQueueRows();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<DavinciBatchRenderQueueRow> QueueRows { get; } = [];

    public IReadOnlyList<string> LanguageOptions { get; } = ["de", "en"];

    [ObservableProperty] private string _davinciPreset = "YouTube - 1080p";
    [ObservableProperty] private string _outputDir = string.Empty;
    [ObservableProperty] private bool _safeOutput = true;
    [ObservableProperty] private string _uiLanguage = "de";
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    public string BatchButtonLabel => QueueRows.Count > 0 ? $"Batch starten ({QueueRows.Count})" : "Batch starten";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(CanStop));
        NotifyQueueEditCommands();
    }

    public bool CanStop => IsBusy;

    public bool CanEditQueue => !IsBusy;

    public void SetSelectedIndex(int index) => _selectedIndex = index;

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        if (!CanEditQueue)
        {
            return;
        }

        AddVideos(ExpandVideoPaths(paths));
    }

    private static IEnumerable<string> ExpandVideoPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                result.AddRange(
                    Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
                        .Where(VideoPathDropHelper.IsVideoFile));
            }
            else if (VideoPathDropHelper.IsVideoFile(full))
            {
                result.Add(full);
            }
        }

        return result;
    }

    private void AddVideos(IEnumerable<string> paths)
    {
        if (!CanEditQueue)
        {
            return;
        }

        var existing = new HashSet<string>(_queue, StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var path in paths)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (existing.Add(full))
            {
                _queue.Add(full);
                added = true;
            }
        }

        if (added)
        {
            RefreshQueueRows();
            Status = $"{_queue.Count} Video(s) in der Warteschlange.";
        }
        else
        {
            Status = Loc.T("davincibatch.noNewVideos");
        }
    }

    private void RefreshQueueRows()
    {
        QueueRows.Clear();
        _rowsByPath.Clear();
        foreach (var path in _queue)
        {
            var row = new DavinciBatchRenderQueueRow { Path = path };
            QueueRows.Add(row);
            _rowsByPath[System.IO.Path.GetFullPath(path)] = row;
        }

        OnPropertyChanged(nameof(BatchButtonLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    private void HandleBatchLogLine(string line)
    {
        if (!BatchItemLogParser.TryParse(line, out var path, out var status))
        {
            return;
        }

        UiDispatcher.Run(() =>
        {
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                _currentRunningPath = path;
                var fullPath = System.IO.Path.GetFullPath(path);
                var idx = _queue.FindIndex(p =>
                    string.Equals(System.IO.Path.GetFullPath(p), fullPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    AppServices.JobProgress.SetBatchItem(idx);
                }
            }
            else if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(_currentRunningPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentRunningPath = null;
                }
            }

            ApplyBatchItemStatus(path, status);
        });
    }

    private void ApplyBatchItemStatus(string path, string bridgeStatus)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!_rowsByPath.TryGetValue(fullPath, out var row))
        {
            row = QueueRows.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(System.IO.Path.GetFullPath(r.Path), fullPath, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                return;
            }
        }

        row.Status = BatchItemLogParser.ToDisplayStatus(bridgeStatus);
    }

    private void ApplyBatchResultsFromJson(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var st = item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            ApplyBatchItemStatus(path, st);
        }
    }

    private void MarkBatchCancelled()
    {
        if (!string.IsNullOrWhiteSpace(_currentRunningPath))
        {
            ApplyBatchItemStatus(_currentRunningPath, "cancelled");
            _currentRunningPath = null;
        }
    }

    private string BuildBatchSummary(bool cancelled)
    {
        var done = QueueRows.Count(r => r.Status == Loc.T("common.done"));
        var failed = QueueRows.Count(r => r.Status == Loc.T("common.error"));
        var aborted = QueueRows.Count(r => r.Status == Loc.T("common.aborted"));
        var pending = QueueRows.Count(r => r.Status == Loc.T("davincibatch.itemPending"));

        if (cancelled)
        {
            if (done > 0 || failed > 0)
            {
                return $"Batch abgebrochen — {done} fertig, {failed} fehlgeschlagen, {aborted + pending} ausstehend.";
            }

            return "Batch abgebrochen.";
        }

        if (failed == 0)
        {
            return $"Batch fertig — {done} ok.";
        }

        return $"Batch fertig — {done} ok, {failed} fehlgeschlagen.";
    }

    private void NotifyQueueEditCommands()
    {
        OnPropertyChanged(nameof(CanEditQueue));
        PickVideosCommand.NotifyCanExecuteChanged();
        PickFolderCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private async Task PickVideosAsync()
    {
        var paths = await FilePickerHelper.PickVideosAsync();
        if (paths.Count > 0)
        {
            AddVideos(paths);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private async Task PickFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var videos = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(VideoPathDropHelper.IsVideoFile)
            .ToList();
        if (videos.Count == 0)
        {
            Status = Loc.T("davincibatch.noVideosInFolder");
            return;
        }

        AddVideos(videos);
    }

    [RelayCommand]
    private async Task PickOutputDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(OutputDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputDir = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void RemoveSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _queue.Count)
        {
            return;
        }

        _queue.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        RefreshQueueRows();
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void ClearQueue()
    {
        _queue.Clear();
        _selectedIndex = -1;
        RefreshQueueRows();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        DavinciBatchRenderConfigReader.Save(new DavinciBatchRenderSettings
        {
            UiLanguage = UiLanguage,
            DavinciPreset = DavinciPreset,
            DavinciOutputDir = OutputDir,
            SafeOutput = SafeOutput,
        });
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private async Task RunBatchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_queue.Count == 0)
        {
            Status = Loc.T("davincibatch.queueEmpty");
            return;
        }

        SaveSettings();
        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;

        IsBusy = true;
        _currentRunningPath = null;
        Status = Loc.T("davincibatch.running");
        foreach (var row in QueueRows)
        {
            row.Status = Loc.T("davincibatch.itemPending");
        }

        AppServices.JobProgress.BeginBatch(_queue.Count, "Batch-Render");

        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_batch_render_{Guid.NewGuid():N}.json");
        var outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm_batch_render_out_{Guid.NewGuid():N}.json");

        var payload = new
        {
            videos = _queue,
            davinci_preset = DavinciPreset,
            davinci_output_dir = OutputDir,
            resolve_exe = DavinciResolvePaths.GetExePath(),
            resolve_modules = DavinciResolvePaths.GetApiModulesPath(),
            resolve_dll = DavinciResolvePaths.GetFusionScriptDll(),
            safe_output = SafeOutput,
            ui_language = UiLanguage,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "batch_render_job.py",
                ["--config-json", configPath, "--output-json", outputPath],
                token,
                onOutputLine: HandleBatchLogLine);

            if (result.ExitCode == -2)
            {
                MarkBatchCancelled();
                Status = BuildBatchSummary(cancelled: true);
            }
            else
            {
                if (File.Exists(outputPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
                        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            ApplyBatchResultsFromJson(items);
                        }
                    }
                    catch
                    {
                        // ignore parse errors
                    }
                }

                Status = BuildBatchSummary(cancelled: false);
            }
        }
        catch (OperationCanceledException)
        {
            MarkBatchCancelled();
            Status = BuildBatchSummary(cancelled: true);
        }
        finally
        {
            AppServices.JobProgress.EndBatch();
            IsBusy = false;
            _batchCts = null;
            _currentRunningPath = null;
            try
            {
                File.Delete(configPath);
                File.Delete(outputPath);
            }
            catch
            {
                // ignore
            }
        }
    }

    [RelayCommand]
    private void StopBatch()
    {
        _batchCts?.Cancel();
        Status = Loc.T("davincibatch.cancelRequested");
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
