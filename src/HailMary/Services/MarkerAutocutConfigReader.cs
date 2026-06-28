using System.Text.Json;

namespace HailMary.Services;

public sealed class MarkerAutocutSettings
{
    public string LastNameFilter { get; set; } = string.Empty;

    public string LastPathFilter { get; set; } = string.Empty;

    public string LastNameExclude { get; set; } = string.Empty;

    public string LastPathExclude { get; set; } = string.Empty;

    public string MediaRoot { get; set; } = string.Empty;

    public string PathSortMode { get; set; } = "stash";

    public string ExportMode { get; set; } = "per_file";

    public string OutputDir { get; set; } = string.Empty;

    public string RenderPreset { get; set; } = string.Empty;

    public double MinSegmentSeconds { get; set; } = 1.0;

    public bool InclusiveEnd { get; set; } = true;

    public bool DoRender { get; set; }

    public double DefaultFps { get; set; } = 25;
}

public static class MarkerAutocutConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StashMarkerDaVinciExport",
            "settings.json");

    public static MarkerAutocutSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var root = doc.RootElement;
                return new MarkerAutocutSettings
                {
                    LastNameFilter = GetString(root, "last_name_filter"),
                    LastPathFilter = GetString(root, "last_path_filter"),
                    LastNameExclude = GetString(root, "filter_name_exclude"),
                    LastPathExclude = GetString(root, "filter_path_exclude"),
                    MediaRoot = GetString(root, "media_root"),
                    PathSortMode = GetString(root, "path_sort_mode", "stash"),
                    ExportMode = GetString(root, "export_mode", "per_file"),
                    OutputDir = GetString(root, "output_dir"),
                    RenderPreset = GetString(root, "render_preset"),
                    MinSegmentSeconds = GetDouble(root, "min_segment_seconds", 1.0),
                    InclusiveEnd = !root.TryGetProperty("inclusive_end", out var ie) || ie.GetBoolean(),
                    DoRender = root.TryGetProperty("do_render", out var dr) && dr.GetBoolean(),
                    DefaultFps = GetDouble(root, "default_fps", 25),
                };
            }
        }
        catch
        {
            // fallback
        }

        return new MarkerAutocutSettings();
    }

    public static void Save(MarkerAutocutSettings settings)
    {
        Dictionary<string, object?> data;
        if (File.Exists(ConfigPath))
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(ConfigPath))
                       ?? new Dictionary<string, object?>();
            }
            catch
            {
                data = new Dictionary<string, object?>();
            }
        }
        else
        {
            data = new Dictionary<string, object?>();
        }

        data["last_name_filter"] = settings.LastNameFilter;
        data["last_path_filter"] = settings.LastPathFilter;
        data["filter_name_exclude"] = settings.LastNameExclude;
        data["filter_path_exclude"] = settings.LastPathExclude;
        data["media_root"] = settings.MediaRoot;
        data["path_sort_mode"] = settings.PathSortMode;
        data["export_mode"] = settings.ExportMode;
        data["output_dir"] = settings.OutputDir;
        data["render_preset"] = settings.RenderPreset;
        data["min_segment_seconds"] = settings.MinSegmentSeconds;
        data["inclusive_end"] = settings.InclusiveEnd;
        data["do_render"] = settings.DoRender;
        data["default_fps"] = settings.DefaultFps;

        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;

    private static double GetDouble(JsonElement el, string name, double fallback) =>
        el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : fallback;
}
