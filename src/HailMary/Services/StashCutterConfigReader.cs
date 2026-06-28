using System.Text.Json;

namespace HailMary.Services;

public sealed class StashCutterSettings
{
    public string Endpoint { get; set; } = "http://localhost:9999/graphql";

    public string ApiKey { get; set; } = string.Empty;

    public string LastSceneSearch { get; set; } = string.Empty;

    public StashPathMapSettings PathMap { get; set; } = new();
}

public static class StashCutterConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath(string projectsRoot) =>
        CutterWorkspacePaths.StashCutter.ConfigPath(projectsRoot);

    public static StashCutterSettings Load(string projectsRoot)
    {
        var path = ConfigPath(projectsRoot);
        if (!File.Exists(path))
        {
            return new StashCutterSettings();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return FromRoot(doc.RootElement);
        }
        catch
        {
            return new StashCutterSettings();
        }
    }

    public static void Save(StashCutterSettings settings, CutterConfigReader.CutterConfig cutter, string projectsRoot)
    {
        var path = ConfigPath(projectsRoot);
        Dictionary<string, object?> data;
        if (File.Exists(path))
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path))
                       ?? new Dictionary<string, object?>();
            }
            catch (JsonException)
            {
                data = new Dictionary<string, object?>();
            }
        }
        else
        {
            data = new Dictionary<string, object?>();
        }

        data["stash_endpoint"] = settings.Endpoint;
        data["stash_api_key"] = settings.ApiKey;
        data["last_scene_search"] = settings.LastSceneSearch;
        data["path_prefix_remote"] = settings.PathMap.PathPrefixRemote;
        data["path_prefix_local"] = settings.PathMap.PathPrefixLocal;
        data["path_prefix_backup"] = settings.PathMap.PathPrefixBackup;
        data["use_backup"] = settings.PathMap.UseBackup ? "1" : "0";
        data["davinci_preset"] = cutter.DavinciPreset;
        data["davinci_output_dir"] = cutter.DavinciOutputDir;
        data["resolve_exe"] = cutter.ResolveExe;
        data["resolve_modules"] = cutter.ResolveModules;
        data["resolve_dll"] = cutter.ResolveDll;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static StashCutterSettings FromRoot(JsonElement root)
    {
        var settings = new StashCutterSettings
        {
            Endpoint = GetString(root, "stash_endpoint", "http://localhost:9999/graphql"),
            ApiKey = GetString(root, "stash_api_key"),
            LastSceneSearch = GetString(root, "last_scene_search"),
            PathMap = new StashPathMapSettings
            {
                PathPrefixRemote = GetString(root, "path_prefix_remote", "/data/"),
                PathPrefixLocal = GetString(root, "path_prefix_local"),
                PathPrefixBackup = GetString(root, "path_prefix_backup"),
                UseBackup = GetString(root, "use_backup") is "1" or "true",
            },
        };

        return settings;
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;
}
