namespace HailMary.Models;

public sealed class ToolDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Type { get; set; } = "subprocess";

    public string? Group { get; set; }

    public string? Bridge { get; set; }

    public string Folder { get; set; } = string.Empty;

    public string Script { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public List<string> Args { get; set; } = [];

    public Dictionary<string, string> EnvMap { get; set; } = [];
}
