namespace HailMary.Services;

public sealed class CutterWorkspacePaths
{
    public static CutterWorkspacePaths VideoCutter { get; } = new("Cutter");

    public static CutterWorkspacePaths StashCutter { get; } = new("Stash Cutter");

    private CutterWorkspacePaths(string folderName)
    {
        FolderName = folderName;
    }

    public string FolderName { get; }

    public string ProjectDirectory(string projectsRoot) =>
        Path.Combine(AppPaths.ResolveProjectsRoot(projectsRoot), FolderName);

    public string ConfigPath(string projectsRoot) =>
        Path.Combine(ProjectDirectory(projectsRoot), "cutter_app_config.json");

    public string AutosavePath(string projectsRoot) =>
        Path.Combine(ProjectDirectory(projectsRoot), "cutter_scene_autosave.json");
}
