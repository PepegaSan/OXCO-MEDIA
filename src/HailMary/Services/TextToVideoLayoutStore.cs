using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class TextToVideoLayout
{
    public string Key { get; set; } = string.Empty;

    public string LastVideoPath { get; set; } = string.Empty;

    public List<TextOverlaySegment> OverlaySegments { get; set; } = [];
}

public static class TextToVideoLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string LayoutsDirectory =>
        Path.Combine(TextToVideoConfigReader.ConfigDirectory, "layouts");

    public static string Sanitize(string raw)
    {
        var forbidden = new HashSet<char>("\\/:*?\"<>|");
        var cleaned = new string(raw.Trim().Where(ch => !forbidden.Contains(ch)).ToArray());
        return cleaned.Trim().Trim('.');
    }

    public static string FilePath(string key)
    {
        var safe = Sanitize(key);
        if (string.IsNullOrWhiteSpace(safe))
        {
            return string.Empty;
        }

        return Path.Combine(LayoutsDirectory, safe + ".json");
    }

    public static bool Exists(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var path = FilePath(key);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static TextToVideoLayout? TryLoad(string key)
    {
        var path = FilePath(key);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return FromJson(doc.RootElement, key);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Save(string key, string lastVideoPath, IEnumerable<TextOverlaySegment> segments)
    {
        var path = FilePath(key);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(LayoutsDirectory);
        var payload = new Dictionary<string, object?>
        {
            ["key"] = key.Trim(),
            ["last_video_path"] = lastVideoPath ?? string.Empty,
            ["overlay_segments"] = segments.Select(s => s.ToJsonObject()).ToList(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void Delete(string key)
    {
        var path = FilePath(key);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    public static IReadOnlyList<string> ListKeys()
    {
        if (!Directory.Exists(LayoutsDirectory))
        {
            return [];
        }

        var keys = new List<string>();
        foreach (var file in Directory.GetFiles(LayoutsDirectory, "*.json"))
        {
            var fallback = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var key = doc.RootElement.TryGetProperty("key", out var k)
                    ? k.GetString()
                    : null;
                keys.Add(string.IsNullOrWhiteSpace(key) ? fallback : key.Trim());
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    keys.Add(fallback);
                }
            }
        }

        return keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One-shot: copy global overlay_segments onto the layout for last_video_path.
    /// </summary>
    public static void MigrateFromSettings(TextToVideoSettings settings)
    {
        if (settings.OverlaySegments.Count == 0)
        {
            return;
        }

        var key = TextToVideoLayoutKey.FromPath(settings.LastVideoPath);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!Exists(key))
        {
            Save(key, settings.LastVideoPath, settings.OverlaySegments);
        }

        settings.OverlaySegments.Clear();
    }

    private static TextToVideoLayout FromJson(JsonElement root, string fallbackKey)
    {
        var layout = new TextToVideoLayout
        {
            Key = root.TryGetProperty("key", out var keyEl)
                ? keyEl.GetString()?.Trim() ?? fallbackKey
                : fallbackKey,
            LastVideoPath = root.TryGetProperty("last_video_path", out var pathEl)
                ? pathEl.GetString() ?? string.Empty
                : string.Empty,
        };

        if (string.IsNullOrWhiteSpace(layout.Key))
        {
            layout.Key = fallbackKey;
        }

        if (root.TryGetProperty("overlay_segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    layout.OverlaySegments.Add(TextOverlaySegment.FromJson(item));
                }
            }
        }

        return layout;
    }
}
