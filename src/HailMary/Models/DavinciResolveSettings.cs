namespace HailMary.Models;

/// <summary>
/// Globale DaVinci Resolve / Scripting-API-Pfade (gelten für alle Tools).
/// </summary>
public sealed class DavinciResolveSettings
{
    /// <summary>Ordner „Modules“ mit DaVinciResolveScript.py</summary>
    public string? ApiModulesPath { get; set; }

    public string? ExePath { get; set; }

    public string? FusionScriptDll { get; set; }
}
