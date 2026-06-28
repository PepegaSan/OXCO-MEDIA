using System.Text.Json;

namespace HailMary.Services;

public sealed class AutotaggerCategory
{
    public string Label { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;
}

public sealed class AutotaggerQueueItem
{
    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public string OutputFolder { get; set; } = string.Empty;
}

public sealed class AutotaggerSettings
{
    public string InputFolder { get; set; } = string.Empty;

    public string OutputFolder { get; set; } = string.Empty;

    public string KeepSuffix { get; set; } = "_hyb,_pro,_exp";

    public string IgnoreSuffix { get; set; } = "_p";

    public string DropSuffix { get; set; } = string.Empty;

    public string PatternToReplace { get; set; } = "YYMMDDHHmmSS";

    public bool ProcessExisting { get; set; }

    public List<AutotaggerQueueItem> Queue { get; set; } = [];
}

public static class AutotaggerConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ToolDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Watchdog tagger");

    public static string SettingsPath =>
        Path.Combine(ToolDirectory, "settings.json");

    public static string CategoriesPath =>
        Path.Combine(ToolDirectory, "categories.json");

    public static string MonitorConfigPath =>
        Path.Combine(AppPaths.SettingsDirectory, "autotagger_monitor.json");

    public static AutotaggerSettings Load()
    {
        var settings = new AutotaggerSettings();
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var root = doc.RootElement;
                settings.InputFolder = GetString(root, "input_folder");
                settings.OutputFolder = GetString(root, "output_folder");
                settings.KeepSuffix = GetString(root, "keep_suffix", settings.KeepSuffix);
                settings.IgnoreSuffix = GetString(root, "ignore_suffix", settings.IgnoreSuffix);
                settings.DropSuffix = GetString(root, "drop_suffix", settings.DropSuffix);
                settings.PatternToReplace = GetString(root, "pattern_to_replace", settings.PatternToReplace);
                settings.ProcessExisting = root.TryGetProperty("process_existing_on_start", out var pe) && pe.GetBoolean();
                if (root.TryGetProperty("profile_queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in queue.EnumerateArray())
                    {
                        settings.Queue.Add(new AutotaggerQueueItem
                        {
                            Name = GetString(item, "name"),
                            Tag = GetString(item, "tag"),
                            OutputFolder = GetString(item, "output_folder"),
                        });
                    }
                }
            }
        }
        catch
        {
            // fallback
        }

        return settings;
    }

    public static void Save(AutotaggerSettings settings)
    {
        Directory.CreateDirectory(ToolDirectory);
        Dictionary<string, object?> data = new();
        if (File.Exists(SettingsPath))
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(SettingsPath), JsonOptions)
                       ?? new Dictionary<string, object?>();
            }
            catch
            {
                data = new Dictionary<string, object?>();
            }
        }

        data["input_folder"] = settings.InputFolder;
        data["output_folder"] = settings.OutputFolder;
        data["keep_suffix"] = settings.KeepSuffix;
        data["ignore_suffix"] = settings.IgnoreSuffix;
        data["drop_suffix"] = settings.DropSuffix;
        data["pattern_to_replace"] = settings.PatternToReplace;
        data["process_existing_on_start"] = settings.ProcessExisting;
        data["profile_queue"] = settings.Queue.Select(q => new Dictionary<string, string>
        {
            ["name"] = q.Name,
            ["tag"] = q.Tag,
            ["output_folder"] = q.OutputFolder,
        }).ToList();

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, JsonOptions));
    }

    public static void SaveMonitorConfig(AutotaggerSettings settings)
    {
        Directory.CreateDirectory(AppPaths.SettingsDirectory);
        var payload = new Dictionary<string, object?>
        {
            ["input_folder"] = settings.InputFolder,
            ["output_folder"] = settings.OutputFolder,
            ["keep_suffix"] = settings.KeepSuffix,
            ["ignore_suffix"] = settings.IgnoreSuffix,
            ["drop_suffix"] = settings.DropSuffix,
            ["pattern_to_replace"] = settings.PatternToReplace,
            ["process_existing"] = settings.ProcessExisting,
            ["profiles"] = settings.Queue.Select(q => new Dictionary<string, string>
            {
                ["name"] = q.Name,
                ["tag"] = q.Tag,
                ["output_folder"] = string.IsNullOrWhiteSpace(q.OutputFolder) ? settings.OutputFolder : q.OutputFolder,
            }).ToList(),
        };
        File.WriteAllText(MonitorConfigPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static List<AutotaggerCategory> LoadCategories()
    {
        try
        {
            if (File.Exists(CategoriesPath))
            {
                var list = JsonSerializer.Deserialize<List<AutotaggerCategory>>(File.ReadAllText(CategoriesPath), JsonOptions);
                if (list is { Count: > 0 })
                {
                    return list;
                }
            }
        }
        catch
        {
            // fallback
        }

        return
        [
            new AutotaggerCategory { Label = "Music", Tag = "[Music]" },
            new AutotaggerCategory { Label = "Twitch", Tag = "[Twitch]" },
        ];
    }

    public static void SaveCategories(IReadOnlyList<AutotaggerCategory> categories)
    {
        Directory.CreateDirectory(ToolDirectory);
        File.WriteAllText(CategoriesPath, JsonSerializer.Serialize(categories, JsonOptions));
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;
}
