using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HailMary.Views;

public sealed partial class MarkerAutocutPanel : UserControl
{
    private bool _syncingFileSelection;

    public MarkerAutocutViewModel ViewModel { get; }

    public MarkerAutocutPanel(MarkerAutocutViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OrderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFileSelection)
        {
            return;
        }

        ViewModel.SetSelectedOrderRows(
            OrderList.SelectedItems.OfType<MarkerAutocutOrderRowViewModel>().ToList());
    }

    private void OrderList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!ViewModel.IsPerFileExportMode)
        {
            return;
        }

        // Vor dem Ziehen: alle Marker der betroffenen Datei(en) mitnehmen
        var seed = OrderList.SelectedItems.OfType<MarkerAutocutOrderRowViewModel>().ToList();
        if (seed.Count == 0 && e.Items.Count > 0)
        {
            seed = e.Items.OfType<MarkerAutocutOrderRowViewModel>().ToList();
        }

        SelectWholeFiles(seed);
    }

    private void OrderList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        ViewModel.OnExportOrderReordered();
    }

    private void GroupHeader_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ViewModel.IsPerFileExportMode)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: MarkerAutocutOrderRowViewModel row })
        {
            return;
        }

        SelectWholeFiles([row]);
        e.Handled = true;
    }

    private void SelectWholeFiles(IReadOnlyList<MarkerAutocutOrderRowViewModel> seed)
    {
        var expanded = ViewModel.ExpandSelectionToWholeFiles(seed);
        if (expanded.Count == 0)
        {
            return;
        }

        _syncingFileSelection = true;
        try
        {
            OrderList.SelectedItems.Clear();
            foreach (var item in expanded)
            {
                OrderList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _syncingFileSelection = false;
        }

        ViewModel.SetSelectedOrderRows(expanded);
    }
}
