using System.Text.RegularExpressions;

namespace HailMary.Services;

/// <summary>
/// Rerun clips often share a title prefix and append _{YYMMDDHHMMSS}_{tag}.
/// Layouts are keyed by that title so quality/timestamp variants reuse the same timings.
/// </summary>
public static class TextToVideoLayoutKey
{
    private static readonly Regex TimestampQualitySuffix = new(
        @"^(.+)_(\d{12})_([A-Za-z]{2,12})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string FromPath(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return string.Empty;
        }

        return FromFileName(Path.GetFileNameWithoutExtension(videoPath));
    }

    public static string FromFileName(string? nameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
        {
            return string.Empty;
        }

        var name = nameWithoutExtension.Trim();
        var match = TimestampQualitySuffix.Match(name);
        return match.Success ? match.Groups[1].Value.Trim() : name;
    }
}
