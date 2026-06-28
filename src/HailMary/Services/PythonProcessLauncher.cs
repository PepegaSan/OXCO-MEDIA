using System.Diagnostics;
using HailMary.Models;

namespace HailMary.Services;

public sealed class LaunchResult
{
    public bool Success { get; init; }

    public int? ProcessId { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class PythonProcessLauncher
{
    private readonly SettingsService _settings;
    private readonly SessionService _session;
    private readonly LogService _log;

    public PythonProcessLauncher(SettingsService settings, SessionService session, LogService log)
    {
        _settings = settings;
        _session = session;
        _log = log;
    }

    public LaunchResult Launch(ToolDefinition tool)
    {
        var projectsRoot = _settings.ProjectsRoot;
        var toolFolder = Path.Combine(projectsRoot, tool.Folder);
        var scriptPath = Path.Combine(toolFolder, tool.Script);

        if (!Directory.Exists(toolFolder))
        {
            var msg = $"Ordner nicht gefunden: {toolFolder}";
            _log.Error(msg);
            return new LaunchResult { Success = false, Message = msg };
        }

        if (!File.Exists(scriptPath))
        {
            var msg = $"Skript nicht gefunden: {scriptPath}";
            _log.Error(msg);
            return new LaunchResult { Success = false, Message = msg };
        }

        var python = _settings.ResolvePythonExecutable();
        var args = string.Join(' ', tool.Args.Select(a => $"\"{a}\""));
        var argumentString = $"\"{scriptPath}\"{ (args.Length > 0 ? " " + args : string.Empty) }";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = argumentString,
                WorkingDirectory = toolFolder,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            foreach (var pair in _session.BuildEnvironment())
            {
                psi.Environment[pair.Key] = pair.Value;
            }

            ApplyEnvMap(psi, tool);

            var process = Process.Start(psi);
            if (process is null)
            {
                var msg = $"Start fehlgeschlagen: {tool.Label}";
                _log.Error(msg);
                return new LaunchResult { Success = false, Message = msg };
            }

            var ok = $"Gestartet: {tool.Label} (PID {process.Id})";
            _log.Success(ok);
            return new LaunchResult { Success = true, ProcessId = process.Id, Message = ok };
        }
        catch (Exception ex)
        {
            var msg = $"Fehler beim Start von {tool.Label}: {ex.Message}";
            _log.Error(msg);
            return new LaunchResult { Success = false, Message = msg };
        }
    }

    private void ApplyEnvMap(ProcessStartInfo psi, ToolDefinition tool)
    {
        foreach (var (key, valuePath) in tool.EnvMap)
        {
            var value = ResolveEnvValue(valuePath);
            if (!string.IsNullOrWhiteSpace(value))
            {
                psi.Environment[key] = value;
            }
        }
    }

    private string? ResolveEnvValue(string path)
    {
        var session = _session.Current;
        return path switch
        {
            "inputPaths.0" => session.PrimaryInput.Length > 0 ? session.PrimaryInput : null,
            "inputPaths" => session.InputPaths.Count > 0 ? string.Join(';', session.InputPaths) : null,
            "outputDir" => session.OutputDir,
            "stashSceneId" => session.StashSceneId,
            "lastOutput" => session.LastOutput,
            _ => null,
        };
    }
}
