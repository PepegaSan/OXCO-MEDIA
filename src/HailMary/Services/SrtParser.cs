using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HailMary.Models;

namespace HailMary.Services;

public static partial class SrtParser
{
    private static readonly Regex TimeLineRegex = TimeLine();

    public static IReadOnlyList<TextOverlaySegment> ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        return ParseContent(text);
    }

    public static IReadOnlyList<TextOverlaySegment> ParseContent(string content)
    {
        var blocks = content.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<TextOverlaySegment>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var timeIdx = Array.FindIndex(lines, l => TimeLineRegex.IsMatch(l));
            if (timeIdx < 0 || timeIdx >= lines.Length - 1)
            {
                continue;
            }

            var match = TimeLineRegex.Match(lines[timeIdx]);
            if (!match.Success)
            {
                continue;
            }

            if (!TryParseTimestamp(match.Groups[1].Value, out var fromSec)
                || !TryParseTimestamp(match.Groups[2].Value, out var toSec))
            {
                continue;
            }

            var text = string.Join('\n', lines.Skip(timeIdx + 1)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add(new TextOverlaySegment
            {
                Text = text,
                From = FormatSeconds(fromSec),
                To = FormatSeconds(toSec),
            });
        }

        return result;
    }

    private static bool TryParseTimestamp(string raw, out double seconds)
    {
        seconds = 0;
        var parts = raw.Trim().Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var secPart = parts[2].Replace(',', '.');
        if (!double.TryParse(secPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    private static string FormatSeconds(double sec)
    {
        sec = Math.Max(0, sec);
        var s = $"{sec:0.###}".TrimEnd('0').TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "0" : s;
    }

    [GeneratedRegex(
        @"(\d{1,2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TimeLine();
}
