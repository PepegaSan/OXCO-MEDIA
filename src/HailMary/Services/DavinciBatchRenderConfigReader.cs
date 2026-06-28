using System.Text.Json;

namespace HailMary.Services;

public sealed class DavinciBatchRenderSettings
{
    public string UiLanguage { get; set; } = "de";

    public string DavinciPreset { get; set; } = "YouTube - 1080p";

    public string DavinciOutputDir { get; set; } = string.Empty;

    public bool SafeOutput { get; set; } = true;
}

public static class DavinciBatchRenderConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Davinci Batch Render", "batch_app_config.json");

    public static string FallbackConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HailMary",
            "batch_app_config.json");

    public static DavinciBatchRenderSettings Load()
    {
        foreach (var path in new[] { ConfigPath, FallbackConfigPath })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return FromJson(doc.RootElement);
            }
            catch
            {
                // try next
            }
        }

        return new DavinciBatchRenderSettings();
    }

    public static void Save(DavinciBatchRenderSettings settings)
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = ToJson(settings);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static DavinciBatchRenderSettings FromJson(JsonElement root) => new()
    {
        UiLanguage = root.TryGetProperty("ui_language", out var lang) ? lang.GetString() ?? "de" : "de",
        DavinciPreset = root.TryGetProperty("davinci_preset", out var preset) ? preset.GetString() ?? "YouTube - 1080p" : "YouTube - 1080p",
        DavinciOutputDir = root.TryGetProperty("davinci_output_dir", out var outDir) ? outDir.GetString() ?? "" : "",
        SafeOutput = !root.TryGetProperty("safe_output", out var safe) || safe.GetBoolean(),
    };

    private static Dictionary<string, object?> ToJson(DavinciBatchRenderSettings settings) => new()
    {
        ["ui_language"] = settings.UiLanguage,
        ["davinci_preset"] = settings.DavinciPreset,
        ["davinci_output_dir"] = settings.DavinciOutputDir,
        ["safe_output"] = settings.SafeOutput,
    };
}
