using System.Globalization;
using System.Text.Json;

namespace HailMary.Services;

public sealed class LocalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string _language = "de";

    public event Action? LanguageChanged;

    public string Language => _language;

    public bool IsEnglish => _language.Equals("en", StringComparison.OrdinalIgnoreCase);

    public void LoadFromSettings()
    {
        var lang = NormalizeLanguage(AppServices.Settings.Current.UiLanguage);
        LoadLanguage(lang, notify: false);
    }

    public void LoadLanguage(string language, bool notify = true)
    {
        _language = NormalizeLanguage(language);
        _strings = LoadCatalog(_language);
        AppServices.Settings.Current.UiLanguage = _language;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(_language);
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(_language);
        }
        catch
        {
            // ignore invalid culture
        }

        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    public void SaveLanguage(string language)
    {
        _language = NormalizeLanguage(language);
        AppServices.Settings.Current.UiLanguage = _language;
        AppServices.Settings.Save();
        LoadLanguage(_language);
    }

    public string Get(string key, string? fallback = null)
    {
        if (_strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback ?? key;
    }

    public string ToolDescription(string toolId, string fallback) =>
        Get($"tool.{toolId}.description", fallback);

    public string ToolLabel(string toolId, string fallback) =>
        Get($"tool.{toolId}.label", fallback);

    public string GroupLabel(string groupId, string fallback) =>
        Get($"group.{groupId}", fallback);

    private static string NormalizeLanguage(string? language) =>
        language?.Trim().Equals("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "de";

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"UiStrings.{language}.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Assets", "UiStrings.de.json");
        }

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // fallback empty
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
