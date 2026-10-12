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

/// <summary>Rendered text overlay (same PNG the FFmpeg export burns in).</summary>
public sealed class TextOverlaySprite
{
    public string Key { get; init; } = string.Empty;

    public byte[] PngBytes { get; init; } = [];

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Top-left of the content box inside the sprite (shadow/outline may extend beyond).</summary>
    public int OriginX { get; init; }

    public int OriginY { get; init; }

    public int ContentWidth { get; init; }

    public int ContentHeight { get; init; }

    public string Warning { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public bool IsValid => PngBytes.Length > 0 && ContentWidth > 0 && ContentHeight > 0;
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

            if (cancellationToken.IsCancellationRequested || result.ExitCode == -2)
            {
                return null;
            }

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
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    public static async Task<IReadOnlyList<TextOverlaySprite>?> RenderSpritesAsync(
        IReadOnlyList<(string Key, TextOverlaySegment Segment)> items,
        int videoWidth,
        int videoHeight,
        CancellationToken cancellationToken = default)
    {
        var configPath = WriteTempConfig(new
        {
            items = items.Select(i => new { key = i.Key, segment = i.Segment.ToJsonObject() }).ToList(),
            video_width = videoWidth,
            video_height = videoHeight,
        });
        var outPath = Path.Combine(Path.GetTempPath(), $"hm_ttv_spr_{Guid.NewGuid():N}.json");
        try
        {
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "text_to_video_sprites_job.py",
                ["--config-json", configPath, "--output-json", outPath],
                cancellationToken,
                quiet: true);
            if (cancellationToken.IsCancellationRequested || !result.Success || !File.Exists(outPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken));
            var list = new List<TextOverlaySprite>();
            foreach (var el in doc.RootElement.GetProperty("sprites").EnumerateArray())
            {
                string Str(string name) => el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
                int Int(string name) => el.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
                var b64 = Str("image_base64");
                list.Add(new TextOverlaySprite
                {
                    Key = Str("key"),
                    PngBytes = string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64),
                    Width = Int("width"),
                    Height = Int("height"),
                    OriginX = Int("origin_x"),
                    OriginY = Int("origin_y"),
                    ContentWidth = Int("content_width"),
                    ContentHeight = Int("content_height"),
                    Warning = Str("warning"),
                    Error = Str("error"),
                });
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(outPath);
        }
    }

    /// <summary>
    /// Segments as they will be exported: the editor draft replaces the segment being edited,
    /// or is appended when it is a new, not yet committed segment with text.
    /// SegmentIndex is -1 for the appended draft.
    /// </summary>
    public static List<(TextOverlaySegment Segment, int SegmentIndex)> EffectiveSegments(
        IEnumerable<TextOverlaySegment> segments,
        TextOverlaySegment? draft,
        int? selectedIndex)
    {
        var list = segments.Select((s, i) => (s, i)).ToList();
        if (draft is not null && !string.IsNullOrWhiteSpace(draft.Text.Trim()))
        {
            if (selectedIndex is >= 0 && selectedIndex < list.Count)
            {
                list[selectedIndex.Value] = (draft, selectedIndex.Value);
            }
            else
            {
                list.Add((draft, -1));
            }
        }

        return list;
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

    public static object BuildPreviewConfig(string videoPath, double timeSec, string srtPath) => new
    {
        video_path = videoPath,
        time_sec = timeSec,
        srt_path = srtPath,
        width_max = 1600,
        height_max = 900,
    };

    public static object BuildExportConfig(
        string videoPath,
        string outputPath,
        IEnumerable<TextOverlaySegment> segments,
        TextOverlaySegment? draft,
        int? selectedIndex,
        TextToVideoSettings settings)
    {
        var segList = EffectiveSegments(segments, draft, selectedIndex)
            .Select(e => e.Segment.ToJsonObject())
            .ToList();

        return new
        {
            video_path = videoPath,
            output_path = outputPath,
            overlay_segments = segList,
            srt_path = settings.SrtPath,
            export_container = settings.ExportContainer,
            codec = FfmpegEncoderCatalog.IdFor(FfmpegEncoderCatalog.TextToVideo, settings.Codec),
            bitrate = settings.Bitrate,
            gif_fps = settings.GifFps,
            target_fps = settings.TargetFps,
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
