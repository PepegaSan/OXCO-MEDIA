using System.Text.Json;

using HailMary.Models;



namespace HailMary.Services;



public sealed class SessionService

{

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };



    public SessionState Current { get; private set; } = new();



    public event Action? SessionChanged;



    public void Load()

    {

        try

        {

            if (File.Exists(AppPaths.SessionFilePath))

            {

                var json = File.ReadAllText(AppPaths.SessionFilePath);

                Current = JsonSerializer.Deserialize<SessionState>(json, JsonOptions) ?? new SessionState();

            }

        }

        catch

        {

            Current = new SessionState();

        }



        NotifySessionChanged();

    }



    public void Save()

    {

        try

        {

            var json = JsonSerializer.Serialize(Current, JsonOptions);

            File.WriteAllText(AppPaths.SessionFilePath, json);

        }

        catch

        {

            // Persistenz ist best-effort.

        }

    }



    public void SetInputPaths(IEnumerable<string> paths)

    {

        Current.InputPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct().ToList();

        Save();

        NotifySessionChanged();

    }



    public void SetOutputDir(string? path)

    {

        Current.OutputDir = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

        Save();

        NotifySessionChanged();

    }



    public void SetStashSceneId(string? id)

    {

        Current.StashSceneId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();

        Save();

        NotifySessionChanged();

    }



    public void SetLastOutput(string? path)

    {

        Current.LastOutput = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

        Save();

        NotifySessionChanged();

    }



    public void UseLastOutputAsInput()

    {

        if (string.IsNullOrWhiteSpace(Current.LastOutput))

        {

            return;

        }



        SetInputPaths([Current.LastOutput]);

    }



    public ToolIoState GetToolIo(string toolId)

    {

        if (!Current.ToolIo.TryGetValue(toolId, out var state) || state is null)

        {

            state = new ToolIoState();

            Current.ToolIo[toolId] = state;

        }



        return state;

    }



    public void SaveToolIo(string toolId, ToolIoState state)

    {

        Current.ToolIo[toolId] = state;

        Save();

    }



    public void UpdateToolIo(string toolId, Action<ToolIoState> mutate)

    {

        var state = GetToolIo(toolId);

        mutate(state);

        SaveToolIo(toolId, state);

    }



    public Dictionary<string, string> BuildEnvironment()

    {

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);



        if (Current.InputPaths.Count > 0)

        {

            env["HAIL_MARY_INPUT"] = Current.PrimaryInput;

            env["HAIL_MARY_INPUTS"] = string.Join(';', Current.InputPaths);

        }



        if (!string.IsNullOrWhiteSpace(Current.OutputDir))

        {

            env["HAIL_MARY_OUTPUT_DIR"] = Current.OutputDir;

        }



        if (!string.IsNullOrWhiteSpace(Current.StashSceneId))

        {

            env["HAIL_MARY_STASH_ID"] = Current.StashSceneId;

        }



        return env;

    }



    private void NotifySessionChanged() => UiDispatcher.Run(() => SessionChanged?.Invoke());

}


