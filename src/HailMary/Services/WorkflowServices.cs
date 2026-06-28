using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HailMary.Services;

public sealed class PythonMonitorRunner
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public PythonMonitorRunner(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    public bool Start(string bridgeFileName, IReadOnlyList<string> bridgeArgs)
    {
        if (IsRunning)
        {
            return false;
        }

        var scriptPath = Path.Combine(AppPaths.BridgesDirectory, bridgeFileName);
        if (!File.Exists(scriptPath))
        {
            _log.Error($"Bridge nicht gefunden: {scriptPath}");
            return false;
        }

        var python = _settings.ResolvePythonExecutable();
        var argsBuilder = new StringBuilder();
        argsBuilder.Append('"').Append(scriptPath).Append('"');
        foreach (var arg in bridgeArgs)
        {
            argsBuilder.Append(' ').Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
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
            };

            ApplyPath(psi);
            psi.Environment["HAIL_MARY_PROJECTS_ROOT"] = _settings.ProjectsRoot;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Info(e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Error(e.Data);
                }
            };
            _process.Exited += (_, _) => UiDispatcher.Run(() => _log.Info("Monitor beendet."));

            if (!_process.Start())
            {
                _process = null;
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _log.Success($"Monitor gestartet: {bridgeFileName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Monitor-Start fehlgeschlagen: {ex.Message}");
            _process = null;
            return false;
        }
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Monitor-Stopp: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _log.Info("Monitor gestoppt.");
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
                parts.Add(segment);
            }
        }

        Add(Environment.GetEnvironmentVariable("PATH"));
        psi.Environment["PATH"] = string.Join(';', parts);
    }
}

public sealed class RobocopySyncService
{
    private readonly LogService _log;
    private readonly Dictionary<int, Process> _running = new();
    private int _nextJobId = 1;

    static RobocopySyncService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public RobocopySyncService(LogService log)
    {
        _log = log;
    }

    public bool HasRunningJobs => _running.Count > 0;

    public void StartJobs(IReadOnlyList<DatenSyncJob> jobs, IReadOnlyList<string> switches, int intervalMinutes)
    {
        if (jobs.Count == 0)
        {
            throw new InvalidOperationException(Loc.T("datensync.noJobs"));
        }

        if (intervalMinutes < 0)
        {
            throw new InvalidOperationException(Loc.T("datensync.invalidInterval"));
        }

        _log.Info(Loc.F("datensync.log.startJobs", jobs.Count));
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Source) || string.IsNullOrWhiteSpace(job.Target))
            {
                continue;
            }

            if (string.Equals(Path.GetFullPath(job.Source), Path.GetFullPath(job.Target), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Loc.T("datensync.sourceTargetSame"));
            }

            var jobId = _nextJobId++;
            var cmd = BuildCommand(job.Source, job.Target, switches, intervalMinutes);
            _ = Task.Run(() => RunJob(jobId, job, cmd));
        }
    }

    public void StopAll()
    {
        foreach (var pair in _running.ToList())
        {
            try
            {
                if (!pair.Value.HasExited)
                {
                    pair.Value.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }
        }

        _running.Clear();
        _log.Info(Loc.T("datensync.log.allStopped"));
    }

    private List<string> BuildCommand(string source, string target, IReadOnlyList<string> switches, int intervalMinutes)
    {
        var cmd = new List<string> { "robocopy", source, target };
        cmd.AddRange(switches);
        if (intervalMinutes > 0)
        {
            cmd.Add($"/MOT:{intervalMinutes}");
        }

        return cmd;
    }

    private void RunJob(int jobId, DatenSyncJob job, List<string> cmd)
    {
        try
        {
            var oem = $"cp{GetOemCodePage()}";
            var psi = new ProcessStartInfo
            {
                FileName = cmd[0],
                Arguments = string.Join(' ', cmd.Skip(1).Select(QuoteArg)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(oem),
                StandardErrorEncoding = Encoding.GetEncoding(oem),
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _log.Error(Loc.F("datensync.log.processFailed", jobId));
                return;
            }

            _running[jobId] = process;
            _log.Info($"Job {jobId}: {job.Source} -> {job.Target}");
            _log.Info(string.Join(' ', cmd.Select(QuoteArg)));

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Info($"[Job {jobId}] {e.Data}");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Error($"[Job {jobId}] {e.Data}");
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            _log.Info(Loc.F("datensync.log.jobFinished", jobId, process.ExitCode));
        }
        catch (Exception ex)
        {
            _log.Error(Loc.F("datensync.log.jobError", jobId, ex.Message));
        }
        finally
        {
            _running.Remove(jobId);
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static int GetOemCodePage()
    {
        try
        {
            return (int)GetOEMCP();
        }
        catch
        {
            return 850;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}

public sealed class DatenSyncJob
{
    public string Source { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;
}
