using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    public string PrimaryActionLabel => Loc.T("oxco.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand => RunCompareCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && !IsCompareRunning;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy || IsCompareRunning;

    public bool HasVideoPreview => false;

    public bool HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CompareSortChoices));
        OnPropertyChanged(nameof(CompareGroupChoices));
        OnPropertyChanged(nameof(BrPresetOptions));
        CompareSortLabel = OxcoCompareListService.LocalizeSortMode(CompareSortMode);
        CompareGroupLabel = OxcoCompareListService.LocalizeGroupMode(CompareGroupMode);
        var presetKey = OxcoBitratePresets.PresetKeyFromLabel(BrPreset) ?? BrPreset;
        if (OxcoBitratePresets.PresetKeys.Contains(presetKey))
        {
            BrPreset = OxcoBitratePresets.LocalizePresetKey(presetKey);
        }

        OnPropertyChanged(nameof(MoveSelectedDeepfakesToBitrateLabel));
        OnPropertyChanged(nameof(MoveSelectedDeepfakesToTaggerLabel));
        OnPropertyChanged(nameof(TaggerPreviewPlayText));
        OnPropertyChanged(nameof(PreviewPlayButtonText));
        foreach (var row in BitrateRows)
        {
            row.ApplyLocalization();
        }

        RebuildDisplayLists();
    }

}
