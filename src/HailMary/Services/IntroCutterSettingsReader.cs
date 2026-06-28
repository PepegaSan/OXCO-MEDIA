using System.Text.Json;

namespace HailMary.Services;

public static class IntroCutterSettingsReader
{
    public sealed record IntroOutroPreset(string Name, double IntroSec, double OutroSec);

    public sealed record IntroCutterSettings(
        double IntroSec,
        double OutroSec,
        bool UseResolve,
        string VideoCodec,
        string VideoBitrate,
        bool VideoBitrateAuto,
        string AudioCodec,
        string AudioBitrate,
        string RenderPreset,
        string SelectedPreset,
        string OutputDir,
        bool OutputBesideSource,
        IReadOnlyList<IntroOutroPreset> Presets);

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IntroCutter",
            "settings.json");

    public static IntroCutterSettings Load()
    {
        const double defaultIntro = 3.0;
        const double defaultOutro = 2.0;
        var presets = new List<IntroOutroPreset>
        {
            new("Standard", defaultIntro, defaultOutro),
        };

        if (!File.Exists(SettingsPath))
        {
            return new IntroCutterSettings(
                defaultIntro, defaultOutro, false, "libx264", "8M", false, "aac", "192k", "YouTube - 1080p", "Standard", "", true, presets);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;

            presets = ParsePresets(root);
            var selected = GetString(root, "selected_intro_outro_preset", "Standard");
            if (presets.All(p => p.Name != selected) && presets.Count > 0)
            {
                selected = presets[0].Name;
            }

            var active = presets.FirstOrDefault(p => p.Name == selected);
            var intro = active?.IntroSec ?? GetDouble(root, "intro_sec", defaultIntro);
            var outro = active?.OutroSec ?? GetDouble(root, "outro_sec", defaultOutro);

            var useResolve = string.Equals(GetString(root, "mode", "ffmpeg"), "resolve", StringComparison.OrdinalIgnoreCase);

            var outputDir = GetString(root, "output_dir", "");
            var besideSource = !root.TryGetProperty("output_beside_source", out var obs)
                ? string.IsNullOrWhiteSpace(outputDir)
                : obs.ValueKind == JsonValueKind.True;

            return new IntroCutterSettings(
                intro,
                outro,
                useResolve,
                GetString(root, "video_codec", "libx264"),
                GetString(root, "video_bitrate", "8M"),
                root.TryGetProperty("video_bitrate_auto", out var vba) && vba.ValueKind == JsonValueKind.True,
                GetString(root, "audio_codec", "aac"),
                GetString(root, "audio_bitrate", "192k"),
                GetString(root, "render_preset", "YouTube - 1080p"),
                selected,
                outputDir,
                besideSource,
                presets);
        }
        catch (JsonException)
        {
            return new IntroCutterSettings(
                defaultIntro, defaultOutro, false, "libx264", "8M", false, "aac", "192k", "YouTube - 1080p", "Standard", "", true, presets);
        }
    }

    public static void SavePreset(string name, double introSec, double outroSec, string selectedPreset)
    {
        var data = LoadMutable();
        var presets = ParsePresetsFromDict(data);
        presets[name] = new Dictionary<string, object>
        {
            ["intro_sec"] = introSec,
            ["outro_sec"] = outroSec,
        };

        data["intro_outro_presets"] = presets;
        data["selected_intro_outro_preset"] = selectedPreset;
        data["intro_sec"] = introSec;
        data["outro_sec"] = outroSec;
        WriteMutable(data);
    }

    public static void SaveRunSettings(
        double introSec,
        double outroSec,
        string selectedPreset,
        bool useResolve,
        string videoCodec,
        string videoBitrate,
        bool videoBitrateAuto,
        string audioCodec,
        string audioBitrate,
        string renderPreset,
        string? outputDir,
        bool outputBesideSource)
    {
        var data = LoadMutable();
        data["intro_sec"] = introSec;
        data["outro_sec"] = outroSec;
        data["selected_intro_outro_preset"] = selectedPreset;
        data["mode"] = useResolve ? "resolve" : "ffmpeg";
        data["video_codec"] = videoCodec;
        data["video_bitrate"] = videoBitrate;
        data["video_bitrate_auto"] = videoBitrateAuto;
        data["audio_codec"] = audioCodec;
        data["audio_bitrate"] = audioBitrate;
        data["render_preset"] = renderPreset;
        data["output_beside_source"] = outputBesideSource;
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            data["output_dir"] = outputDir;
        }

        WriteMutable(data);
    }

    public static void SaveOutputSettings(string? outputDir, bool outputBesideSource)
    {
        var data = LoadMutable();
        data["output_beside_source"] = outputBesideSource;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            data.Remove("output_dir");
        }
        else
        {
            data["output_dir"] = outputDir;
        }

        WriteMutable(data);
    }

    private static Dictionary<string, object?> LoadMutable()
    {
        if (!File.Exists(SettingsPath))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(SettingsPath))
                   ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static void WriteMutable(Dictionary<string, object?> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void DeletePreset(string name)
    {
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(SettingsPath))
                       ?? new Dictionary<string, object?>();
            var presets = ParsePresetsFromDict(data);
            presets.Remove(name);
            if (presets.Count == 0)
            {
                presets["Standard"] = new Dictionary<string, object>
                {
                    ["intro_sec"] = 3.0,
                    ["outro_sec"] = 2.0,
                };
            }

            data["intro_outro_presets"] = presets;
            var selected = GetStringFromDict(data, "selected_intro_outro_preset", "Standard");
            if (!presets.ContainsKey(selected))
            {
                selected = presets.Keys.First();
                data["selected_intro_outro_preset"] = selected;
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            // best-effort
        }
    }

    private static List<IntroOutroPreset> ParsePresets(JsonElement root)
    {
        var list = new List<IntroOutroPreset>();
        if (!root.TryGetProperty("intro_outro_presets", out var presetsEl) || presetsEl.ValueKind != JsonValueKind.Object)
        {
            return list.Count > 0 ? list : [new IntroOutroPreset("Standard", 3.0, 2.0)];
        }

        foreach (var prop in presetsEl.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new IntroOutroPreset(
                prop.Name,
                GetDouble(prop.Value, "intro_sec", 0),
                GetDouble(prop.Value, "outro_sec", 0)));
        }

        return list.Count > 0 ? list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : [new IntroOutroPreset("Standard", 3.0, 2.0)];
    }

    private static Dictionary<string, object> ParsePresetsFromDict(Dictionary<string, object?> data)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!data.TryGetValue("intro_outro_presets", out var raw) || raw is null)
        {
            return result;
        }

        var json = JsonSerializer.Serialize(raw);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText()) ?? new object();
        }

        return result;
    }

    private static string GetString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? fallback
            : fallback;

    private static string GetStringFromDict(Dictionary<string, object?> data, string name, string fallback) =>
        data.TryGetValue(name, out var val) && val is string s ? s : fallback;

    private static double GetDouble(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var prop))
        {
            return fallback;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDouble(),
            JsonValueKind.String when double.TryParse(prop.GetString()?.Replace(",", "."),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback,
        };
    }
}
