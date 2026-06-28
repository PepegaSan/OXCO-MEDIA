namespace HailMary.Services;

public static class OxcoBitratePathHelper
{
    public static string? ResolveConvertedOutputPath(
        string sourcePath,
        string inputRoot,
        string outputRoot,
        string suffix,
        bool outputMp4)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(inputRoot) ||
            string.IsNullOrWhiteSpace(outputRoot))
        {
            return null;
        }

        try
        {
            var inRoot = Path.GetFullPath(inputRoot.Trim());
            var outRoot = Path.GetFullPath(outputRoot.Trim());
            var src = Path.GetFullPath(sourcePath);

            var relative = src.StartsWith(inRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(inRoot, src)
                : Path.GetFileName(src);

            var relDir = Path.GetDirectoryName(relative) ?? string.Empty;
            var relName = Path.GetFileName(relative);
            var relStem = Path.GetFileNameWithoutExtension(relName);
            var relExt = Path.GetExtension(relName);
            var plannedExt = outputMp4 ? ".mp4" : relExt;

            var outParent = string.IsNullOrEmpty(relDir)
                ? outRoot
                : Path.Combine(outRoot, relDir);

            var effSuffix = EffectiveSuffix(inRoot, outRoot, relName, plannedExt, suffix);
            return Path.Combine(outParent, $"{relStem}{effSuffix}{plannedExt}");
        }
        catch
        {
            return null;
        }
    }

    private static string EffectiveSuffix(
        string inputRoot,
        string outputRoot,
        string sourceName,
        string plannedExt,
        string suffix)
    {
        var raw = suffix.Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        if (!string.Equals(inputRoot, outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var plannedName = Path.ChangeExtension(sourceName, plannedExt);
        return string.Equals(sourceName, plannedName, StringComparison.OrdinalIgnoreCase)
            ? "_bitrate"
            : string.Empty;
    }
}
