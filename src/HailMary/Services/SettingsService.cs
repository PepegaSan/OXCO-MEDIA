using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();

    public string ProjectsRoot =>
        AppPaths.ResolveProjectsRoot(Current.ProjectsRoot);

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFilePath))
            {
                var json = File.ReadAllText(AppPaths.SettingsFilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }

            Current.Davinci ??= new DavinciResolveSettings();
            DavinciResolvePaths.MigrateLegacyInto(Current);
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFilePath, json);
    }

    public string ResolvePythonExecutable()
    {
        if (!string.IsNullOrWhiteSpace(Current.PythonExecutable) && File.Exists(Current.PythonExecutable))
        {
            return Current.PythonExecutable;
        }

        var fromEnv = Environment.GetEnvironmentVariable("HAIL_MARY_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var fromPath = FindOnPath("python.exe") ?? FindOnPath("python3.exe");
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "python";
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = Path.Combine(dir, fileName);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}
