using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.Views;

public sealed partial class DavinciBatchRenderPanel : UserControl
{
    public DavinciBatchRenderViewModel ViewModel { get; }

    public DavinciBatchRenderPanel(DavinciBatchRenderViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        EnableDrop(DropZone);
        EnableDrop(QueueList);
    }

    private void EnableDrop(UIElement element)
    {
        element.AllowDrop = true;
        element.DragOver += (_, e) =>
        {
            if (!ViewModel.CanEditQueue)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }

            e.Handled = true;
        };
        element.Drop += OnDropAsync;
    }

    private async void OnDropAsync(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEditQueue)
        {
            e.Handled = true;
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        ViewModel.AddDroppedPaths(paths);
        e.Handled = true;
    }

    private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedIndex(QueueList.SelectedIndex);
    }
}
