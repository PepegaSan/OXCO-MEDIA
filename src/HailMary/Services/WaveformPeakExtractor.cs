using System.Diagnostics;

namespace HailMary.Services;

public static class WaveformPeakExtractor
{
    public static async Task<float[]?> ExtractAsync(
        string path,
        int buckets = 420,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || buckets < 8)
        {
            return null;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments =
                    $"-hide_banner -nostdin -i \"{path}\" -vn -ac 1 -ar 2000 -f f32le -acodec pcm_f32le pipe:1",
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

            _ = process.StandardError.ReadToEndAsync(cancellationToken);

            // 2000 Hz → keep 1 max per 20 samples (~100 envelopes/sec), then downsample to `buckets`.
            const int fold = 20;
            var envelope = new List<float>(16_384);
            var foldMax = 0f;
            var foldCount = 0;
            var buffer = new byte[8192];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       cancellationToken)) > 0)
            {
                var floats = read / 4;
                for (var i = 0; i < floats; i++)
                {
                    foldMax = Math.Max(foldMax, Math.Abs(BitConverter.ToSingle(buffer, i * 4)));
                    foldCount++;
                    if (foldCount >= fold)
                    {
                        envelope.Add(foldMax);
                        foldMax = 0f;
                        foldCount = 0;
                    }
                }
            }

            if (foldCount > 0)
            {
                envelope.Add(foldMax);
            }

            await process.WaitForExitAsync(cancellationToken);
            if (envelope.Count < 4)
            {
                return null;
            }

            var peaks = new float[buckets];
            var n = envelope.Count;
            for (var b = 0; b < buckets; b++)
            {
                var a = (int)Math.Floor(b * (double)n / buckets);
                var z = Math.Min(n, Math.Max(a + 1, (int)Math.Ceiling((b + 1) * (double)n / buckets)));
                var m = 0f;
                for (var i = a; i < z; i++)
                {
                    m = Math.Max(m, envelope[i]);
                }

                peaks[b] = m;
            }

            var peak = peaks.Max();
            if (peak > 1e-6f)
            {
                for (var i = 0; i < peaks.Length; i++)
                {
                    peaks[i] /= peak;
                }
            }

            return peaks;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFfmpeg()
    {
        var candidates = new List<string>();
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                continue;
            }

            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(dir, name));
            }
        }

        foreach (var full in candidates)
        {
            if (File.Exists(full))
            {
                return full;
            }
        }

        return "ffmpeg.exe";
    }
}
