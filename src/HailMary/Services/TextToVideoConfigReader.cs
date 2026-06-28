using System.Collections.ObjectModel;
using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class TextToVideoSettings
{
    public string LastVideoDir { get; set; } = string.Empty;

    public string LastVideoPath { get; set; } = string.Empty;

    public string Codec { get; set; } = "H.264 (libx264)";

    public string Bitrate { get; set; } = "5000k";

    public string ExportContainer { get; set; } = "mp4";

    public int GifFps { get; set; } = 15;

    public int GifMaxWidth { get; set; } = 720;

    public int GifPaletteColors { get; set; } = 128;

    public bool AudioCopy { get; set; } = true;

    public string SrtPath { get; set; } = string.Empty;

    public string DavinciPreset { get; set; } = "YouTube - 1080p";

    public string DavinciOutputDir { get; set; } = string.Empty;

    public string ResolveModules { get; set; } = string.Empty;

    public string ResolveDll { get; set; } = string.Empty;

    public string ResolveExe { get; set; } = string.Empty;

    public List<TextOverlaySegment> OverlaySegments { get; set; } = [];
}

public static class TextToVideoConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Sub");

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "video_text_tool_settings.json");

    public static TextToVideoSettings Load()
    {
        var settings = new TextToVideoSettings();
        if (!File.Exists(ConfigPath))
        {
            return settings;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            ApplyJson(doc.RootElement, settings);
        }
        catch
        {
            // fallback defaults
        }

        return settings;
    }

    public static void Save(TextToVideoSettings settings)
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

        data["last_video_dir"] = settings.LastVideoDir;
        data["last_video_path"] = settings.LastVideoPath;
        data["codec"] = settings.Codec;
        data["bitrate"] = settings.Bitrate;
        data["export_container"] = settings.ExportContainer;
        data["gif_fps"] = settings.GifFps;
        data["gif_max_width"] = settings.GifMaxWidth;
        data["gif_palette_colors"] = settings.GifPaletteColors;
        data["audio_copy"] = settings.AudioCopy;
        data["srt_path"] = settings.SrtPath;
        data["davinci_preset"] = settings.DavinciPreset;
        data["davinci_output_dir"] = settings.DavinciOutputDir;
        data["resolve_modules"] = settings.ResolveModules;
        data["resolve_dll"] = settings.ResolveDll;
        data["resolve_exe"] = settings.ResolveExe;
        data["overlay_segments"] = settings.OverlaySegments.Select(s => s.ToJsonObject()).ToList();

        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOptions));
    }

    public static void ApplyToObservable(TextToVideoSettings settings, ObservableCollection<TextOverlaySegment> target)
    {
        target.Clear();
        foreach (var seg in settings.OverlaySegments)
        {
            target.Add(seg.Clone());
        }
    }

    private static void ApplyJson(JsonElement root, TextToVideoSettings settings)
    {
        settings.LastVideoDir = GetString(root, "last_video_dir");
        settings.LastVideoPath = GetString(root, "last_video_path");
        settings.Codec = NormalizeCodec(GetString(root, "codec", "H.264 (libx264)"));
        settings.Bitrate = GetString(root, "bitrate", "5000k");
        settings.ExportContainer = GetString(root, "export_container", "mp4");
        settings.GifFps = GetInt(root, "gif_fps", 15);
        settings.GifMaxWidth = GetInt(root, "gif_max_width", 720);
        settings.GifPaletteColors = GetInt(root, "gif_palette_colors", 128);
        settings.AudioCopy = !root.TryGetProperty("audio_copy", out var ac) || ac.ValueKind != JsonValueKind.False;
        settings.SrtPath = GetString(root, "srt_path");
        settings.DavinciPreset = GetString(root, "davinci_preset", "YouTube - 1080p");
        settings.DavinciOutputDir = GetString(root, "davinci_output_dir");
        settings.ResolveModules = GetString(root, "resolve_modules");
        settings.ResolveDll = GetString(root, "resolve_dll");
        settings.ResolveExe = GetString(root, "resolve_exe");

        settings.OverlaySegments.Clear();
        if (root.TryGetProperty("overlay_segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    settings.OverlaySegments.Add(TextOverlaySegment.FromJson(item));
                }
            }
        }
    }

    private static string NormalizeCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return "H.264 (libx264)";
        }

        return codec switch
        {
            "libx264" => "H.264 (libx264)",
            "libx265" => "H.265 / HEVC (libx265)",
            "libvpx-vp9" => "VP9 (libvpx-vp9)",
            "libsvtav1" => "AV1 (libsvtav1)",
            _ => codec,
        };
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var prop) ? prop.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var v) ? v : fallback;
}
