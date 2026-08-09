using Microsoft.Win32;

namespace HailMary.Services;

public sealed class WindowsFontChoice
{
    public WindowsFontChoice(string displayName, string path)
    {
        DisplayName = displayName;
        Path = path;
    }

    public string DisplayName { get; }

    public string Path { get; }

    public override string ToString() => DisplayName;
}

/// <summary>Lists installed Windows font files (TTF/OTF/TTC) for overlay pickers.</summary>
public static class WindowsFontCatalog
{
    private static IReadOnlyList<WindowsFontChoice>? _cache;

    public static IReadOnlyList<WindowsFontChoice> GetFonts(bool refresh = false)
    {
        if (_cache is not null && !refresh)
        {
            return _cache;
        }

        try
        {
            var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HarvestRegistry(byPath);
            MergeScannedFiles(byPath);

            var list = new List<WindowsFontChoice>
            {
                new(Loc.T("texttovideo.fontDefault"), string.Empty),
            };
            list.AddRange(
                byPath
                    .Select(kv => new WindowsFontChoice(CleanDisplayName(kv.Value), kv.Key))
                    .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase));

            _cache = list;
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Fontkatalog: {ex.Message}");
            _cache =
            [
                new(Loc.T("texttovideo.fontDefault"), string.Empty),
            ];
        }

        return _cache;
    }

    public static WindowsFontChoice? FindByPath(string? path)
    {
        var fonts = GetFonts();
        if (fonts.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return fonts[0];
        }

        return fonts.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanDisplayName(string raw)
    {
        var name = raw.Trim();
        foreach (var suffix in new[] { " (TrueType)", " (OpenType)", " (TrueType Collection)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(name) ? raw : name;
    }

    private static IEnumerable<string> FontDirectories()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windir))
        {
            yield return Path.Combine(windir, "Fonts");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
        }
    }

    private static void HarvestRegistry(Dictionary<string, string> byPath)
    {
        var dirs = FontDirectories().Select(d => new DirectoryInfo(d)).Where(d => d.Exists).ToList();
        var systemDir = dirs.FirstOrDefault()?.FullName ?? string.Empty;
        var localDir = dirs.ElementAtOrDefault(1)?.FullName ?? string.Empty;

        void Harvest(RegistryKey root)
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null)
                {
                    return;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var resolved = ResolveFontFile(raw, systemDir, localDir);
                    if (resolved is null)
                    {
                        continue;
                    }

                    byPath.TryAdd(resolved, valueName);
                }
            }
            catch
            {
                // ignore registry failures
            }
        }

        Harvest(Registry.LocalMachine);
        Harvest(Registry.CurrentUser);
    }

    private static void MergeScannedFiles(Dictionary<string, string> byPath)
    {
        foreach (var dir in FontDirectories())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var pattern in new[] { "*.ttf", "*.otf" })
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern))
                {
                    try
                    {
                        var full = Path.GetFullPath(file);
                        byPath.TryAdd(full, Path.GetFileNameWithoutExtension(full));
                    }
                    catch
                    {
                        // skip unreadable
                    }
                }
            }

            // .ttc last — works via Pillow, but skip scanning every collection face as clutter
            foreach (var file in Directory.EnumerateFiles(dir, "*.ttc"))
            {
                try
                {
                    var full = Path.GetFullPath(file);
                    byPath.TryAdd(full, Path.GetFileNameWithoutExtension(full));
                }
                catch
                {
                    // skip
                }
            }
        }
    }

    private static string? ResolveFontFile(string raw, string systemDir, string localDir)
    {
        var v = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(v) || v.EndsWith(".fon", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (File.Exists(v))
        {
            return Path.GetFullPath(v);
        }

        if (Path.IsPathRooted(v))
        {
            return null;
        }

        foreach (var baseDir in new[] { systemDir, localDir })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            var cand = Path.GetFullPath(Path.Combine(baseDir, v));
            if (File.Exists(cand))
            {
                return cand;
            }
        }

        return null;
    }
}
