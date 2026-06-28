using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;

namespace HailMary.ViewModels;

public sealed partial class ToolNavItemViewModel : ObservableObject
{
    public required string ToolId { get; init; }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class ToolShellViewModel : ObservableObject
{
    private readonly Dictionary<string, ToolWorkspace> _workspacesByToolId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ToolNavItemViewModel>> _navByGroup = new(StringComparer.OrdinalIgnoreCase);

    public ToolShellViewModel()
    {
        foreach (var group in AppServices.Tools.Groups)
        {
            Groups.Add(group);
            var navItems = new List<ToolNavItemViewModel>();
            foreach (var tool in group.Tools)
            {
                var workspace = HybridPanelFactory.CreateWorkspace(tool, group.Id);
                _workspacesByToolId[tool.Id] = workspace;
                navItems.Add(new ToolNavItemViewModel { ToolId = tool.Id, Label = ToolText.Label(tool) });
            }

            _navByGroup[group.Id] = navItems;
        }

        if (Groups.Count > 0)
        {
            SelectedGroup = Groups[0];
        }
    }

    public ObservableCollection<ToolGroup> Groups { get; } = [];

    [ObservableProperty]
    private ToolGroup? _selectedGroup;

    [ObservableProperty]
    private ToolNavItemViewModel? _selectedToolNav;

    [ObservableProperty]
    private IToolShellHost? _activeHost;

    [ObservableProperty]
    private Microsoft.UI.Xaml.UIElement? _activeContent;

    [ObservableProperty]
    private VideoPreviewKind _activePreviewKind = VideoPreviewKind.None;

    [ObservableProperty]
    private object? _activePreviewViewModel;

    public IReadOnlyList<ToolNavItemViewModel> CurrentNavItems =>
        SelectedGroup is null ? [] : _navByGroup.GetValueOrDefault(SelectedGroup.Id) ?? [];

    partial void OnSelectedGroupChanged(ToolGroup? value)
    {
        if (value is null)
        {
            return;
        }

        var lastId = ToolShellSelectionStore.GetLastTool(value.Id);
        var navItems = CurrentNavItems;
        var pick = navItems.FirstOrDefault(n => n.ToolId.Equals(lastId, StringComparison.OrdinalIgnoreCase))
                   ?? navItems.FirstOrDefault();
        SelectToolNav(pick);
    }

    public void SelectToolNav(ToolNavItemViewModel? item)
    {
        foreach (var nav in CurrentNavItems)
        {
            nav.IsSelected = nav == item;
        }

        SelectedToolNav = item;
        if (item is null || SelectedGroup is null)
        {
            ActiveHost = null;
            ActiveContent = null;
            ActivePreviewKind = VideoPreviewKind.None;
            ActivePreviewViewModel = null;
            return;
        }

        ToolShellSelectionStore.SetLastTool(SelectedGroup.Id, item.ToolId);
        if (!_workspacesByToolId.TryGetValue(item.ToolId, out var workspace))
        {
            return;
        }

        ActiveHost = workspace.Host;
        ActiveContent = workspace.Content;
        ActivePreviewKind = workspace.PreviewKind;
        ActivePreviewViewModel = workspace.PreviewKind switch
        {
            VideoPreviewKind.SceneCutter => workspace.Host as SceneCutterViewModel,
            VideoPreviewKind.IntroCutter => workspace.Host as IntroCutterViewModel,
            VideoPreviewKind.MarkerUpdater => workspace.Host as MarkerUpdaterViewModel,
            _ => null,
        };
    }

    public void RefreshLocalization()
    {
        AppServices.Tools.Load();
        Groups.Clear();
        foreach (var group in AppServices.Tools.Groups)
        {
            Groups.Add(group);
        }

        foreach (var navList in _navByGroup.Values)
        {
            foreach (var nav in navList)
            {
                var tool = AppServices.Tools.Tools.FirstOrDefault(t =>
                    t.Id.Equals(nav.ToolId, StringComparison.OrdinalIgnoreCase));
                if (tool is not null)
                {
                    nav.Label = ToolText.Label(tool);
                }
            }
        }

        OnPropertyChanged(nameof(CurrentNavItems));

        foreach (var workspace in _workspacesByToolId.Values)
        {
            LocalizationNotify.Description(workspace.Host);
            if (workspace.Host is ILocalizable localizable)
            {
                localizable.RefreshLocalization();
            }
        }
    }
}
