using System.Text.Json;

namespace HailMary.Services;

/// <summary>Half-open frame range [Start, End) in analysis indices; Keep=false means it gets cut.</summary>
public readonly record struct OxcoSegment(int Start, int End, bool Keep)
{
    public int Length => End - Start;
}

public sealed record OxcoFilterParams(
    int PixelThreshold,
    int PixelMax,
    int BufferFrames,
    int MinDiffRun,
    int MinKeepFrames,
    int CutPadding);

/// <summary>
/// C# port of oxco_analysis.compute_segments (bridges/vendor/oxco) for live timeline updates.
/// Kept in sync via the shared vectors in tests/python/fixtures/oxco_segment_vectors.json.
/// </summary>
public static class OxcoSegmentFilter
{
    public static (List<OxcoSegment> Segments, bool[] DiffFlags) Compute(IReadOnlyList<int> metric, OxcoFilterParams p)
    {
        var flags = DiffFlags(metric, p.PixelThreshold, p.PixelMax);
        DropShortTrueRuns(flags, p.MinDiffRun);
        var keep = KeepFromFlags(flags, p.BufferFrames);
        keep = PadCuts(keep, p.CutPadding);
        DropShortKeeps(keep, p.MinKeepFrames);
        return (Runs(keep).Select(r => new OxcoSegment(r.Start, r.End, r.Value)).ToList(), flags);
    }

    public static (int Kept, int Cut, int CutCount) Summary(IEnumerable<OxcoSegment> segments)
    {
        int kept = 0, cut = 0, count = 0;
        foreach (var s in segments)
        {
            if (s.Keep)
            {
                kept += s.Length;
            }
            else
            {
                cut += s.Length;
                count++;
            }
        }

        return (kept, cut, count);
    }

    private static bool[] DiffFlags(IReadOnlyList<int> metric, int threshold, int max)
    {
        var flags = new bool[metric.Count];
        for (var i = 0; i < flags.Length; i++)
        {
            var m = metric[i];
            flags[i] = max > 0 ? m > threshold && m <= max : m > threshold;
        }

        return flags;
    }

    private static void DropShortTrueRuns(bool[] flags, int minLen)
    {
        if (minLen <= 1)
        {
            return;
        }

        foreach (var (start, end, value) in Runs(flags).ToList())
        {
            if (value && end - start < minLen)
            {
                Array.Fill(flags, false, start, end - start);
            }
        }
    }

    private static bool[] KeepFromFlags(bool[] flags, int bufferFrames)
    {
        var keep = new bool[flags.Length];
        Array.Fill(keep, true);
        if (bufferFrames <= 0)
        {
            return keep;
        }

        foreach (var (start, end, value) in Runs(flags))
        {
            if (!value && end - start < bufferFrames)
            {
                Array.Fill(keep, false, start, end - start);
            }
        }

        return keep;
    }

    private static bool[] PadCuts(bool[] keep, int padding)
    {
        var output = (bool[])keep.Clone();
        if (padding <= 0)
        {
            return output;
        }

        foreach (var (start, end, value) in Runs(keep))
        {
            if (!value)
            {
                var a = Math.Max(0, start - padding);
                var b = Math.Min(keep.Length, end + padding);
                Array.Fill(output, false, a, b - a);
            }
        }

        return output;
    }

    private static void DropShortKeeps(bool[] keep, int minLen)
    {
        if (minLen <= 1)
        {
            return;
        }

        var runs = Runs(keep).ToList();
        if (runs.Count <= 1)
        {
            return;
        }

        foreach (var (start, end, value) in runs)
        {
            if (value && end - start < minLen)
            {
                Array.Fill(keep, false, start, end - start);
            }
        }
    }

    private static IEnumerable<(int Start, int End, bool Value)> Runs(bool[] mask)
    {
        if (mask.Length == 0)
        {
            yield break;
        }

        var start = 0;
        var cur = mask[0];
        for (var i = 1; i < mask.Length; i++)
        {
            if (mask[i] != cur)
            {
                yield return (start, i, cur);
                start = i;
                cur = mask[i];
            }
        }

        yield return (start, mask.Length, cur);
    }
}

/// <summary>Per-frame measurements written by compare.py (metrics cache JSON).</summary>
public sealed class OxcoMetricsData
{
    public required string Path { get; init; }

    public required int[] Metric { get; init; }

    public double Fps { get; init; }

    public int SrcOffset { get; init; }

    public int DfOffset { get; init; }

    public string Mode { get; init; } = "classic";

    public int Noise { get; init; }

    public int AnalysisWidth { get; init; }

    public string SourceVideo { get; init; } = string.Empty;

    public string DeepfakeVideo { get; init; } = string.Empty;

    public static OxcoMetricsData? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("metric", out var metricEl) || metricEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var metric = new int[metricEl.GetArrayLength()];
            var i = 0;
            foreach (var v in metricEl.EnumerateArray())
            {
                metric[i++] = v.GetInt32();
            }

            var p = root.TryGetProperty("params", out var pe) ? pe : default;
            return new OxcoMetricsData
            {
                Path = path,
                Metric = metric,
                Fps = root.TryGetProperty("fps", out var f) ? f.GetDouble() : 0,
                SrcOffset = root.TryGetProperty("src_offset", out var so) ? so.GetInt32() : 0,
                DfOffset = root.TryGetProperty("df_offset", out var dfo) ? dfo.GetInt32() : 0,
                Mode = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("mode", out var m) ? m.GetString() ?? "classic" : "classic",
                Noise = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("noise", out var n) ? n.GetInt32() : 0,
                AnalysisWidth = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("analysis_width", out var w) ? w.GetInt32() : 0,
                SourceVideo = root.TryGetProperty("source_video", out var sv) ? sv.GetString() ?? "" : "",
                DeepfakeVideo = root.TryGetProperty("deepfake_video", out var dv) ? dv.GetString() ?? "" : "",
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
