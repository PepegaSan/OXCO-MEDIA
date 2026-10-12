using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using HailMary.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HailMary.Views;

public sealed partial class MarkerUpdaterPanel : UserControl
{
    public MarkerUpdaterViewModel ViewModel { get; }

    public MarkerUpdaterPanel(MarkerUpdaterViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
        InitializeComponent();
        PreviewHost.Content = new MarkerUpdaterVideoPreviewHost(viewModel);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MarkerUpdaterViewModel.SelectedResult))
            {
                ResultsList.SelectedItem = ViewModel.SelectedResult;
            }
            else if (e.PropertyName == nameof(MarkerUpdaterViewModel.SelectedMarker))
            {
                if (!ReferenceEquals(MarkersList.SelectedItem, ViewModel.SelectedMarker))
                {
                    MarkersList.SelectedItem = ViewModel.SelectedMarker;
                }

                SyncPrimaryTagComboText();
            }
            else if (e.PropertyName == nameof(MarkerUpdaterViewModel.NewMarkerTagName))
            {
                SyncPrimaryTagComboText();
            }
        };
    }

    private void SyncPrimaryTagComboText()
    {
        var text = ViewModel.NewMarkerTagName ?? string.Empty;
        if (!string.Equals(PrimaryTagCombo.Text, text, StringComparison.Ordinal))
        {
            PrimaryTagCombo.Text = text;
        }

        if (string.IsNullOrEmpty(text))
        {
            PrimaryTagCombo.SelectedItem = null;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = Loc.T("common.continue"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        return (await dialog.ShowAsync()) == ContentDialogResult.Primary;
    }

    private void ResultsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedResult = ResultsList.SelectedItem as StashSceneRowViewModel;
    }

    private async void ResultsList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StashSceneRowViewModel row)
        {
            await ViewModel.OpenResultCommand.ExecuteAsync(row);
        }
    }

    private void FindBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SearchBox_OnKeyDown(sender, e, ViewModel.FindOrLoadCommand);

    private async void TagMarkerHere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            await ViewModel.MarkerWithTagHereCommand.ExecuteAsync(tag);
        }
    }

    private void TagRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.RemoveSceneTagCommand.Execute(tag);
        }
    }

    private async void DeleteMarker_Click(object sender, RoutedEventArgs e)
    {
        var marker = ViewModel.SelectedMarker;
        if (marker is null)
        {
            return;
        }

        if (!await ConfirmAsync(
                Loc.T("markerupdater.deleteMarker"),
                Loc.F("markerupdater.deleteMarkerConfirm", marker.TitleText, marker.RangeText)))
        {
            return;
        }

        await ViewModel.DeleteSelectedMarkerCommand.ExecuteAsync(null);
    }

    private void MarkersList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedMarker = MarkersList.SelectedItem as StashMarkerRowViewModel;
    }

    // Click anywhere on the row toggles its checkbox (a double-click toggles twice and opens the scene).
    private void BatchList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MarkerBatchSceneRowViewModel row)
        {
            row.IsChecked = !row.IsChecked;
        }
    }

    private void CleanupClampList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetCleanupClampSelection(
            CleanupClampList.SelectedItems.OfType<MarkerCleanupRowViewModel>());
    }

    private void CleanupDeleteList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetCleanupDeleteSelection(
            CleanupDeleteList.SelectedItems.OfType<MarkerCleanupRowViewModel>());
    }

    private void BatchSearchBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SearchBox_OnKeyDown(sender, e, ViewModel.BatchSearchCommand);

    private async void BatchList_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (StashPanelInteraction.FindDataContext<MarkerBatchSceneRowViewModel>(e.OriginalSource) is not { } row)
        {
            return;
        }

        ViewModel.SelectedBatchMatch = row;
        ViewModel.SceneId = row.SceneId;
        e.Handled = true;

        if (ViewModel.LoadSceneCommand.CanExecute(null))
        {
            RootTabs.SelectedIndex = 0;
            await ViewModel.LoadSceneCommand.ExecuteAsync(null);
        }
    }

    private void BatchSelectAll_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.BatchSelectAllRows();
    }

    private void BatchClearSelection_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.BatchClearSelection();
    }

    private async void CleanupClampSelected_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.CleanupClampSelectedCommand.CanExecute(null))
        {
            await ViewModel.CleanupClampSelectedCommand.ExecuteAsync(null);
        }
    }

    private async void CleanupDeleteSelected_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.CleanupDeleteSelectedCommand.CanExecute(null))
        {
            await ViewModel.CleanupDeleteSelectedCommand.ExecuteAsync(null);
        }
    }
}
