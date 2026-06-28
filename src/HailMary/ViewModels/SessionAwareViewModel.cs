using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.ViewModels;

public abstract partial class SessionAwareViewModel : ObservableObject
{
    protected SessionAwareViewModel()
    {
        AppServices.Session.SessionChanged += OnSessionChanged;
        RefreshFromSession();
    }

    [ObservableProperty]
    private string _inputPath = string.Empty;

    partial void OnInputPathChanged(string value) => OnInputPathUpdated(value);

    protected virtual void OnInputPathUpdated(string value)
    {
    }

    [ObservableProperty]
    private string _outputDir = string.Empty;

    [ObservableProperty]
    private string _lastOutput = string.Empty;

    protected virtual void RefreshFromSession()
    {
        var session = AppServices.Session.Current;
        InputPath = session.PrimaryInput;
        OutputDir = session.OutputDir ?? string.Empty;
        LastOutput = session.LastOutput ?? string.Empty;
    }

    private void OnSessionChanged()
    {
        UiDispatcher.Run(RefreshFromSession);
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
        foreach (var path in AppServices.Session.Current.InputPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                if (IsVideoFile(path))
                {
                    result.Add(Path.GetFullPath(path));
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
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
                // Ordner nicht lesbar — überspringen
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
}
