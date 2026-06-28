using System.Text.Json;
using HailMary.Models;

namespace HailMary.Services;

public sealed class ToolRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] GroupOrder = ["video", "stash", "oxco", "workflow", "other"];

    private static readonly Dictionary<string, string> GroupLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video"] = "Video",
        ["stash"] = "Stash",
        ["oxco"] = "Oxco",
        ["workflow"] = "Organisation",
        ["other"] = "Sonstiges",
    };

    public IReadOnlyList<ToolDefinition> Tools { get; private set; } = [];

    public IReadOnlyList<ToolGroup> Groups { get; private set; } = [];

    public void Load()
    {
        var path = AppPaths.ToolsJsonPath;
        if (!File.Exists(path))
        {
            Tools = [];
            Groups = [];
            return;
        }

        var json = File.ReadAllText(path);
        var tools = JsonSerializer.Deserialize<List<ToolDefinition>>(json, JsonOptions) ?? [];
        Tools = tools.Where(t => t.Enabled).ToList();
        Groups = BuildGroups(Tools);
    }

    private static IReadOnlyList<ToolGroup> BuildGroups(IReadOnlyList<ToolDefinition> tools)
    {
        var grouped = tools
            .GroupBy(t => NormalizeGroupId(t.Group))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ToolGroup>();
        foreach (var id in GroupOrder)
        {
            if (!grouped.TryGetValue(id, out var list) || list.Count == 0)
            {
                continue;
            }

            result.Add(new ToolGroup
            {
                Id = id,
                Label = AppServices.Localization.GroupLabel(id, GroupLabels[id]),
                Tools = list,
            });
            grouped.Remove(id);
        }

        foreach (var extra in grouped.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new ToolGroup
            {
                Id = extra.Key,
                Label = AppServices.Localization.GroupLabel(extra.Key, GroupLabels.GetValueOrDefault(extra.Key, extra.Key)),
                Tools = extra.Value,
            });
        }

        return result;
    }

    private static string NormalizeGroupId(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return "other";
        }

        return group.Trim().ToLowerInvariant() switch
        {
            "simple_video" => "video",
            _ => group.Trim().ToLowerInvariant(),
        };
    }
}
