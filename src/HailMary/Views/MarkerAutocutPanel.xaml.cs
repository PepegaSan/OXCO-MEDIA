using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class MarkerAutocutPanel : UserControl
{
    public MarkerAutocutViewModel ViewModel { get; }

    public MarkerAutocutPanel(MarkerAutocutViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OrderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedOrderRows(
            OrderList.SelectedItems.OfType<MarkerAutocutOrderRowViewModel>().ToList());
    }

    private void OrderList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        ViewModel.OnExportOrderReordered();
    }
}
