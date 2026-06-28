using HailMary.Models;

namespace HailMary.Services;

public static class StashConnectionSync
{
    public static event Action<string>? StashConnected;

    public static void BroadcastConnected(string version) =>
        StashConnected?.Invoke(version);

    public static void SubscribeToCentralChanges(Action applyFromCentral)
    {
        AppServices.StashSettings.Changed += (_, _) => applyFromCentral();
    }

    public static void SubscribeToGlobalConnect(Action<string> onConnected) =>
        StashConnected += onConnected;

    public static void ApplyCentralToTool(
        Action<string> setEndpoint,
        Action<string> setApiKey,
        Action<string> setRemote,
        Action<string> setLocal,
        Action<string> setBackup,
        Action<bool> setUseBackup)
    {
        AppServices.StashSettings.ApplyToTool(
            setEndpoint,
            setApiKey,
            setRemote,
            setLocal,
            setBackup,
            setUseBackup);
    }

    public static void PushToolToCentral(
        string endpoint,
        string apiKey,
        StashPathMapSettings pathMap)
    {
        AppServices.StashSettings.Update(endpoint, apiKey, pathMap, AppServices.Settings);
    }

    public static StashConnectionSettings Snapshot() =>
        AppServices.StashSettings.Current.Clone();
}
