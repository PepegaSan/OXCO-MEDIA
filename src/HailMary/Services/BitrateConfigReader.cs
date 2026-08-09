using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class BitrateChangerSettings
{
    public string InputFolder { get; set; } = string.Empty;

    public string OutputFolder { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    public bool OnlyLower { get; set; } = true;

    /// <summary>bitrate | problem_codecs | bitrate_or_codecs</summary>
    public string ScanMode { get; set; } = "bitrate";

    public bool OutputBesideSource { get; set; }

    public bool OutputMp4 { get; set; }

    public bool StripAutobitrateSuffix { get; set; }

    public bool RenameOnlyVideo { get; set; } = true;

    public string AudioMode { get; set; } = "copy";

    public string Codec { get; set; } = "libx264";

    /// <summary>bitrate | crf | auto (CRF nur bei problematischen Codecs)</summary>
    public string RateControl { get; set; } = "auto";

    public int Crf { get; set; } = 20;

    public string CrfPreset { get; set; } = "balanced";

    /// <summary>Gleichzeitige Encodes (1 = gaming-freundlich).</summary>
    public int ParallelJobs { get; set; } = 1;

    /// <summary>ffmpeg -threads (0 = auto).</summary>
    public int FfmpegThreads { get; set; } = 6;

    public bool LowPriority { get; set; } = true;

    /// <summary>libx264/libx265 preset: ultrafast…veryslow</summary>
    public string X264Preset { get; set; } = "veryfast";

    /// <summary>0 = Quell-FPS behalten; sonst Ziel-FPS (z. B. 30).</summary>
    public double TargetFps { get; set; }

    public string Suffix { get; set; } = "_bitrate";

    public string PostSuccessAction { get; set; } = "keep";

    public string PresetName { get; set; } = "Standard";

    public Dictionary<string, string> RuleValues { get; set; } = new()
    {
        ["2160"] = "12000", ["1440"] = "8000", ["1080"] = "5000",
        ["720"] = "2800", ["480"] = "1500", ["360"] = "900", ["0"] = "700",
    };

    public Dictionary<string, Dictionary<string, int>> CustomPresets { get; set; } = new();
}

public static class BitrateConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Videobitratechanger", "mass_bitrate_gui_config.json");

    public static BitrateChangerSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<BitrateChangerSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // fallback
        }

        return new BitrateChangerSettings();
    }

    public static void Save(BitrateChangerSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // ignore
        }
    }

    public static string ToJobJson(BitrateChangerSettings s) =>
        JsonSerializer.Serialize(new
        {
            input_folder = s.InputFolder,
            output_folder = s.OutputFolder,
            recursive = s.Recursive,
            only_lower = s.OnlyLower,
            scan_mode = s.ScanMode,
            audio_mode = s.AudioMode,
            codec = s.Codec,
            rate_control = s.RateControl,
            crf = s.Crf,
            crf_preset = s.CrfPreset,
            parallel_jobs = Math.Clamp(s.ParallelJobs <= 0 ? 1 : s.ParallelJobs, 1, 8),
            ffmpeg_threads = Math.Clamp(s.FfmpegThreads, 0, 64),
            low_priority = s.LowPriority,
            x264_preset = string.IsNullOrWhiteSpace(s.X264Preset) ? "veryfast" : s.X264Preset,
            target_fps = s.TargetFps,
            suffix = s.Suffix,
            output_mp4 = s.OutputMp4,
            strip_autobitrate_suffix = s.StripAutobitrateSuffix,
            post_success_action = s.PostSuccessAction,
            delete_source_after_ok = string.Equals(s.PostSuccessAction, "delete_original", StringComparison.OrdinalIgnoreCase),
            rule_values = s.RuleValues,
        }, JsonOptions);
}
