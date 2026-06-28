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

    private void ClipList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipJoinerClipRow row)
        {
            ViewModel.SetSelectedClipIndex(row.Index - 1);
        }
        else
        {
            ViewModel.SetSelectedClipIndex(-1);
        }
    }

    private void BatchList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedBatchIndex(BatchList.SelectedIndex);
    }
}
