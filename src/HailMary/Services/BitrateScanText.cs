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
            "Aufloesung unbekannt" => Loc.T("bitrate.reason.unknownResolution"),
            "Scan-Fehler" => Loc.T("bitrate.reason.scanError"),
            _ when reasonRaw.StartsWith("Problematischer Codec", StringComparison.OrdinalIgnoreCase)
                => LocalizeCodecReason(reasonRaw, "bitrate.reason.problemCodec"),
            _ when reasonRaw.StartsWith("Codec OK", StringComparison.OrdinalIgnoreCase)
                => LocalizeCodecReason(reasonRaw, "bitrate.reason.codecOk"),
            _ => actionCode.ToLowerInvariant() switch
            {
                "convert" => Loc.T("bitrate.reason.reduce"),
                "skip" when reasonRaw.Contains("unbekannt", StringComparison.OrdinalIgnoreCase)
                    => Loc.T("bitrate.reason.unknownBitrate"),
                "skip" => Loc.T("bitrate.reason.noSavings"),
                _ => reasonRaw,
            },
        };

    private static string LocalizeCodecReason(string reasonRaw, string key)
    {
        var open = reasonRaw.IndexOf('(');
        var close = reasonRaw.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var codec = reasonRaw[(open + 1)..close].Trim();
            return Loc.F(key, codec);
        }

        return Loc.F(key, "?");
    }
}
