using System.Globalization;
using System.Text.RegularExpressions;

namespace HailMary.Services;

public enum JobProgressParseKind
{
    None,
    Update,
    End,
}

public readonly struct JobProgressParseResult
{
    public JobProgressParseKind Kind { get; init; }
    public double Percent { get; init; }
    public string Label { get; init; }

    public static JobProgressParseResult Update(double percent, string label) =>
        new() { Kind = JobProgressParseKind.Update, Percent = percent, Label = label };

    public static JobProgressParseResult End() =>
        new() { Kind = JobProgressParseKind.End };
}

/// <summary>
/// Parses tool-internal progress from bridge stdout (e.g. compare frame analysis).
/// Ignores FFmpeg/DaVinci export progress lines.
/// </summary>
public static partial class JobLogProgressParser
{
    [GeneratedRegex(@"HM_PROGRESS:([\d.]+):([^:\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HmProgressRegex();

    [GeneratedRegex(@"(?:Analysiere|Analyzing):\s*([\d.]+)\s*%", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AnalysisFallbackRegex();

    [GeneratedRegex(@"(?:^|\s)(?:Fortschritt|Progress):\s*[\d.]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegProgressRegex();

    public static bool TryParse(string line, out JobProgressParseResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("HM_PROGRESS_END", StringComparison.Ordinal))
        {
            result = JobProgressParseResult.End();
            return true;
        }

        var hm = HmProgressRegex().Match(trimmed);
        if (hm.Success
            && double.TryParse(hm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hmPercent))
        {
            result = JobProgressParseResult.Update(Clamp(hmPercent), hm.Groups[2].Value.Trim());
            return true;
        }

        if (FfmpegProgressRegex().IsMatch(trimmed))
        {
            return false;
        }

        if (IsExportPhaseLine(trimmed))
        {
            return false;
        }

        var analysis = AnalysisFallbackRegex().Match(trimmed);
        if (analysis.Success
            && double.TryParse(analysis.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var analysisPercent))
        {
            result = JobProgressParseResult.Update(Clamp(analysisPercent), "Analyse");
            return true;
        }

        return false;
    }

    private static bool IsExportPhaseLine(string line)
    {
        if (line.Contains("via FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("Rendering in progress", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("DaVinci", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Render", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static double Clamp(double percent) => Math.Clamp(percent, 0, 100);
}
