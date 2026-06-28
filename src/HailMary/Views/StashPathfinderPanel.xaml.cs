using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HailMary.Views;

public sealed partial class StashPathfinderPanel : UserControl
{
    public StashPathfinderViewModel ViewModel { get; }

    public StashPathfinderPanel(StashPathfinderViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void ResultsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedResult = ResultsList.SelectedItem as StashSceneRowViewModel;
    }

    private void SearchBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SearchBox_OnKeyDown(sender, e, ViewModel.SearchCommand);

    private void SceneIdBox_OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        StashPanelInteraction.SceneIdBox_OnKeyDown(sender, e, ViewModel.LoadSceneCommand);

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
            ViewModel.LoadSceneCommand);
    }
}
