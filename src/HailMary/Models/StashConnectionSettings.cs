using HailMary.Services;

namespace HailMary.Models;

public sealed class StashConnectionSettings
{
    public string Endpoint { get; set; } = "http://localhost:9999/graphql";

    public string ApiKey { get; set; } = string.Empty;

    public StashPathMapSettings PathMap { get; set; } = new();

    public StashConnectionSettings Clone() => new()
    {
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        PathMap = new StashPathMapSettings
        {
            PathPrefixRemote = PathMap.PathPrefixRemote,
            PathPrefixLocal = PathMap.PathPrefixLocal,
            PathPrefixBackup = PathMap.PathPrefixBackup,
            UseBackup = PathMap.UseBackup,
        },
    };

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(ApiKey)
        || !string.IsNullOrWhiteSpace(PathMap.PathPrefixLocal)
        || !string.IsNullOrWhiteSpace(PathMap.PathPrefixBackup)
        || !Endpoint.Equals("http://localhost:9999/graphql", StringComparison.OrdinalIgnoreCase);
}
