using HailMary.Models;

namespace HailMary.Services;

public sealed class StashSettingsService
{
    public StashConnectionSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Initialize(SettingsService settings)
    {
        Current = settings.Current.Stash?.Clone() ?? new StashConnectionSettings();
        Current.PathMap = StashPathMapper.NormalizeMap(Current.PathMap);
        if (!Current.PathMap.UseBackup || !StashPathMapper.BackupAvailable(Current.PathMap))
        {
            Current.PathMap.UseBackup = false;
        }

        if (!Current.IsConfigured())
        {
            MigrateFromToolConfigs(settings.ProjectsRoot);
        }

        // Keep migrated values for the next settings save. Startup also runs in
        // the verifier, which only needs to read the user's configuration.
        settings.Current.Stash = Current.Clone();
    }

    public void Update(
        string endpoint,
        string apiKey,
        StashPathMapSettings pathMap,
        SettingsService settings)
    {
        var normalized = StashPathMapper.NormalizeMap(pathMap);
        if (normalized.UseBackup && !StashPathMapper.BackupAvailable(normalized))
        {
            normalized.UseBackup = false;
        }

        Current = new StashConnectionSettings
        {
            Endpoint = endpoint.Trim(),
            ApiKey = apiKey,
            PathMap = normalized,
        };
        settings.Current.Stash = Current.Clone();
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyToTool(
        Action<string> setEndpoint,
        Action<string> setApiKey,
        Action<string> setRemote,
        Action<string> setLocal,
        Action<string> setBackup,
        Action<bool> setUseBackup)
    {
        setEndpoint(Current.Endpoint);
        setApiKey(Current.ApiKey);
        setRemote(Current.PathMap.PathPrefixRemote);
        setLocal(Current.PathMap.PathPrefixLocal);
        setBackup(Current.PathMap.PathPrefixBackup);
        setUseBackup(Current.PathMap.UseBackup && StashPathMapper.BackupAvailable(Current.PathMap));
    }

    public StashPathMapSettings CurrentPathMap(bool useBackupOverride) => new()
    {
        PathPrefixRemote = Current.PathMap.PathPrefixRemote,
        PathPrefixLocal = Current.PathMap.PathPrefixLocal,
        PathPrefixBackup = Current.PathMap.PathPrefixBackup,
        UseBackup = useBackupOverride,
    };

    private void MigrateFromToolConfigs(string projectsRoot)
    {
        var candidates = new[]
        {
            FromPathfinder(StashPathfinderConfigReader.Load()),
            FromCutter(StashCutterConfigReader.Load(projectsRoot)),
            FromMarker(MarkerUpdaterConfigReader.Load()),
        };

        foreach (var candidate in candidates)
        {
            if (candidate.IsConfigured())
            {
                Current = candidate;
                return;
            }
        }
    }

    private static StashConnectionSettings FromPathfinder(StashPathfinderSettings settings) => new()
    {
        Endpoint = settings.Endpoint,
        ApiKey = settings.ApiKey,
        PathMap = settings.PathMap.Clone(),
    };

    private static StashConnectionSettings FromCutter(StashCutterSettings settings) => new()
    {
        Endpoint = settings.Endpoint,
        ApiKey = settings.ApiKey,
        PathMap = settings.PathMap.Clone(),
    };

    private static StashConnectionSettings FromMarker(MarkerUpdaterSettings settings) => new()
    {
        Endpoint = settings.Endpoint,
        ApiKey = settings.ApiKey,
        PathMap = settings.PathMap.Clone(),
    };
}

internal static class StashPathMapSettingsExtensions
{
    public static StashPathMapSettings Clone(this StashPathMapSettings map) => new()
    {
        PathPrefixRemote = map.PathPrefixRemote,
        PathPrefixLocal = map.PathPrefixLocal,
        PathPrefixBackup = map.PathPrefixBackup,
        UseBackup = map.UseBackup,
    };
}
