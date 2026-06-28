using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.ViewModels;
using HailMary.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Services;

public static class HybridPanelFactory
{
    public static ToolWorkspace CreateWorkspace(ToolDefinition tool, string groupId) => tool.Type.Equals("hybrid", StringComparison.OrdinalIgnoreCase)
        ? CreateHybrid(tool, groupId)
        : CreateSubprocess(tool, groupId);

    private static ToolWorkspace CreateHybrid(ToolDefinition tool, string groupId) => tool.Id switch
    {
        "cutter" => With(new SceneCutterViewModel(tool), tool, groupId, VideoPreviewKind.SceneCutter, vm => new SceneCutterPanel(vm)),
        "intro_cutter" => With(new IntroCutterViewModel(tool), tool, groupId, VideoPreviewKind.IntroCutter, vm => new IntroCutterPanel(vm)),
        "bitratechanger" => With(new BitrateChangerViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new BitrateChangerPanel(vm)),
        "audiocleaner" => With(new AudioCleanerViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new AudioCleanerPanel(vm)),
        "clip_joiner" => With(new ClipJoinerViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new ClipJoinerPanel(vm)),
        "davinci_batch_render" => With(new DavinciBatchRenderViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new DavinciBatchRenderPanel(vm)),
        "daten_sync" => With(new DatenSyncViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new DatenSyncPanel(vm)),
        "dl_sort" => With(new DlSortViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new DlSortPanel(vm)),
        "autotagger" => With(new AutotaggerViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new AutotaggerPanel(vm)),
        "stash_pathfinder" => With(new StashPathfinderViewModel(tool), tool, groupId, preview: VideoPreviewKind.None, vm => new StashPathfinderPanel(vm)),
        "stash_cutter" => With(new StashCutterViewModel(tool), tool, groupId, VideoPreviewKind.SceneCutter, vm => new StashCutterPanel(vm)),
        "marker_updater" => With(new MarkerUpdaterViewModel(tool), tool, groupId, VideoPreviewKind.MarkerUpdater, vm => new MarkerUpdaterPanel(vm)),
        "marker_autocut" => With(new MarkerAutocutViewModel(tool), tool, groupId, VideoPreviewKind.None, vm => new MarkerAutocutPanel(vm)),
        "text_to_video" => With(new TextToVideoViewModel(tool), tool, groupId, VideoPreviewKind.None, vm => new TextToVideoPanel(vm)),
        "oxco" => With(new OxcoCompareViewModel(tool), tool, groupId, VideoPreviewKind.None, vm => new OxcoComparePanel(vm)),
        _ => new ToolWorkspace
        {
            GroupId = groupId,
            Tool = tool,
            Host = new FallbackToolShellHost(tool),
            Content = new TextBlock { Text = $"Hybrid-Tab '{tool.Id}' noch nicht implementiert.", Padding = new Thickness(16) },
        },
    };

    private static ToolWorkspace CreateSubprocess(ToolDefinition tool, string groupId)
    {
        var vm = new ToolTabViewModel(tool);
        return new ToolWorkspace
        {
            GroupId = groupId,
            Tool = tool,
            Host = vm,
            Content = new SubprocessToolView(vm),
        };
    }

    private static ToolWorkspace With<TVm>(
        TVm vm,
        ToolDefinition tool,
        string groupId,
        VideoPreviewKind preview,
        Func<TVm, UIElement> createPanel)
        where TVm : IToolShellHost =>
        new()
        {
            GroupId = groupId,
            Tool = tool,
            Host = vm,
            Content = createPanel(vm),
            PreviewKind = preview,
        };
}

internal sealed class FallbackToolShellHost : IToolShellHost
{
    private readonly ToolDefinition _tool;

    public FallbackToolShellHost(ToolDefinition tool) => _tool = tool;

    public string PrimaryActionLabel => "Start";
    public IAsyncRelayCommand PrimaryActionCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
    public bool IsPrimaryActionEnabled => false;
    public string StatusText => _tool.Description;
    public bool IsBusy => false;
    public bool HasVideoPreview => false;
    public bool HasSettings => false;
    public IRelayCommand? OpenSettingsCommand => null;
    public bool HasOpenFullGui => false;
    public string OpenFullGuiLabel => string.Empty;
    public IRelayCommand? OpenFullGuiCommand => null;
    public object? SettingsContext => null;
}
