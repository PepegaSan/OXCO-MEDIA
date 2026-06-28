using System.Text.Json;
using HailMary.Models;
using HailMary.ViewModels;
using Windows.Storage;

namespace HailMary.Services;

public enum VideoPreviewKind
{
    None,
    SceneCutter,
    IntroCutter,
    MarkerUpdater,
}

public sealed class ToolWorkspace
{
    public required string GroupId { get; init; }

    public required ToolDefinition Tool { get; init; }

    public required IToolShellHost Host { get; init; }

    public required Microsoft.UI.Xaml.UIElement Content { get; init; }

    public VideoPreviewKind PreviewKind { get; init; } = VideoPreviewKind.None;
}

public static class ToolShellSelectionStore
{
    private const string SettingsKey = "ToolShell.LastToolByGroup";
    private static readonly Dictionary<string, string> LastToolByGroup = Load();

    public static string? GetLastTool(string groupId) =>
        LastToolByGroup.TryGetValue(groupId, out var id) ? id : null;

    public static void SetLastTool(string groupId, string toolId)
    {
        LastToolByGroup[groupId] = toolId;
        Save();
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[SettingsKey] is string json
                && !string.IsNullOrWhiteSpace(json))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // ignore corrupt settings
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[SettingsKey] =
                JsonSerializer.Serialize(LastToolByGroup);
        }
        catch
        {
            // ignore persistence failures
        }
    }
}
