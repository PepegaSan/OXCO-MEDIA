namespace HailMary.Models;

public sealed class ToolGroup
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
}
