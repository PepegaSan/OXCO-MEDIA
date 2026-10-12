using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HailMary.Services;

public sealed class OxcoPreviewOpenResult
{
    public int FrameCount { get; init; }

    public double Fps { get; init; }

    public double DurationSeconds { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string NameA { get; init; } = string.Empty;

    public bool HasB { get; init; }
}

/// <summary>
/// Keeps one oxco_preview_server.py process alive (videos stay open), so scrubbing and playback do not start
/// a Python process per frame. Requests are serialized; a crashed or hung worker is restarted on the next call.
/// </summary>
public sealed class OxcoPreviewWorker : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _nextId;
    private (string A, string B)? _openPaths;

    public async Task<OxcoPreviewOpenResult?> OpenAsync(string pathA, string pathB, CancellationToken ct = default)
    {
        var resp = await SendAsync(new JsonObject { ["cmd"] = "open", ["path_a"] = pathA, ["path_b"] = pathB }, ct);
        if (resp is null)
        {
            _openPaths = null;
            return null;
        }

        _openPaths = (pathA, pathB);
        return new OxcoPreviewOpenResult
        {
            FrameCount = GetInt(resp, "frame_count"),
            Fps = GetDouble(resp, "fps"),
            DurationSeconds = GetDouble(resp, "duration_sec"),
            Width = GetInt(resp, "width"),
            Height = GetInt(resp, "height"),
            NameA = resp["name_a"]?.GetValue<string>() ?? string.Empty,
            HasB = resp["has_b"]?.GetValue<bool>() ?? false,
        };
    }

    public bool IsOpen(string pathA, string pathB) =>
        _openPaths is { } p
        && string.Equals(p.A, pathA, StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.B, pathB, StringComparison.OrdinalIgnoreCase);

    public async Task<OxcoPreviewFrameResult?> RenderAsync(JsonObject request, CancellationToken ct = default)
    {
        request["cmd"] = "frame";
        var resp = await SendAsync(request, ct);
        if (resp is null)
        {
            return null;
        }

        return new OxcoPreviewFrameResult
        {
            FrameIndex = GetInt(resp, "frame_index"),
            FrameCount = GetInt(resp, "frame_count"),
            DiffCount = GetInt(resp, "diff_count"),
            DiffOverThreshold = resp["diff_over_threshold"]?.GetValue<bool>() ?? false,
            ImageBytes = Convert.FromBase64String(resp["image_base64"]?.GetValue<string>() ?? string.Empty),
        };
    }

    /// <summary>Path of the cached per-frame metrics for the pair (exact params first, else newest analysis).</summary>
    public async Task<(string? Path, bool Exact)> FindMetricsAsync(JsonObject request, CancellationToken ct = default)
    {
        request["cmd"] = "metrics";
        var resp = await SendAsync(request, ct);
        var path = resp?["path"]?.GetValue<string>();
        return (string.IsNullOrWhiteSpace(path) ? null : path, resp?["exact"]?.GetValue<bool>() ?? false);
    }

    /// <summary>Releases the open video files (call before moving, renaming or recycling them).</summary>
    public async Task ReleaseFilesAsync()
    {
        if (_openPaths is null || _process is null || _process.HasExited)
        {
            _openPaths = null;
            return;
        }

        _openPaths = null;
        await SendAsync(new JsonObject { ["cmd"] = "close" }, CancellationToken.None);
    }

    private async Task<JsonObject?> SendAsync(JsonObject request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var process = EnsureStarted();
            var id = ++_nextId;
            request["id"] = id;
            await process.StandardInput.WriteLineAsync(request.ToJsonString());
            await process.StandardInput.FlushAsync();

            // A started request is always read to the end so the protocol stays in sync; ct only affects waiting.
            using var timeout = new CancellationTokenSource(RequestTimeout);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    throw new IOException("Preview worker exited.");
                }

                if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                {
                    continue;
                }

                var node = JsonNode.Parse(line) as JsonObject;
                if (node?["id"]?.GetValue<int>() != id)
                {
                    continue;
                }

                if (node["ok"]?.GetValue<bool>() != true)
                {
                    AppServices.Log.Info($"Oxco-Vorschau: {node["error"]?.GetValue<string>()}");
                    return null;
                }

                return node;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            AppServices.Log.Info($"Oxco-Vorschau neu gestartet: {ex.Message}");
            Kill();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        Kill();
        var psi = AppServices.JobRunner.CreateBridgeServerStartInfo("oxco_preview_server.py");
        var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        _process = process;
        _openPaths = null;
        return process;
    }

    private void Kill()
    {
        var p = _process;
        _process = null;
        _openPaths = null;
        if (p is null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }

        p.Dispose();
    }

    private static int GetInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : 0;

    private static double GetDouble(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    public void Dispose()
    {
        try
        {
            _process?.StandardInput.Close();
        }
        catch
        {
            // ignore
        }

        Kill();
        _gate.Dispose();
    }
}
