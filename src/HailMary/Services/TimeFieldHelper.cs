using System.Globalization;

namespace HailMary.Services;

public static class TimeFieldHelper
{
    public static bool TryParseSeconds(string? raw, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim().Replace(',', '.');

        // Allow "30s" / "30 s" (hint and TimecodeHelper use a trailing s).
        if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !raw.Contains(':'))
        {
            raw = raw[..^1].Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
        }

        if (raw.Contains(':'))
        {
            var parts = raw.Split(':');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            {
                seconds = minutes * 60 + secs;
                return true;
            }

            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mins)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secPart))
            {
                seconds = hours * 3600 + mins * 60 + secPart;
                return true;
            }

            return false;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    public static string FormatForField(double sec)
    {
        sec = Math.Max(0, sec);
        // Only strip trailing zeros after the decimal point — never from the integer part
        // (otherwise 30 → "3", 100 → "1").
        var s = sec.ToString("0.###", CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }

        return string.IsNullOrEmpty(s) ? "0" : s;
    }

    public static string FormatDetailedHint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Loc.T("texttovideo.timeHint.empty");
        }

        if (!TryParseSeconds(raw, out var sec))
        {
            return Loc.T("texttovideo.timeHint.invalid");
        }

        return FormatDetailed(sec);
    }

    public static string FormatDetailed(double sec)
    {
        sec = Math.Max(0, sec);
        var hours = (int)(sec / 3600);
        var minutes = (int)((sec % 3600) / 60);
        var seconds = sec % 60;

        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add(Loc.F("texttovideo.time.hours", hours));
        }

        if (minutes > 0 || hours > 0)
        {
            parts.Add(Loc.F("texttovideo.time.minutes", minutes));
        }

        parts.Add(Loc.F("texttovideo.time.seconds", seconds.ToString("0.##", CultureInfo.InvariantCulture)));
        return $"{string.Join(" ", parts)} · {FormatClock(sec)} · {FormatForField(sec)} s";
    }

    public static string FormatShortFromField(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }

        return TryParseSeconds(raw, out var sec) ? FormatClock(sec) : raw.Trim();
    }

    public static string FormatClock(double sec)
    {
        sec = Math.Max(0, sec);
        var hours = (int)(sec / 3600);
        var minutes = (int)((sec % 3600) / 60);
        var seconds = sec % 60;

        if (hours > 0)
        {
            return $"{hours}:{minutes:00}:{seconds:00.0}";
        }

        return $"{minutes}:{seconds:00.0}";
    }
}
