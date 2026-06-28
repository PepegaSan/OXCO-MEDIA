using System.Text.Json;
using System.Text.Json.Serialization;
using HailMary.Models;

namespace HailMary.Services;

public static class CutterSceneAutosave
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string AutosavePath(string projectsRoot, CutterWorkspacePaths? workspace = null) =>
        (workspace ?? CutterWorkspacePaths.VideoCutter).AutosavePath(projectsRoot);

    public sealed class AutosaveData
    {
        public string InputFile { get; init; } = string.Empty;

        public bool ChronoSort { get; init; }

        public bool SceneSelectMode { get; init; }

        public List<SceneEntry> Scenes { get; init; } = [];
    }

    public static AutosaveData? TryLoad(
        string projectsRoot,
        string currentInput,
        CutterWorkspacePaths? workspace = null)
    {
        var path = AutosavePath(projectsRoot, workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var ver) && ver.GetInt32() != 1)
            {
                return null;
            }

            var inputFile = root.TryGetProperty("input_file", out var inp)
                ? inp.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(inputFile) ||
                !File.Exists(inputFile) ||
                !PathsEqual(inputFile, currentInput))
            {
                return null;
            }

            if (!root.TryGetProperty("scene_pairs", out var pairsEl) ||
                pairsEl.ValueKind != JsonValueKind.Array ||
                pairsEl.GetArrayLength() == 0)
            {
                return null;
            }

            var picks = new List<bool>();
            if (root.TryGetProperty("picks", out var picksEl) &&
                picksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in picksEl.EnumerateArray())
                {
                    picks.Add(p.GetBoolean());
                }
            }

            var sceneSelect = root.TryGetProperty("scene_select_mode", out var sm) && sm.GetBoolean();
            var chrono = !root.TryGetProperty("chrono_sort", out var ch) || ch.GetBoolean();
            var scenes = new List<SceneEntry>();
            var idx = 0;
            foreach (var pair in pairsEl.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                {
                    return null;
                }

                var startRaw = pair[0].GetString() ?? string.Empty;
                var endRaw = pair[1].GetString() ?? string.Empty;
                if (!TimecodeHelper.TryParse(startRaw, out var start, out _) ||
                    !TimecodeHelper.TryParse(endRaw, out var end, out _))
                {
                    if (string.IsNullOrWhiteSpace(endRaw))
                    {
                        continue;
                    }

                    return null;
                }

                if (end <= start)
                {
                    continue;
                }

                var entry = SceneEntry.FromSeconds(start, end, scenes.Count + 1);
                if (sceneSelect && idx < picks.Count)
                {
                    entry.IsSelected = picks[idx];
                }

                scenes.Add(entry);
                idx++;
            }

            if (scenes.Count == 0)
            {
                return null;
            }

            return new AutosaveData
            {
                InputFile = inputFile,
                ChronoSort = chrono,
                SceneSelectMode = sceneSelect,
                Scenes = scenes,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(
        string projectsRoot,
        string inputPath,
        IEnumerable<SceneEntry> scenes,
        bool chronoSort,
        bool partialSelectMode,
        CutterWorkspacePaths? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return;
        }

        var list = scenes.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var payload = new AutosavePayload
        {
            Version = 1,
            InputFile = Path.GetFullPath(inputPath),
            ScenePairs = list.Select(s => new[] { s.StartInput, s.EndInput }).ToArray(),
            ChronoSort = chronoSort,
            SceneSelectMode = partialSelectMode,
            Picks = partialSelectMode ? list.Select(s => s.IsSelected).ToArray() : null,
            ActiveSceneIndex = 0,
        };

        var path = AutosavePath(projectsRoot, workspace);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    public static void Clear(string projectsRoot, CutterWorkspacePaths? workspace = null)
    {
        try
        {
            var path = AutosavePath(projectsRoot, workspace);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class AutosavePayload
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

        [JsonPropertyName("picks")]
        public bool[]? Picks { get; set; }
    }
}
