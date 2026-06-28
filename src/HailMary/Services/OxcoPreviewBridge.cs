using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class OxcoPreviewProbeResult
{
    public int FrameCount { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string NameA { get; init; } = string.Empty;

    public bool HasPathB { get; init; }
}

public sealed class OxcoPreviewFrameResult
{
    public int FrameIndex { get; init; }

    public int FrameCount { get; init; }

    public int DiffCount { get; init; }

    public bool DiffOverThreshold { get; init; }

    public byte[] ImageBytes { get; init; } = [];
}

public static class OxcoPreviewBridge
{
    public static async Task<OxcoPreviewProbeResult?> ProbeAsync(
        string pathA,
        string pathB,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(new { path_a = pathA, path_b = pathB });
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_prev_probe_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_preview_job.py",
                ["--action", "probe", "--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (!result.Success || !File.Exists(outPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            var root = doc.RootElement;
            return new OxcoPreviewProbeResult
            {
                FrameCount = root.GetProperty("frame_count").GetInt32(),
                Width = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                Height = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                NameA = root.TryGetProperty("name_a", out var n) ? n.GetString() ?? "" : "",
                HasPathB = root.TryGetProperty("path_b", out var pb) && !string.IsNullOrWhiteSpace(pb.GetString()),
            };
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    public static async Task<OxcoPreviewFrameResult?> RenderFrameAsync(
        string pathA,
        string pathB,
        int frameIndex,
        int noise,
        int pixelThreshold,
        bool sideBySide,
        bool overlay,
        int maxWidth = 1280,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(new
        {
            path_a = pathA,
            path_b = pathB,
            frame_index = frameIndex,
            noise,
            pixel_threshold = pixelThreshold,
            side_by_side = sideBySide,
            overlay,
            max_width = maxWidth,
        });
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_prev_frame_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_preview_job.py",
                ["--action", "render", "--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (!result.Success || !File.Exists(outPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            var root = doc.RootElement;
            var b64 = root.GetProperty("image_base64").GetString() ?? "";
            return new OxcoPreviewFrameResult
            {
                FrameIndex = root.GetProperty("frame_index").GetInt32(),
                FrameCount = root.GetProperty("frame_count").GetInt32(),
                DiffCount = root.GetProperty("diff_count").GetInt32(),
                DiffOverThreshold = root.GetProperty("diff_over_threshold").GetBoolean(),
                ImageBytes = Convert.FromBase64String(b64),
            };
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    private static string WriteTempConfig(object payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hm_oxco_preview_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
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
