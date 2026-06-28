using HailMary.Models;
using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class IntroCutterPanel : UserControl
{
    public IntroCutterViewModel ViewModel { get; }

    public IntroCutterPanel(IntroCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void BatchList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        var selected = list.SelectedItems
            .OfType<IntroBatchEntry>()
            .ToList();
        ViewModel.UpdateBatchSelection(selected);

        var preview = selected.LastOrDefault();
        if (preview is not null)
        {
            ViewModel.LoadPreviewForPath(preview.Path);
        }
    }

    private void BatchList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is IntroBatchEntry entry)
        {
            ViewModel.LoadPreviewForPath(entry.Path);
        }
    }
}
