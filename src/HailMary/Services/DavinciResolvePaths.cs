using HailMary.Models;

namespace HailMary.Services;

public static class DavinciResolvePaths
{
    public const string DefaultApiModulesPathWindows =
        @"C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting\Modules";

    public const string DefaultExePathWindows =
        @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe";

    public const string DefaultFusionScriptDllWindows =
        @"C:\Program Files\Blackmagic Design\DaVinci Resolve\fusionscript.dll";

    public static string GetApiModulesPath() =>
        ResolveApiPath(AppServices.Settings.Current.Davinci.ApiModulesPath);

    public static string GetExePath() =>
        ResolveExePath(AppServices.Settings.Current.Davinci.ExePath);

    public static string GetFusionScriptDll() =>
        ResolveFusionScriptDll(AppServices.Settings.Current.Davinci.FusionScriptDll);

    public static string ResolveApiPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var fromIni = ReadIniPath("davinci_api_path");
        if (!string.IsNullOrWhiteSpace(fromIni))
        {
            return fromIni;
        }

        return DefaultApiModulesPathWindows;
    }

    public static string ResolveExePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var fromIni = ReadIniPath("davinci_exe_path");
        if (!string.IsNullOrWhiteSpace(fromIni))
        {
            return fromIni;
        }

        return File.Exists(DefaultExePathWindows) ? DefaultExePathWindows : string.Empty;
    }

    public static string ResolveFusionScriptDll(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return File.Exists(DefaultFusionScriptDllWindows) ? DefaultFusionScriptDllWindows : string.Empty;
    }

    public static bool ApiScriptExists(string? apiPath = null)
    {
        var path = string.IsNullOrWhiteSpace(apiPath) ? GetApiModulesPath() : apiPath;
        return File.Exists(Path.Combine(path, "DaVinciResolveScript.py"));
    }

    public static void MigrateLegacyInto(AppSettings settings)
    {
        settings.Davinci ??= new DavinciResolveSettings();
        if (!string.IsNullOrWhiteSpace(settings.Davinci.ApiModulesPath)
            && !string.IsNullOrWhiteSpace(settings.Davinci.ExePath))
        {
            return;
        }

        TryMigrateFromOxcoConfig(settings);
        TryMigrateFromClipJoinerConfig(settings);
        TryMigrateFromBatchRenderConfig(settings);
    }

    private static void TryMigrateFromOxcoConfig(AppSettings settings)
    {
        var path = OxcoCompareConfigReader.ConfigPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            settings.Davinci.ApiModulesPath ??= GetJsonString(root, "DavinciApiPath", "davinci_api_path");
            settings.Davinci.ExePath ??= GetJsonString(root, "DavinciExePath", "davinci_exe_path");
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryMigrateFromClipJoinerConfig(AppSettings settings)
    {
        var path = ClipJoinerConfigReader.ConfigPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("davinci_api_path", out var api)
                && api.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                settings.Davinci.ApiModulesPath ??= api.GetString();
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryMigrateFromBatchRenderConfig(AppSettings settings)
    {
        var path = DavinciBatchRenderConfigReader.ConfigPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            settings.Davinci.ExePath ??= GetJsonString(root, "resolve_exe");
            settings.Davinci.ApiModulesPath ??= GetJsonString(root, "resolve_modules");
            settings.Davinci.FusionScriptDll ??= GetJsonString(root, "resolve_dll");
        }
        catch
        {
            // best-effort
        }
    }

    private static string? GetJsonString(System.Text.Json.JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop)
                && prop.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadIniPath(string key)
    {
        var iniPath = OxcoCompareConfigReader.SettingsIniPath;
        if (!File.Exists(iniPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var value = trimmed[(eq + 1)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }
}
