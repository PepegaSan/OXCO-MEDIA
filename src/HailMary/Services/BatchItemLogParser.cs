namespace HailMary.Services;

public static class BatchItemLogParser
{
    private const string Prefix = "HM_BATCH_ITEM|";

    public static bool TryParse(string line, out string path, out string status)
    {
        path = string.Empty;
        status = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = trimmed[Prefix.Length..];
        var separator = body.LastIndexOf('|');
        if (separator <= 0 || separator >= body.Length - 1)
        {
            return false;
        }

        path = body[..separator];
        status = body[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(status);
    }

    public static string NormalizeStatusCode(string bridgeStatus) =>
        bridgeStatus.Trim().ToLowerInvariant() switch
        {
            "running" => "running",
            "done" => "done",
            "failed" => "failed",
            "cancelled" or "canceled" or "aborted" => "cancelled",
            "pending" => "pending",
            _ => bridgeStatus.Trim().ToLowerInvariant(),
        };

    public static string ToDisplayStatus(string bridgeStatus) =>
        NormalizeStatusCode(bridgeStatus) switch
        {
            "running" => Loc.T("davincibatch.itemRunning"),
            "done" => Loc.T("davincibatch.itemDone"),
            "failed" => Loc.T("davincibatch.itemError"),
            "cancelled" => Loc.T("davincibatch.itemAborted"),
            "pending" => Loc.T("davincibatch.itemPending"),
            _ => bridgeStatus,
        };
}
