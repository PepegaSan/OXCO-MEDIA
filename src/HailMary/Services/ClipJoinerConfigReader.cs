using System.Text.Json;

namespace HailMary.Services;

public sealed class ClipJoinerSettings
{
    public string OutputDir { get; set; } = string.Empty;

    public string OutputName { get; set; } = "joined";

    public string Mode { get; set; } = "ffmpeg";

    public string FfmpegEncoder { get; set; } = "nvidia_h264";

    public string DavinciPreset { get; set; } = "YouTube - 1080p";

    public double DavinciTimeoutSeconds { get; set; } = 3600;
}

public static class ClipJoinerConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "clip-joiner", "clip_joiner_config.json");

    public static string FallbackConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HailMary",
            "clip_joiner_config.json");

    public static ClipJoinerSettings Load()
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

        return new ClipJoinerSettings();
    }

    public static void Save(ClipJoinerSettings settings)
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = ToJson(settings);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static ClipJoinerSettings FromJson(JsonElement root) => new()
    {
        OutputDir = root.TryGetProperty("output_dir", out var od) ? od.GetString() ?? "" : "",
        OutputName = root.TryGetProperty("output_name", out var on) ? on.GetString() ?? "joined" : "joined",
        Mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "ffmpeg" : "ffmpeg",
        FfmpegEncoder = root.TryGetProperty("ffmpeg_encoder", out var fe) ? fe.GetString() ?? "nvidia_h264" : "nvidia_h264",
        DavinciPreset = root.TryGetProperty("davinci_preset", out var dp) ? dp.GetString() ?? "YouTube - 1080p" : "YouTube - 1080p",
        DavinciTimeoutSeconds = root.TryGetProperty("davinci_timeout_s", out var dt) && dt.TryGetDouble(out var t) ? t : 3600,
    };

    private static Dictionary<string, object?> ToJson(ClipJoinerSettings settings) => new()
    {
        ["output_dir"] = settings.OutputDir,
        ["output_name"] = settings.OutputName,
        ["mode"] = settings.Mode,
        ["ffmpeg_encoder"] = settings.FfmpegEncoder,
        ["davinci_preset"] = settings.DavinciPreset,
        ["davinci_timeout_s"] = settings.DavinciTimeoutSeconds,
    };
}
