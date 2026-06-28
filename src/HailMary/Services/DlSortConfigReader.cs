using System.Text.Json;

namespace HailMary.Services;

public sealed class DlSortRuleCriterion
{
    public string IfType { get; set; } = "extension";

    public string Condition { get; set; } = "contains";

    public List<string> Values { get; set; } = [""];
}

public sealed class DlSortRule
{
    public List<DlSortRuleCriterion> Criteria { get; set; } = [new()];

    public string Action { get; set; } = "move";

    public string TargetFolder { get; set; } = "";
}

public sealed class DlSortProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = "Profil 1";

    public string WatchFolder { get; set; } = "";

    public List<DlSortRule> Rules { get; set; } = [new()];

    public bool RunEnabled { get; set; }
}

public sealed class DlSortConfig
{
    public double SettleDelaySeconds { get; set; } = 1.5;

    public double StablePollIntervalSeconds { get; set; } = 0.4;

    public double MaxWaitSeconds { get; set; } = 120;

    public List<DlSortProfile> Profiles { get; set; } = [new()];
}

public static class DlSortConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(AppServices.Settings.ProjectsRoot, "download_sorter");

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "config.json");

    public static DlSortConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<DlSortConfig>(json, JsonOptions);
                if (loaded is not null && loaded.Profiles.Count > 0)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // fallback
        }

        return new DlSortConfig();
    }

    public static void Save(DlSortConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var payload = new Dictionary<string, object?>
        {
            ["settle_delay_seconds"] = config.SettleDelaySeconds,
            ["stable_poll_interval_seconds"] = config.StablePollIntervalSeconds,
            ["max_wait_seconds"] = config.MaxWaitSeconds,
            ["ui_language"] = "de",
            ["ui_appearance"] = "dark",
            ["profiles"] = config.Profiles.Select(ToProfileDict).ToList(),
        };

        if (config.Profiles.Count > 0)
        {
            payload["watch_folder"] = config.Profiles[0].WatchFolder;
            payload["rules"] = config.Profiles[0].Rules.Select(ToRuleDict).ToList();
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static Dictionary<string, object?> ToProfileDict(DlSortProfile profile) => new()
    {
        ["profile_id"] = profile.ProfileId,
        ["name"] = profile.Name,
        ["watch_folder"] = profile.WatchFolder,
        ["run_enabled"] = profile.RunEnabled,
        ["rules"] = profile.Rules.Select(ToRuleDict).ToList(),
    };

    private static Dictionary<string, object?> ToRuleDict(DlSortRule rule) => new()
    {
        ["criteria"] = rule.Criteria.Select(c => new Dictionary<string, object?>
        {
            ["if_type"] = c.IfType,
            ["condition"] = c.Condition,
            ["values"] = c.Values,
        }).ToList(),
        ["action"] = rule.Action,
        ["target_folder"] = rule.TargetFolder,
    };
}
