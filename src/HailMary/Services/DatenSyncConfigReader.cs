using System.Text.Json;

namespace HailMary.Services;

public sealed class DatenSyncProfile
{
    public string EditorSource { get; set; } = string.Empty;

    public string EditorTarget { get; set; } = string.Empty;

    public string IntervalMinutes { get; set; } = "60";

    public Dictionary<string, bool> Options { get; set; } = DatenSyncConfigReader.DefaultOptions();

    public List<DatenSyncJob> Jobs { get; set; } = [];

    public bool LogToFile { get; set; }

    public string LogFilePath { get; set; } = string.Empty;

    public string SelectedProfile { get; set; } = string.Empty;

    public string StartupProfile { get; set; } = string.Empty;
}

public static class DatenSyncConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string SyncRoot =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "Sync");

    public static string DefaultProfilePath =>
        Path.Combine(SyncRoot, "default_profile.json");

    public static string ProfilesDirectory =>
        Path.Combine(SyncRoot, "profiles");

    public static Dictionary<string, bool> DefaultOptions() => new(StringComparer.Ordinal)
    {
        ["/MIR"] = true,
        ["/E"] = false,
        ["/XO"] = false,
        ["/COPY:DAT"] = false,
        ["/Z"] = true,
        ["/W:2"] = true,
        ["/R:3"] = true,
        ["/NP"] = true,
        ["/L"] = false,
    };

    public static IReadOnlyList<(string LabelKey, string Switch)> OptionDefinitions { get; } =
    [
        ("datensync.optionMir", "/MIR"),
        ("datensync.optionE", "/E"),
        ("datensync.optionXo", "/XO"),
        ("datensync.optionCopyDat", "/COPY:DAT"),
        ("datensync.optionZ", "/Z"),
        ("datensync.optionW2", "/W:2"),
        ("datensync.optionR3", "/R:3"),
        ("datensync.optionNp", "/NP"),
        ("datensync.optionL", "/L"),
    ];

    public static DatenSyncProfile Load()
    {
        try
        {
            if (File.Exists(DefaultProfilePath))
            {
                var json = File.ReadAllText(DefaultProfilePath);
                using var doc = JsonDocument.Parse(json);
                return FromPayload(doc.RootElement);
            }
        }
        catch
        {
            // fallback
        }

        return new DatenSyncProfile();
    }

    public static DatenSyncProfile LoadNamedProfile(string profileName)
    {
        var path = Path.Combine(ProfilesDirectory, $"{SanitizeProfileName(profileName)}.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(Loc.F("datensync.profileNotFound", path));
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return FromPayload(doc.RootElement);
    }

    public static void Save(DatenSyncProfile profile, string? profileName = null)
    {
        Directory.CreateDirectory(SyncRoot);
        var payload = ToPayload(profile);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            File.WriteAllText(DefaultProfilePath, json);
            return;
        }

        Directory.CreateDirectory(ProfilesDirectory);
        var safe = SanitizeProfileName(profileName);
        File.WriteAllText(Path.Combine(ProfilesDirectory, $"{safe}.json"), json);
    }

    public static IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(ProfilesDirectory))
        {
            return [];
        }

        return Directory.GetFiles(ProfilesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => n!)
            .ToList();
    }

    public static string SanitizeProfileName(string raw)
    {
        var forbidden = new HashSet<char>("\\/:*?\"<>|");
        var cleaned = new string(raw.Trim().Where(ch => !forbidden.Contains(ch)).ToArray());
        return cleaned.Trim().Trim('.');
    }

    public static IReadOnlyList<string> BuildSwitches(Dictionary<string, bool> options)
    {
        var result = new List<string>();
        foreach (var pair in options)
        {
            if (pair.Value)
            {
                result.Add(pair.Key);
            }
        }

        return result;
    }

    private static DatenSyncProfile FromPayload(JsonElement payload)
    {
        var profile = new DatenSyncProfile
        {
            EditorSource = payload.TryGetProperty("editor_source", out var es) ? es.GetString() ?? "" : "",
            EditorTarget = payload.TryGetProperty("editor_target", out var et) ? et.GetString() ?? "" : "",
            IntervalMinutes = payload.TryGetProperty("interval_minutes", out var im) ? im.GetString() ?? "60" : "60",
            LogToFile = payload.TryGetProperty("log_to_file", out var ltf) && ltf.GetBoolean(),
            LogFilePath = payload.TryGetProperty("log_file_path", out var lfp) ? lfp.GetString() ?? "" : "",
            SelectedProfile = payload.TryGetProperty("selected_profile", out var sp) ? sp.GetString() ?? "" : "",
            StartupProfile = payload.TryGetProperty("startup_profile", out var stp) ? stp.GetString() ?? "" : "",
        };

        profile.Options = DefaultOptions();
        if (payload.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in opts.EnumerateObject())
            {
                profile.Options[prop.Name] = prop.Value.GetBoolean();
            }
        }

        profile.Jobs = [];
        if (payload.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                var source = job.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                var target = job.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
                {
                    profile.Jobs.Add(new DatenSyncJob { Source = source, Target = target });
                }
            }
        }

        return profile;
    }

    private static Dictionary<string, object?> ToPayload(DatenSyncProfile profile) => new()
    {
        ["editor_source"] = profile.EditorSource,
        ["editor_target"] = profile.EditorTarget,
        ["interval_minutes"] = profile.IntervalMinutes,
        ["options"] = profile.Options,
        ["jobs"] = profile.Jobs.Select(j => new Dictionary<string, string>
        {
            ["source"] = j.Source,
            ["target"] = j.Target,
        }).ToList(),
        ["log_to_file"] = profile.LogToFile,
        ["log_file_path"] = profile.LogFilePath,
        ["selected_profile"] = profile.SelectedProfile,
        ["startup_profile"] = profile.StartupProfile,
    };
}
