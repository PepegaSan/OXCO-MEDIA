using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class TextToVideoProbeResult
{
    public double DurationSec { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double Fps { get; init; }

    public string SuggestedBitrate { get; init; } = string.Empty;
}

public sealed class TextToVideoPreviewResult
{
    public byte[] ImageBytes { get; init; } = [];

    public int Width { get; init; }

    public int Height { get; init; }

    public double DurationSec { get; init; }
}

public static class TextToVideoBridge
{
    public static async Task<TextToVideoProbeResult?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(new { video_path = videoPath });
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_ttv_probe_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "text_to_video_probe_job.py",
                ["--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (!result.Success || !File.Exists(outPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            var root = doc.RootElement;
            return new TextToVideoProbeResult
            {
                DurationSec = root.GetProperty("duration_sec").GetDouble(),
                Width = root.GetProperty("width").GetInt32(),
                Height = root.GetProperty("height").GetInt32(),
                Fps = root.GetProperty("fps").GetDouble(),
                SuggestedBitrate = root.TryGetProperty("suggested_bitrate", out var br) ? br.GetString() ?? "" : "",
            };
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    public static async Task<TextToVideoPreviewResult?> RenderPreviewAsync(
        object config,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(config);
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_ttv_prev_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "text_to_video_preview_job.py",
                ["--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (!result.Success || !File.Exists(outPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            var root = doc.RootElement;
            var b64 = root.GetProperty("image_base64").GetString() ?? "";
            return new TextToVideoPreviewResult
            {
                ImageBytes = Convert.FromBase64String(b64),
                Width = root.GetProperty("width").GetInt32(),
                Height = root.GetProperty("height").GetInt32(),
                DurationSec = root.GetProperty("duration_sec").GetDouble(),
            };
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    public static async Task<JobResult> ExportAsync(object config, CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(config);
        try
        {
            return await AppServices.JobRunner.RunBridgeAsync(
                "text_to_video_export_job.py",
                ["--config-json", configPath],
                cancellationToken);
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    public static async Task<JobResult> ResolveExportAsync(object config, CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(config);
        try
        {
            return await AppServices.JobRunner.RunBridgeAsync(
                "text_to_video_resolve_job.py",
                ["--config-json", configPath],
                cancellationToken);
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    public static object BuildResolveConfig(
        string videoPath,
        string outputDir,
        string preset,
        TextToVideoSettings settings) => new
    {
        video_path = videoPath,
        output_dir = outputDir,
        preset,
        resolve_modules = settings.ResolveModules,
        resolve_dll = settings.ResolveDll,
        resolve_exe = settings.ResolveExe,
    };

    public static object BuildPreviewConfig(
        string videoPath,
        double timeSec,
        IEnumerable<TextOverlaySegment> segments,
        TextOverlaySegment? draft,
        string srtPath,
        int? selectedIndex)
    {
        var segList = segments.Select(s => s.ToJsonObject()).ToList();
        object? draftObj = null;
        if (draft is not null && !string.IsNullOrWhiteSpace(draft.Text.Trim()))
        {
            if (selectedIndex is >= 0 && selectedIndex < segList.Count)
            {
                segList[selectedIndex.Value] = draft.ToJsonObject();
            }
            else
            {
                draftObj = draft.ToJsonObject();
            }
        }

        return new
        {
            video_path = videoPath,
            time_sec = timeSec,
            overlay_segments = segList,
            draft_segment = draftObj,
            srt_path = srtPath,
        };
    }

    public static object BuildExportConfig(
        string videoPath,
        string outputPath,
        IEnumerable<TextOverlaySegment> segments,
        TextOverlaySegment? draft,
        int? selectedIndex,
        TextToVideoSettings settings)
    {
        var segList = segments.Select(s => s.ToJsonObject()).ToList();
        object? draftObj = null;
        if (draft is not null && !string.IsNullOrWhiteSpace(draft.Text.Trim()))
        {
            if (selectedIndex is >= 0 && selectedIndex < segList.Count)
            {
                segList[selectedIndex.Value] = draft.ToJsonObject();
            }
            else
            {
                draftObj = draft.ToJsonObject();
            }
        }

        return new
        {
            video_path = videoPath,
            output_path = outputPath,
            overlay_segments = segList,
            draft_segment = draftObj,
            srt_path = settings.SrtPath,
            export_container = settings.ExportContainer,
            codec = settings.Codec,
            bitrate = settings.Bitrate,
            gif_fps = settings.GifFps,
            gif_max_width = settings.GifMaxWidth,
            gif_palette_colors = settings.GifPaletteColors,
            audio_copy = settings.AudioCopy,
        };
    }

    private static string WriteTempConfig(object payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hm_ttv_{Guid.NewGuid():N}.json");
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
