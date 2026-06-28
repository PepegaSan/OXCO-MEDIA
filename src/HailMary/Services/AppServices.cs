namespace HailMary.Services;

public static class AppServices
{
    public static LogService Log { get; } = new();

    public static JobProgressService JobProgress { get; } = new();

    public static SessionService Session { get; } = new();

    public static SettingsService Settings { get; } = new();

    public static StashSettingsService StashSettings { get; } = new();

    public static ViewModels.GlobalStashSettingsViewModel GlobalStashSettings { get; } = new();

    public static ToolRegistry Tools { get; } = new();

    public static PythonProcessLauncher Launcher { get; private set; } = null!;

    public static PythonJobRunner JobRunner { get; private set; } = null!;

    public static PythonMonitorRunner MonitorRunner { get; private set; } = null!;

    public static RobocopySyncService DatenSync { get; private set; } = null!;

    public static ThemeService Theme { get; } = new();

    public static LocalizationService Localization { get; } = new();

    public static void Initialize()
    {
        Settings.Load();
        Localization.LoadFromSettings();
        StashSettings.Initialize(Settings);
        Session.Load();
        Tools.Load();
        Launcher = new PythonProcessLauncher(Settings, Session, Log);
        JobRunner = new PythonJobRunner(Settings, Session, Log);
        MonitorRunner = new PythonMonitorRunner(Settings, Log);
        DatenSync = new RobocopySyncService(Log);
        Log.Info($"Projects-Root: {Settings.ProjectsRoot}");
        Log.Info($"Python: {Settings.ResolvePythonExecutable()}");
        Log.Info($"{Tools.Tools.Count} Tools in {Tools.Groups.Count} Gruppen geladen.");
    }
}
