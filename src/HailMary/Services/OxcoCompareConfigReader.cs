using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class OxcoCompareSettings
{
    public string CompareExportDir { get; set; } = string.Empty;

    public string LastSource { get; set; } = string.Empty;

    public string LastDeepfake { get; set; } = string.Empty;

    public string FilterBuffer { get; set; } = "2.0";

    public string FilterNoise { get; set; } = "15";

    public string FilterPixel { get; set; } = "200";

    public string FilterPixelMax { get; set; } = "0";

    public bool FilterFfmpeg { get; set; }

    public bool FilterDavinci { get; set; } = true;

    public string FilterFfmpegTarget { get; set; } = "deepfake";

    public string FilterDavinciTimeout { get; set; } = "1800";

    public bool FilterExportUnique { get; set; } = true;

    /// <summary>Batch: Analyse parallel zum laufenden DaVinci-Render (schneller, mehr CPU/GPU).</summary>
    public bool CompareBatchPipeline { get; set; } = true;

    public string DavinciRenderPreset { get; set; } = "AutoCutPreset";

    public string DavinciStartupWaitSeconds { get; set; } = "20";

    public string BitrateInDir { get; set; } = string.Empty;

    public string BitrateOutDir { get; set; } = string.Empty;

    public string TaggerInDir { get; set; } = string.Empty;

    public string TaggerOutDir { get; set; } = string.Empty;

    public string CompareSourceDir { get; set; } = string.Empty;

    public string CompareDeepfakeDir { get; set; } = string.Empty;

    public bool CompareRecursive { get; set; } = true;

    public string CompareSort { get; set; } = "date_desc";

    public string CompareGroup { get; set; } = "folder";

    public string TaggerPattern { get; set; } = "YYMMDDHHmmSS";

    public string TaggerTag { get; set; } = "[Stash]";

    public string TaggerProfileName { get; set; } = "Schritt1";

    public string TaggerKeep { get; set; } = "_hyb,_pro,_exp";

    public string TaggerIgnore { get; set; } = "_p";

    public string TaggerDrop { get; set; } = string.Empty;

    public bool TaggerRouteAuto { get; set; }

    public List<TagRouteRule> TaggerRouteRules { get; set; } = [];

    public string BrSuffix { get; set; } = "_bitrate";

    public bool BrRecursive { get; set; } = true;

    public bool BrOnlyLower { get; set; } = true;

    public bool BrOutputMp4 { get; set; }

    public string BrCodec { get; set; } = "libx264";

    public string BrAudio { get; set; } = "copy";

    public bool BrDeleteSourceAfterOk { get; set; } = true;

    public string BrPreset { get; set; } = "Standard";

    public Dictionary<string, string> BrRuleValues { get; set; } = OxcoBitratePresets.DefaultRuleValues();
}

