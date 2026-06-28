namespace HailMary.Services;

public static class OxcoBitratePresets
{
    public static readonly int[] RuleOrder = [2160, 1440, 1080, 720, 480, 360, 0];

    public static readonly IReadOnlyList<string> PresetKeys = ["Standard", "Leicht reduziert", "Reduziert"];

    public static string LocalizePresetKey(string key) => key switch
    {
        "Standard" => Loc.T("oxco.brPresetStandard"),
        "Leicht reduziert" => Loc.T("oxco.brPresetLight"),
        "Reduziert" => Loc.T("oxco.brPresetReduced"),
        _ => key,
    };

    public static string? PresetKeyFromLabel(string label) =>
        PresetKeys.FirstOrDefault(k => LocalizePresetKey(k) == label);

    public static readonly IReadOnlyList<string> CodecOptions = ["libx264", "libx265", "h264_nvenc", "hevc_nvenc"];

    public static readonly IReadOnlyList<string> AudioOptions = ["copy", "aac_128k"];

    public static readonly Dictionary<string, Dictionary<int, int>> BuiltinPresets = new()
    {
        ["Standard"] = new()
        {
            [2160] = 12000, [1440] = 8000, [1080] = 5000,
            [720] = 2800, [480] = 1500, [360] = 900, [0] = 700,
        },
        ["Leicht reduziert"] = new()
        {
            [2160] = 8000, [1440] = 6000, [1080] = 4000,
            [720] = 2000, [480] = 1000, [360] = 800, [0] = 700,
        },
        ["Reduziert"] = new()
        {
            [2160] = 6000, [1440] = 4000, [1080] = 3000,
            [720] = 1500, [480] = 800, [360] = 600, [0] = 500,
        },
    };

    public static Dictionary<string, string> DefaultRuleValues() =>
        BuiltinPresets["Standard"]
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());
}
