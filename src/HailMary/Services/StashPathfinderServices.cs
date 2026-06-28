using System.Text.Json;

namespace HailMary.Services;

public sealed class StashPathMapSettings
{
    public string PathPrefixRemote { get; set; } = "/data/";

    public string PathPrefixLocal { get; set; } = string.Empty;

    public string PathPrefixBackup { get; set; } = string.Empty;

    public bool UseBackup { get; set; }
}

public sealed class StashPathfinderSettings
{
    public string Endpoint { get; set; } = "http://localhost:9999/graphql";

    public string ApiKey { get; set; } = string.Empty;

    public string LastSceneSearch { get; set; } = string.Empty;

    public StashPathMapSettings PathMap { get; set; } = new();
}

public static class StashPathfinderConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Stash path copy");

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "app_config.json");

    public static StashPathfinderSettings Load()
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

        return new StashPathfinderSettings();
    }

    public static void Save(StashPathfinderSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var payload = new Dictionary<string, object?>
        {
            ["endpoint"] = settings.Endpoint,
            ["api_key"] = settings.ApiKey,
            ["language"] = "de",
            ["appearance"] = "dark",
            ["last_scene_search"] = settings.LastSceneSearch,
            ["path_map"] = new Dictionary<string, object?>
            {
                ["path_prefix_remote"] = settings.PathMap.PathPrefixRemote,
                ["path_prefix_local"] = settings.PathMap.PathPrefixLocal,
                ["path_prefix_backup"] = settings.PathMap.PathPrefixBackup,
                ["use_backup"] = settings.PathMap.UseBackup,
            },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static StashPathfinderSettings FromRoot(JsonElement root)
    {
        var settings = new StashPathfinderSettings
        {
            Endpoint = GetString(root, "endpoint", "http://localhost:9999/graphql"),
            ApiKey = GetString(root, "api_key"),
            LastSceneSearch = GetString(root, "last_scene_search"),
        };

        if (root.TryGetProperty("path_map", out var pathMap) && pathMap.ValueKind == JsonValueKind.Object)
        {
            settings.PathMap = new StashPathMapSettings
            {
                PathPrefixRemote = GetString(pathMap, "path_prefix_remote", "/data/"),
                PathPrefixLocal = GetString(pathMap, "path_prefix_local"),
                PathPrefixBackup = GetString(pathMap, "path_prefix_backup"),
                UseBackup = pathMap.TryGetProperty("use_backup", out var ub) && ub.GetBoolean(),
            };
        }

        return settings;
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;
}

public static class StashPathMapper
{
    public static string NormalizePathPrefix(string? path)
    {
        var p = (path ?? string.Empty).Trim();
        if (p.Length >= 3
            && char.IsLetter(p[0])
            && p[1] == '.'
            && (p[2] == '\\' || p[2] == '/'))
        {
            return $"{char.ToUpperInvariant(p[0])}:\\{p[3..].TrimStart('\\', '/')}";
        }

        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            return char.ToUpperInvariant(p[0]) + p[1..];
        }

        return p;
    }

    public static StashPathMapSettings NormalizeMap(StashPathMapSettings map) => new()
    {
        PathPrefixRemote = (map.PathPrefixRemote ?? string.Empty).Trim(),
        PathPrefixLocal = NormalizePathPrefix(map.PathPrefixLocal),
        PathPrefixBackup = NormalizePathPrefix(map.PathPrefixBackup),
        UseBackup = map.UseBackup,
    };

    public static string Apply(string path, StashPathMapSettings map, bool useBackup)
    {
        var raw = (path ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        if (LooksLikeWindowsPath(raw))
        {
            return Path.GetFullPath(raw);
        }

        var normalized = NormalizeMap(map);
        var remote = normalized.PathPrefixRemote;
        var local = normalized.PathPrefixLocal;
        var backup = normalized.PathPrefixBackup;
        var target = useBackup && !string.IsNullOrEmpty(backup) ? backup : local;

        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(target))
        {
            return raw;
        }

        if (!raw.StartsWith(remote, StringComparison.Ordinal))
        {
            return raw;
        }

        var suffix = raw[remote.Length..].TrimStart('/', '\\');
        var targetBase = target.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(suffix))
        {
            return Path.GetFullPath(targetBase);
        }

        suffix = suffix.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.Combine(targetBase, suffix);
        return Path.IsPathRooted(combined)
            ? Path.GetFullPath(combined)
            : combined;
    }

    public static bool BackupAvailable(StashPathMapSettings map)
    {
        var normalized = NormalizeMap(map);
        return !string.IsNullOrWhiteSpace(normalized.PathPrefixRemote)
               && !string.IsNullOrWhiteSpace(normalized.PathPrefixBackup)
               && Path.IsPathRooted(normalized.PathPrefixBackup);
    }

    private static bool LooksLikeWindowsPath(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'
        || path.StartsWith(@"\\", StringComparison.Ordinal);
}

public sealed class ResolvedMediaPath
{
    public string StashPath { get; init; } = string.Empty;

    public string ResolvedPath { get; init; } = string.Empty;

    public string SourceLabel { get; init; } = string.Empty;

    public bool FileExists { get; init; }
}

public static class StashPathResolver
{
    public static ResolvedMediaPath Resolve(string? stashPath, StashPathMapSettings map)
    {
        var raw = (stashPath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return new ResolvedMediaPath();
        }

        var normalized = StashPathMapper.NormalizeMap(map);
        var candidates = new List<(string Label, string Path)>();

        void AddCandidate(string label, bool useBackup)
        {
            var mapped = StashPathMapper.Apply(raw, normalized, useBackup);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                candidates.Add((label, mapped));
            }
        }

        AddCandidate(normalized.UseBackup ? "Backup" : "NAS", normalized.UseBackup);
        AddCandidate("NAS", false);
        AddCandidate("Backup", true);

        if (raw.Length >= 2 && (char.IsLetter(raw[0]) && raw[1] == ':' || raw.StartsWith(@"\\", StringComparison.Ordinal)))
        {
            candidates.Add(("Stash", Path.GetFullPath(raw)));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, candidate) in candidates)
        {
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return new ResolvedMediaPath
                {
                    StashPath = raw,
                    ResolvedPath = candidate,
                    SourceLabel = label,
                    FileExists = true,
                };
            }
        }

        var fallback = candidates.FirstOrDefault();
        return new ResolvedMediaPath
        {
            StashPath = raw,
            ResolvedPath = fallback.Path ?? string.Empty,
            SourceLabel = fallback.Label ?? "Gemappt",
            FileExists = !string.IsNullOrWhiteSpace(fallback.Path) && File.Exists(fallback.Path),
        };
    }
}

