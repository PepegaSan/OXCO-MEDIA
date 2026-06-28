using System.Text.Json;
using System.Text.Json.Serialization;

namespace HailMary.Services;

/// <summary>
/// Schreibt cutter_scene_autosave.json im Cutter-Ordner, damit cutter.py beim Start
/// die Session-Eingabe lädt — ohne cutter.py zu ändern (Runtime-Datei wie beim Original).
/// </summary>
public static class CutterAutosaveSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool TrySeed(
        string projectsRoot,
        string inputPath,
        LogService log,
        CutterWorkspacePaths? workspace = null)
    {
        workspace ??= CutterWorkspacePaths.VideoCutter;
        var video = Path.GetFullPath(inputPath);
        if (!File.Exists(video))
        {
            log.Error($"Autosave: Datei nicht gefunden: {video}");
            return false;
        }

        var cutterDir = workspace.ProjectDirectory(projectsRoot);
        if (!Directory.Exists(cutterDir))
        {
            log.Error($"Autosave: Cutter-Ordner fehlt: {cutterDir}");
            return false;
        }

        var payload = new CutterAutosavePayload
        {
            Version = 1,
            InputFile = video,
            ScenePairs = [["0:00:00.000", ""]],
            ActiveSceneIndex = 0,
            ChronoSort = true,
            SceneSelectMode = false,
        };

        var path = workspace.AutosavePath(projectsRoot);
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(path, json);
            log.Info($"Szenecutter-Vorbereitung: Video in Autosave ({Path.GetFileName(video)})");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Autosave schreiben fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    private sealed class CutterAutosavePayload
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("input_file")]
        public string InputFile { get; set; } = string.Empty;

        [JsonPropertyName("scene_pairs")]
        public string[][] ScenePairs { get; set; } = [];

        [JsonPropertyName("active_scene_index")]
        public int ActiveSceneIndex { get; set; }

        [JsonPropertyName("chrono_sort")]
        public bool ChronoSort { get; set; }

        [JsonPropertyName("scene_select_mode")]
        public bool SceneSelectMode { get; set; }
    }
}
