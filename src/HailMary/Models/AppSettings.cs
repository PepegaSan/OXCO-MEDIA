namespace HailMary.Models;

public sealed class AppSettings
{
    public string? PythonExecutable { get; set; }

    public string? ProjectsRoot { get; set; }

    public StashConnectionSettings Stash { get; set; } = new();

    public DavinciResolveSettings Davinci { get; set; } = new();

    public double WindowWidth { get; set; } = 1100;

    public double WindowHeight { get; set; } = 780;

    public int WindowX { get; set; } = 100;

    public int WindowY { get; set; } = 100;

    /// <summary>System, Light or Dark.</summary>
    public UiTheme UiTheme { get; set; } = UiTheme.System;

    /// <summary>de or en.</summary>
    public string UiLanguage { get; set; } = "de";
}
