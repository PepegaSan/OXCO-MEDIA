using HailMary.ViewModels;
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
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MarkerUpdaterViewModel.SelectedResult))
            {
                ResultsList.SelectedItem = ViewModel.SelectedResult;
            }
        };
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = "Fortfahren",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        return (await dialog.ShowAsync()) == ContentDialogResult.Primary;
    }

    private void ResultsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedResult = ResultsList.SelectedItem as StashSceneRowViewModel;
    }

    private void SceneTagsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedSceneTag = SceneTagsList.SelectedItem as string;
    }

    private void MarkersList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedMarker = MarkersList.SelectedItem as StashMarkerRowViewModel;
    }

    private void BatchList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = BatchList.SelectedItems
            .OfType<MarkerBatchSceneRowViewModel>()
            .ToList();
        ViewModel.SetBatchSelection(selected);
        ViewModel.SelectedBatchMatch = selected.FirstOrDefault();
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

    private void SearchBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SearchBox_OnKeyDown(sender, e, ViewModel.SearchCommand);

    private void SceneIdBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SceneIdBox_OnKeyDown(sender, e, ViewModel.LoadSceneCommand);

    private void BatchSearchBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SearchBox_OnKeyDown(sender, e, ViewModel.BatchSearchCommand);

    private void ResultsList_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        StashPanelInteraction.ResultsList_OnDoubleTapped(
            sender,
            e,
            ResultsList,
            row =>
            {
                ViewModel.SelectedResult = row;
                ViewModel.SceneId = row.SceneId;
            },
            ViewModel.UseSelectedResultCommand);
    }

    private void BatchList_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (BatchList.SelectedItem is not MarkerBatchSceneRowViewModel row)
        {
            return;
        }

        ViewModel.SceneId = row.SceneId;
        ViewModel.OpenInStashCommand.Execute(null);
    }

    private void BatchSelectAll_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        BatchList.SelectAll();
        ViewModel.BatchSelectAllRows();
    }

    private void BatchClearSelection_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        BatchList.SelectedItems.Clear();
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
