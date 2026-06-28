using System.Text.Json;



namespace HailMary.Services;



public static class CutterConfigReader

{

    public sealed record CutterConfig(

        string DavinciPreset,

        string DavinciOutputDir,

        string ResolveExe,

        string ResolveModules,

        string ResolveDll);



    public static CutterConfig Load(string? projectsRoot, CutterWorkspacePaths? workspace = null)
    {
        workspace ??= CutterWorkspacePaths.VideoCutter;
        const string fallbackPreset = "YouTube - 1080p";
        var fallbackOut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        var path = workspace.ConfigPath(AppPaths.ResolveProjectsRoot(projectsRoot));



        if (!File.Exists(path))

        {

            return new CutterConfig(fallbackPreset, fallbackOut, "", "", "");

        }



        try

        {

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var root = doc.RootElement;

            return new CutterConfig(

                GetString(root, "davinci_preset", fallbackPreset),

                GetString(root, "davinci_output_dir", fallbackOut),

                GetString(root, "resolve_exe", ""),

                GetString(root, "resolve_modules", ""),

                GetString(root, "resolve_dll", ""));

        }

        catch (JsonException)

        {

            return new CutterConfig(fallbackPreset, fallbackOut, "", "", "");

        }

    }



    private static string GetString(JsonElement root, string name, string fallback) =>

        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String

            ? prop.GetString() ?? fallback

            : fallback;

    public static void Save(CutterConfig config, string projectsRoot, CutterWorkspacePaths? workspace = null)
    {
        workspace ??= CutterWorkspacePaths.VideoCutter;
        var path = workspace.ConfigPath(AppPaths.ResolveProjectsRoot(projectsRoot));

        try
        {
            Dictionary<string, object?> data;
            if (File.Exists(path))
            {
                try
                {
                    data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path))
                           ?? new Dictionary<string, object?>();
                }
                catch (JsonException)
                {
                    data = new Dictionary<string, object?>();
                }
            }
            else
            {
                data = new Dictionary<string, object?>();
            }

            data["davinci_preset"] = config.DavinciPreset;
            data["davinci_output_dir"] = config.DavinciOutputDir;
            data["resolve_exe"] = config.ResolveExe;
            data["resolve_modules"] = config.ResolveModules;
            data["resolve_dll"] = config.ResolveDll;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}


