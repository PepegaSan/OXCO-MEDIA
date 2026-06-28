namespace HailMary.Models;

public sealed class ToolIoState
{
    public string? InputPath { get; set; }

    public List<string> InputPaths { get; set; } = [];

    public string? OutputDir { get; set; }
}
