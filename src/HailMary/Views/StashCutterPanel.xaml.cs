using HailMary.ViewModels;
using HailMary.Views.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HailMary.Views;

public sealed partial class StashCutterPanel : UserControl
{
    public StashCutterViewModel ViewModel { get; }

    public StashCutterPanel(StashCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        CutterContentHost.Content = new SceneCutterScenesPanel(viewModel);
    }

    private async void ClipboardButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.LoadFromClipboardCommand.CanExecute(null))
        {
            await ViewModel.LoadFromClipboardCommand.ExecuteAsync(null);
        }
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
