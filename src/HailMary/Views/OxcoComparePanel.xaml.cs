using System.Collections.ObjectModel;
using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace HailMary.Views;

public sealed partial class OxcoComparePanel : UserControl
{
    public OxcoCompareViewModel ViewModel { get; }

    private bool _suppressDeepfakeSelectionSync;
    private bool _suppressOriginalSelectionSync;

    public OxcoComparePanel(OxcoCompareViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DeepfakeList.ContextRequested += DeepfakeList_OnContextRequested;
        DeepfakeList.RightTapped += DeepfakeList_OnRightTapped;
        TaggerTagBox.DropDownOpened += (_, _) => ViewModel.RefreshTaggerTagChoices();
        ViewModel.ScrollToOriginalRequested += path =>
            ScheduleScrollToPath(
                OriginalList,
                ViewModel.OriginalDisplayItems,
                path,
                selectItem: true,
                suppressSelectionHandler: true);
        ViewModel.ScrollToDeepfakeRequested += path =>
            ScheduleScrollToPath(
                DeepfakeList,
                ViewModel.DeepfakeDisplayItems,
                path,
                selectItem: false,
                suppressSelectionHandler: true);
        ViewModel.RestoreDeepfakeMultiSelectionRequested += RestoreDeepfakeMultiSelection;
        ViewModel.ClearDeepfakeListSelectionRequested += ClearDeepfakeListSelection;
    }

