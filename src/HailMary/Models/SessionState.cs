namespace HailMary.Models;

public sealed class SessionState
{
    public List<string> InputPaths { get; set; } = [];

    public string? OutputDir { get; set; }

    public string? StashSceneId { get; set; }

    public string? LastOutput { get; set; }

    public Dictionary<string, ToolIoState> ToolIo { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string PrimaryInput => InputPaths.Count > 0 ? InputPaths[0] : string.Empty;

    public string InputSummary =>
        InputPaths.Count switch
        {
            0 => "(keine Eingabe)",
            1 => InputPaths[0],
            _ => $"{InputPaths[0]} (+{InputPaths.Count - 1})",
        };
}
