using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    public IReadOnlyList<string> BrPresetOptions =>
        OxcoBitratePresets.PresetKeys.Select(OxcoBitratePresets.LocalizePresetKey).ToList();

    public IReadOnlyList<string> BrCodecOptions => OxcoBitratePresets.CodecOptions;

    public IReadOnlyList<string> BrAudioOptions => OxcoBitratePresets.AudioOptions;

    [ObservableProperty] private bool _brRecursive = true;

    [ObservableProperty] private bool _brOnlyLower = true;

    [ObservableProperty] private bool _brOutputMp4;

    [ObservableProperty] private bool _brDeleteSourceAfterOk = true;

    [ObservableProperty] private string _brCodec = "libx264";

    [ObservableProperty] private string _brAudio = "copy";

    [ObservableProperty] private string _brPreset = Loc.T("bitrate.presetStandard");

    [ObservableProperty] private string _brRule2160 = "12000";

    [ObservableProperty] private string _brRule1440 = "8000";

    [ObservableProperty] private string _brRule1080 = "5000";

    [ObservableProperty] private string _brRule720 = "2800";

    [ObservableProperty] private string _brRule480 = "1500";

    [ObservableProperty] private string _brRule360 = "900";

    [ObservableProperty] private string _brRule0 = "700";

    internal void InitializeBitrateFromSettings(OxcoCompareSettings settings)
    {
        BrRecursive = settings.BrRecursive;
        BrOnlyLower = settings.BrOnlyLower;
        BrOutputMp4 = settings.BrOutputMp4;
        BrDeleteSourceAfterOk = settings.BrDeleteSourceAfterOk;
        BrCodec = settings.BrCodec;
        BrAudio = settings.BrAudio;
        BrPreset = OxcoBitratePresets.PresetKeys.Contains(settings.BrPreset)
            ? OxcoBitratePresets.LocalizePresetKey(settings.BrPreset)
            : OxcoBitratePresets.LocalizePresetKey(
                OxcoBitratePresets.PresetKeyFromLabel(settings.BrPreset) ?? settings.BrPreset);
        BrSuffix = settings.BrSuffix;
        BrRule2160 = settings.BrRuleValues.GetValueOrDefault("2160", "12000");
        BrRule1440 = settings.BrRuleValues.GetValueOrDefault("1440", "8000");
        BrRule1080 = settings.BrRuleValues.GetValueOrDefault("1080", "5000");
        BrRule720 = settings.BrRuleValues.GetValueOrDefault("720", "2800");
        BrRule480 = settings.BrRuleValues.GetValueOrDefault("480", "1500");
        BrRule360 = settings.BrRuleValues.GetValueOrDefault("360", "900");
        BrRule0 = settings.BrRuleValues.GetValueOrDefault("0", "700");
    }

    private Dictionary<string, string> BuildBrRuleValues() => new()
    {
        ["2160"] = BrRule2160.Trim(),
        ["1440"] = BrRule1440.Trim(),
        ["1080"] = BrRule1080.Trim(),
        ["720"] = BrRule720.Trim(),
        ["480"] = BrRule480.Trim(),
        ["360"] = BrRule360.Trim(),
        ["0"] = BrRule0.Trim(),
    };

    internal BitrateChangerSettings BuildBitrateChangerSettings()
    {
        var rules = BuildBrRuleValues();
        return new BitrateChangerSettings
        {
            InputFolder = BitrateInDir,
            OutputFolder = BitrateOutDir,
            Recursive = BrRecursive,
            OnlyLower = BrOnlyLower,
            OutputMp4 = BrOutputMp4,
            Codec = BrCodec,
            AudioMode = BrAudio,
            Suffix = string.IsNullOrWhiteSpace(BrSuffix) ? "_bitrate" : BrSuffix.Trim(),
            PostSuccessAction = BrDeleteSourceAfterOk ? "delete_original" : "keep",
            PresetName = OxcoBitratePresets.PresetKeyFromLabel(BrPreset) ?? BrPreset,
            RuleValues = rules,
        };
    }

    [RelayCommand]
    private void ApplyBrPreset()
    {
        if (!OxcoBitratePresets.BuiltinPresets.TryGetValue(
                OxcoBitratePresets.PresetKeyFromLabel(BrPreset) ?? BrPreset,
                out var preset))
        {
            return;
        }

        BrRule2160 = preset[2160].ToString();
        BrRule1440 = preset[1440].ToString();
        BrRule1080 = preset[1080].ToString();
        BrRule720 = preset[720].ToString();
        BrRule480 = preset[480].ToString();
        BrRule360 = preset[360].ToString();
        BrRule0 = preset[0].ToString();
    }
}
