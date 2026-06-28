using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private OxcoCompareSettings _settings;

    public OxcoCompareViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = OxcoCompareConfigReader.Load();
        SourcePath = string.IsNullOrWhiteSpace(_settings.LastSource)
            ? AppServices.Session.Current.PrimaryInput
            : _settings.LastSource;
        DeepfakePath = _settings.LastDeepfake;
        CompareExportDir = _settings.CompareExportDir;
        FilterBuffer = _settings.FilterBuffer;
        FilterNoise = _settings.FilterNoise;
        FilterPixel = _settings.FilterPixel;
        FilterPixelMax = _settings.FilterPixelMax;
        FilterFfmpeg = _settings.FilterFfmpeg;
        FilterDavinci = _settings.FilterDavinci;
        FilterFfmpegTarget = _settings.FilterFfmpegTarget;
        FilterDavinciTimeout = _settings.FilterDavinciTimeout;
        FilterExportUnique = _settings.FilterExportUnique;
        CompareBatchPipeline = _settings.CompareBatchPipeline;
        DavinciRenderPreset = _settings.DavinciRenderPreset;
        DavinciStartupWaitSeconds = _settings.DavinciStartupWaitSeconds;
        BitrateInDir = _settings.BitrateInDir;
        BitrateOutDir = _settings.BitrateOutDir;
        TaggerInDir = _settings.TaggerInDir;
        TaggerOutDir = _settings.TaggerOutDir;
        CompareSourceDir = _settings.CompareSourceDir;
        CompareDeepfakeDir = _settings.CompareDeepfakeDir;
        CompareRecursive = _settings.CompareRecursive;
        CompareSortMode = _settings.CompareSort;
        CompareGroupMode = _settings.CompareGroup;
        TaggerPattern = _settings.TaggerPattern;
        CompareSortLabel = OxcoCompareListService.LocalizeSortMode(CompareSortMode);
        CompareGroupLabel = OxcoCompareListService.LocalizeGroupMode(CompareGroupMode);
        InitializePreviewFromFilters();
        InitializeBitrateFromSettings(_settings);
        InitializeTaggerFromSettings(_settings);
    }

    public string Description => ToolText.Description(_tool);

    public static IReadOnlyList<string> FfmpegTargetOptions { get; } = ["deepfake", "source", "both"];

    public IReadOnlyList<string> TargetChoices => FfmpegTargetOptions;

    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _deepfakePath = string.Empty;
    [ObservableProperty] private string _compareExportDir = string.Empty;
    [ObservableProperty] private string _filterBuffer = "2.0";
    [ObservableProperty] private string _filterNoise = "15";
    [ObservableProperty] private string _filterPixel = "200";
    [ObservableProperty] private string _filterPixelMax = "0";
    [ObservableProperty] private bool _filterFfmpeg;
    [ObservableProperty] private bool _filterDavinci = true;
    [ObservableProperty] private string _filterFfmpegTarget = "deepfake";
    [ObservableProperty] private string _filterDavinciTimeout = "1800";
    [ObservableProperty] private bool _filterExportUnique = true;
    [ObservableProperty] private bool _compareBatchPipeline = true;
    [ObservableProperty] private string _davinciRenderPreset = "AutoCutPreset";
    [ObservableProperty] private string _davinciStartupWaitSeconds = "20";
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;

    private void PersistSettings()
    {
        _settings = new OxcoCompareSettings
        {
            LastSource = SourcePath,
            LastDeepfake = DeepfakePath,
            CompareExportDir = CompareExportDir,
            FilterBuffer = FilterBuffer,
            FilterNoise = FilterNoise,
            FilterPixel = FilterPixel,
            FilterPixelMax = FilterPixelMax,
            FilterFfmpeg = FilterFfmpeg,
            FilterDavinci = FilterDavinci,
            FilterFfmpegTarget = FilterFfmpegTarget,
            FilterDavinciTimeout = FilterDavinciTimeout,
            FilterExportUnique = FilterExportUnique,
            CompareBatchPipeline = CompareBatchPipeline,
            DavinciRenderPreset = DavinciRenderPreset,
            DavinciStartupWaitSeconds = DavinciStartupWaitSeconds,
            BitrateInDir = BitrateInDir,
            BitrateOutDir = BitrateOutDir,
            TaggerInDir = TaggerInDir,
            TaggerOutDir = TaggerOutDir,
            CompareSourceDir = CompareSourceDir,
            CompareDeepfakeDir = CompareDeepfakeDir,
            CompareRecursive = CompareRecursive,
            CompareSort = CompareSortMode,
            CompareGroup = CompareGroupMode,
            TaggerPattern = TaggerPattern,
            TaggerTag = TaggerTag,
            TaggerProfileName = TaggerProfileName,
            TaggerKeep = TaggerKeep,
            TaggerIgnore = TaggerIgnore,
            TaggerDrop = TaggerDrop,
            TaggerRouteAuto = TaggerRouteAuto,
            TaggerRouteRules = TagRouteRules
                .Select(r => new TagRouteRule { Tag = r.Tag.Trim(), Folder = r.Folder.Trim() })
                .Where(r => !string.IsNullOrEmpty(r.Tag) && !string.IsNullOrEmpty(r.Folder))
                .ToList(),
            BrSuffix = BrSuffix,
            BrRecursive = BrRecursive,
            BrOnlyLower = BrOnlyLower,
            BrOutputMp4 = BrOutputMp4,
            BrCodec = BrCodec,
            BrAudio = BrAudio,
            BrDeleteSourceAfterOk = BrDeleteSourceAfterOk,
            BrPreset = OxcoBitratePresets.PresetKeyFromLabel(BrPreset) ?? BrPreset,
            BrRuleValues = BuildBrRuleValues(),
        };
        OxcoCompareConfigReader.Save(_settings);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        PersistSettings();
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private void UseSessionInput()
    {
        var input = AppServices.Session.Current.PrimaryInput;
        if (!string.IsNullOrWhiteSpace(input))
        {
            SourcePath = input;
            Status = Loc.T("oxco.sessionInputAdopted");
        }
        else
        {
            Status = Loc.T("oxco.noSessionInput");
        }
    }

    [RelayCommand]
    private async Task PickSourceAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(SourcePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourcePath = path;
        }
    }

    [RelayCommand]
    private async Task PickDeepfakeAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(DeepfakePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            DeepfakePath = path;
        }
    }

    [RelayCommand]
    private async Task PickExportDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(CompareExportDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CompareExportDir = path;
        }
    }

    [RelayCommand]
    private async Task RunCompareAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
        {
            Status = Loc.T("oxco.sourceVideoMissing");
            return;
        }

        if (string.IsNullOrWhiteSpace(DeepfakePath) || !File.Exists(DeepfakePath))
        {
            Status = Loc.T("oxco.deepfakeVideoMissing");
            return;
        }

        if (!File.Exists(OxcoCompareConfigReader.SettingsIniPath))
        {
            Status = $"settings.ini fehlt: {OxcoCompareConfigReader.SettingsIniPath}";
            return;
        }

        PersistSettings();
        IsBusy = true;
        Status = Loc.T("oxco.compareRunning");
        try
        {
            await RunCompareCoreAsync();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    [RelayCommand]
    private async Task OpenOxcoGuiAsync()
    {
        OpenFullGui();
        await Task.CompletedTask;
    }
}