    private void ScheduleScrollToPath(
        ListView list,
        ObservableCollection<OxcoCompareDisplayItem> items,
        string path,
        bool selectItem,
        bool suppressSelectionHandler = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        void TryScroll()
        {
            if (suppressSelectionHandler)
            {
                if (ReferenceEquals(list, DeepfakeList))
                {
                    _suppressDeepfakeSelectionSync = true;
                }
                else if (ReferenceEquals(list, OriginalList))
                {
                    _suppressOriginalSelectionSync = true;
                }
            }

            try
            {
                ScrollListToPath(list, items, path, selectItem);
            }
            finally
            {
                if (suppressSelectionHandler)
                {
                    if (ReferenceEquals(list, DeepfakeList))
                    {
                        _suppressDeepfakeSelectionSync = false;
                    }
                    else if (ReferenceEquals(list, OriginalList))
                    {
                        _suppressOriginalSelectionSync = false;
                    }
                }
            }
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, TryScroll);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, TryScroll);
    }

    private static void ScrollListToPath(
        ListView list,
        ObservableCollection<OxcoCompareDisplayItem> items,
        string path,
        bool selectItem)
    {
        var rows = items.ToList();
        var index = rows.FindIndex(i =>
            !i.IsGroupHeader
            && i.Entry is not null
            && string.Equals(i.Entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        for (var i = index; i >= 0; i--)
        {
            if (rows[i].IsGroupHeader)
            {
                list.ScrollIntoView(rows[i], ScrollIntoViewAlignment.Leading);
                break;
            }
        }

        var item = rows[index];
        list.UpdateLayout();
        list.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        if (selectItem && !Equals(list.SelectedItem, item))
        {
            list.SelectedItem = item;
        }
    }

    private void RestoreDeepfakeMultiSelection(IReadOnlyList<string> paths)
    {
        var mergedPaths = paths
            .Concat(ViewModel.SelectedDeepfakeEntries.Select(e => e.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mergedPaths.Count == 0)
        {
            return;
        }

        void Apply()
        {
            _suppressDeepfakeSelectionSync = true;
            try
            {
                DeepfakeList.SelectedItems.Clear();
                var entries = new List<OxcoCompareFileEntry>();
                foreach (var item in ViewModel.DeepfakeDisplayItems)
                {
                    if (item.IsGroupHeader || item.Entry is null)
                    {
                        continue;
                    }

                    if (mergedPaths.Contains(item.Entry.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        DeepfakeList.SelectedItems.Add(item);
                        entries.Add(item.Entry);
                    }
                }

                ViewModel.SetSelectedDeepfakeEntries(entries);
            }
            finally
            {
                _suppressDeepfakeSelectionSync = false;
            }
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, Apply);
    }

    private void ClearDeepfakeListSelection()
    {
        _suppressDeepfakeSelectionSync = true;
        try
        {
            DeepfakeList.SelectedItems.Clear();
        }
        finally
        {
            _suppressDeepfakeSelectionSync = false;
        }
    }

    private async void MoveSelectedDeepfakesToBitrate_Click(object sender, RoutedEventArgs e) =>
        await MoveDeepfakesWithListSelectionAsync(ViewModel.MoveDeepfakesToBitrateInCommand);

    private async void MoveSelectedDeepfakesToTagger_Click(object sender, RoutedEventArgs e) =>
        await MoveDeepfakesWithListSelectionAsync(ViewModel.MoveDeepfakesToTaggerInCommand);

    private async Task MoveDeepfakesWithListSelectionAsync(
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<IReadOnlyList<string>?> command)
    {
        var paths = ViewModel.SelectedDeepfakePaths.ToList();
        if (paths.Count == 0)
        {
            ViewModel.Status = Loc.T("oxco.status.noDeepfakesSelected");
            return;
        }

        await command.ExecuteAsync(paths);
    }

    private void OriginalList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOriginalSelectionSync)
        {
            return;
        }

        if (OriginalList.SelectedItem is OxcoCompareDisplayItem item
            && !item.IsGroupHeader
            && item.Entry is not null)
        {
            ViewModel.SelectedOriginalItem = item;
        }
    }

    private static bool IsExtendSelectionGesture()
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        return ctrl.HasFlag(CoreVirtualKeyStates.Down) || shift.HasFlag(CoreVirtualKeyStates.Down);
    }

    private void DeepfakeList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeepfakeSelectionSync)
        {
            return;
        }

        var extending = IsExtendSelectionGesture();
        var entries = CollectSelectedDeepfakeEntries(e, mergePrevious: extending);
        ViewModel.SetSelectedDeepfakeEntries(entries);

        if (extending || entries.Count > 1)
        {
            SyncListSelectionFromViewModel(entries);
            if (entries.Count > 0)
            {
                ViewModel.Status = entries.Count == 1
                    ? Loc.T("oxco.status.deepfakeMarkedSingle")
                    : Loc.F("oxco.status.deepfakesSelectedMulti", entries.Count);
            }

            return;
        }

        if (DeepfakeList.SelectedItem is OxcoCompareDisplayItem item && !item.IsGroupHeader)
        {
            ViewModel.ApplyDeepfakeSelection(item);
            ViewModel.SelectedDeepfakeItem = item;
        }
        else if (entries.Count == 0)
        {
            ViewModel.SelectedDeepfakeItem = null;
        }
    }

    private List<OxcoCompareFileEntry> CollectSelectedDeepfakeEntries(
        SelectionChangedEventArgs e,
        bool mergePrevious)
    {
        var map = new Dictionary<string, OxcoCompareFileEntry>(StringComparer.OrdinalIgnoreCase);

        void AddDisplayItem(OxcoCompareDisplayItem? displayItem)
        {
            if (displayItem?.Entry is null || displayItem.IsGroupHeader)
            {
                return;
            }

            map[displayItem.Entry.Path] = displayItem.Entry;
        }

        foreach (OxcoCompareDisplayItem displayItem in DeepfakeList.SelectedItems)
        {
            AddDisplayItem(displayItem);
        }

        foreach (OxcoCompareDisplayItem displayItem in e.AddedItems)
        {
            AddDisplayItem(displayItem);
        }

        if (mergePrevious)
        {
            foreach (var entry in ViewModel.SelectedDeepfakeEntries)
            {
                map[entry.Path] = entry;
            }
        }

        foreach (OxcoCompareDisplayItem displayItem in e.RemovedItems)
        {
            if (displayItem.Entry is not null)
            {
                map.Remove(displayItem.Entry.Path);
            }
        }

        return map.Values.ToList();
    }

    private void SyncListSelectionFromViewModel(IReadOnlyList<OxcoCompareFileEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var paths = entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listMatches = DeepfakeList.SelectedItems
            .OfType<OxcoCompareDisplayItem>()
            .Where(i => !i.IsGroupHeader && i.Entry is not null)
            .Select(i => i.Entry!.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (paths.SetEquals(listMatches))
        {
            return;
        }

        _suppressDeepfakeSelectionSync = true;
        try
        {
            DeepfakeList.SelectedItems.Clear();
            foreach (var item in ViewModel.DeepfakeDisplayItems)
            {
                if (item.IsGroupHeader || item.Entry is null)
                {
                    continue;
                }

                if (paths.Contains(item.Entry.Path))
                {
                    DeepfakeList.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _suppressDeepfakeSelectionSync = false;
        }
    }

    private void TaggerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaggerList.SelectedItem is TaggerFileRowViewModel row)
        {
            ViewModel.OnTaggerFileSelected(row);
        }
        else if (TaggerList.SelectedItems.Count == 0)
        {
            ViewModel.OnTaggerFileSelected(null);
        }
    }

    private void SyncTaggerTagFromUi() => ViewModel.TaggerTag = TaggerTagBox.Text;

    private async void RunTaggerAll_Click(object sender, RoutedEventArgs e)
    {
        SyncTaggerTagFromUi();
        await ViewModel.RunTaggerCommand.ExecuteAsync(null);
    }

    private async void RunTaggerSelected_Click(object sender, RoutedEventArgs e)
    {
        SyncTaggerTagFromUi();
        var paths = TaggerList.SelectedItems
            .OfType<TaggerFileRowViewModel>()
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            ViewModel.NotifyTaggerSelectionRequired();
            return;
        }

        await ViewModel.RunTaggerCommand.ExecuteAsync(paths);
    }

    private async void TagRouteSetup_Click(object sender, RoutedEventArgs e)
    {
        var rows = ViewModel.CloneTagRouteRules();
        if (rows.Count == 0)
        {
            rows.Add(new TagRouteRuleRow());
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var stack = new StackPanel { Spacing = 8 };
        scroll.Content = stack;

        var rowControls = new List<(TagRouteRuleRow Model, TextBox TagBox, TextBox FolderBox)>();
        void RebuildRows()
        {
            stack.Children.Clear();
            rowControls.Clear();
            foreach (var model in rows)
            {
                var grid = new Grid { ColumnSpacing = 6 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tagBox = new TextBox { PlaceholderText = Loc.T("oxco.tagPlaceholderShort"), Text = model.Tag };
                tagBox.TextChanged += (_, _) => model.Tag = tagBox.Text;
                Grid.SetColumn(tagBox, 0);

                var folderBox = new TextBox { PlaceholderText = Loc.T("oxco.targetFolderPlaceholder"), Text = model.Folder };
                folderBox.TextChanged += (_, _) => model.Folder = folderBox.Text;
                Grid.SetColumn(folderBox, 1);

                var browseBtn = new Button { Content = "…", Width = 36 };
                browseBtn.Click += async (_, _) =>
                {
                    var path = await Services.FolderPickerHelper.PickFolderAsync(folderBox.Text);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        folderBox.Text = path;
                        model.Folder = path;
                    }
                };
                Grid.SetColumn(browseBtn, 2);

                var removeBtn = new Button { Content = "−" };
                var captured = model;
                removeBtn.Click += (_, _) =>
                {
                    rows.Remove(captured);
                    RebuildRows();
                };
                Grid.SetColumn(removeBtn, 3);

                grid.Children.Add(tagBox);
                grid.Children.Add(folderBox);
                grid.Children.Add(browseBtn);
                grid.Children.Add(removeBtn);
                stack.Children.Add(grid);
                rowControls.Add((model, tagBox, folderBox));
            }
        }

        RebuildRows();

        var addBtn = new Button { Content = Loc.T("oxco.tagRouteAddRow"), HorizontalAlignment = HorizontalAlignment.Left };
        addBtn.Click += (_, _) =>
        {
            rows.Add(new TagRouteRuleRow());
            RebuildRows();
        };

        var outer = new StackPanel { Spacing = 10 };
        outer.Children.Add(new TextBlock
        {
            Text = Loc.T("oxco.tagRouteHint"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.8,
        });
        outer.Children.Add(scroll);
        outer.Children.Add(addBtn);

        var dialog = new ContentDialog
        {
            Title = Loc.T("oxco.tagRouteDialogTitle"),
            Content = outer,
            PrimaryButtonText = Loc.T("common.save"),
            SecondaryButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ApplyTagRouteRulesFromDialog(rows);
        }
    }

    private void DeepfakeList_OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var clicked = FindDisplayItemFromSource(e.OriginalSource);
        if (ShowDeepfakeContextFlyout(clicked, e.GetPosition(DeepfakeList)))
        {
            e.Handled = true;
        }
    }

    private void DeepfakeList_OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        OxcoCompareDisplayItem? clicked = null;
        if (args.TryGetPosition(DeepfakeList, out var point))
        {
            clicked = FindDisplayItemAtPoint(point);
        }

        if (ShowDeepfakeContextFlyout(clicked, null, args))
        {
            args.Handled = true;
        }
    }

    private bool ShowDeepfakeContextFlyout(
        OxcoCompareDisplayItem? clickedItem,
        Windows.Foundation.Point? position = null,
        ContextRequestedEventArgs? contextArgs = null)
    {
        var paths = ResolveDeepfakePathsForAction(clickedItem);
        if (paths.Count == 0)
        {
            return false;
        }

        var pathSnapshot = paths.ToList();
        var flyout = new MenuFlyout { XamlRoot = DeepfakeList.XamlRoot ?? XamlRoot };

        var bitrateItem = new MenuFlyoutItem { Text = Loc.T("oxco.contextMoveToBitrate") };
        bitrateItem.Click += async (_, _) =>
            await ViewModel.MoveDeepfakesToBitrateInCommand.ExecuteAsync(pathSnapshot);
        flyout.Items.Add(bitrateItem);

        var taggerItem = new MenuFlyoutItem { Text = Loc.T("oxco.contextMoveToTagger") };
        taggerItem.Click += async (_, _) =>
            await ViewModel.MoveDeepfakesToTaggerInCommand.ExecuteAsync(pathSnapshot);
        flyout.Items.Add(taggerItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var removeItem = new MenuFlyoutItem { Text = Loc.T("oxco.contextRemoveFromList") };
        removeItem.Click += (_, _) =>
            ViewModel.RemoveDeepfakesFromListCommand.Execute(pathSnapshot);
        flyout.Items.Add(removeItem);

        var recycleItem = new MenuFlyoutItem { Text = Loc.T("oxco.contextRecycle") };
        recycleItem.Click += async (_, _) =>
            await ViewModel.RecycleDeepfakesCommand.ExecuteAsync(pathSnapshot);
        flyout.Items.Add(recycleItem);

        if (position is { } p)
        {
            flyout.ShowAt(DeepfakeList, p);
        }
        else if (contextArgs?.TryGetPosition(DeepfakeList, out var ctxPoint) == true)
        {
            flyout.ShowAt(DeepfakeList, ctxPoint);
        }
        else
        {
            flyout.ShowAt(DeepfakeList);
        }

        return true;
    }

    private List<string> ResolveDeepfakePathsForAction(OxcoCompareDisplayItem? clickedItem = null)
    {
        var paths = ViewModel.SelectedDeepfakePaths.ToList();

        if (clickedItem?.Entry is not null && !clickedItem.IsGroupHeader)
        {
            var clickedPath = clickedItem.Entry.Path;
            if (!paths.Contains(clickedPath, StringComparer.OrdinalIgnoreCase))
            {
                paths = paths.Count == 0 || !IsExtendSelectionGesture()
                    ? [clickedPath]
                    : [.. paths, clickedPath];
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static OxcoCompareDisplayItem? FindDisplayItemFromSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            switch (current)
            {
                case ListViewItem { Content: OxcoCompareDisplayItem content } when !content.IsGroupHeader:
                    return content;
                case ListViewItem { DataContext: OxcoCompareDisplayItem dataContext } when !dataContext.IsGroupHeader:
                    return dataContext;
                case FrameworkElement { DataContext: OxcoCompareDisplayItem feItem } when !feItem.IsGroupHeader:
                    return feItem;
            }
        }

        return null;
    }

    private OxcoCompareDisplayItem? FindDisplayItemAtPoint(Windows.Foundation.Point point)
    {
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, DeepfakeList, true))
        {
            var item = FindDisplayItemFromSource(element);
            if (item is not null)
            {
                return item;
            }
        }

        return null;
    }
}
