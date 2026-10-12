using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.Views;

public sealed partial class ClipJoinerPanel : UserControl
{
    public ClipJoinerViewModel ViewModel { get; }

    public ClipJoinerPanel(ClipJoinerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        EnableDrop(DropZone);
        EnableDrop(ClipList);
    }

    private void EnableDrop(UIElement element)
    {
        element.AllowDrop = true;
        element.DragOver += (_, e) =>
        {
            // Only claim file drops — the list's own drag-to-reorder must pass through.
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
            }
        };
        element.Drop += OnDropAsync;
    }

    private async void OnDropAsync(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // Internal reorder drop — handled by the ListView itself.
            return;
        }

        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count > 0)
        {
            ViewModel.AddDroppedPaths(paths);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveClipUpCommand.Execute((sender as FrameworkElement)?.Tag as ClipJoinerClipRow);

    private void MoveDown_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveClipDownCommand.Execute((sender as FrameworkElement)?.Tag as ClipJoinerClipRow);

    private void Remove_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveClipCommand.Execute((sender as FrameworkElement)?.Tag as ClipJoinerClipRow);

    private void BatchList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelectedBatchIndex(BatchList.SelectedIndex);
}
