using System.Text.Json;

namespace HailMary.Services;

public sealed class MarkerUpdaterSettings
{
    public string Endpoint { get; set; } = "http://localhost:9999/graphql";

    public string ApiKey { get; set; } = string.Empty;

    public string LastSceneSearch { get; set; } = string.Empty;

    public StashPathMapSettings PathMap { get; set; } = new();
}

public static class MarkerUpdaterConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "stash_metadaten_updt");

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "app_config.json");

    public static MarkerUpdaterSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                return FromRoot(doc.RootElement);
            }
        }
        catch
        {
            // fallback
        }

        return new MarkerUpdaterSettings();
    }

    public static void Save(MarkerUpdaterSettings settings)
    {
        Dictionary<string, object?> data;
        if (File.Exists(ConfigPath))
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(ConfigPath))
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

        data["endpoint"] = settings.Endpoint;
        data["api_key"] = settings.ApiKey;
        data["last_scene_search"] = settings.LastSceneSearch;

        var markerPlayer = new Dictionary<string, object?>
        {
            ["path_prefix_remote"] = settings.PathMap.PathPrefixRemote,
            ["path_prefix_local"] = settings.PathMap.PathPrefixLocal,
            ["path_prefix_backup"] = settings.PathMap.PathPrefixBackup,
            ["use_backup"] = settings.PathMap.UseBackup,
        };
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("marker_player", out var el) && el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Name is not ("path_prefix_remote" or "path_prefix_local" or "path_prefix_backup" or "use_backup"))
                        {
                            markerPlayer[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => prop.Value.GetRawText(),
                            };
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // keep defaults
            }
        }

        data["marker_player"] = markerPlayer;

        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static MarkerUpdaterSettings FromRoot(JsonElement root)
    {
        var settings = new MarkerUpdaterSettings
        {
            Endpoint = GetString(root, "endpoint", "http://localhost:9999/graphql"),
            ApiKey = GetString(root, "api_key"),
            LastSceneSearch = GetString(root, "last_scene_search"),
        };

        if (root.TryGetProperty("marker_player", out var mp) && mp.ValueKind == JsonValueKind.Object)
        {
            settings.PathMap = new StashPathMapSettings
            {
                PathPrefixRemote = GetString(mp, "path_prefix_remote", "/data/"),
                PathPrefixLocal = GetString(mp, "path_prefix_local"),
                PathPrefixBackup = GetString(mp, "path_prefix_backup"),
                UseBackup = mp.TryGetProperty("use_backup", out var ub) && ub.GetBoolean(),
            };
        }

        return settings;
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;
}