public static class OxcoCompareConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Oxco");

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "oxco_config.json");

    public static string VendoredOxcoDirectory =>
        Path.Combine(AppPaths.BridgesDirectory, "vendor", "oxco");

    public static string SettingsIniPath =>
        Path.Combine(VendoredOxcoDirectory, "settings.ini");

    public static OxcoCompareSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var root = doc.RootElement;
                var settings = new OxcoCompareSettings
                {
                    CompareExportDir = Get(root, "CompareExportDir", "compare_export_dir"),
                    LastSource = Get(root, "LastSource", "last_source"),
                    LastDeepfake = Get(root, "LastDeepfake", "last_deepfake"),
                    FilterBuffer = Get(root, "FilterBuffer", "filter_buffer", "2.0"),
                    FilterNoise = Get(root, "FilterNoise", "filter_noise", "15"),
                    FilterPixel = Get(root, "FilterPixel", "filter_pixel", "200"),
                    FilterPixelMax = Get(root, "FilterPixelMax", "filter_pixel_max", "0"),
                    FilterFfmpeg = GetBool(root, "FilterFfmpeg", "filter_ffmpeg", false),
                    FilterDavinci = GetBool(root, "FilterDavinci", "filter_davinci", true),
                    FilterFfmpegTarget = Get(root, "FilterFfmpegTarget", "filter_ffmpeg_target", "deepfake"),
                    FilterDavinciTimeout = Get(root, "FilterDavinciTimeout", "filter_davinci_timeout", "1800"),
                    FilterExportUnique = GetBool(root, "FilterExportUnique", "filter_export_unique", true),
                    CompareBatchPipeline = GetBool(root, "CompareBatchPipeline", "compare_batch_pipeline", true),
                    DavinciRenderPreset = Get(root, "DavinciRenderPreset", "davinci_render_preset", "AutoCutPreset"),
                    DavinciStartupWaitSeconds = Get(root, "DavinciStartupWaitSeconds", "davinci_startup_wait", "20"),
                    BitrateInDir = Get(root, "BitrateInDir", "bitrate_in_dir"),
                    BitrateOutDir = Get(root, "BitrateOutDir", "bitrate_out_dir"),
                    TaggerInDir = Get(root, "TaggerInDir", "tagger_in_dir"),
                    TaggerOutDir = Get(root, "TaggerOutDir", "tagger_out_dir"),
                    CompareSourceDir = Get(root, "CompareSourceDir", "compare_source_dir"),
                    CompareDeepfakeDir = Get(root, "CompareDeepfakeDir", "compare_deepfake_dir"),
                    CompareRecursive = GetBool(root, "CompareRecursive", "compare_recursive", true),
                    CompareSort = Get(root, "CompareSort", "compare_sort", "date_desc"),
                    CompareGroup = Get(root, "CompareGroup", "compare_group", "folder"),
                    TaggerPattern = Get(root, "TaggerPattern", "tagger_pattern", "YYMMDDHHmmSS"),
                    TaggerTag = Get(root, "TaggerTag", "tagger_tag", "[Stash]"),
                    TaggerProfileName = Get(root, "TaggerProfileName", "tagger_profile_name", "Schritt1"),
                    TaggerKeep = Get(root, "TaggerKeep", "tagger_keep", "_hyb,_pro,_exp"),
                    TaggerIgnore = Get(root, "TaggerIgnore", "tagger_ignore", "_p"),
                    TaggerDrop = Get(root, "TaggerDrop", "tagger_drop"),
                    TaggerRouteAuto = GetBool(root, "TaggerRouteAuto", "tagger_route_auto", false),
                    TaggerRouteRules = ParseRouteRules(root),
                    BrSuffix = Get(root, "BrSuffix", "br_suffix", "_bitrate"),
                    BrRecursive = GetBool(root, "BrRecursive", "br_recursive", true),
                    BrOnlyLower = GetBool(root, "BrOnlyLower", "br_only_lower", true),
                    BrOutputMp4 = GetBool(root, "BrOutputMp4", "br_output_mp4", false),
                    BrCodec = Get(root, "BrCodec", "br_codec", "libx264"),
                    BrAudio = Get(root, "BrAudio", "br_audio", "copy"),
                    BrDeleteSourceAfterOk = GetBool(root, "BrDeleteSourceAfterOk", "br_delete_source_after_ok", true),
                    BrPreset = Get(root, "BrPreset", "br_preset", "Standard"),
                    BrRuleValues = ParseBrRuleValues(root),
                };

                if (string.IsNullOrWhiteSpace(settings.BitrateInDir))
                {
                    settings.BitrateInDir = settings.CompareExportDir;
                }

                if (string.IsNullOrWhiteSpace(settings.TaggerInDir))
                {
                    settings.TaggerInDir = settings.BitrateOutDir;
                }

                settings.DavinciRenderPreset = string.IsNullOrWhiteSpace(settings.DavinciRenderPreset)
                    ? "AutoCutPreset"
                    : settings.DavinciRenderPreset;

                return settings;
            }
        }
        catch
        {
            // fallback
        }

        return new OxcoCompareSettings();
    }

    public static void Save(OxcoCompareSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var payload = new Dictionary<string, object?>
        {
            ["CompareExportDir"] = settings.CompareExportDir,
            ["compare_export_dir"] = settings.CompareExportDir,
            ["LastSource"] = settings.LastSource,
            ["last_source"] = settings.LastSource,
            ["LastDeepfake"] = settings.LastDeepfake,
            ["last_deepfake"] = settings.LastDeepfake,
            ["FilterBuffer"] = settings.FilterBuffer,
            ["FilterNoise"] = settings.FilterNoise,
            ["FilterPixel"] = settings.FilterPixel,
            ["FilterPixelMax"] = settings.FilterPixelMax,
            ["FilterFfmpeg"] = settings.FilterFfmpeg,
            ["filter_ffmpeg"] = settings.FilterFfmpeg,
            ["FilterDavinci"] = settings.FilterDavinci,
            ["filter_davinci"] = settings.FilterDavinci,
            ["FilterFfmpegTarget"] = settings.FilterFfmpegTarget,
            ["FilterDavinciTimeout"] = settings.FilterDavinciTimeout,
            ["FilterExportUnique"] = settings.FilterExportUnique,
            ["filter_export_unique"] = settings.FilterExportUnique,
            ["CompareBatchPipeline"] = settings.CompareBatchPipeline,
            ["compare_batch_pipeline"] = settings.CompareBatchPipeline,
            ["DavinciRenderPreset"] = settings.DavinciRenderPreset,
            ["DavinciStartupWaitSeconds"] = settings.DavinciStartupWaitSeconds,
            ["BitrateInDir"] = settings.BitrateInDir,
            ["bitrate_in_dir"] = settings.BitrateInDir,
            ["BitrateOutDir"] = settings.BitrateOutDir,
            ["bitrate_out_dir"] = settings.BitrateOutDir,
            ["TaggerInDir"] = settings.TaggerInDir,
            ["tagger_in_dir"] = settings.TaggerInDir,
            ["TaggerOutDir"] = settings.TaggerOutDir,
            ["tagger_out_dir"] = settings.TaggerOutDir,
            ["CompareSourceDir"] = settings.CompareSourceDir,
            ["compare_source_dir"] = settings.CompareSourceDir,
            ["CompareDeepfakeDir"] = settings.CompareDeepfakeDir,
            ["compare_deepfake_dir"] = settings.CompareDeepfakeDir,
            ["CompareRecursive"] = settings.CompareRecursive,
            ["compare_recursive"] = settings.CompareRecursive,
            ["CompareSort"] = settings.CompareSort,
            ["compare_sort"] = settings.CompareSort,
            ["CompareGroup"] = settings.CompareGroup,
            ["compare_group"] = settings.CompareGroup,
            ["TaggerPattern"] = settings.TaggerPattern,
            ["tagger_pattern"] = settings.TaggerPattern,
            ["TaggerTag"] = settings.TaggerTag,
            ["tagger_tag"] = settings.TaggerTag,
            ["TaggerProfileName"] = settings.TaggerProfileName,
            ["tagger_profile_name"] = settings.TaggerProfileName,
            ["TaggerKeep"] = settings.TaggerKeep,
            ["tagger_keep"] = settings.TaggerKeep,
            ["TaggerIgnore"] = settings.TaggerIgnore,
            ["tagger_ignore"] = settings.TaggerIgnore,
            ["TaggerDrop"] = settings.TaggerDrop,
            ["tagger_drop"] = settings.TaggerDrop,
            ["TaggerRouteAuto"] = settings.TaggerRouteAuto,
            ["tagger_route_auto"] = settings.TaggerRouteAuto,
            ["tagger_route_rules"] = settings.TaggerRouteRules
                .Select(r => new Dictionary<string, string> { ["tag"] = r.Tag, ["folder"] = r.Folder })
                .ToList(),
            ["BrSuffix"] = settings.BrSuffix,
            ["br_suffix"] = settings.BrSuffix,
            ["BrRecursive"] = settings.BrRecursive,
            ["br_recursive"] = settings.BrRecursive,
            ["BrOnlyLower"] = settings.BrOnlyLower,
            ["br_only_lower"] = settings.BrOnlyLower,
            ["BrOutputMp4"] = settings.BrOutputMp4,
            ["br_output_mp4"] = settings.BrOutputMp4,
            ["BrCodec"] = settings.BrCodec,
            ["br_codec"] = settings.BrCodec,
            ["BrAudio"] = settings.BrAudio,
            ["br_audio"] = settings.BrAudio,
            ["BrDeleteSourceAfterOk"] = settings.BrDeleteSourceAfterOk,
            ["br_delete_source_after_ok"] = settings.BrDeleteSourceAfterOk,
            ["BrPreset"] = settings.BrPreset,
            ["br_preset"] = settings.BrPreset,
        };

        foreach (var threshold in OxcoBitratePresets.RuleOrder)
        {
            var key = threshold.ToString();
            var value = settings.BrRuleValues.GetValueOrDefault(key, OxcoBitratePresets.DefaultRuleValues()[key]);
            payload[$"br_rule_{threshold}"] = value;
            payload[$"BrRule_{threshold}"] = value;
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static Dictionary<string, string> ParseBrRuleValues(JsonElement root)
    {
        var defaults = OxcoBitratePresets.DefaultRuleValues();
        foreach (var threshold in OxcoBitratePresets.RuleOrder)
        {
            var snake = $"br_rule_{threshold}";
            var pascal = $"BrRule_{threshold}";
            if (root.TryGetProperty(snake, out var s))
            {
                defaults[threshold.ToString()] = s.GetString() ?? defaults[threshold.ToString()];
            }
            else if (root.TryGetProperty(pascal, out var p))
            {
                defaults[threshold.ToString()] = p.GetString() ?? defaults[threshold.ToString()];
            }
        }

        return defaults;
    }

    private static List<TagRouteRule> ParseRouteRules(JsonElement root)
    {
        if (!root.TryGetProperty("tagger_route_rules", out var snake) && !root.TryGetProperty("TaggerRouteRules", out snake))
        {
            return [];
        }

        if (snake.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<TagRouteRule>();
        foreach (var item in snake.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tag = item.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
            var folder = item.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(tag) && !string.IsNullOrWhiteSpace(folder))
            {
                list.Add(new TagRouteRule { Tag = tag.Trim(), Folder = folder.Trim() });
            }
        }

        return list;
    }

    private static string Get(JsonElement root, string pascal, string snake, string fallback = "") =>
        root.TryGetProperty(pascal, out var p) ? p.GetString() ?? fallback
        : root.TryGetProperty(snake, out var s) ? s.GetString() ?? fallback
        : fallback;

    private static bool GetBool(JsonElement root, string pascal, string snake, bool fallback)
    {
        if (root.TryGetProperty(pascal, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return p.GetBoolean();
        }

        if (root.TryGetProperty(snake, out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return s.GetBoolean();
        }

        return fallback;
    }
}
