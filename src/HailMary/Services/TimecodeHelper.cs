using System.Globalization;

namespace HailMary.Services;

public static class TimecodeHelper
{
    /// <summary>Lesbare Anzeige: unter 60s in Sekunden, darüber mm:ss oder h:mm:ss.</summary>
    public static string FormatDisplay(double sec)
    {
        if (sec < 0)
        {
            sec = 0;
        }

        if (sec < 60)
        {
            return $"{sec.ToString("0.###", CultureInfo.InvariantCulture)}s";
        }

        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1)
        {
            return HasSubSecondFraction(sec)
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return HasSubSecondFraction(sec)
            ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    public static string FormatDisplayRange(double start, double end)
    {
        if (end <= start)
        {
            return FormatDisplay(start);
        }

        return $"{FormatDisplay(start)}–{FormatDisplay(end)}";
    }

    public static string FormatDisplayFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        return TryParseFlexible(text, out var sec, out _)
            ? FormatDisplay(sec)
            : text;
    }

    public static string FormatRangeFromText(string? startText, string? endText)
    {
        if (!TryParseFlexible(startText ?? string.Empty, out var start, out _))
        {
            return FormatDisplayFromText(startText);
        }

        if (string.IsNullOrWhiteSpace(endText)
            || !TryParseFlexible(endText, out var end, out _))
        {
            return FormatDisplay(start);
        }

        return FormatDisplayRange(start, end);
    }

    /// <summary>Wert für Eingabefelder — parsebar via <see cref="TryParseFlexible"/>.</summary>
    public static string FormatForEditor(double sec)
    {
        if (sec < 0)
        {
            sec = 0;
        }

        if (sec < 60)
        {
            return sec.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return FormatDisplay(sec).TrimEnd('s');
    }

    public static string SecondsToTimecode(double sec)
    {
        if (sec < 0)
        {
            sec = 0;
        }

        var ms = (int)Math.Round((sec - (int)sec) * 1000);
        var sInt = (int)sec + (ms >= 1000 ? 1 : 0);
        ms %= 1000;
        var h = sInt / 3600;
        var m = sInt % 3600 / 60;
        var s = sInt % 60;
        return $"{h:D2}:{m:D2}:{s:D2}.{ms:D3}";
    }

    public static bool TryParseFlexible(string? raw, out double seconds, out string error)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^1].Trim();
        }

        return TryParse(s, out seconds, out error);
    }

    public static bool TryParse(string raw, out double seconds, out string error)
    {
        seconds = 0;
        error = string.Empty;
        var s = (raw ?? string.Empty).Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(s))
        {
            error = "Zeit leer";
            return false;
        }

        var parts = s.Split(':');
        try
        {
            seconds = parts.Length switch
            {
                1 => double.Parse(parts[0], CultureInfo.InvariantCulture),
                2 => int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 +
                     double.Parse(parts[1], CultureInfo.InvariantCulture),
                3 => int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 +
                     int.Parse(parts[1], CultureInfo.InvariantCulture) * 60 +
                     double.Parse(parts[2], CultureInfo.InvariantCulture),
                _ => throw new FormatException(),
            };
            return true;
        }
        catch (FormatException)
        {
            error = $"Ungueltige Zeit: {raw}";
            return false;
        }
    }

    private static bool HasSubSecondFraction(double sec) =>
        Math.Abs(sec - Math.Floor(sec)) > 0.0005;
}
