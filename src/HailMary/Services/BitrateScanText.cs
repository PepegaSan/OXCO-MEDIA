namespace HailMary.Services;

public static class BitrateScanText
{
    public static string LocalizeAction(string actionCode) =>
        actionCode.ToLowerInvariant() switch
        {
            "convert" => Loc.T("bitrate.action.convert"),
            "skip" => Loc.T("bitrate.action.skip"),
            _ => actionCode,
        };

    public static string LocalizeReason(string actionCode, string reasonRaw) =>
        reasonRaw switch
        {
            "Keine Einsparung" => Loc.T("bitrate.reason.noSavings"),
            "Reduzieren" => Loc.T("bitrate.reason.reduce"),
            "Bitrate unbekannt" => Loc.T("bitrate.reason.unknownBitrate"),
            _ => actionCode.ToLowerInvariant() switch
            {
                "convert" => Loc.T("bitrate.reason.reduce"),
                "skip" when reasonRaw.Contains("unbekannt", StringComparison.OrdinalIgnoreCase)
                    => Loc.T("bitrate.reason.unknownBitrate"),
                "skip" => Loc.T("bitrate.reason.noSavings"),
                _ => reasonRaw,
            },
        };
}
