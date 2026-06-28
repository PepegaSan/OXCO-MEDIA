using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HailMary.ViewModels;

public sealed partial class AppSettingsViewModel : ObservableObject
{
    public GlobalStashSettingsViewModel Stash => AppServices.GlobalStashSettings;

    [ObservableProperty] private string _projectsRoot = string.Empty;

    [ObservableProperty] private string _pythonPath = string.Empty;

    [ObservableProperty] private string _davinciApiModulesPath = string.Empty;

    [ObservableProperty] private string _davinciExePath = string.Empty;

    [ObservableProperty] private string _davinciFusionScriptDll = string.Empty;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private UiTheme _selectedTheme = UiTheme.System;

    [ObservableProperty] private string _selectedLanguage = "de";

    public AppSettingsViewModel()
    {
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
    }

    public string Title => AppServices.Localization.Get("settings.title");

    public string AppearanceSection => AppServices.Localization.Get("settings.appearance");

    public string ThemeHeader => AppServices.Localization.Get("settings.theme");

    public string LanguageHeader => AppServices.Localization.Get("settings.language");

    public string SaveAppearanceLabel => AppServices.Localization.Get("settings.saveAppearance");

    public string GeneralSection => AppServices.Localization.Get("settings.general");

    public string ProjectsRootHeader => AppServices.Localization.Get("settings.projectsRoot");

    public string ProjectsRootHint => AppServices.Localization.Get("settings.projectsRootHint");

    public string PickFolderLabel => AppServices.Localization.Get("settings.pickFolder");

    public string SaveLabel => AppServices.Localization.Get("settings.save");

    public string PythonHeader => AppServices.Localization.Get("settings.pythonOptional");

    public string PickPythonLabel => AppServices.Localization.Get("settings.pickPython");

    public string DavinciSection => AppServices.Localization.Get("settings.davinci");

    public string DavinciHint => AppServices.Localization.Get("settings.davinciHint");

    public string DavinciModulesHeader => AppServices.Localization.Get("settings.davinciModules");

    public string DavinciExeHeader => AppServices.Localization.Get("settings.davinciExe");

    public string DavinciDllHeader => AppServices.Localization.Get("settings.davinciDll");

    public string StashSection => AppServices.Localization.Get("settings.stash");

    public string StashHint => AppServices.Localization.Get("settings.stashHint");

    public IReadOnlyList<UiTheme> ThemeChoices { get; } = [UiTheme.System, UiTheme.Light, UiTheme.Dark];

    public IReadOnlyList<string> LanguageChoices { get; } = ["de", "en"];

    public string ThemeDisplayName(UiTheme theme) => theme switch
    {
        UiTheme.Light => AppServices.Localization.Get("settings.theme.light"),
        UiTheme.Dark => AppServices.Localization.Get("settings.theme.dark"),
        _ => AppServices.Localization.Get("settings.theme.system"),
    };

    public string LanguageDisplayName(string code) => code switch
    {
        "en" => AppServices.Localization.Get("settings.language.en"),
        _ => AppServices.Localization.Get("settings.language.de"),
    };

    public void Load()
    {
        ProjectsRoot = AppServices.Settings.ProjectsRoot;
        PythonPath = AppServices.Settings.Current.PythonExecutable ?? AppServices.Settings.ResolvePythonExecutable();
        DavinciApiModulesPath = DavinciResolvePaths.GetApiModulesPath();
        DavinciExePath = DavinciResolvePaths.GetExePath();
        DavinciFusionScriptDll = DavinciResolvePaths.GetFusionScriptDll();
        SelectedTheme = AppServices.Settings.Current.UiTheme;
        SelectedLanguage = AppServices.Localization.Language;
        Stash.ReloadFromService();
        RefreshLocalizedLabels();
    }

    [RelayCommand]
    private async Task PickProjectsRootAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(App.Window);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        ProjectsRoot = folder.Path;
    }

    [RelayCommand]
    private async Task PickPythonAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        var hwnd = WindowNative.GetWindowHandle(App.Window);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        PythonPath = file.Path;
    }

    [RelayCommand]
    private async Task PickDavinciApiModulesAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(DavinciApiModulesPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            DavinciApiModulesPath = path;
        }
    }

    [RelayCommand]
    private async Task PickDavinciExeAsync()
    {
        var path = await FilePickerHelper.PickAnyFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            DavinciExePath = path;
        }
    }

    [RelayCommand]
    private async Task PickDavinciFusionDllAsync()
    {
        var path = await FilePickerHelper.PickAnyFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            DavinciFusionScriptDll = path;
        }
    }

    [RelayCommand]
    private void SaveGeneral()
    {
        AppServices.Settings.Current.ProjectsRoot = ProjectsRoot.Trim();
        AppServices.Settings.Current.PythonExecutable = PythonPath.Trim();
        AppServices.Settings.Save();
        Status = AppServices.Localization.Get("settings.generalSaved");
        AppServices.Log.Info($"Projects-Root: {AppServices.Settings.ProjectsRoot}");
    }

    [RelayCommand]
    private void SaveDavinci()
    {
        AppServices.Settings.Current.Davinci.ApiModulesPath = DavinciApiModulesPath.Trim();
        AppServices.Settings.Current.Davinci.ExePath = DavinciExePath.Trim();
        AppServices.Settings.Current.Davinci.FusionScriptDll = DavinciFusionScriptDll.Trim();
        AppServices.Settings.Save();
        Status = AppServices.Localization.Get("settings.davinciSaved");
    }

    [RelayCommand]
    private void SaveAppearance()
    {
        AppServices.Settings.Current.UiTheme = SelectedTheme;
        AppServices.Settings.Save();
        AppServices.Theme.Apply(SelectedTheme);

        if (!SelectedLanguage.Equals(AppServices.Localization.Language, StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Localization.SaveLanguage(SelectedLanguage);
        }

        Status = AppServices.Localization.Get("settings.appearanceSaved");
    }

    private void OnLanguageChanged() => RefreshLocalizedLabels();

    private void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AppearanceSection));
        OnPropertyChanged(nameof(ThemeHeader));
        OnPropertyChanged(nameof(LanguageHeader));
        OnPropertyChanged(nameof(SaveAppearanceLabel));
        OnPropertyChanged(nameof(GeneralSection));
        OnPropertyChanged(nameof(ProjectsRootHeader));
        OnPropertyChanged(nameof(ProjectsRootHint));
        OnPropertyChanged(nameof(PickFolderLabel));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(PythonHeader));
        OnPropertyChanged(nameof(PickPythonLabel));
        OnPropertyChanged(nameof(DavinciSection));
        OnPropertyChanged(nameof(DavinciHint));
        OnPropertyChanged(nameof(DavinciModulesHeader));
        OnPropertyChanged(nameof(DavinciExeHeader));
        OnPropertyChanged(nameof(DavinciDllHeader));
        OnPropertyChanged(nameof(StashSection));
        OnPropertyChanged(nameof(StashHint));
    }
}
