using System.Diagnostics;
using System.Text.Json;

namespace HailMary.Services;

public static class FfprobeHelper
{
    public static async Task<double?> ProbeDurationSecondsAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var ffprobe = FindFfprobe();
        if (ffprobe is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of json \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durEl)
                && double.TryParse(durEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)
                && sec > 0)
            {
                return sec;
            }
        }
        catch
        {
            // fallback
        }

        return null;
    }

    public static async Task<(int Width, int Height)?> ProbeVideoSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var ffprobe = FindFfprobe();
        if (ffprobe is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries \"stream=width,height:stream_tags=rotate:stream_side_data=rotation\" -of json \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
            {
                return null;
            }

            var stream = streams[0];
            if (stream.TryGetProperty("width", out var wEl)
                && stream.TryGetProperty("height", out var hEl)
                && wEl.TryGetInt32(out var w)
                && hEl.TryGetInt32(out var h)
                && w > 0
                && h > 0)
            {
                var rotation = ReadRotationDegrees(stream);
                if (rotation is 90 or 270)
                {
                    (w, h) = (h, w);
                }

                return (w, h);
            }
        }
        catch
        {
            // fallback
        }

        return null;
    }

    private static int ReadRotationDegrees(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags)
            && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("rotate", out var rotateTag)
            && TryReadDegrees(rotateTag, out var tagDegrees))
        {
            return tagDegrees;
        }

        if (stream.TryGetProperty("side_data_list", out var sideData)
            && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation)
                    && TryReadDegrees(rotation, out var sideDegrees))
                {
                    return sideDegrees;
                }
            }
        }

        return 0;
    }

    private static bool TryReadDegrees(JsonElement value, out int degrees)
    {
        degrees = 0;
        double raw;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out raw))
            {
                return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(
                     value.GetString(),
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var parsed))
        {
            raw = parsed;
        }
        else
        {
            return false;
        }

        degrees = ((int)Math.Round(raw) % 360 + 360) % 360;
        return true;
    }

    private static string? FindFfprobe()
    {
        foreach (var name in new[] { "ffprobe.exe", "ffprobe" })
        {
            var fromPath = FindOnPath(name);
            if (!string.IsNullOrWhiteSpace(fromPath))
            {
                return fromPath;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = Path.Combine(dir, fileName);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}

public static class VideoPreviewUriHelper
{
    public static Uri? ToMediaUri(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (!File.Exists(full))
        {
            return null;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new Uri($"file:{full.Replace('\\', '/')}");
        }

        return new Uri(full);
    }
}