public static class StashSceneIdParser
{
    public static string? FromText(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var pattern in SceneIdPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                raw,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups[1].Value.Trim();
            if (IsValidSceneId(id))
            {
                return id;
            }
        }

        return IsValidSceneId(raw) ? raw : null;
    }

    public static string? FromHtml(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : FromText(html) ?? ExtractHrefSceneIds(html).FirstOrDefault();

    public static string? ResolveForLoad(
        string? clipboardText,
        string? sceneIdField,
        string? loadedSceneId,
        string? selectedSceneId)
    {
        return FromText(clipboardText)
               ?? FromText(sceneIdField)
               ?? NormalizeId(loadedSceneId)
               ?? NormalizeId(selectedSceneId);
    }

    private static string? NormalizeId(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        return IsValidSceneId(raw) ? raw : FromText(raw);
    }

    private static IEnumerable<string> ExtractHrefSceneIds(string html)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html,
            @"href=[""']([^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var sid = FromText(match.Groups[1].Value);
            if (sid is not null)
            {
                yield return sid;
            }
        }
    }

    private static bool IsValidSceneId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"^(\d+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static readonly string[] SceneIdPatterns =
    [
        @"(?:#/)?(?:/)?scenes/([^/?#""'\s]+)",
        @"(?:#/)?(?:/)?scene/([^/?#""'\s]+)",
        @"[?&](?:scene_?id|id)=([^&#""'\s]+)",
        @"(?:scene|szene|geladen|auswahl|loaded|id)[:\s#-]+(\d+|[0-9a-f-]{36})",
        @"^(\d+|[0-9a-f-]{36})\s*[|—-]",
    ];

    public static string? StashBrowserUrl(string endpoint, string sceneId)
    {
        var ep = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        var sid = (sceneId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(ep) || string.IsNullOrEmpty(sid))
        {
            return null;
        }

        if (ep.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            ep = ep[..^"/graphql".Length];
        }

        return $"{ep}/scenes/{sid}";
    }
}
