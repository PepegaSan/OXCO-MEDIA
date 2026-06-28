using System.Diagnostics;
using System.Text;
using HailMary.Models;

namespace HailMary.Services;

public sealed class PythonJobRunner
{
    private readonly SettingsService _settings;
    private readonly SessionService _session;
    private readonly LogService _log;

    public PythonJobRunner(SettingsService settings, SessionService session, LogService log)
    {
        _settings = settings;
        _session = session;
        _log = log;
    }

    public async Task<JobResult> RunBridgeAsync(
        string bridgeFileName,
        IReadOnlyList<string> bridgeArgs,
        CancellationToken cancellationToken = default,
        bool quiet = false,
        Action<string>? onOutputLine = null)
    {
        var scriptPath = Path.Combine(AppPaths.BridgesDirectory, bridgeFileName);
        if (!File.Exists(scriptPath))
        {
            var msg = $"Bridge nicht gefunden: {scriptPath}";
            _log.Error(msg);
            return new JobResult { Success = false, ExitCode = -1, Message = msg };
        }

        var python = _settings.ResolvePythonExecutable();
        var argsBuilder = new StringBuilder();
        argsBuilder.Append('"').Append(scriptPath).Append('"');
        foreach (var arg in bridgeArgs)
        {
            argsBuilder.Append(' ').Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
        }

        if (!quiet)
        {
            _log.Info($"Job startet: {bridgeFileName}{FormatArgsSummary(bridgeArgs)}");
        }

        var trackProgress = !quiet;

        if (trackProgress)
        {
            AppServices.JobProgress.BeginJob();
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = argsBuilder.ToString(),
                WorkingDirectory = AppPaths.BridgesDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            ApplyPath(psi);

            psi.Environment["HAIL_MARY_PROJECTS_ROOT"] = _settings.ProjectsRoot;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            foreach (var pair in _session.BuildEnvironment())
            {
                psi.Environment[pair.Key] = pair.Value;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                stdout.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
                if (trackProgress)
                {
                    AppServices.JobProgress.TryApplyFromLogLine(e.Data);
                }
                if (!quiet)
                {
                    _log.Info(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                stderr.AppendLine(e.Data);
            };

            if (!process.Start())
            {
                return new JobResult { Success = false, ExitCode = -1, Message = "Prozess konnte nicht gestartet werden." };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using var _ = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignore
                }
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new JobResult { Success = false, ExitCode = -2, Message = "Job abgebrochen." };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new JobResult { Success = false, ExitCode = -2, Message = "Job abgebrochen." };
            }

            var exitCode = process.ExitCode;
            var outputPath = ParseOutputPath(stdout.ToString());

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(outputPath))
            {
                _session.SetLastOutput(outputPath);
                var ok = $"Fertig: {Path.GetFileName(outputPath)}";
                if (!quiet)
                {
                    _log.Success(ok);
                }

                return new JobResult { Success = true, ExitCode = 0, OutputPath = outputPath, Message = ok };
            }

            if (exitCode == 0)
            {
                if (!quiet)
                {
                    _log.Success("Job abgeschlossen.");
                }

                return new JobResult { Success = true, ExitCode = 0, Message = "Job abgeschlossen." };
            }

            var fail = $"Job fehlgeschlagen (Code {exitCode})";
            LogStderrTail(stderr, asError: true);
            _log.Error(fail);
            return new JobResult { Success = false, ExitCode = exitCode, Message = fail };
        }
        catch (Exception ex)
        {
            var msg = $"Job-Fehler: {ex.Message}";
            _log.Error(msg);
            return new JobResult { Success = false, ExitCode = -1, Message = msg };
        }
        finally
        {
            if (trackProgress)
            {
                AppServices.JobProgress.EndJob();
            }
        }
    }

    private static void ApplyPath(ProcessStartInfo psi)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    parts.Add(segment);
                }
            }
        }

        Add(Environment.GetEnvironmentVariable("PATH"));
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        parts.Add(Path.Combine(localApp, "Microsoft", "WinGet", "Links"));
        parts.Add(Path.Combine(localApp, "Programs", "Python", "Python312"));
        parts.Add(Path.Combine(localApp, "Programs", "Python", "Python312", "Scripts"));
        parts.Add(Path.Combine(localApp, "Programs", "Python", "Python311"));
        parts.Add(Path.Combine(localApp, "Programs", "Python", "Python311", "Scripts"));

        psi.Environment["PATH"] = string.Join(';', parts);
    }

    private void LogStderrTail(StringBuilder stderr, bool asError)
    {
        var errTail = stderr.ToString().Trim();
        if (string.IsNullOrWhiteSpace(errTail))
        {
            return;
        }

        foreach (var line in errTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(5))
        {
            if (asError)
            {
                _log.Error(line);
            }
        }
    }

    private static string? ParseOutputPath(string stdout)
    {
        string? last = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("OUTPUT:", StringComparison.OrdinalIgnoreCase))
            {
                last = line["OUTPUT:".Length..].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    private static string FormatArgsSummary(IReadOnlyList<string> bridgeArgs)
    {
        if (bridgeArgs.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (var i = 0; i < bridgeArgs.Count; i++)
        {
            var arg = bridgeArgs[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsJsonArgFlag(arg))
            {
                parts.Add(JsonArgLabel(arg));
                i++;
                continue;
            }

            if (i + 1 < bridgeArgs.Count && !bridgeArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parts.Add($"{arg[2..]}={bridgeArgs[i + 1]}");
                i++;
            }
            else
            {
                parts.Add(arg[2..]);
            }
        }

        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    private static bool IsJsonArgFlag(string flag) =>
        flag is "--config-json" or "--output-json" or "--rows-json";

    private static string JsonArgLabel(string flag) => flag switch
    {
        "--config-json" => "Konfig",
        "--output-json" => "Ausgabe",
        "--rows-json" => "Zeilen",
        _ => "JSON",
    };
}
