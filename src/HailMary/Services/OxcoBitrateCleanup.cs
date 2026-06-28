namespace HailMary.Services;

public static class OxcoBitrateCleanup
{
    private const int MinOutputBytes = 4096;
    private const int DeleteRetryCount = 8;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    public static void DeleteSourcesAfterConvert(
        IEnumerable<BitrateConvertRow> rows,
        string inputRoot,
        string outputRoot,
        string suffix,
        bool outputMp4,
        bool deleteEnabled)
    {
        if (!deleteEnabled)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.Action, "convert", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outputPath = OxcoBitratePathHelper.ResolveConvertedOutputPath(
                row.Path, inputRoot, outputRoot, suffix, outputMp4);
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            {
                AppServices.Log.Error(
                    $"Original nicht gelöscht: Ausgabe fehlt für {Path.GetFileName(row.Path)}");
                continue;
            }

            try
            {
                if (new FileInfo(outputPath).Length < MinOutputBytes)
                {
                    AppServices.Log.Error(
                        $"Original nicht gelöscht: Ausgabe zu klein für {Path.GetFileName(row.Path)}");
                    continue;
                }
            }
            catch
            {
                continue;
            }

            if (!File.Exists(row.Path))
            {
                continue;
            }

            if (PathsEqual(row.Path, outputPath))
            {
                continue;
            }

            TryDeleteWithRetry(row.Path);
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteWithRetry(string path)
    {
        for (var attempt = 1; attempt <= DeleteRetryCount; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                if (!File.Exists(path))
                {
                    AppServices.Log.Info($"Original gelöscht: {Path.GetFileName(path)}");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (attempt == DeleteRetryCount)
                {
                    AppServices.Log.Error(
                        $"Original nicht gelöscht ({Path.GetFileName(path)}): {ex.Message}");
                    return;
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }
}

public sealed class BitrateConvertRow
{
    public string Path { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;
}
