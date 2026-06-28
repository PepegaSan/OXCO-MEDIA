using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class OxcoTaggerProcessResult
{
    public int Ok { get; init; }

    public int Skipped { get; init; }

    public IReadOnlyList<string> Log { get; init; } = [];
}

public sealed class OxcoTaggerDistributeResult
{
    public int Moved { get; init; }

    public int NoMatch { get; init; }

    public int Errors { get; init; }

    public IReadOnlyList<string> Log { get; init; } = [];
}

public static class OxcoTaggerBridge
{
    public static async Task<OxcoTaggerProcessResult?> ProcessAsync(
        string inputDir,
        string outputDir,
        string tag,
        string profileName,
        string keepSuffixCsv,
        string ignoreSuffixCsv,
        string dropSuffixCsv,
        string patternText,
        IReadOnlyList<string>? onlyFiles,
        double filterBuffer,
        int filterNoise,
        int filterPixel,
        int filterPixelMax,
        string bitrateSuffix,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTemp(new
        {
            mode = "process",
            input_dir = inputDir,
            output_dir = outputDir,
            tag,
            profile_name = profileName,
            keep_suffix_csv = keepSuffixCsv,
            ignore_suffix_csv = ignoreSuffixCsv,
            drop_suffix_csv = dropSuffixCsv,
            pattern_text = patternText,
            only_files = onlyFiles ?? [],
            filter_buffer_seconds = filterBuffer,
            filter_noise_threshold = filterNoise,
            filter_pixel_threshold = filterPixel,
            filter_pixel_max_threshold = filterPixelMax,
            bitrate_output_suffix = bitrateSuffix,
        });
        return await RunAsync(configPath, cancellationToken, ParseProcess);
    }

    public static async Task<OxcoTaggerDistributeResult?> DistributeAsync(
        string sourceDir,
        IReadOnlyList<TagRouteRule> rules,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTemp(new
        {
            mode = "distribute",
            source_dir = sourceDir,
            route_rules = rules.Select(r => new { tag = r.Tag, folder = r.Folder }).ToList(),
        });
        return await RunAsync(configPath, cancellationToken, ParseDistribute);
    }

    private static async Task<T?> RunAsync<T>(
        string configPath,
        CancellationToken cancellationToken,
        Func<string, T?> parse)
        where T : class
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_tag_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_tagger_job.py",
                ["--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (!File.Exists(outPath))
            {
                if (!result.Success && result.ExitCode != -2)
                {
                    AppServices.Log.Error(result.Message);
                }

                return null;
            }

            var parsed = parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            if (parsed is not null)
            {
                LogBridgeLines(parsed);
                AppServices.Log.Success("Autotagger-Job abgeschlossen.");
            }

            return parsed;
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    private static OxcoTaggerProcessResult? ParseProcess(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new OxcoTaggerProcessResult
        {
            Ok = root.GetProperty("ok").GetInt32(),
            Skipped = root.GetProperty("skipped").GetInt32(),
            Log = root.TryGetProperty("log", out var log)
                ? log.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [],
        };
    }

    private static OxcoTaggerDistributeResult? ParseDistribute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new OxcoTaggerDistributeResult
        {
            Moved = root.GetProperty("moved").GetInt32(),
            NoMatch = root.GetProperty("no_match").GetInt32(),
            Errors = root.GetProperty("errors").GetInt32(),
            Log = root.TryGetProperty("log", out var log)
                ? log.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [],
        };
    }

    private static string WriteTemp(object payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hm_oxco_tag_cfg_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private static void LogBridgeLines(object parsed)
    {
        IEnumerable<string> lines = parsed switch
        {
            OxcoTaggerProcessResult process => process.Log,
            OxcoTaggerDistributeResult distribute => distribute.Log,
            _ => [],
        };

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                AppServices.Log.Info(line);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
