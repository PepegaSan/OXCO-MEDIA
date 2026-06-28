using HailMary.Services;
using HailMary.ViewModels;
using HailMary.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HailMary.Views.Shell;

public sealed partial class ToolShellPage : UserControl
{
    private readonly ToolShellViewModel _viewModel = new();

    public ToolShellPage()
    {
        InitializeComponent();
        BuildGroupTabs();
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
        KeyDown += ToolShellPage_KeyDown;
        Loaded += (_, _) => SelectInitialGroup();
        IsTabStop = true;
    }

    private void OnLanguageChanged()
    {
        var groupIndex = GroupTabView.SelectedIndex;
        var toolId = _viewModel.SelectedToolNav?.ToolId;

        _viewModel.RefreshLocalization();
        BuildGroupTabs();

        if (GroupTabView.TabItems.Count == 0)
        {
            return;
        }

        GroupTabView.SelectedIndex = Math.Clamp(groupIndex, 0, GroupTabView.TabItems.Count - 1);
        if (GroupTabView.SelectedItem is TabViewItem tab && tab.Tag is Models.ToolGroup group)
        {
            _viewModel.SelectedGroup = group;
        }

        ToolNavList.ItemsSource = _viewModel.CurrentNavItems;
        var selected = _viewModel.CurrentNavItems.FirstOrDefault(n =>
                           n.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase))
                       ?? _viewModel.CurrentNavItems.FirstOrDefault(n => n.IsSelected)
                       ?? _viewModel.CurrentNavItems.FirstOrDefault();
        ToolNavList.SelectedItem = selected;
        if (selected is not null)
        {
            _viewModel.SelectToolNav(selected);
            ApplyActiveTool();
        }
    }

    private void BuildGroupTabs()
    {
        GroupTabView.TabItems.Clear();
        foreach (var group in _viewModel.Groups)
        {
            GroupTabView.TabItems.Add(new TabViewItem
            {
                Header = group.Label,
                Tag = group,
                IsClosable = false,
            });
        }
    }

    private void SelectInitialGroup()
    {
        if (GroupTabView.TabItems.Count > 0)
        {
            GroupTabView.SelectedIndex = 0;
        }
    }

    private void GroupTabView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupTabView.SelectedItem is not TabViewItem item || item.Tag is not Models.ToolGroup group)
        {
            return;
        }

        _viewModel.SelectedGroup = group;
        ToolNavList.ItemsSource = _viewModel.CurrentNavItems;
        var selected = _viewModel.CurrentNavItems.FirstOrDefault(n => n.IsSelected)
                       ?? _viewModel.CurrentNavItems.FirstOrDefault();
        ToolNavList.SelectedItem = selected;
        if (selected is not null)
        {
            _viewModel.SelectToolNav(selected);
            ApplyActiveTool();
        }
    }

    private void ToolNavList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolNavList.SelectedItem is not ToolNavItemViewModel nav)
        {
            return;
        }

        _viewModel.SelectToolNav(nav);
        ApplyActiveTool();
    }

    private void ApplyActiveTool()
    {
        var split = _viewModel.ActiveHost is ISplitPaneToolHost { UsesSplitPaneLayout: true };
        ToolContentScroll.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        ToolContentSplitHost.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

        if (split)
        {
            ToolContentHost.Content = null;
            ToolContentSplitHost.Content = _viewModel.ActiveContent;
        }
        else
        {
            ToolContentSplitHost.Content = null;
            ToolContentHost.Content = _viewModel.ActiveContent;
        }

        ActionBar.Bind(_viewModel.ActiveHost);
        UpdatePreviewHost();
    }

    private void UpdatePreviewHost()
    {
        PreviewHost.Content = null;
        PreviewHostBorder.Visibility = Visibility.Collapsed;

        switch (_viewModel.ActivePreviewKind)
        {
            case VideoPreviewKind.SceneCutter when _viewModel.ActivePreviewViewModel is SceneCutterViewModel sceneVm:
                PreviewHost.Content = new SceneCutterVideoPreviewHost(sceneVm);
                PreviewHostBorder.Visibility = Visibility.Visible;
                break;
            case VideoPreviewKind.IntroCutter when _viewModel.ActivePreviewViewModel is IntroCutterViewModel introVm:
                PreviewHost.Content = new IntroVideoPreviewHost(introVm);
                PreviewHostBorder.Visibility = Visibility.Visible;
                break;
            case VideoPreviewKind.MarkerUpdater when _viewModel.ActivePreviewViewModel is MarkerUpdaterViewModel markerVm:
                PreviewHost.Content = new MarkerUpdaterVideoPreviewHost(markerVm);
                PreviewHostBorder.Visibility = Visibility.Visible;
                break;
        }
    }

    private async void ToolShellPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || e.Handled)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox)
        {
            return;
        }

        e.Handled = true;
        await ActionBar.TryExecutePrimaryAsync();
    }
}
