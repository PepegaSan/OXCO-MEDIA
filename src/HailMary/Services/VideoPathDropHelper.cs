namespace HailMary.Services;

public static class VideoPathDropHelper
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".mpg", ".mpeg", ".wmv",
        ".mts", ".m2ts", ".flv",
    };

    public static bool IsVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return VideoExtensions.Contains(Path.GetExtension(path));
    }

    public static IEnumerable<string> ExpandVideoPaths(
        IEnumerable<string> paths,
        bool recursive = true)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(full, "*.*", option))
                    {
                        if (!IsVideoFile(file))
                        {
                            continue;
                        }

                        var normalized = Path.GetFullPath(file);
                        if (seen.Add(normalized))
                        {
                            result.Add(normalized);
                        }
                    }
                }
                catch (IOException)
                {
                    // Ordner nicht lesbar
                }
            }
            else if (IsVideoFile(full) && seen.Add(full))
            {
                result.Add(full);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static string? PickFirstVideoOrFolder(IEnumerable<string> paths, bool allowFolders)
    {
        string? firstVideo = null;
        string? firstFolder = null;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (File.Exists(full) && IsVideoFile(full))
            {
                firstVideo ??= full;
            }
            else if (allowFolders && Directory.Exists(full))
            {
                firstFolder ??= full;
            }
        }

        return firstVideo ?? firstFolder;
    }
}
