using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>
/// Pro-Tool Eingabe/Ausgabe — unabhängig von der globalen Session-Leiste oben.
/// </summary>
public abstract partial class ToolIoViewModel : ObservableObject
{
    private readonly string _toolId;
    private bool _loading;

    protected ToolIoViewModel(string toolId)
    {
        _toolId = toolId;
        LoadFromToolIo();
    }

    [ObservableProperty]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _outputDir = string.Empty;

    partial void OnInputPathChanged(string value)
    {
        if (!_loading)
        {
            PersistInputPath(value);
        }

        OnInputPathUpdated(value);
    }

    partial void OnOutputDirChanged(string value)
    {
        if (!_loading)
        {
            PersistOutputDir(value);
        }

        OnOutputDirUpdated(value);
    }

    protected virtual void OnOutputDirUpdated(string value)
    {
    }

    protected virtual void OnInputPathUpdated(string value)
    {
    }

    protected void LoadFromToolIo()
    {
        _loading = true;
        try
        {
            var state = AppServices.Session.GetToolIo(_toolId);
            InputPath = state.InputPath ?? string.Empty;
            OutputDir = state.OutputDir ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    protected IReadOnlyList<string> GetStoredInputPaths()
    {
        var state = AppServices.Session.GetToolIo(_toolId);
        return state.InputPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected void SetToolInputPaths(IEnumerable<string> paths)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppServices.Session.UpdateToolIo(_toolId, state => state.InputPaths = list);
        InputPath = list.FirstOrDefault() ?? string.Empty;
    }

    protected void AddToolInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);
        var existing = GetStoredInputPaths().ToList();
        if (!existing.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(full);
        }

        SetToolInputPaths(existing);
    }

    private void PersistInputPath(string value)
    {
        AppServices.Session.UpdateToolIo(_toolId, state =>
        {
            state.InputPath = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
            if (!string.IsNullOrWhiteSpace(state.InputPath)
                && !state.InputPaths.Contains(state.InputPath, StringComparer.OrdinalIgnoreCase))
            {
                state.InputPaths.Insert(0, state.InputPath);
            }
        });
    }

    private void PersistOutputDir(string value)
    {
        AppServices.Session.UpdateToolIo(_toolId, state =>
            state.OutputDir = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value));
    }

    protected string? RequireInputPath()
    {
        if (!string.IsNullOrWhiteSpace(InputPath) && File.Exists(InputPath))
        {
            return InputPath;
        }

        return null;
    }

    protected string? OptionalOutputDir() =>
        string.IsNullOrWhiteSpace(OutputDir) ? null : OutputDir;

    protected IReadOnlyList<string> GetBatchVideoPaths()
    {
        var result = new List<string>();
        foreach (var path in GetStoredInputPaths())
        {
            CollectVideosFromPath(path, result);
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(InputPath))
        {
            CollectVideosFromPath(InputPath, result);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectVideosFromPath(string path, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            if (IsVideoFile(path))
            {
                result.Add(Path.GetFullPath(path));
            }

            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (IsVideoFile(file))
                {
                    result.Add(Path.GetFullPath(file));
                }
            }
        }
        catch (IOException)
        {
            // Ordner nicht lesbar
        }
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task PickInputFileAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(InputPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SetToolInputPaths([path]);
        }
    }

    [RelayCommand]
    private async Task PickInputFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(
            string.IsNullOrWhiteSpace(InputPath) ? null : Path.GetDirectoryName(InputPath));
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddToolInputPath(path);
        }
    }

    [RelayCommand]
    private void ClearInput()
    {
        SetToolInputPaths([]);
        InputPath = string.Empty;
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

    public void ApplyDroppedInput(string path, bool allowFolders)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            if (allowFolders)
            {
                AddToolInputPath(full);
            }

            return;
        }

        if (File.Exists(full) && IsVideoFile(full))
        {
            SetToolInputPaths([full]);
        }
    }
}
