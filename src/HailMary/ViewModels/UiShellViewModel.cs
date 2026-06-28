using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>Shell strings for MainPage and settings — updates when language changes.</summary>
public sealed partial class UiShellViewModel : ObservableObject
{
    public UiShellViewModel()
    {
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
        RefreshAll();
    }

    public string AppTitle => AppServices.Localization.Get("app.title", "Hail Mary");

    public string SettingsTooltip => AppServices.Localization.Get("main.settingsTooltip");

    public string LogHeader => AppServices.Localization.Get("main.logHeader");

    public string LogClear => AppServices.Localization.Get("main.logClear");

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public IReadOnlyList<string> LanguageOptions { get; } = ["de", "en"];

    public string ThemeOptionLabel(string option) => option switch
    {
        "System" => AppServices.Localization.Get("settings.theme.system"),
        "Light" => AppServices.Localization.Get("settings.theme.light"),
        "Dark" => AppServices.Localization.Get("settings.theme.dark"),
        _ => option,
    };

    public string LanguageOptionLabel(string code) => code switch
    {
        "en" => AppServices.Localization.Get("settings.language.en"),
        _ => AppServices.Localization.Get("settings.language.de"),
    };

    private void OnLanguageChanged() => RefreshAll();

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(SettingsTooltip));
        OnPropertyChanged(nameof(LogHeader));
        OnPropertyChanged(nameof(LogClear));
    }
}
