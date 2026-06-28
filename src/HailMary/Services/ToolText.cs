using HailMary.Models;

namespace HailMary.Services;

public static class ToolText
{
    public static string Description(ToolDefinition tool) =>
        AppServices.Localization.ToolDescription(tool.Id, tool.Description);

    public static string Label(ToolDefinition tool) =>
        AppServices.Localization.ToolLabel(tool.Id, tool.Label);
}
