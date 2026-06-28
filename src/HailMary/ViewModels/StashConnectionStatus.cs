using HailMary.Services;

namespace HailMary.ViewModels;

public interface IStashToolHost
{
    bool IsStashConnected { get; }

    string StashConnectionTooltip { get; }
}

public static class StashConnectionStatus
{
    public static bool IsConnected(string? connectionInfo) =>
        (connectionInfo ?? string.Empty).StartsWith("Stash ", StringComparison.Ordinal);

    public static string Tooltip(string? connectionInfo) =>
        IsConnected(connectionInfo)
            ? connectionInfo ?? "Mit Stash verbunden"
            : string.IsNullOrWhiteSpace(connectionInfo) || connectionInfo == Loc.T("stash.notConnected")
                ? "Nicht mit Stash verbunden"
                : connectionInfo;

    public static string FormatConnected(string version) => $"Stash {version}";
}

public static class StashConnectionHelper
{
    public static async Task EnsureReachableAsync(
        StashGraphQlClient client,
        string currentConnectionInfo,
        Action<string> setConnectionInfo)
    {
        if (StashConnectionStatus.IsConnected(currentConnectionInfo))
        {
            return;
        }

        var version = await client.PingAsync();
        setConnectionInfo(StashConnectionStatus.FormatConnected(version));
    }
}
