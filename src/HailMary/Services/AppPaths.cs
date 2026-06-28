namespace HailMary.Services;

public static class AppPaths
{
    public static string SettingsDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HailMary");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SessionFilePath => Path.Combine(SettingsDirectory, "session.json");

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    public static string ToolsJsonPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "tools.json"),
                Path.Combine(AppContext.BaseDirectory, "tools.json"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return candidates[0];
        }
    }

    public static string ResolveProjectsRoot(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var env = Environment.GetEnvironmentVariable("HAIL_MARY_PROJECTS_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Name.Equals("Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                return dir.Parent?.FullName ?? dir.FullName;
            }

            dir = dir.Parent;
        }

        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        if (cwd.Name.Equals("Hail Mary", StringComparison.OrdinalIgnoreCase))
        {
            return cwd.Parent?.FullName ?? cwd.FullName;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    public static string HailMaryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (dir.Name.Equals("Hail Mary", StringComparison.OrdinalIgnoreCase))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
            if (cwd.Name.Equals("Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                return cwd.FullName;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        }
    }

    public static string BridgesDirectory
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "bridges"),
                Path.Combine(HailMaryRoot, "bridges"),
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return candidates[1];
        }
    }
}
